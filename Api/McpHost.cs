using System.Net;
using KeelAssistant.Alarm;
using KeelAssistant.Calendar;
using KeelAssistant.Db;
using KeelAssistant.Models;
using KeelAssistant.Recording;
using KeelAssistant.Sandbox;
// 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - Start
using KeelAssistant.Ssh;
// 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KeelAssistant.Api;

/// <summary>
/// 本機 MCP Server（內嵌 Kestrel，僅綁 127.0.0.1，避開 IIS 佔用的 80/443/8080）。
/// 以 Streamable HTTP transport 對外，Claude（Keel）透過 MCP 工具直接調用：
///   claude mcp add --transport http keel http://127.0.0.1:38600/mcp
/// 常駐狀態（鬧鐘 / 錄音 / 用量）活在 WinForm process，故只能用 HTTP transport，不可用 stdio。
/// 取代原 HttpApiServer（REST）。
/// </summary>
public sealed class McpHost
{
    private readonly WebApplication _app;

    public McpHost(
        int port,
        AlarmEngine alarm,
        // 2026/11/10 - Arlen - 移除音樂與生活記憶模組（不再注入 MusicController / MemoryStore）- Start
        CalendarService calendar,
        // 2026/11/10 - Arlen - 移除音樂與生活記憶模組 - end
        // 2026/07/13 - Arlen - 新增會議錄音模組（含 Whisper 事後逐字稿轉檔）- Start
        RecordingController recording,
        TranscriptionService transcription,
        ModelDownloader models,
        // 2026/07/13 - Arlen - 新增會議錄音模組 - end
        // 2026/07/22 - Arlen - 新增 SQL 執行模組（帳密由本程式控管，Claude 只拿得到 SQLConnID）- Start
        DbConnectionStore dbConnections,
        SqlExecutor sqlExecutor,
        // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end
        // 2026/07/23 - Arlen - 新增 Python 沙盒模組（Pyodide/WASM，供 python_run 等工具使用）- Start
        PyodideRunner sandbox,
        SandboxPaths sandboxPaths,
        // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end
        // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組（供 ssh_exec / ssh_status 等工具使用）- Start
        SshConnectionStore sshConnections,
        SshSessionManager sshSessions,
        // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
        Func<OAuthUsageResponse?> getUsage,
        // 2026/08/18 - Arlen - 新增 get_usage_5h 工具：需知道快照有多舊 - Start
        Func<DateTime?> getUsageAt,
        // 2026/08/18 - Arlen - 新增 get_usage_5h 工具 - end
        SynchronizationContext uiContext)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // WinForm 無主控台，靜音 ASP.NET 記錄，避免噪音。
        builder.Logging.ClearProviders();
        // 2026/07/22 - Arlen - 工具呼叫失敗時 MCP 只回「An error occurred」，現場毫無線索；
        // 保留一條 Warning 以上的檔案軌跡供診斷 - Start
        builder.Logging.AddProvider(new FileLoggerProvider(
            Path.Combine(AppContext.BaseDirectory, "mcp-error.log")));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // 2026/07/22 - Arlen - 新增 MCP 錯誤日誌 - end

        // 僅綁回送位址（IPv4/IPv6 loopback），不對外曝露。
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

        // 把現有常駐單例注入 DI，供工具方法以參數注入取用。
        builder.Services.AddSingleton(alarm);
        // 2026/11/10 - Arlen - 移除音樂與生活記憶模組 - Start
        builder.Services.AddSingleton(calendar);
        // 2026/11/10 - Arlen - 移除音樂與生活記憶模組 - end
        // 2026/07/13 - Arlen - 新增會議錄音模組（含 Whisper 事後逐字稿轉檔）- Start
        builder.Services.AddSingleton(recording);
        builder.Services.AddSingleton(transcription);
        builder.Services.AddSingleton(models);
        // 2026/07/13 - Arlen - 新增會議錄音模組 - end
        // 2026/07/22 - Arlen - 新增 SQL 執行模組 - Start
        builder.Services.AddSingleton(dbConnections);
        builder.Services.AddSingleton(sqlExecutor);
        // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end
        // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - Start
        builder.Services.AddSingleton(sandbox);
        builder.Services.AddSingleton(sandboxPaths);
        // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end
        // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - Start
        builder.Services.AddSingleton(sshConnections);
        builder.Services.AddSingleton(sshSessions);
        // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
        builder.Services.AddSingleton(new UiDispatcher(uiContext));
        // 2026/08/18 - Arlen - 新增 get_usage_5h 工具：UsageProvider 改帶快照時間 - Start
        builder.Services.AddSingleton(new UsageProvider(getUsage, getUsageAt));
        // 2026/08/18 - Arlen - 新增 get_usage_5h 工具 - end

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)   // 本機自用、不需 server→client，stateless 較輕
            .WithToolsFromAssembly();                     // 掃描本組件 [McpServerToolType]（KeelTools）

        _app = builder.Build();
        _app.MapMcp("/mcp");
    }

    /// <summary>啟動 Kestrel（同步等候綁定完成）。綁定失敗會拋出，由呼叫端處理。</summary>
    public void Start() => _app.StartAsync().GetAwaiter().GetResult();

    public void Stop()
    {
        // 2026/06/25 - Arlen - 修「結束無法關閉」：StopAsync 在 UI 執行緒同步等待，
        // 若仍有 MCP in-flight 連線會吊住 graceful shutdown，導致 Dispose 永遠卡住、行程退不掉。
        // 設 3 秒逾時上限，逾時即放棄等待（後續由 Environment.Exit 強制終結）- Start
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _app.StopAsync(cts.Token).Wait(TimeSpan.FromSeconds(3));
        }
        catch { }
        try { _app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        // 2026/06/25 - Arlen - 修「結束無法關閉」- end
    }
}

/// <summary>在 UI 執行緒同步執行（操作 WinForms 視窗、讀取 Outlook COM 需 STA）。</summary>
public sealed class UiDispatcher(SynchronizationContext ui)
{
    public T Send<T>(Func<T> func)
    {
        T result = default!;
        Exception? err = null;
        ui.Send(_ => { try { result = func(); } catch (Exception ex) { err = ex; } }, null);
        if (err is not null) throw err;
        return result;
    }

    public void Send(Action action)
    {
        Exception? err = null;
        ui.Send(_ => { try { action(); } catch (Exception ex) { err = ex; } }, null);
        if (err is not null) throw err;
    }
}

/// <summary>提供最近一次用量快照（由 TrayApplicationContext 持有並更新）。</summary>
// 2026/08/18 - Arlen - 新增 get_usage_5h 工具：快照最舊可達輪詢間隔（>=180 秒），
// 故一併帶出取得時間，讓工具回傳能揭露陳舊程度，避免 Claude 把舊數字當即時值 - Start
public sealed class UsageProvider(Func<OAuthUsageResponse?> getUsage, Func<DateTime?> getUsageAt)
{
    public OAuthUsageResponse? Current => getUsage();

    /// <summary>快照取得時間（本機時間）；尚未輪詢或上次取得失敗為 null。</summary>
    public DateTime? CurrentAt => getUsageAt();
}
// 2026/08/18 - Arlen - 新增 get_usage_5h 工具 - end
