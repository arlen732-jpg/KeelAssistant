using Microsoft.Extensions.Logging;

namespace KeelAssistant.Api;

/// <summary>
/// 極簡檔案日誌，只記錄 Warning 以上到 <c>mcp-error.log</c>。
///
/// 存在理由：WinForms 無主控台，且 <see cref="McpHost"/> 為了避免噪音而
/// <c>ClearProviders()</c> 靜音了 ASP.NET 記錄——代價是 MCP 工具呼叫一旦出錯，
/// 現場沒有任何線索（MCP 協定只會回給 client 一句通用的「An error occurred」）。
/// 故保留一條最小的錯誤軌跡，平時幾乎不寫入，出事時有東西可查。
/// </summary>
public sealed class FileLoggerProvider(string logPath) : ILoggerProvider
{
    private readonly string _logPath = logPath;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_logPath, categoryName);

    public void Dispose() { }

    private sealed class FileLogger(string path, string category) : ILogger
    {
        private static readonly object Gate = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:yyyy\\/MM\\/dd HH:mm:ss} [{logLevel}] {category}{Environment.NewLine}" +
                       $"  {formatter(state, exception)}" +
                       (exception is null ? "" : $"{Environment.NewLine}{exception}");

            // 寫檔失敗不影響執行期：日誌是診斷輔助，不該反過來拖垮服務
            try
            {
                lock (Gate) File.AppendAllText(path, line + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
