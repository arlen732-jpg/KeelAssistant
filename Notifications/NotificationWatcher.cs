using System.Media;
using KeelAssistant.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace KeelAssistant.Notifications;

/// <summary>
/// Windows 通知中心監看器：定時輪詢 Toast 通知，命中關鍵寄件者或被 @ 時跳置頂視窗。
///
/// 為什麼是「輪詢」而不是掛 <c>NotificationChanged</c> 事件：該事件在傳統桌面程式上
/// 長期不可靠（經常不觸發），輪詢通知中心的成本僅數毫秒，3 秒一次完全可接受。
///
/// 為什麼不是掃 UI：通知的視覺元素一閃即逝且無穩定定位點，
/// <see cref="UserNotificationListener"/> 是官方管道，LINE 改版也不會壞。
///
/// 前置條件（任一不成立則本功能靜默停用，並以氣泡告知原因）：
///   1. 使用者已於「設定 → 隱私權與安全性 → 通知」允許本程式存取通知
///   2. LINE 為市集版（傳統桌面安裝版自繪視窗，通知不進通知中心）
///   3. LINE 未關閉「訊息預覽」（關閉時內文只剩「你有一則新訊息」，內文比對會失效）
/// </summary>
public sealed class NotificationWatcher : IDisposable
{
    private readonly AppSettings _settings;
    private readonly string _baseDir;
    private readonly Action<string, string> _showBalloon;

    private readonly System.Windows.Forms.Timer _timer;

    // 已處理過的通知 id → 最後一次看到的時間（用於定期清理，避免無限成長）
    private readonly Dictionary<uint, DateTime> _seen = new();

    // 冷卻表：聊天室 → 最後一次提醒時間
    private readonly Dictionary<string, DateTime> _lastAlert = new(StringComparer.OrdinalIgnoreCase);

    private UserNotificationListener? _listener;
    private SoundPlayer? _player;
    private MentionAlertForm? _alertForm;
    private bool _busy;

    // 首次掃描只建索引不提醒：程式啟動時通知中心裡的舊訊息不該一次全跳出來
    private bool _primed;

    private const int SeenRetentionMinutes = 60;

    /// <summary>目前狀態（供托盤選單顯示，讓功能失效時看得出來，而不是靜默無聲）。</summary>
    public string Status { get; private set; } = "尚未啟動";

    /// <summary>暫停中則照常掃描並記錄 id，但不發出任何提醒（避免恢復後補跳一堆舊訊息）。</summary>
    public bool Paused { get; set; }

    /// <summary>最近一次命中的摘要，供托盤選單顯示。</summary>
    public string? LastHit { get; private set; }

    // 2026/07/28 - Arlen - 診斷：授權對話框未出現時，無從判斷是「讀不到通知」還是「讀得到但沒命中」。
    // 把每輪實際看到的通知數與來源 App 攤在托盤選單上，讓兩者一眼可分 - Start

    /// <summary>最近一輪掃描實際讀到的 Toast 通知數（含所有 App，未經過濾）。</summary>
    public int LastScanCount { get; private set; }

    /// <summary>最近一輪掃描看到的來源 App（顯示名稱＋AppUserModelId），供比對過濾條件是否寫對。</summary>
    public IReadOnlyList<string> RecentApps => _recentApps;

    private readonly List<string> _recentApps = new();

    // 2026/07/28 - Arlen - 診斷 - end

    public NotificationWatcher(AppSettings settings, string baseDir, Action<string, string> showBalloon)
    {
        _settings = settings;
        _baseDir = baseDir;
        _showBalloon = showBalloon;

        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, _settings.NotificationScanSeconds) * 1000
        };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// 請求通知存取權並開始輪詢。授權對話框由系統顯示，故必須在 UI 執行緒呼叫。
    /// 任何失敗都只停用本功能，不影響其他模組。
    /// </summary>
    public async void Start()
    {
        if (!_settings.NotificationWatchEnabled)
        {
            Status = "已停用（appsettings.json）";
            return;
        }

        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                _listener = null;
                Status = "未授權存取通知";
                _showBalloon("LINE 提醒未啟用",
                    "請到「設定 → 隱私權與安全性 → 通知」允許 Keel Assistant 存取通知，再重啟本程式。");
                return;
            }
        }
        catch (Exception ex)
        {
            _listener = null;
            Status = $"啟動失敗：{ex.Message}";
            _showBalloon("LINE 提醒啟動失敗", ex.Message);
            return;
        }

        _timer.Start();
        Status = $"監看中（每 {Math.Max(1, _settings.NotificationScanSeconds)} 秒）";
    }

    // ── 輪詢 ──────────────────────────────────────────────────────

    /// <summary>
    /// Tick 已在 UI 執行緒，await 後仍回到 UI 執行緒，因此可直接開視窗。
    /// 讀取通知本身是毫秒級操作，不需要丟到執行緒池；_busy 防止前一次尚未回來就再排一次。
    /// </summary>
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || _listener is null) return;
        _busy = true;
        try
        {
            await ScanAsync();
        }
        catch (Exception ex)
        {
            // 通知中心偶發讀取失敗（系統忙碌、通知正被移除）不該中斷監看，記在狀態即可
            Status = $"讀取通知失敗：{ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ScanAsync()
    {
        var notes = await _listener!.GetNotificationsAsync(NotificationKinds.Toast);
        var now = DateTime.Now;

        // 2026/07/28 - Arlen - 診斷：記錄本輪讀到的通知數與來源 App - Start
        LastScanCount = notes.Count;
        _recentApps.Clear();
        // 2026/07/28 - Arlen - 診斷 - end

        foreach (var n in notes)
        {
            uint id;
            try { id = n.Id; } catch { continue; }

            // 2026/07/28 - Arlen - 診斷：來源 App 不論是否命中過濾條件都要收集 - Start
            var label = DescribeApp(n);
            if (label.Length > 0 && !_recentApps.Contains(label)) _recentApps.Add(label);
            // 2026/07/28 - Arlen - 診斷 - end

            bool isNew = !_seen.ContainsKey(id);
            _seen[id] = now;

            // 舊通知、首輪建索引、暫停中：都只記 id 不提醒
            if (!isNew || !_primed || Paused) continue;

            try { Evaluate(n); }
            catch { /* 單則通知解析失敗（欄位結構特殊）不影響其餘通知 */ }
        }

        if (!_primed)
        {
            _primed = true;
            Status = $"監看中（每 {Math.Max(1, _settings.NotificationScanSeconds)} 秒）";
        }

        Prune(now);
    }

    /// <summary>清掉超過保留期的 id 與冷卻記錄，避免長時間常駐後字典無限成長。</summary>
    private void Prune(DateTime now)
    {
        var cutoff = now.AddMinutes(-SeenRetentionMinutes);

        foreach (var id in _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            _seen.Remove(id);

        foreach (var key in _lastAlert.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            _lastAlert.Remove(key);
    }

    // ── 比對 ──────────────────────────────────────────────────────

    private void Evaluate(UserNotification n)
    {
        if (!IsWatchedApp(n, out var appName)) return;

        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is null) return;

        var texts = binding.GetTextElements();
        if (texts is null || texts.Count == 0) return;

        // 第一段文字是標題（1:1 為對方名稱、群組為群組名），其餘串成內文
        var title = texts[0].Text?.Trim() ?? "";
        var body = string.Join(" ", texts.Skip(1).Select(t => t.Text?.Trim() ?? "")).Trim();
        var haystack = title + "\n" + body;

        string reason;
        bool strong;

        if (ContainsAny(haystack, _settings.NotificationSenders))
        {
            reason = "關鍵聯絡人訊息";
            strong = true;
        }
        else if (ContainsAny(haystack, _settings.NotificationMentions))
        {
            reason = "有人 @ 你";
            strong = true;
        }
        else if (ContainsAny(body, _settings.NotificationKeywords))
        {
            reason = "訊息提到你";
            strong = false;
        }
        else
        {
            return;
        }

        // 冷卻以「聊天室 + 強弱」為單位：同一室連發只提醒一次，但弱訊號不會壓掉後續強訊號
        var key = $"{appName}|{title}|{strong}";
        var now = DateTime.Now;
        int cooldown = Math.Max(0, _settings.NotificationCooldownSeconds);
        if (_lastAlert.TryGetValue(key, out var last) && (now - last).TotalSeconds < cooldown) return;
        _lastAlert[key] = now;

        LastHit = $"{now:HH:mm:ss}　{reason}：{title}";

        if (strong) AlertStrong(reason, title, body, now);
        else _showBalloon($"{reason}：{title}", body);
    }

    /// <summary>
    /// 是否為監看中的應用程式。AppInfo 與 DisplayInfo 對部分來源（系統通知、已移除的 App）
    /// 會直接拋例外，因此整段包起來，取不到就當作不監看。
    /// </summary>
    private bool IsWatchedApp(UserNotification n, out string appName)
    {
        appName = "";
        try
        {
            var info = n.AppInfo;
            if (info is null) return false;

            var appId = info.AppUserModelId ?? "";
            var name = "";
            try { name = info.DisplayInfo?.DisplayName ?? ""; } catch { }
            appName = name;

            var filters = _settings.NotificationApps;
            if (filters is null || filters.Length == 0) return true;

            return filters.Any(f =>
                !string.IsNullOrWhiteSpace(f) &&
                (appId.Contains(f.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 name.Contains(f.Trim(), StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    // 2026/07/28 - Arlen - 診斷用：把來源 App 描述成「顯示名稱 ‹AppUserModelId›」，
    // 讓 NotificationApps 的過濾字串可以直接對照著填 - Start
    private static string DescribeApp(UserNotification n)
    {
        try
        {
            var info = n.AppInfo;
            if (info is null) return "";

            var appId = info.AppUserModelId ?? "";
            var name = "";
            try { name = info.DisplayInfo?.DisplayName ?? ""; } catch { }

            if (appId.Length == 0 && name.Length == 0) return "";
            return name.Length > 0 ? $"{name} ‹{appId}›" : appId;
        }
        catch
        {
            return "（無法取得來源 App）";
        }
    }
    // 2026/07/28 - Arlen - 診斷用 - end

    private static bool ContainsAny(string haystack, string[]? needles)
        => needles is not null && needles.Any(k =>
               !string.IsNullOrWhiteSpace(k) &&
               haystack.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase));

    // ── 提醒 ──────────────────────────────────────────────────────

    // 2026/07/28 - Arlen - 診斷：把「偵測」與「提醒」兩段拆開驗證。
    // 沒跳視窗時可先按這個確認提醒本身是好的，問題就一定出在讀取通知那一段 - Start

    /// <summary>手動觸發一次示範提醒，用於驗證視窗與音效路徑是否正常。</summary>
    public void TestAlert()
        => AlertStrong("測試提醒", "ASVT Brian", "這是一則測試訊息，看到這個視窗代表提醒功能正常。", DateTime.Now);

    // 2026/07/28 - Arlen - 診斷 - end

    /// <summary>強訊號：一次性音效 + 置頂視窗。前一個提醒未關就先收掉，避免視窗堆疊。</summary>
    private void AlertStrong(string reason, string title, string body, DateTime at)
    {
        StopAlertSound();

        var soundPath = Path.IsPathRooted(_settings.AlarmSoundPath)
            ? _settings.AlarmSoundPath
            : Path.Combine(_baseDir, _settings.AlarmSoundPath);

        if (File.Exists(soundPath))
        {
            try
            {
                _player = new SoundPlayer(soundPath);
                _player.Play();   // 單次播放，不循環：這是提醒不是鬧鐘
            }
            catch { SystemSounds.Exclamation.Play(); }
        }
        else
        {
            SystemSounds.Exclamation.Play();
        }

        if (_alertForm is { IsDisposed: false })
        {
            var old = _alertForm;
            _alertForm = null;
            try { old.Close(); } catch { }
        }

        _alertForm = new MentionAlertForm(reason, title, body, at, StopAlertSound);
        _alertForm.Show();   // 不呼叫 Activate：靠 TopMost + ShowWithoutActivation 露臉即可，不搶焦點
    }

    private void StopAlertSound()
    {
        if (_player is null) return;
        try { _player.Stop(); } catch { }
        _player.Dispose();
        _player = null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();
        StopAlertSound();

        if (_alertForm is { IsDisposed: false })
        {
            try { _alertForm.Close(); } catch { }
        }
    }
}
