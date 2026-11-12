using System.Text;
using KeelAssistant.Alarm;
using KeelAssistant.Api;
using KeelAssistant.Calendar;
using KeelAssistant.Db;
using KeelAssistant.Models;
// 2026/07/28 - Arlen - 新增通知監看模組 - Start
using KeelAssistant.Notifications;
// 2026/07/28 - Arlen - 新增通知監看模組 - end
using KeelAssistant.Recording;
using KeelAssistant.Sandbox;
// 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - Start
using KeelAssistant.Ssh;
// 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
using KeelAssistant.Time;
using KeelAssistant.Usage;

namespace KeelAssistant;

/// <summary>
/// Keel Assistant 系統匣常駐主控：整合「鬧鐘/計時」「Claude 用量」「時間」等模組，
/// 並啟動本機 HTTP API 供 Claude 下指令。單一托盤圖示、選單分區呈現。
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;

    // ── 用量模組（移植自 ClaudeUsageMonitor）──
    private readonly System.Windows.Forms.Timer _usageTimer;
    private readonly AppSettings _settings;
    private readonly CredentialStore _store;
    private readonly TranscriptScanner _scanner;
    private OAuthUsageClient? _usageClient;
    private OAuthUsageResponse? _lastUsage;
    // 2026/08/18 - Arlen - 新增 get_usage_5h 工具：記下快照取得時間，供 MCP 回報陳舊程度 - Start
    private DateTime? _lastUsageAt;
    // 2026/08/18 - Arlen - 新增 get_usage_5h 工具 - end
    private string _statusLine = "尚未取得資料…";
    private readonly Dictionary<string, double> _notified = new();
    private bool _busy;

    // 2026/07/09 - Arlen - 查用量前置條件：無 Claude Code 執行緒時退避 10 分鐘再試 - Start
    private readonly int _normalIntervalMs;
    private const int NoClaudeBackoffMs = 10 * 60 * 1000;
    // 2026/07/09 - Arlen - end

    // ── 新增模組 ──
    private readonly AlarmEngine _alarm;
    // 2026/11/10 - Arlen - 移除音樂與生活記憶模組 - Start
    private readonly CalendarService _calendar;
    // 2026/11/10 - Arlen - 移除音樂與生活記憶模組 - end
    private readonly McpHost _api;

    // 2026/07/22 - Arlen - 新增 SQL 執行模組：連線帳密由本程式以 DPAPI 控管，不外流給 Claude - Start
    private readonly DbConnectionStore _dbConnections;
    private readonly SqlExecutor _sqlExecutor;
    private DbConnectionForm? _dbForm;
    // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end

    // 2026/07/23 - Arlen - 新增 Python 沙盒模組：LLM 產生的 Python 一律隔離在 WASM 內執行 - Start
    private readonly SandboxPaths _sandboxPaths;
    private readonly PyodideRunner _sandbox;
    // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end

    // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組：主機位址與金鑰由本程式以 DPAPI 控管，
    // Claude 只拿得到 sshId；每條指令都需人工確認，全程在可視終端呈現 - Start
    private readonly SshConnectionStore _sshConnections;
    private readonly SshSessionManager _sshSessions;
    private SshConnectionForm? _sshForm;
    // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end

    // 2026/07/28 - Arlen - 新增通知監看模組：LINE 訊息一閃而過常漏看，改由本程式輪詢通知中心 - Start
    private readonly NotificationWatcher _notifications;
    // 2026/07/28 - Arlen - 新增通知監看模組 - end

    // 2026/07/13 - Arlen - 新增會議錄音模組：錄音中托盤圖示換成紅點，讓錄音無法在無人察覺下進行 - Start
    private readonly RecordingController _recording;
    private readonly TranscriptionService _transcription;
    private readonly ModelDownloader _models;
    private readonly Icon _normalIcon;
    private readonly Icon _recordingIcon;
    private readonly SynchronizationContext _uiContext;
    private RecordingState _lastRecordingState = RecordingState.Idle;

    // 托盤 Tooltip 的兩個來源：轉檔進度優先（有在跑時），否則顯示用量
    private string _usageText = "Keel Assistant";
    private string? _transcribeText;
    // 2026/07/13 - Arlen - 新增會議錄音模組 - end

    public TrayApplicationContext()
    {
        var baseDir = AppContext.BaseDirectory;
        _settings = ConfigLoader.LoadSettings(Path.Combine(baseDir, "appsettings.json"));
        var pricing = PricingTable.Load(Path.Combine(baseDir, "pricing.json"));
        _store = new CredentialStore();
        _scanner = new TranscriptScanner(pricing);

        // 2026/11/10 - Arlen - 移除音樂與生活記憶模組 - Start
        _alarm = new AlarmEngine(_settings, baseDir);
        _calendar = new CalendarService();
        // 2026/11/10 - Arlen - 移除音樂與生活記憶模組 - end

        // 2026/07/22 - Arlen - 新增 SQL 執行模組：連線設定與執行器（設定檔 dbconnections.json 隨執行檔目錄）- Start
        _dbConnections = new DbConnectionStore(baseDir);
        _sqlExecutor = new SqlExecutor(_dbConnections);
        // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end

        // 2026/07/13 - Arlen - 新增會議錄音模組：錄音 + 事後 Whisper 逐字稿（模型未安裝時僅停用轉檔，不影響錄音）- Start
        var whisperModel = Path.Combine(baseDir, _settings.TranscriptionModelPath);
        _transcription = new TranscriptionService(whisperModel);
        _transcription.JobStarted += OnTranscriptionStarted;
        _transcription.JobProgress += OnTranscriptionProgress;
        _transcription.JobFinished += OnTranscriptionFinished;

        // 模型不隨程式發佈（約 1.5GB），按需下載，不在啟動時自動抓
        _models = new ModelDownloader(whisperModel);

        _recording = new RecordingController();
        _recording.Changed += OnRecordingChanged;
        // 2026/07/13 - Arlen - 新增會議錄音模組 - end

        var iconPath = Path.Combine(baseDir, "assets", "app.ico");
        // 2026/07/13 - Arlen - 新增會議錄音模組：保留常態圖示，另備紅色錄音圖示 - Start
        _normalIcon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
        _recordingIcon = CreateRecordingIcon();
        _icon = new NotifyIcon
        {
            Icon = _normalIcon,
            Visible = true,
            Text = "Keel Assistant"
        };
        // 2026/07/13 - Arlen - 新增會議錄音模組 - end
        _icon.ContextMenuStrip = BuildMenu();
        _icon.DoubleClick += (_, _) => RefreshNowAsync();

        // 本機 MCP Server（UI SynchronizationContext 供跨執行緒操作視窗）
        // ⚠ 必須在此處（BuildMenu 建立 ContextMenuStrip 之後）才取得：WinForms 的
        // SynchronizationContext 是在第一個 Control 被建構時才安裝的，放到建構子開頭會拿到 null。
        _uiContext = SynchronizationContext.Current
                     ?? throw new InvalidOperationException("必須在 UI 執行緒建立 TrayApplicationContext。");

        // 2026/07/23 - Arlen - 新增 Python 沙盒模組：WebView2 用時才建（見 PyodideRunner），此處不會啟動瀏覽器 - Start
        _sandboxPaths = new SandboxPaths(_settings);
        _sandbox = new PyodideRunner(_sandboxPaths, _settings, _uiContext);
        // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end
        // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組：連線用時才建（見 SshSessionManager），
        // 此處不會連任何主機 - Start
        _sshConnections = new SshConnectionStore(baseDir);
        _sshSessions = new SshSessionManager(_sshConnections, _settings, _uiContext);
        // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
        // 2026/07/13 - Arlen - 新增會議錄音模組：注入 McpHost 供 open_recorder / transcribe_recording 等工具使用 - Start
        // 2026/07/22 - Arlen - 新增 SQL 執行模組：注入 McpHost 供 sql_query / sql_exec 使用 - Start
        // 2026/07/23 - Arlen - 新增 Python 沙盒模組：注入 McpHost 供 python_run 等工具使用 - Start
        _api = new McpHost(_settings.ApiPort, _alarm, _calendar,
                           _recording, _transcription, _models,
                           _dbConnections, _sqlExecutor,
                           // 2026/08/18 - Arlen - 新增 get_usage_5h 工具：一併傳入快照時間 - Start
                           _sandbox, _sandboxPaths,
                           // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組：注入 McpHost 供 ssh_exec 等工具使用 - Start
                           _sshConnections, _sshSessions,
                           // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
                           () => _lastUsage, () => _lastUsageAt, _uiContext);
                           // 2026/08/18 - Arlen - 新增 get_usage_5h 工具 - end
        // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end
        // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end
        // 2026/07/13 - Arlen - 新增會議錄音模組 - end
        try
        {
            _api.Start();
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(8000, "Keel MCP 啟動失敗",
                $"埠 {_settings.ApiPort} 無法綁定：{ex.Message}", ToolTipIcon.Error);
        }

        // 2026/07/28 - Arlen - 新增通知監看模組：弱訊號走托盤氣泡（不搶畫面），故把 _icon 的氣泡能力傳進去。
        // Start() 內含系統授權請求，必須在 UI 執行緒呼叫；失敗只停用本功能，不影響其他模組 - Start
        _notifications = new NotificationWatcher(_settings, baseDir,
            (title, text) => _icon.ShowBalloonTip(8000, title, text, ToolTipIcon.Info));
        _notifications.Start();
        // 2026/07/28 - Arlen - 新增通知監看模組 - end

        int interval = Math.Max(180, _settings.PollIntervalSeconds);
        // 2026/07/09 - Arlen - 記下正常輪詢間隔，退避後可還原 - Start
        _normalIntervalMs = interval * 1000;
        _usageTimer = new System.Windows.Forms.Timer { Interval = _normalIntervalMs };
        // 2026/07/09 - Arlen - end
        _usageTimer.Tick += (_, _) => RefreshNowAsync();
        _usageTimer.Start();

        RefreshNowAsync();
    }

    // ── 托盤選單 ──────────────────────────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMenuItems(menu);
        return menu;
    }

    private void RebuildMenuItems(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        // ── 現在時間 ──
        var t = (dynamic)TimeService.Now();
        menu.Items.Add(new ToolStripMenuItem($"🕒 {t.full_datetime}（{t.weekday}・{t.period}）") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        // ── 鬧鐘 / 計時 ──
        var tasks = _alarm.List();
        var alarmRoot = new ToolStripMenuItem($"⏰ 鬧鐘 / 計時（{tasks.Count}）");
        if (tasks.Count == 0)
        {
            alarmRoot.DropDownItems.Add(new ToolStripMenuItem("（無待命任務）") { Enabled = false });
        }
        else
        {
            foreach (var task in tasks)
            {
                var remain = task.FireAt - DateTime.Now;
                var label = $"{task.FireAt:MM/dd HH:mm}  {task.Label}  (剩 {FormatRemain(remain)})";
                var item = new ToolStripMenuItem(label);
                var id = task.Id;
                item.DropDownItems.Add(new ToolStripMenuItem("取消此任務", null, (_, _) => _alarm.Cancel(id)));
                alarmRoot.DropDownItems.Add(item);
            }
        }
        alarmRoot.DropDownItems.Add(new ToolStripSeparator());
        alarmRoot.DropDownItems.Add("📅 同步今日會議到鬧鐘", null, (_, _) => SyncTodayMeetings());
        alarmRoot.DropDownItems.Add("停止響鈴", null, (_, _) => _alarm.StopRinging());
        menu.Items.Add(alarmRoot);

        // 2026/11/10 - Arlen - 移除音樂模組：拿掉托盤「音樂」選單 - Start
        // 2026/11/10 - Arlen - 移除音樂模組 - end

        // 2026/07/13 - Arlen - 新增會議錄音模組：托盤可直接叫出錄音視窗（不必透過 Claude）- Start
        var recState = _recording.State switch
        {
            RecordingState.Recording => $"錄音中 {_recording.Elapsed:hh\\:mm\\:ss}",
            RecordingState.Paused => $"已暫停 {_recording.Elapsed:hh\\:mm\\:ss}",
            _ => "未錄音"
        };
        var recRoot = new ToolStripMenuItem($"🎙️ 會議錄音（{recState}）");
        recRoot.DropDownItems.Add("開啟錄音視窗", null, (_, _) => OpenRecorderFromTray());

        var running = _transcription.List()
            .Where(j => j.Status is TranscriptionStatus.Queued or TranscriptionStatus.Running)
            .ToList();
        if (running.Count > 0)
        {
            recRoot.DropDownItems.Add(new ToolStripSeparator());
            foreach (var j in running)
                recRoot.DropDownItems.Add(new ToolStripMenuItem(
                    $"轉逐字稿中：{Path.GetFileName(j.AudioPath)}（{j.Percent:N0}%）") { Enabled = false });
        }
        menu.Items.Add(recRoot);
        // 2026/07/13 - Arlen - 新增會議錄音模組 - end

        // 2026/07/22 - Arlen - 新增 SQL 執行模組：連線設定入口（密碼加密後才寫檔，不能手動編 json）- Start
        var conns = _dbConnections.List();
        var sqlRoot = new ToolStripMenuItem($"🗄️ SQL 連線（{conns.Count}）");
        foreach (var c in conns)
            sqlRoot.DropDownItems.Add(new ToolStripMenuItem(
                $"{c.Id}　{c.Provider}／{c.Database}{(c.ReadOnly ? "（唯讀）" : "")}") { Enabled = false });
        if (conns.Count > 0) sqlRoot.DropDownItems.Add(new ToolStripSeparator());
        sqlRoot.DropDownItems.Add("連線設定…", null, (_, _) => OpenDbSettings());
        menu.Items.Add(sqlRoot);
        // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end

        // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組：連線中的主機必須看得見。
        // 這個模組沒有隔離層，「現在有幾條連線開著」不該只有 Claude 知道 - Start
        var sshConns = _sshConnections.List();
        var sshLive = _sshSessions.ConnectedIds();
        var sshRoot = new ToolStripMenuItem(
            $"🖥️ SSH（{(_sshSessions.Enabled ? $"{sshLive.Count} 連線中／{sshConns.Count} 已設定" : "未啟用")}）");

        if (!_sshSessions.Enabled)
        {
            sshRoot.DropDownItems.Add(new ToolStripMenuItem(
                "於 appsettings.json 設 SshEnabled = true 後重啟") { Enabled = false });
        }
        else
        {
            foreach (var c in sshConns)
            {
                var live = sshLive.Contains(c.Id, StringComparer.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem($"{(live ? "🟢" : "⚪")} {c.Id}　{c.Display}");
                if (live)
                {
                    var id = c.Id;
                    item.DropDownItems.Add("顯示終端視窗", null, (_, _) => ShowSshTerminal(id));
                    item.DropDownItems.Add("中斷連線", null, (_, _) => _sshSessions.Close(id));
                }
                else
                {
                    item.Enabled = false;
                }
                sshRoot.DropDownItems.Add(item);
            }
            if (sshConns.Count > 0) sshRoot.DropDownItems.Add(new ToolStripSeparator());
        }

        sshRoot.DropDownItems.Add("SSH 連線設定…", null, (_, _) => OpenSshSettings());
        menu.Items.Add(sshRoot);
        // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end

        // 2026/07/28 - Arlen - 新增通知監看模組：狀態要看得見，否則授權被關掉時會靜默失效而不自知 - Start
        var noteRoot = new ToolStripMenuItem(
            $"💬 LINE 提醒（{(_notifications.Paused ? "已暫停" : _notifications.Status)}）");
        noteRoot.DropDownItems.Add(new ToolStripMenuItem(
            _notifications.LastHit is { } hit ? $"最近命中：{hit}" : "（尚無命中記錄）") { Enabled = false });

        // 2026/07/28 - Arlen - 診斷：讀不到通知 vs 讀得到但沒命中，兩者症狀相同（都是無命中），
        // 攤開本輪通知數與來源 App 才分得出來 - Start
        noteRoot.DropDownItems.Add(new ToolStripSeparator());
        noteRoot.DropDownItems.Add(new ToolStripMenuItem(
            $"本輪讀到通知：{_notifications.LastScanCount} 則") { Enabled = false });

        var apps = _notifications.RecentApps;
        if (apps.Count == 0)
        {
            noteRoot.DropDownItems.Add(new ToolStripMenuItem("（通知中心目前是空的）") { Enabled = false });
        }
        else
        {
            var appsRoot = new ToolStripMenuItem($"來源 App（{apps.Count}）");
            foreach (var a in apps)
                appsRoot.DropDownItems.Add(new ToolStripMenuItem(a) { Enabled = false });
            noteRoot.DropDownItems.Add(appsRoot);
        }
        // 2026/07/28 - Arlen - 診斷 - end

        noteRoot.DropDownItems.Add(new ToolStripSeparator());
        noteRoot.DropDownItems.Add("測試提醒視窗", null, (_, _) => _notifications.TestAlert());
        noteRoot.DropDownItems.Add(_notifications.Paused ? "恢復提醒" : "暫停提醒", null,
            (_, _) => _notifications.Paused = !_notifications.Paused);
        menu.Items.Add(noteRoot);
        // 2026/07/28 - Arlen - 新增通知監看模組 - end

        menu.Items.Add(new ToolStripSeparator());

        // ── Claude 用量 ──
        if (_lastUsage is { } u)
        {
            AddWindowItem(menu, "5 小時視窗", u.FiveHour);
            AddWindowItem(menu, "7 天 (全模型)", u.SevenDay);
            AddWindowItem(menu, "7 天 Opus", u.SevenDayOpus);
            AddWindowItem(menu, "7 天 Sonnet", u.SevenDaySonnet);
            if (u.ExtraUsage is { IsEnabled: true } ex)
                menu.Items.Add(new ToolStripMenuItem(
                    $"額外用量：{ex.UsedCredits:N2} / {ex.MonthlyLimit:N2} 點") { Enabled = false });
            // 2026/07/02 - Arlen - 官方新格式新增 spend，顯示信用額度使用情形 - Start
            AddSpendItem(menu, u.Spend);
            // 2026/07/02 - Arlen - 官方新格式新增 spend，顯示信用額度使用情形 - end
        }
        else
        {
            menu.Items.Add(new ToolStripMenuItem(_statusLine) { Enabled = false });
        }

        var today = _scanner.TodayTotal();
        menu.Items.Add(new ToolStripMenuItem(
            $"今日 token：{today.TotalTokens:N0}（in {today.InputTokens:N0} / out {today.OutputTokens:N0}）")
            { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"今日估算成本：US$ {today.CostUsd:N2}") { Enabled = false });

        var byModel = _scanner.TodayByModel();
        if (byModel.Count > 0)
        {
            var sub = new ToolStripMenuItem("今日各模型明細");
            foreach (var (model, s) in byModel)
                sub.DropDownItems.Add(new ToolStripMenuItem(
                    $"{model}：{s.TotalTokens:N0} tok / US$ {s.CostUsd:N2}") { Enabled = false });
            menu.Items.Add(sub);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add($"MCP：127.0.0.1:{_settings.ApiPort}/mcp").Enabled = false;
        menu.Items.Add("立即重新整理用量", null, (_, _) => RefreshNowAsync());
        menu.Items.Add("結束", null, (_, _) => ExitThread());
    }

    /// <summary>
    /// 托盤手動觸發：把今天的 Outlook 會議同步成鬧鐘（開始前 10 分鐘提醒），
    /// 避免 Outlook 關掉就忘了開會。已過時間、整日事件、重複者自動略過。
    /// </summary>
    private void SyncTodayMeetings()
    {
        try
        {
            const string LabelPrefix = "[會議] ";
            const double minutesBefore = 10;

            var events = _calendar.ListDay(DateTime.Today);
            var existing = _alarm.List()
                .Where(t => t.Label.StartsWith(LabelPrefix, StringComparison.Ordinal))
                .Select(t => t.Label)
                .ToHashSet(StringComparer.Ordinal);

            int added = 0;
            var now = DateTime.Now;
            foreach (var e in events)
            {
                if (e.IsAllDay) continue;
                var fireAt = e.Start.AddMinutes(-minutesBefore);
                if (fireAt <= now) continue;

                var label = $"{LabelPrefix}{e.Subject}（{e.Start:HH:mm}）";
                if (!existing.Add(label)) continue;

                _alarm.AddAlarm(fireAt, label, loop: false);
                added++;
            }

            _icon.ShowBalloonTip(6000, "同步今日會議",
                added > 0 ? $"已新增 {added} 個會議提醒（開始前 {minutesBefore:N0} 分鐘）。"
                          : "沒有需要新增的會議提醒（皆已過時間或已同步）。",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(8000, "同步今日會議失敗", ex.Message, ToolTipIcon.Error);
        }
    }

    // 2026/07/22 - Arlen - 新增 SQL 執行模組 - Start

    /// <summary>
    /// 開啟 SQL 連線設定視窗。以非強制回應（Show）開啟並保留實例，
    /// 避免設定視窗卡住托盤選單與 MCP 的確認視窗；重複點選時把既有視窗帶到前景。
    /// </summary>
    private void OpenDbSettings()
    {
        if (_dbForm is { IsDisposed: false })
        {
            _dbForm.WindowState = FormWindowState.Normal;
            _dbForm.Activate();
            return;
        }

        _dbForm = new DbConnectionForm(_dbConnections, _sqlExecutor);
        _dbForm.FormClosed += (_, _) => _dbForm = null;
        _dbForm.Show();
    }

    // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end

    // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - Start

    /// <summary>
    /// 開啟 SSH 連線設定視窗。比照 SQL 設定視窗以 Show 開啟並保留實例——
    /// 用 ShowDialog 會卡住托盤選單與 MCP 的指令確認視窗。
    /// </summary>
    private void OpenSshSettings()
    {
        if (_sshForm is { IsDisposed: false })
        {
            _sshForm.WindowState = FormWindowState.Normal;
            _sshForm.Activate();
            return;
        }

        _sshForm = new SshConnectionForm(_sshConnections);
        _sshForm.FormClosed += (_, _) => _sshForm = null;
        _sshForm.Show();
    }

    /// <summary>
    /// 把指定 sshId 的終端視窗帶到前景。刻意用只作用於「已連線」的同步版本——
    /// 建立連線會等待網路往返，在 UI 執行緒上同步等待會讓整個托盤程式看起來當掉。
    /// 本選單項只對連線中的主機顯示，故不需要建立連線的路徑。
    /// </summary>
    private void ShowSshTerminal(string sshId)
    {
        if (_sshSessions.ShowTerminalIfConnected(sshId)) return;
        _icon.ShowBalloonTip(5000, "SSH", $"「{sshId}」目前沒有連線。", ToolTipIcon.Info);
    }

    // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end

    // 2026/07/13 - Arlen - 新增會議錄音模組 - Start

    /// <summary>
    /// 托盤手動開啟錄音視窗。托盤沒有「當下專案」的脈絡（那是 Claude 才知道的），
    /// 因此沿用上次的輸出資料夾；從未指定過則退回 exe 目錄，至少能錄得起來。
    /// </summary>
    private void OpenRecorderFromTray()
    {
        try
        {
            // OutputDirectory 是 {專案}\_Project\Recordings，往上兩層還原成專案根目錄
            var projectRoot = _recording.OutputDirectory is { } dir
                ? Directory.GetParent(dir)?.Parent?.FullName ?? AppContext.BaseDirectory
                : AppContext.BaseDirectory;

            _recording.OpenRecorder(projectRoot);
        }
        catch (Exception ex)
        {
            _icon.ShowBalloonTip(8000, "開啟錄音視窗失敗", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// 錄音狀態變動時切換托盤圖示（錄音/暫停顯示紅點）。Changed 每 100ms 就會觸發一次，
    /// 因此只在「狀態真的改變」時才動圖示，避免每秒重設 NotifyIcon.Icon 十次造成閃爍與浪費。
    /// </summary>
    private void OnRecordingChanged()
    {
        var state = _recording.State;
        if (state == _lastRecordingState) return;
        _lastRecordingState = state;

        _icon.Icon = state == RecordingState.Idle ? _normalIcon : _recordingIcon;
    }

    /// <summary>
    /// 逐字稿轉檔結束時跳氣泡通知。轉檔動輒數十分鐘，Arlen 早就去忙別的事了，
    /// 沒有這個通知就得自己回來輪詢。由背景執行緒觸發，故需切回 UI 執行緒碰 NotifyIcon。
    /// </summary>
    private void OnTranscriptionFinished(TranscriptionJob job)
    {
        _uiContext.Post(_ =>
        {
            // 讓 Tooltip 回到顯示用量（佇列還有下一件的話，它的 JobStarted 會再蓋回來）
            _transcribeText = null;
            RefreshTrayText();

            if (!_icon.Visible) return;

            if (job.Status == TranscriptionStatus.Completed)
                _icon.ShowBalloonTip(10000, "逐字稿完成",
                    $"{Path.GetFileName(job.AudioPath)}\n已輸出：{Path.GetFileName(job.TextPath ?? "")}",
                    ToolTipIcon.Info);
            else
                _icon.ShowBalloonTip(10000, "逐字稿轉檔失敗",
                    $"{Path.GetFileName(job.AudioPath)}\n{job.Error}", ToolTipIcon.Error);
        }, null);
    }

    /// <summary>
    /// 產生紅色錄音圖示（紅底白圈的錄音鍵符號）。以程式繪製而非外掛 .ico 檔，
    /// 少一個需要一起發佈的資產。整個行程只建立一次，故 GetHicon 的 handle 不另行釋放。
    /// </summary>
    private static Icon CreateRecordingIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.FillEllipse(Brushes.Firebrick, 1, 1, 30, 30);
            g.FillEllipse(Brushes.White, 11, 11, 10, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // 2026/07/13 - Arlen - 新增會議錄音模組 - end

    private static string FormatRemain(TimeSpan ts)
    {
        if (ts.TotalSeconds <= 0) return "即將觸發";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}時{ts.Minutes}分";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}分{ts.Seconds}秒";
        return $"{ts.Seconds}秒";
    }

    private static void AddWindowItem(ContextMenuStrip menu, string label, UsageWindow? w)
    {
        if (w is null) { return; }
        string reset = w.ResetsAt.HasValue
            ? w.ResetsAt.Value.ToLocalTime().ToString("MM/dd HH:mm")
            : "—";
        menu.Items.Add(new ToolStripMenuItem(
            $"{label}：{w.Utilization:N0}%（重置 {reset}）") { Enabled = false });
    }

    // 2026/07/02 - Arlen - 顯示 spend 信用額度（used/limit + 百分比），未啟用或無資料則不顯示 - Start
    private static void AddSpendItem(ContextMenuStrip menu, Spend? spend)
    {
        if (spend is null) { return; }
        if (!spend.Enabled && (spend.Used is null || spend.Used.AmountMinor == 0)) { return; }

        string cur = spend.Used?.Currency ?? spend.Limit?.Currency ?? "USD";
        string used = spend.Used is { } u ? u.Amount.ToString("N2") : "0.00";
        string text = spend.Limit is { } lim
            ? $"信用額度：{cur} {used} / {lim.Amount:N2}（{spend.Percent:N0}%）"
            : $"信用額度：{cur} {used}";
        menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
    }
    // 2026/07/02 - Arlen - 顯示 spend 信用額度（used/limit + 百分比），未啟用或無資料則不顯示 - end

    // ── 用量輪詢（移植自 ClaudeUsageMonitor）──────────────────────

    private async void RefreshNowAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // 2026/07/09 - Arlen - 前置條件：無 Claude Code 執行緒時不查用量、不觸發 token refresh，避免自行換掉 refresh token 造成 auth 失效；退避 10 分鐘再試 - Start
            if (!IsClaudeCodeRunning())
            {
                _statusLine = "Claude Code 未執行，暫停查詢用量（10 分鐘後重試）";
                _usageText = "Keel／" + _statusLine;
                RefreshTrayText();
                if (_usageTimer.Interval != NoClaudeBackoffMs) _usageTimer.Interval = NoClaudeBackoffMs;
                return;
            }
            if (_usageTimer.Interval != _normalIntervalMs) _usageTimer.Interval = _normalIntervalMs;
            // 2026/07/09 - Arlen - end

            _scanner.Scan();
            _usageClient ??= new OAuthUsageClient(_store, _settings, _scanner.LatestVersion);

            _lastUsage = await _usageClient.GetUsageAsync();
            // 2026/08/18 - Arlen - 新增 get_usage_5h 工具：成功才更新時間戳 - Start
            _lastUsageAt = DateTime.Now;
            // 2026/08/18 - Arlen - 新增 get_usage_5h 工具 - end
            UpdateIconText(_lastUsage);
            CheckThresholds(_lastUsage);
        }
        catch (Exception ex)
        {
            _statusLine = $"取得失敗：{ex.Message}";
            _lastUsage = null;
            // 2026/08/18 - Arlen - 新增 get_usage_5h 工具：快照失效時一併清掉時間戳，避免回報舊時間 - Start
            _lastUsageAt = null;
            // 2026/08/18 - Arlen - 新增 get_usage_5h 工具 - end
            _usageText = "Keel／Claude 用量：" + ex.Message;
            RefreshTrayText();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 偵測是否有 Claude Code（claude.exe）處理程序在執行。查用量的前置條件：
    /// 無此程序時不查用量、也不觸發 token refresh，避免 Keel 自行換新 refresh token
    /// 導致 Claude Code 端 auth 失效需重登。
    /// </summary>
    private static bool IsClaudeCodeRunning()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("claude");
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    private void UpdateIconText(OAuthUsageResponse u)
    {
        var sb = new StringBuilder("Keel Assistant\n");
        if (u.FiveHour is { } f) sb.Append($"5h {f.Utilization:N0}%  ");
        if (u.SevenDay is { } d) sb.Append($"7d {d.Utilization:N0}%");
        _usageText = sb.ToString();
        RefreshTrayText();
    }

    // 2026/07/13 - Arlen - 轉逐字稿要跑數十分鐘，過程中托盤毫無存在感；把進度掛到 Tooltip - Start

    /// <summary>
    /// 托盤 Tooltip 的唯一出口。轉檔進行中時優先顯示進度（那是當下唯一在跑的長工作），
    /// 沒在轉檔就顯示用量。Tooltip 上限 63 字元，故一律截斷。
    /// </summary>
    private void RefreshTrayText()
        => _icon.Text = Truncate(_transcribeText ?? _usageText, 63);

    /// <summary>轉檔開始：跳氣泡告知已接手並給出預估時間，讓 Arlen 可以安心去忙別的。</summary>
    private void OnTranscriptionStarted(TranscriptionJob job)
    {
        _uiContext.Post(_ =>
        {
            if (!_icon.Visible) return;

            // medium 模型在純 CPU 上約是「音檔長度」等量的時間，用 1:1 粗估即可
            var estimate = job.Duration > TimeSpan.Zero
                ? $"預估約 {Math.Max(1, (int)job.Duration.TotalMinutes)} 分鐘"
                : "時間未知";

            _icon.ShowBalloonTip(6000, "開始轉逐字稿",
                $"{Path.GetFileName(job.AudioPath)}\n{estimate}，完成後會再通知。", ToolTipIcon.Info);
        }, null);
    }

    /// <summary>轉檔進度：只更新 Tooltip，不跳通知（每個 segment 都跳會炸死人）。</summary>
    private void OnTranscriptionProgress(TranscriptionJob job)
    {
        _uiContext.Post(_ =>
        {
            _transcribeText = $"Keel／轉逐字稿 {job.Percent:N0}%\n{Path.GetFileName(job.AudioPath)}";
            RefreshTrayText();
        }, null);
    }

    // 2026/07/13 - Arlen - 轉逐字稿進度掛到 Tooltip - end

    private void CheckThresholds(OAuthUsageResponse u)
    {
        CheckWindow("5 小時視窗", u.FiveHour);
        CheckWindow("7 天額度", u.SevenDay);
        CheckWindow("7 天 Opus", u.SevenDayOpus);
        CheckWindow("7 天 Sonnet", u.SevenDaySonnet);
    }

    private void CheckWindow(string name, UsageWindow? w)
    {
        if (w is null) return;

        double crossed = 0;
        if (w.Utilization >= _settings.CriticalThresholdPercent) crossed = _settings.CriticalThresholdPercent;
        else if (w.Utilization >= _settings.WarnThresholdPercent) crossed = _settings.WarnThresholdPercent;

        if (crossed == 0)
        {
            _notified.Remove(name);
            return;
        }

        double already = _notified.TryGetValue(name, out var v) ? v : 0;
        if (crossed > already)
        {
            _notified[name] = crossed;
            string reset = w.ResetsAt.HasValue
                ? w.ResetsAt.Value.ToLocalTime().ToString("MM/dd HH:mm")
                : "—";
            _icon.ShowBalloonTip(
                10000,
                $"Claude 用量警示：{name}",
                $"{name} 已使用 {w.Utilization:N0}%（重置 {reset}）",
                crossed >= _settings.CriticalThresholdPercent ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _api.Stop();
            _usageTimer.Stop();
            _usageTimer.Dispose();
            // 2026/06/25 - Arlen - 修「結束無法關閉、背景殘留」：補釋放鬧鐘(Timer/響鈴)與音樂(WebView2)模組 - Start
            _alarm.Dispose();
            // 2026/11/10 - Arlen - 移除音樂模組，不再釋放 _music
            // 2026/06/25 - Arlen - 修「結束無法關閉、背景殘留」- end
            // 2026/07/13 - Arlen - 新增會議錄音模組：釋放擷取裝置並關檔（錄音中結束程式仍保住已錄內容）- Start
            _recording.Changed -= OnRecordingChanged;
            _recording.Dispose();
            _transcription.JobStarted -= OnTranscriptionStarted;
            _transcription.JobProgress -= OnTranscriptionProgress;
            _transcription.JobFinished -= OnTranscriptionFinished;
            _transcription.Dispose();
            _models.Dispose();
            // 2026/07/13 - Arlen - 新增會議錄音模組 - end
            // 2026/07/22 - Arlen - 新增 SQL 執行模組：關閉可能還開著的連線設定視窗 - Start
            try { _dbForm?.Dispose(); } catch { }
            // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end
            // 2026/07/28 - Arlen - 新增通知監看模組：停掉輪詢 Timer 並收掉提醒視窗/音效 - Start
            _notifications.Dispose();
            // 2026/07/28 - Arlen - 新增通知監看模組 - end
            // 2026/07/23 - Arlen - 新增 Python 沙盒模組：釋放 WebView2（未銷毀會留下 msedgewebview2 子行程）- Start
            _sandbox.Dispose();
            // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end
            // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組：斷開所有連線並關閉終端視窗，
            // 否則 WebView2 與讀取執行緒會讓行程退不掉 - Start
            try { _sshForm?.Dispose(); } catch { }
            _sshSessions.Dispose();
            // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
            _icon.Visible = false;
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
