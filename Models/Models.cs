using System.Text.Json.Serialization;

namespace KeelAssistant.Models;

// ── OAuth 憑證（.credentials.json）────────────────────────────────
public sealed class CredentialsFile
{
    [JsonPropertyName("claudeAiOauth")]
    public OAuthCredentials? ClaudeAiOauth { get; set; }
}

public sealed class OAuthCredentials
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expiresAt")]
    public long ExpiresAt { get; set; }   // epoch milliseconds

    [JsonPropertyName("scopes")]
    public string[]? Scopes { get; set; }

    [JsonPropertyName("subscriptionType")]
    public string? SubscriptionType { get; set; }

    [JsonPropertyName("rateLimitTier")]
    public string? RateLimitTier { get; set; }

    [JsonIgnore]
    public DateTimeOffset ExpiresAtUtc => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt);

    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc.AddMinutes(-1);
}

// ── OAuth Token Refresh 回應 ─────────────────────────────────────
public sealed class TokenRefreshResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }   // seconds
}

// ── OAuth Usage API 回應 ─────────────────────────────────────────
public sealed class OAuthUsageResponse
{
    // 2026/07/02 - Arlen - 官方新增 limits[]/spend 結構，改優先讀 limits[]，舊 window 欄位保留為 fallback - Start
    [JsonPropertyName("five_hour")]
    public UsageWindow? RawFiveHour { get; set; }

    [JsonPropertyName("seven_day")]
    public UsageWindow? RawSevenDay { get; set; }
    // 2026/07/02 - Arlen - 官方新增 limits[]/spend 結構，改優先讀 limits[]，舊 window 欄位保留為 fallback - end

    [JsonPropertyName("seven_day_opus")]
    public UsageWindow? SevenDayOpus { get; set; }

    [JsonPropertyName("seven_day_sonnet")]
    public UsageWindow? SevenDaySonnet { get; set; }

    [JsonPropertyName("extra_usage")]
    public ExtraUsage? ExtraUsage { get; set; }

    // 2026/07/02 - Arlen - 新增 limits[]/spend/member_dashboard_available 對應官方新格式 - Start
    [JsonPropertyName("limits")]
    public List<UsageLimit>? Limits { get; set; }

    [JsonPropertyName("spend")]
    public Spend? Spend { get; set; }

    [JsonPropertyName("member_dashboard_available")]
    public bool MemberDashboardAvailable { get; set; }

    /// <summary>5 小時視窗：優先取 limits[] 的 session，沒有才 fallback 到舊 five_hour 欄位。</summary>
    [JsonIgnore]
    public UsageWindow? FiveHour => FindLimit("session")?.ToWindow() ?? RawFiveHour;

    /// <summary>7 天(全模型)視窗：優先取 limits[] 的 weekly_all，沒有才 fallback 到舊 seven_day 欄位。</summary>
    [JsonIgnore]
    public UsageWindow? SevenDay => FindLimit("weekly_all")?.ToWindow() ?? RawSevenDay;

    private UsageLimit? FindLimit(string kind) =>
        Limits?.FirstOrDefault(l => string.Equals(l.Kind, kind, StringComparison.OrdinalIgnoreCase));
    // 2026/07/02 - Arlen - 新增 limits[]/spend/member_dashboard_available 對應官方新格式 - end
}

public sealed class UsageWindow
{
    [JsonPropertyName("utilization")]
    public double Utilization { get; set; }   // 0-100

    [JsonPropertyName("resets_at")]
    public DateTimeOffset? ResetsAt { get; set; }
}

// 2026/07/02 - Arlen - 官方新格式：limits[] 為每個額度桶的統一表示 - Start
public sealed class UsageLimit
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }        // session / weekly_all / weekly_scoped

    [JsonPropertyName("group")]
    public string? Group { get; set; }       // session / weekly

    [JsonPropertyName("percent")]
    public double Percent { get; set; }      // 0-100

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }    // normal / warning / critical ...

    [JsonPropertyName("resets_at")]
    public DateTimeOffset? ResetsAt { get; set; }

    [JsonPropertyName("scope")]
    public LimitScope? Scope { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    /// <summary>轉為既有 UsageWindow，讓下游沿用不變。</summary>
    public UsageWindow ToWindow() => new() { Utilization = Percent, ResetsAt = ResetsAt };
}

public sealed class LimitScope
{
    [JsonPropertyName("model")]
    public ScopeModel? Model { get; set; }

    [JsonPropertyName("surface")]
    public string? Surface { get; set; }
}

public sealed class ScopeModel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

public sealed class Spend
{
    [JsonPropertyName("used")]
    public MoneyAmount? Used { get; set; }

    [JsonPropertyName("limit")]
    public MoneyAmount? Limit { get; set; }

    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("can_purchase_credits")]
    public bool CanPurchaseCredits { get; set; }

    [JsonPropertyName("can_toggle")]
    public bool CanToggle { get; set; }
}

public sealed class MoneyAmount
{
    [JsonPropertyName("amount_minor")]
    public long AmountMinor { get; set; }    // 以最小貨幣單位表示（分）

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("exponent")]
    public int Exponent { get; set; }        // 小數位數，2 表示 amount_minor/100

    /// <summary>還原為主要貨幣單位金額（decimal，避免浮點誤差）。</summary>
    [JsonIgnore]
    public decimal Amount => AmountMinor / (decimal)Math.Pow(10, Exponent);
}
// 2026/07/02 - Arlen - 官方新格式：limits[] 為每個額度桶的統一表示 - end

public sealed class ExtraUsage
{
    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("monthly_limit")]
    public decimal? MonthlyLimit { get; set; }

    [JsonPropertyName("used_credits")]
    public decimal? UsedCredits { get; set; }

    [JsonPropertyName("utilization")]
    public double? Utilization { get; set; }
}

// ── 設定檔 ───────────────────────────────────────────────────────
public sealed class AppSettings
{
    [JsonPropertyName("ApiPort")]
    public int ApiPort { get; set; } = 38600;

    [JsonPropertyName("AlarmSoundPath")]
    public string AlarmSoundPath { get; set; } = "assets/alarm.wav";

    [JsonPropertyName("PollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("WarnThresholdPercent")]
    public double WarnThresholdPercent { get; set; } = 80;

    [JsonPropertyName("CriticalThresholdPercent")]
    public double CriticalThresholdPercent { get; set; } = 95;

    [JsonPropertyName("OAuthClientId")]
    public string OAuthClientId { get; set; } = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    // 2026/07/13 - Arlen - 新增會議錄音的事後逐字稿設定（Whisper 模型路徑）- Start

    [JsonPropertyName("TranscriptionModelPath")]
    public string TranscriptionModelPath { get; set; } = "models/ggml-medium.bin";

    // 2026/07/13 - Arlen - 新增會議錄音的事後逐字稿設定 - end

    // 2026/07/23 - Arlen - 新增 Python 沙盒設定：路徑參數化，避免日後搬移要改程式碼 - Start

    /// <summary>
    /// 沙盒可存取的唯一根目錄（絕對路徑）。刻意與專案目錄平行而非置於其下——
    /// 路徑正規化一旦有疏漏，逃逸的第一站不該是放著 dbconnections.json 的地方。
    /// 此值只能由本檔設定，MCP 參數不可覆寫。
    /// </summary>
    [JsonPropertyName("SandboxRoot")]
    public string SandboxRoot { get; set; } = @"C:\Vista\_sandbox";

    [JsonPropertyName("SandboxTimeoutSeconds")]
    public int SandboxTimeoutSeconds { get; set; } = 30;

    /// <summary>WebView2 行程群組相對於基準值的記憶體上限，超過即硬殺 Worker。</summary>
    [JsonPropertyName("SandboxMemoryLimitMb")]
    public int SandboxMemoryLimitMb { get; set; } = 1024;

    [JsonPropertyName("SandboxMaxFileSizeMb")]
    public int SandboxMaxFileSizeMb { get; set; } = 50;

    /// <summary>閒置多久後銷毀 WebView2（0 = 每次用完立即銷毀）。</summary>
    [JsonPropertyName("SandboxIdleShutdownMinutes")]
    public int SandboxIdleShutdownMinutes { get; set; } = 5;

    /// <summary>
    /// 沙盒一律不得讀取的檔案樣式（比對完整路徑，支援 * ?，不分大小寫）。
    ///
    /// 讀檔開放絕對路徑後，確認視窗成為主要防線，但它依賴「每次都認真看」——
    /// 彈窗一多就會變成無腦按允許。這份黑名單是不受彈窗影響的硬性阻擋：
    /// 命中者連視窗都不會跳，直接拒絕，確保憑證類檔案不會因為一時手滑而外流。
    /// </summary>
    [JsonPropertyName("SandboxDenyPatterns")]
    public string[] SandboxDenyPatterns { get; set; } =
    {
        @"*\dbconnections.json",
        @"*\.credentials.json",
        @"*\credentials.json",
        @"*\secrets.json",
        @"*\appsettings*.json",
        @"*\web.config",
        @"*\.env",
        @"*\.env.*",
        @"*\.ssh\*",
        @"*\.aws\*",
        @"*\id_rsa*",
        "*.pfx",
        "*.p12",
        "*.key",
        "*.pem"
    };

    // 2026/07/23 - Arlen - 新增 Python 沙盒設定 - end

    // 2026/07/28 - Arlen - 新增 Windows 通知監看設定：LINE 訊息一閃而過常漏看，
    // 改由本程式輪詢通知中心，命中關鍵人／被 @ 時跳置頂視窗 - Start

    /// <summary>是否啟用通知監看。關閉後完全不呼叫 UserNotificationListener（也不會請求授權）。</summary>
    [JsonPropertyName("NotificationWatchEnabled")]
    public bool NotificationWatchEnabled { get; set; } = true;

    /// <summary>掃描間隔秒數。下限 1 秒；通知中心的讀取成本很低，3 秒不會造成負擔。</summary>
    [JsonPropertyName("NotificationScanSeconds")]
    public int NotificationScanSeconds { get; set; } = 3;

    /// <summary>
    /// 只監看這些應用程式的通知（比對 AppUserModelId 與顯示名稱，含子字串即可）。
    /// 留空陣列＝不過濾（所有 App 都看），但會大幅提高誤報，正常情況不要留空。
    /// </summary>
    [JsonPropertyName("NotificationApps")]
    public string[] NotificationApps { get; set; } = { "LINE" };

    /// <summary>
    /// 關鍵寄件者（強訊號）。比對通知標題與內文——1:1 聊天的標題是對方名稱，
    /// 群組聊天的標題是群組名、發話者落在內文，因此兩處都要比才不會漏掉群組發言。
    /// </summary>
    [JsonPropertyName("NotificationSenders")]
    public string[] NotificationSenders { get; set; } = { "ASVT Brian", "Brian" };

    /// <summary>
    /// 被明確點名的樣式（強訊號）。填 LINE 上的**顯示名稱**而非英文本名，
    /// 因為群組 @ 你時通知內文顯示的是 @{顯示名稱}。
    /// </summary>
    [JsonPropertyName("NotificationMentions")]
    public string[] NotificationMentions { get; set; } = { "@蔡東祐(Arlen)", "@Arlen" };

    /// <summary>
    /// 內文提到你的名字（弱訊號，只跳氣泡不搶畫面）。閒聊提到也會命中，
    /// 因此刻意與強訊號分開——誤報高的規則不該有打斷你的權力。
    /// </summary>
    [JsonPropertyName("NotificationKeywords")]
    public string[] NotificationKeywords { get; set; } = { "蔡東祐", "Arlen" };

    /// <summary>同一聊天室的重複提醒間隔秒數。老闆連發五則訊息不該跳五個視窗。</summary>
    [JsonPropertyName("NotificationCooldownSeconds")]
    public int NotificationCooldownSeconds { get; set; } = 120;

    // 2026/07/28 - Arlen - 新增 Windows 通知監看設定 - end

    // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組：讓 Claude 能對遠端主機下指令，
    // 全程於可視終端呈現，且每一條都需人工確認 - Start

    /// <summary>
    /// 是否啟用 SSH 模組。關閉後 ssh_* 工具一律拒絕，連線設定選單也不顯示。
    ///
    /// 預設 false：本模組是三個高風險模組中唯一「沒有隔離層」的——SQL 有唯讀帳號與唯讀交易、
    /// Python 有 WASM 邊界，而 SSH 送過去就是遠端主機上的真實指令。要用請自行打開。
    /// </summary>
    [JsonPropertyName("SshEnabled")]
    public bool SshEnabled { get; set; } = false;

    /// <summary>單條指令的預設逾時秒數；MCP 參數可在 1~1800 內覆寫。</summary>
    [JsonPropertyName("SshDefaultTimeoutSeconds")]
    public int SshDefaultTimeoutSeconds { get; set; } = 120;

    /// <summary>閒置多久後自動斷線（0 = 不自動斷）。斷線會一併清掉持續 shell 的 cwd 與環境變數。</summary>
    [JsonPropertyName("SshIdleShutdownMinutes")]
    public int SshIdleShutdownMinutes { get; set; } = 30;

    /// <summary>單次回傳給 Claude 的輸出上限字元數，超過即截斷並標記 truncated。</summary>
    [JsonPropertyName("SshMaxOutputChars")]
    public int SshMaxOutputChars { get; set; } = 8000;

    /// <summary>終端視窗欄數。此值同時決定 PTY 的寬度，改變後需重新連線才生效。</summary>
    [JsonPropertyName("SshTerminalCols")]
    public int SshTerminalCols { get; set; } = 120;

    /// <summary>終端視窗列數（PTY 高度）。</summary>
    [JsonPropertyName("SshTerminalRows")]
    public int SshTerminalRows { get; set; } = 32;

    [JsonPropertyName("SshTerminalFontSize")]
    public int SshTerminalFontSize { get; set; } = 14;

    // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
}

// ── 本地 JSONL token/成本統計結果 ────────────────────────────────
public sealed class TokenStats
{
    public long InputTokens;
    public long OutputTokens;
    public long CacheCreationTokens;
    public long CacheReadTokens;
    public decimal CostUsd;

    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    public void Add(TokenStats other)
    {
        InputTokens += other.InputTokens;
        OutputTokens += other.OutputTokens;
        CacheCreationTokens += other.CacheCreationTokens;
        CacheReadTokens += other.CacheReadTokens;
        CostUsd += other.CostUsd;
    }
}

// ── 鬧鐘 / 計時任務（tasks.json 持久化）──────────────────────────
public enum ScheduledAction
{
    Alarm,        // 響鈴（循環音檔）
    MusicStop     // 停止音樂
}

public sealed class ScheduledTask
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("fireAt")]
    public DateTime FireAt { get; set; }   // 本地時間

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("action")]
    public ScheduledAction Action { get; set; } = ScheduledAction.Alarm;

    [JsonPropertyName("loop")]
    public bool Loop { get; set; } = true;   // 鬧鈴是否循環

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

// 2026/07/22 - Arlen - 新增 SQL 執行模組：連線設定（dbconnections.json 持久化，密碼以 DPAPI 加密）- Start

// ── SQL 連線設定（dbconnections.json 持久化）─────────────────────
public enum DbProvider
{
    Mssql,        // Microsoft SQL Server
    Postgresql    // PostgreSQL
}

/// <summary>
/// 一組具名的資料庫連線。以 <see cref="Id"/>（SQLConnID）對外識別，
/// Claude 只拿得到 Id，帳密不會出現在 MCP 回傳中。
/// 密碼欄位存 DPAPI 密文（<see cref="PasswordEnc"/>），明文不落地。
/// </summary>
public sealed class DbConnectionEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("provider")]
    public DbProvider Provider { get; set; } = DbProvider.Mssql;

    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("port")]
    public int? Port { get; set; }            // 省略則用該 provider 預設埠

    [JsonPropertyName("database")]
    public string Database { get; set; } = "";

    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    /// <summary>DPAPI（CurrentUser）加密後的 Base64 密文。此欄位絕不對外回傳。</summary>
    [JsonPropertyName("passwordEnc")]
    public string PasswordEnc { get; set; } = "";

    /// <summary>唯讀連線：拒絕一切非 SELECT 語句（第二道防線，主防線是 DB 端唯讀帳號）。</summary>
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; } = true;

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>SQL 查詢結果。columns/rows 分離，避免每列重複欄位名造成回傳膨脹。</summary>
public sealed class SqlQueryResult
{
    public string[] Columns { get; set; } = Array.Empty<string>();
    public List<object?[]> Rows { get; set; } = new();
    public int RowCount => Rows.Count;

    /// <summary>是否因 maxRows 上限而截斷（true 表示 DB 端還有更多資料）。</summary>
    public bool Truncated { get; set; }

    public long ElapsedMs { get; set; }
}

// 2026/07/22 - Arlen - 新增 SQL 執行模組 - end

// 2026/07/23 - Arlen - 新增 Python 沙盒模組：WASM 執行結果 - Start

// ── Python 沙盒執行結果 ─────────────────────────────────────────

/// <summary>
/// 沙盒單次執行的結果。對應 worker.js 回傳的 JSON，欄位名刻意用 camelCase
/// 與 JS 端一致，避免中間再做一層名稱對應。
/// </summary>
public sealed class SandboxRunResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("stdout")]
    public string Stdout { get; set; } = "";

    [JsonPropertyName("stderr")]
    public string Stderr { get; set; } = "";

    /// <summary>最後一行運算式的 repr（等同 REPL 回顯），沒有則為 null。</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>Python 例外訊息（含 traceback），成功時為 null。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>stdout/stderr 是否因長度上限而截斷。</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("files")]
    public List<SandboxOutputFile> Files { get; set; } = new();

    [JsonPropertyName("filesTruncated")]
    public bool FilesTruncated { get; set; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }
}

/// <summary>沙盒虛擬 FS 的 /output 內容，執行結束後由 .NET 端寫入實體 output 目錄。</summary>
public sealed class SandboxOutputFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("b64")]
    public string B64 { get; set; } = "";
}

// 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end

// 2026/09/01 - Arlen - 新增 SSH 遠端執行模組：連線設定（sshconnections.json 持久化，
// 密碼/passphrase 以 DPAPI 加密），比照 DbConnectionEntry 的代號制 - Start

// ── SSH 連線設定（sshconnections.json 持久化）───────────────────

public enum SshAuthMode
{
    PrivateKey,   // 私鑰檔（建議）
    Password      // 密碼
}

/// <summary>
/// 一組具名的 SSH 連線。以 <see cref="Id"/>（sshId）對外識別，
/// Claude 只拿得到 Id，主機位址與帳密不會出現在 MCP 回傳中。
///
/// <see cref="HostKeyFingerprint"/> 採 TOFU（Trust On First Use）：首次連線記下指紋，
/// 之後每次比對。指紋變了代表對端換了主機或遭中間人攔截，一律拒絕連線而非詢問——
/// 這種提示出現時人幾乎都會直接按確定，問了等於沒問。
/// </summary>
public sealed class SshConnectionEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("authMode")]
    public SshAuthMode AuthMode { get; set; } = SshAuthMode.PrivateKey;

    /// <summary>私鑰檔絕對路徑（AuthMode = PrivateKey 時使用）。</summary>
    [JsonPropertyName("keyPath")]
    public string KeyPath { get; set; } = "";

    /// <summary>DPAPI 密文：密碼（Password 模式）或私鑰 passphrase（PrivateKey 模式）。絕不對外回傳。</summary>
    [JsonPropertyName("secretEnc")]
    public string SecretEnc { get; set; } = "";

    /// <summary>已信任的主機金鑰指紋（SHA-256, Base64）。空值表示尚未連過，首次連線時填入。</summary>
    [JsonPropertyName("hostKeyFingerprint")]
    public string HostKeyFingerprint { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>供 UI 與確認視窗顯示的簡短標示，不含任何機密。</summary>
    [JsonIgnore]
    public string Display => $"{User}@{Host}:{Port}";
}

/// <summary>單條遠端指令的執行結果。</summary>
public sealed class SshExecResult
{
    public string Output { get; set; } = "";

    /// <summary>遠端 shell 的離開碼；逾時或無法解析時為 null。</summary>
    public int? ExitCode { get; set; }

    /// <summary>指令結束後的工作目錄。持續 shell 的狀態是隱含的，故每次都揭露。</summary>
    public string Cwd { get; set; } = "";

    /// <summary>是否因 SshMaxOutputChars 上限而截斷。</summary>
    public bool Truncated { get; set; }

    /// <summary>是否逾時（已送出 Ctrl+C，遠端可能仍在收尾）。</summary>
    public bool TimedOut { get; set; }

    public long ElapsedMs { get; set; }
}

// 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end

// ── 生活習慣記憶（memory.json 持久化）───────────────────────────
public sealed class MemoryEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = Array.Empty<string>();

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
