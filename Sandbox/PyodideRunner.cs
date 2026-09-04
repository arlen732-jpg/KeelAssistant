using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using KeelAssistant.Api;
using KeelAssistant.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KeelAssistant.Sandbox;

/// <summary>
/// Python 沙盒執行引擎：以 WebView2 載入本機打包的 Pyodide（CPython 編譯成 WebAssembly）。
///
/// 隔離來自 WASM 本身——沒有 syscall、沒有真實檔案系統、沒有 socket，
/// 因此 LLM 誤產生的 rmtree／requests.get 之類的程式碼在此無從生效。
/// 這是刻意的選擇：威脅模型是「誤傷」而非「刻意逃逸」，WASM 對前者是完備的，
/// 且不必像容器方案那樣背上 Docker 常駐與映像維護的成本。
///
/// 三個併發相關的設計，因為多個 Claude Code session 與 SubAgent 共用同一個
/// MCP Server（Stateless=true，沒有 session 身分可以區分）：
///   1. SemaphoreSlim 全域序列化，一次只跑一個，避免共用直譯器導致輸出交錯與變數互相覆寫。
///   2. 每次執行都建立全新 Worker、跑完即銷毀，跨次不殘留狀態（代價是每次重新初始化）。
///   3. 30 秒的等待發生在執行緒池，UI 執行緒只用於「送出請求」與「銷毀 Worker」兩個瞬間動作——
///      UI 執行緒上跑著鬧鐘的 1 秒 Timer 與錄音的 100ms pump，卡住它會連帶傷到既有功能。
/// </summary>
public sealed class PyodideRunner : IDisposable
{
    private const int QueueWaitMs = 60_000;      // 排隊等候上限，超過即回報忙碌而非無限堆積
    private const int BrowserInitMs = 30_000;    // WebView2 建立 + 頁面載入的上限
    private const int WatchdogIntervalMs = 500;

    private readonly SandboxPaths _paths;
    private readonly UiDispatcher _ui;
    private readonly string _contentRoot;
    private readonly string _userDataFolder;
    private readonly int _timeoutMs;
    private readonly long _memoryLimitBytes;
    private readonly int _idleShutdownMs;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SandboxRunResult>> _pending = new();

    // 以下欄位一律只在 UI 執行緒讀寫（經 _ui.Send），故不另外加鎖
    private Form? _host;
    private WebView2? _web;
    private CoreWebView2Environment? _environment;
    private TaskCompletionSource<bool>? _readyTcs;
    private System.Windows.Forms.Timer? _watchdog;
    private System.Windows.Forms.Timer? _idleTimer;
    private long _memoryBaseline;
    private string? _activeReqId;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PyodideRunner(SandboxPaths paths, AppSettings settings, SynchronizationContext uiContext)
    {
        _paths = paths;
        _ui = new UiDispatcher(uiContext);
        _contentRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "sandbox");
        _userDataFolder = Path.Combine(AppContext.BaseDirectory, "webview2-sandbox");
        _timeoutMs = Math.Clamp(settings.SandboxTimeoutSeconds, 1, 300) * 1000;
        _memoryLimitBytes = Math.Clamp(settings.SandboxMemoryLimitMb, 128, 8192) * 1024L * 1024L;
        _idleShutdownMs = Math.Clamp(settings.SandboxIdleShutdownMinutes, 0, 240) * 60 * 1000;

        TimeoutSeconds = _timeoutMs / 1000;
        MemoryLimitMb = (int)(_memoryLimitBytes / 1024 / 1024);
    }

    public int TimeoutSeconds { get; }
    public int MemoryLimitMb { get; }

    /// <summary>執行環境是否備妥（Pyodide 資源存在且沙盒目錄可用）。</summary>
    public bool Available => _paths.Available && File.Exists(Path.Combine(_contentRoot, "index.html"));

    public string? DisabledReason =>
        !_paths.Available ? _paths.DisabledReason
        : !File.Exists(Path.Combine(_contentRoot, "index.html"))
            ? $"找不到沙盒執行環境：{_contentRoot}\\index.html"
            : null;

    /// <summary>
    /// 執行一段 Python 程式碼。inputs 已由呼叫端驗證並讀取完畢（含人工確認），
    /// 本方法只負責送進沙盒與取回結果。
    /// </summary>
    public async Task<SandboxRunResult> RunAsync(string code, IReadOnlyList<(string Name, byte[] Data)> inputs)
    {
        if (!Available)
            throw new InvalidOperationException(DisabledReason ?? "沙盒不可用");

        // 序列化：多個 session／SubAgent 打進來時排隊，而不是共用同一個直譯器互相污染
        if (!await _gate.WaitAsync(QueueWaitMs).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"沙盒忙碌中（已等候 {QueueWaitMs / 1000} 秒仍未輪到），請稍後再試。");

        try
        {
            await EnsureBrowserAsync().ConfigureAwait(false);

            var reqId = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<SandboxRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[reqId] = tcs;

            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    kind = "run",
                    reqId,
                    code,
                    files = inputs.Select(f => new { name = f.Name, b64 = Convert.ToBase64String(f.Data) }).ToArray()
                }, JsonOpts);

                _ui.Send(() =>
                {
                    _activeReqId = reqId;
                    StartWatchdog();
                    // PostWebMessageAsString 而非 ExecuteScriptAsync：Python 程式碼含大量引號與
                    // 反斜線，塞進 JS 字串會有跳脫地雷，走訊息通道則完全不經過 JS 解析器。
                    _web!.CoreWebView2.PostWebMessageAsString(payload);
                });

                var finished = await Task.WhenAny(tcs.Task, Task.Delay(_timeoutMs)).ConfigureAwait(false);
                if (finished != tcs.Task)
                {
                    Abort();
                    throw new InvalidOperationException(
                        $"執行超過 {TimeoutSeconds} 秒已強制中止（常見原因是無窮迴圈或資料量過大）。");
                }

                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(reqId, out _);
                _ui.Send(() =>
                {
                    _activeReqId = null;
                    StopWatchdog();
                });
            }
        }
        finally
        {
            ScheduleIdleShutdown();
            _gate.Release();
        }
    }

    // ── WebView2 生命週期 ─────────────────────────────────────────

    /// <summary>
    /// 確保 WebView2 與沙盒頁面就緒。跑 Python 的頻率不高，讓 WebView2 常駐要付
    /// 150-250MB 的待機記憶體，因此改為用時才建、閒置逾時銷毀。
    /// 呼叫端已持有 _gate，故不會有併發建立的競態。
    /// </summary>
    private async Task EnsureBrowserAsync()
    {
        var ready = _ui.Send(() =>
        {
            CancelIdleShutdown();
            if (_web?.CoreWebView2 is not null && _readyTcs?.Task.IsCompletedSuccessfully == true)
                return Task.FromResult(true);
            return CreateBrowserAsync();
        });

        var completed = await Task.WhenAny(ready, Task.Delay(BrowserInitMs)).ConfigureAwait(false);
        if (completed != ready)
            throw new InvalidOperationException($"沙盒執行環境啟動逾時（{BrowserInitMs / 1000} 秒）。");

        await ready.ConfigureAwait(false);
    }

    /// <summary>在 UI 執行緒建立隱藏宿主視窗與 WebView2，並等待頁面回報 ready。</summary>
    private async Task<bool> CreateBrowserAsync()
    {
        DisposeBrowser();

        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // WebView2 在未顯示的視窗上不會完成初始化，故把宿主視窗移到螢幕外而非隱藏它
        _host = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(400, 300),
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            Opacity = 0
        };
        _web = new WebView2 { Dock = DockStyle.Fill };
        _host.Controls.Add(_web);
        _host.Show();

        // 專屬的 user data folder：與音樂播放器的 WebView2 分開，
        // 一來沙盒不該共用 cookie/storage，二來記憶體才能歸屬到這組行程來計算。
        _environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, null);
        await _web.EnsureCoreWebView2Async(_environment);

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        // 用虛擬主機而非 file://：module worker 在 file:// 下無法建立。
        // 資源全部來自本機資料夾，不會有任何連外請求。
        core.SetVirtualHostNameToFolderMapping(
            "keel-sandbox", _contentRoot, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;
        core.Navigate("https://keel-sandbox/index.html");

        return await _readyTcs.Task;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.TryGetWebMessageAsString(); }
        catch { return; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;

            if (kind == "ready")
            {
                _readyTcs?.TrySetResult(true);
                _memoryBaseline = CurrentMemoryBytes();
                return;
            }

            if (kind != "result") return;

            var reqId = doc.RootElement.TryGetProperty("reqId", out var r) ? r.GetString() : null;
            if (reqId is null || !_pending.TryGetValue(reqId, out var tcs)) return;   // 逾時後才回來的舊結果，丟棄

            var result = JsonSerializer.Deserialize<SandboxRunResult>(json)
                         ?? new SandboxRunResult { Ok = false, Error = "沙盒回傳了無法解析的結果" };
            tcs.TrySetResult(result);
        }
        catch (Exception ex)
        {
            _readyTcs?.TrySetException(ex);
        }
    }

    /// <summary>WebView2 的 renderer 崩潰（多半是記憶體耗盡）。讓等待中的請求立即失敗，不必空等到逾時。</summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var reason = $"沙盒行程異常結束（{e.ProcessFailedKind}），常見原因是記憶體耗盡。";
        foreach (var tcs in _pending.Values)
            tcs.TrySetException(new InvalidOperationException(reason));

        _readyTcs?.TrySetException(new InvalidOperationException(reason));
        // 下次呼叫時重建（EnsureBrowserAsync 會因 _readyTcs 未成功而重走建立流程）
    }

    // ── 中止與記憶體看門狗 ────────────────────────────────────────

    /// <summary>硬殺目前的 Worker。terminate() 是唯一能停掉無窮迴圈的手段——Worker 內部無法自我中止。</summary>
    private void Abort()
    {
        _ui.Send(() =>
        {
            try
            {
                _web?.CoreWebView2?.PostWebMessageAsString(
                    JsonSerializer.Serialize(new { kind = "abort" }, JsonOpts));
            }
            catch { }   // 已崩潰或已銷毀，中止本來就是目的，失敗無妨
        });
    }

    /// <summary>
    /// 記憶體看門狗。Pyodide 沒有提供 WASM 線性記憶體上限的設定，
    /// 只能從外部觀察 WebView2 行程群組，超過基準值 + 上限就中止。
    /// 誤傷情境要防的是「吃光主機記憶體導致系統卡死」，這個精度已足夠。
    /// </summary>
    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdog = new System.Windows.Forms.Timer { Interval = WatchdogIntervalMs };
        _watchdog.Tick += (_, _) =>
        {
            var used = CurrentMemoryBytes() - _memoryBaseline;
            if (used <= _memoryLimitBytes) return;

            StopWatchdog();
            var reqId = _activeReqId;
            if (reqId is not null && _pending.TryGetValue(reqId, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException(
                    $"記憶體用量超過 {MemoryLimitMb} MB 上限，已強制中止。"));
            }
            Abort();
        };
        _watchdog.Start();
    }

    private void StopWatchdog()
    {
        if (_watchdog is null) return;
        try { _watchdog.Stop(); _watchdog.Dispose(); } catch { }
        _watchdog = null;
    }

    /// <summary>目前 WebView2 行程群組的記憶體總量。必須在 UI 執行緒呼叫。</summary>
    private long CurrentMemoryBytes()
    {
        if (_environment is null) return 0;

        long total = 0;
        try
        {
            foreach (var info in _environment.GetProcessInfos())
            {
                try { total += Process.GetProcessById(info.ProcessId).WorkingSet64; }
                catch { }   // 行程可能剛好結束，略過即可
            }
        }
        catch { }
        return total;
    }

    // ── 閒置銷毀 ──────────────────────────────────────────────────

    private void ScheduleIdleShutdown() => _ui.Send(() =>
    {
        CancelIdleShutdown();
        if (_disposed) return;

        if (_idleShutdownMs == 0)
        {
            DisposeBrowser();
            return;
        }

        _idleTimer = new System.Windows.Forms.Timer { Interval = _idleShutdownMs };
        _idleTimer.Tick += (_, _) =>
        {
            CancelIdleShutdown();
            DisposeBrowser();
        };
        _idleTimer.Start();
    });

    private void CancelIdleShutdown()
    {
        if (_idleTimer is null) return;
        try { _idleTimer.Stop(); _idleTimer.Dispose(); } catch { }
        _idleTimer = null;
    }

    /// <summary>銷毀 WebView2 與宿主視窗。必須在 UI 執行緒呼叫。</summary>
    private void DisposeBrowser()
    {
        StopWatchdog();

        if (_web is not null)
        {
            try
            {
                if (_web.CoreWebView2 is not null)
                {
                    _web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _web.CoreWebView2.ProcessFailed -= OnProcessFailed;
                }
            }
            catch { }
            try { _web.Dispose(); } catch { }
            _web = null;
        }

        if (_host is not null)
        {
            try { _host.Close(); _host.Dispose(); } catch { }
            _host = null;
        }

        _environment = null;
        _readyTcs = null;
        _memoryBaseline = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _ui.Send(() => { CancelIdleShutdown(); DisposeBrowser(); }); } catch { }
        try { _gate.Dispose(); } catch { }
    }
}
