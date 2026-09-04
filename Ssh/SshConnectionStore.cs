using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeelAssistant.Models;

namespace KeelAssistant.Ssh;

/// <summary>
/// SSH 連線設定的持久化儲存（sshconnections.json），沿用 <see cref="Db.DbConnectionStore"/>
/// 的 Load/Save + lock + DPAPI 模式。
///
/// 密碼與私鑰 passphrase 以 DPAPI（CurrentUser scope）加密後才寫檔：密文綁定目前 Windows
/// 使用者帳號，即使 json 被複製到別台機器也解不開。解密只發生在建立連線的當下，
/// 明文不對外暴露，也不會出現在任何 MCP 工具回傳中。
///
/// ⚠ 與 SQL 模組相同的約束：<b>不提供列舉連線的 MCP 工具</b>。sshId 必須由 Arlen 主動告知，
/// 連錯誤訊息都不列出可用代號——否則等於讓 Claude 能自己掃出有哪些主機可打。
/// </summary>
public sealed class SshConnectionStore
{
    private readonly string _configPath;
    private readonly Dictionary<string, SshConnectionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>DPAPI 的額外熵值。刻意與 Db 模組不同，讓兩份密文無法互相解開。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KeelAssistant.Ssh.v1");

    public SshConnectionStore(string baseDir)
    {
        _configPath = Path.Combine(baseDir, "sshconnections.json");
        Load();
    }

    /// <summary>列出所有連線（含密文欄位，僅供設定 UI 使用，不可直接回給 Claude）。</summary>
    public IReadOnlyList<SshConnectionEntry> List()
    {
        lock (_gate)
            return _entries.Values.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SshConnectionEntry? Get(string id)
    {
        lock (_gate)
            return _entries.GetValueOrDefault(id);
    }

    /// <summary>
    /// 依 sshId 取出連線，找不到即拋錯。錯誤訊息刻意不列出可用代號（見類別註解）。
    /// </summary>
    public SshConnectionEntry Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("缺少 sshId");
        return Get(id.Trim())
               ?? throw new InvalidOperationException($"查無 sshId「{id}」，請向 Arlen 確認正確代號。");
    }

    /// <summary>
    /// 新增或覆寫一組連線。<paramref name="plainSecret"/> 傳 null 表示沿用原密文
    /// （編輯但沒改密碼的情境），傳空字串則清空。
    /// </summary>
    public SshConnectionEntry Upsert(SshConnectionEntry entry, string? plainSecret)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)) throw new ArgumentException("缺少連線 ID");
        if (string.IsNullOrWhiteSpace(entry.Host)) throw new ArgumentException("缺少主機位址");
        if (string.IsNullOrWhiteSpace(entry.User)) throw new ArgumentException("缺少登入帳號");

        lock (_gate)
        {
            var existing = _entries.GetValueOrDefault(entry.Id);
            entry.Id = entry.Id.Trim();
            entry.Host = entry.Host.Trim();
            entry.User = entry.User.Trim();
            entry.KeyPath = entry.KeyPath.Trim();
            entry.Description = entry.Description.Trim();
            entry.SecretEnc = plainSecret is not null
                ? Protect(plainSecret)
                : existing?.SecretEnc ?? entry.SecretEnc ?? "";
            entry.UpdatedAt = DateTime.Now;

            _entries[entry.Id] = entry;
            Save();
            return entry;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var removed = _entries.Remove(id);
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>
    /// 取出明文密碼／passphrase。這是明文唯一出現的地方，呼叫端建立連線後即丟棄，
    /// 不可記錄到檔案或回傳給 Claude。
    /// </summary>
    public string RevealSecret(SshConnectionEntry entry) => Unprotect(entry.SecretEnc);

    /// <summary>
    /// 記錄首次連線取得的主機金鑰指紋（TOFU）。已有值時不覆寫——
    /// 指紋變更代表對端換機或遭攔截，該由 <c>SshSessionManager</c> 擋下並要求人工處理，
    /// 不能靠這裡默默更新掉。
    /// </summary>
    public void TrustHostKey(string id, string fingerprint)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            if (!string.IsNullOrEmpty(entry.HostKeyFingerprint)) return;
            entry.HostKeyFingerprint = fingerprint;
            entry.UpdatedAt = DateTime.Now;
            Save();
        }
    }

    /// <summary>清除已信任的指紋，供「對端確實換機了」時由設定視窗手動重置。</summary>
    public void ForgetHostKey(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            entry.HostKeyFingerprint = "";
            entry.UpdatedAt = DateTime.Now;
            Save();
        }
    }

    /// <summary>將明文加密成可放進 <see cref="SshConnectionEntry.SecretEnc"/> 的密文（供設定視窗測試連線用）。</summary>
    public string EncryptSecret(string plain) => Protect(plain);

    // ── DPAPI 加解密 ──────────────────────────────────────────────

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            // 換過 Windows 帳號或 json 是從別台複製來的，密文就解不開
            throw new InvalidOperationException(
                $"SSH 密碼解密失敗（可能是設定檔來自其他電腦或使用者帳號），" +
                $"請於「SSH 連線設定」重新輸入：{ex.Message}", ex);
        }
    }

    // ── 持久化 ────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_configPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<SshConnectionEntry>>(
                File.ReadAllText(_configPath), JsonOpts);
            if (loaded is not null)
            {
                _entries.Clear();
                foreach (var e in loaded) _entries[e.Id] = e;
            }
        }
        catch { /* 壞檔忽略，視為無連線設定 */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_entries.Values.ToList(), JsonOpts));
        }
        catch { /* 寫入失敗不影響執行期 */ }
    }
}
