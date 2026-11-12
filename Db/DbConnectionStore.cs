using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeelAssistant.Models;

namespace KeelAssistant.Db;

/// <summary>
/// SQL 連線設定的持久化儲存（dbconnections.json），沿用 MemoryStore（已移除）
/// 的 Load/Save + lock 模式。
///
/// 密碼以 DPAPI（CurrentUser scope）加密後才寫檔：密文綁定目前 Windows 使用者帳號，
/// 即使 json 被複製到別台機器或別的帳號也解不開，且不需自行保管任何金鑰。
/// 解密只發生在 <see cref="BuildConnectionString"/> 內部，明文密碼不對外暴露，
/// 也不會出現在任何 MCP 工具回傳中。
/// </summary>
public sealed class DbConnectionStore
{
    private readonly string _configPath;
    private readonly Dictionary<string, DbConnectionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>DPAPI 的額外熵值，讓密文無法被其他程式以同帳號直接解開。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KeelAssistant.Db.v1");

    public DbConnectionStore(string baseDir)
    {
        _configPath = Path.Combine(baseDir, "dbconnections.json");
        Load();
    }

    /// <summary>列出所有連線設定（含密文欄位，僅供設定 UI 使用，不可直接回給 Claude）。</summary>
    public IReadOnlyList<DbConnectionEntry> List()
    {
        lock (_gate)
            return _entries.Values.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public DbConnectionEntry? Get(string id)
    {
        lock (_gate)
            return _entries.GetValueOrDefault(id);
    }

    /// <summary>
    /// 新增或覆寫一組連線。<paramref name="plainPassword"/> 傳 null 表示沿用原密碼
    /// （編輯既有連線但沒改密碼的情境），傳空字串則清空密碼。
    ///
    /// 沿用原密碼時優先取同 ID 的既有密文；查不到才用 <paramref name="entry"/> 自帶的
    /// <see cref="DbConnectionEntry.PasswordEnc"/>——「改 ID 等於重新命名」的情境會走這條，
    /// 呼叫端已先把舊密文帶進來，否則改個 ID 密碼就沒了。
    /// </summary>
    public DbConnectionEntry Upsert(DbConnectionEntry entry, string? plainPassword)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)) throw new ArgumentException("缺少連線 ID");

        lock (_gate)
        {
            var existing = _entries.GetValueOrDefault(entry.Id);
            entry.Id = entry.Id.Trim();
            entry.Server = entry.Server.Trim();
            entry.Database = entry.Database.Trim();
            entry.User = entry.User.Trim();
            entry.Description = entry.Description.Trim();
            entry.PasswordEnc = plainPassword is not null
                ? Protect(plainPassword)
                : existing?.PasswordEnc ?? entry.PasswordEnc ?? "";
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
    /// 組出可用的連線字串。這是明文密碼唯一出現的地方，呼叫端拿去開連線後即丟棄，
    /// 不可記錄到檔案或回傳給 Claude。
    /// </summary>
    public string BuildConnectionString(DbConnectionEntry entry)
    {
        var password = Unprotect(entry.PasswordEnc);

        return entry.Provider switch
        {
            DbProvider.Mssql =>
                $"Server={entry.Server}{(entry.Port is { } p ? $",{p}" : "")};" +
                $"Database={entry.Database};User Id={entry.User};Password={password};" +
                // 內部網路多為自簽憑證，不信任憑證會直接連不上
                "TrustServerCertificate=True;Encrypt=True;Connect Timeout=15",

            DbProvider.Postgresql =>
                $"Host={entry.Server};Port={entry.Port ?? 5432};Database={entry.Database};" +
                $"Username={entry.User};Password={password};Timeout=15",

            _ => throw new InvalidOperationException($"不支援的資料庫類型：{entry.Provider}")
        };
    }

    /// <summary>
    /// 將明文密碼加密成可放進 <see cref="DbConnectionEntry.PasswordEnc"/> 的密文。
    /// 供設定視窗在「尚未儲存前」組出暫時 entry 來測試連線用。
    /// </summary>
    public string EncryptPassword(string plain) => Protect(plain);

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
                $"密碼解密失敗（可能是設定檔來自其他電腦或使用者帳號），請於「SQL 連線設定」重新輸入密碼：{ex.Message}", ex);
        }
    }

    // ── 持久化 ────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_configPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<DbConnectionEntry>>(
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
