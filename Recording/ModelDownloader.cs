namespace KeelAssistant.Recording;

/// <summary>Whisper 模型的安裝／下載狀態。</summary>
public sealed record ModelState(
    string Path,
    bool Installed,
    bool Downloading,
    double Percent,
    long ReceivedBytes,
    long TotalBytes,
    string? Error);

/// <summary>
/// Whisper 逐字稿模型（medium，約 1.5GB）按需下載。
///
/// 刻意「不」在程式啟動時自動抓：一個系統匣常駐程式不該在使用者沒點頭的情況下
/// 默默吃掉 1.5GB 頻寬與磁碟。改由 Claude 呼叫 download_whisper_model 觸發——
/// 那是明確的意思表示。
///
/// 下載先寫到 .part 暫存檔，完成才改名成正式檔名：中途斷線／關程式留下的半截檔案，
/// 不會被 File.Exists 誤判成「模型已安裝」而讓 Whisper 載入時炸掉。
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin";

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _path;
    private readonly object _gate = new();
    private ModelState? _downloading;
    private string? _lastError;

    /// <summary>下載進度或狀態變動時觸發（背景執行緒，訂閱端需自行切回 UI）。</summary>
    public event Action<ModelState>? Changed;

    public ModelDownloader(string modelPath) => _path = modelPath;

    public string ModelPath => _path;

    public bool IsInstalled => File.Exists(_path);

    /// <summary>目前狀態（未在下載中就即時反映檔案是否存在）。</summary>
    public ModelState State
    {
        get
        {
            lock (_gate)
            {
                if (_downloading is { Downloading: true } d) return d;
                return new ModelState(_path, IsInstalled, false, 0, 0, 0, _lastError);
            }
        }
    }

    /// <summary>開始下載（背景執行，立即返回）。已安裝或已在下載中則直接返回目前狀態，不重複抓。</summary>
    public ModelState Start()
    {
        lock (_gate)
        {
            var current = State;
            if (current.Installed || current.Downloading) return current;

            _lastError = null;
            _downloading = current with { Downloading = true, Percent = 0, Error = null };
            _ = Task.Run(DownloadAsync);
            return _downloading;
        }
    }

    private async Task DownloadAsync()
    {
        var partPath = _path + ".part";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            using var resp = await _http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead)
                                        .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? 0;
            long received = 0;

            await using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var dst = File.Create(partPath))
            {
                var buffer = new byte[1 << 20];   // 1MB
                var lastReport = DateTime.MinValue;
                int read;

                while ((read = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    received += read;

                    // 每 0.5 秒回報一次即可，不必每個 buffer 都打擾訂閱端
                    if ((DateTime.Now - lastReport).TotalMilliseconds < 500) continue;
                    lastReport = DateTime.Now;

                    double percent = total > 0 ? (double)received / total * 100 : 0;
                    Publish(new ModelState(_path, false, true, percent, received, total, null));
                }
            }

            // 完成才改名：半截的 .part 不會被誤判成可用模型
            File.Move(partPath, _path, overwrite: true);
            Publish(new ModelState(_path, true, false, 100, received, total, null));
        }
        catch (Exception ex)
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            Publish(new ModelState(_path, false, false, 0, 0, 0, ex.Message));
        }
    }

    private void Publish(ModelState state)
    {
        lock (_gate)
        {
            _downloading = state.Downloading ? state : null;
            _lastError = state.Error;
        }
        Changed?.Invoke(state);
    }

    public void Dispose() => _http.Dispose();
}
