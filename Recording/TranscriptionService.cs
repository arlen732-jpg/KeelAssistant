using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace KeelAssistant.Recording;

/// <summary>轉檔工作狀態。</summary>
public enum TranscriptionStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

/// <summary>一件事後轉檔工作。</summary>
public sealed class TranscriptionJob
{
    public required string Id { get; init; }
    public required string AudioPath { get; init; }

    /// <summary>音檔長度，排入佇列時就讀出來，供估算轉檔要跑多久。</summary>
    public TimeSpan Duration { get; init; }

    public TranscriptionStatus Status { get; set; } = TranscriptionStatus.Queued;

    /// <summary>0~100，依已辨識到的音訊時間佔總長度估算。</summary>
    public double Percent { get; set; }

    public string? TextPath { get; set; }
    public string? SrtPath { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
}

/// <summary>
/// 事後逐字稿：用 Whisper medium 把錄好的 WAV 轉成 .txt 與 .srt。
///
/// 為何不即時：medium 在 7840U 上約要花「音檔長度」等量的時間（一小時會議約 40~60 分鐘），
/// 但這正是它的定位——會議結束後才呼叫，跑多久都無所謂，換來的是遠優於即時字幕的品質
/// （有標點、中英混雜正常、繁體）。這份才是餵給 GenMeetingMinutes 的正式逐字稿。
///
/// 工作採佇列序列化執行（一次只跑一件）：medium 模型光載入就吃 1.5GB，
/// 同時跑多件會把記憶體與 CPU 都拖垮。呼叫端（MCP 工具）只排隊、立即返回，不阻塞。
/// </summary>
public sealed class TranscriptionService : IDisposable
{
    private const int SampleRate = 16000;

    private readonly string _modelPath;
    private readonly ConcurrentDictionary<string, TranscriptionJob> _jobs = new();
    private readonly Channel<TranscriptionJob> _queue =
        Channel.CreateUnbounded<TranscriptionJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    /// <summary>一件工作真正開始跑（而非還在排隊）時觸發。背景執行緒呼叫。</summary>
    public event Action<TranscriptionJob>? JobStarted;

    /// <summary>轉檔進度更新時觸發，供托盤 Tooltip 顯示百分比。背景執行緒呼叫。</summary>
    public event Action<TranscriptionJob>? JobProgress;

    /// <summary>一件工作結束（成功或失敗）時觸發，供托盤跳通知。背景執行緒呼叫。</summary>
    public event Action<TranscriptionJob>? JobFinished;

    /// <summary>模型檔存在才可用。</summary>
    public bool IsAvailable => File.Exists(_modelPath);

    public string ModelPath => _modelPath;

    public TranscriptionService(string modelPath)
    {
        _modelPath = modelPath;
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    /// <summary>排入一件轉檔工作，立即回傳 job（不等待完成）。</summary>
    public TranscriptionJob Enqueue(string audioPath)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                $"尚未安裝 Whisper medium 模型（{_modelPath}）。請先呼叫 download_whisper_model(\"medium\") 下載（約 1.5GB），" +
                "並用 get_model_status 確認完成後再轉檔。");
        if (!File.Exists(audioPath))
            throw new ArgumentException($"找不到音檔：{audioPath}");

        var job = new TranscriptionJob
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            AudioPath = audioPath,
            Duration = TryReadDuration(audioPath)
        };
        _jobs[job.Id] = job;
        _queue.Writer.TryWrite(job);
        return job;
    }

    /// <summary>只讀 WAV 標頭取長度（供估算轉檔時間），失敗不影響轉檔本身。</summary>
    private static TimeSpan TryReadDuration(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            return reader.TotalTime;
        }
        catch { return TimeSpan.Zero; }
    }

    public TranscriptionJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>所有工作，最新的排前面。</summary>
    public IReadOnlyList<TranscriptionJob> List()
        => _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    // ── 背景執行 ──────────────────────────────────────────────────

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var job))
            {
                if (ct.IsCancellationRequested) return;
                await RunJobAsync(job, ct).ConfigureAwait(false);
                JobFinished?.Invoke(job);
            }
        }
    }

    private async Task RunJobAsync(TranscriptionJob job, CancellationToken ct)
    {
        job.Status = TranscriptionStatus.Running;
        JobStarted?.Invoke(job);

        // medium 模型佔 1.5GB，每件工作用完就釋放，不常駐記憶體。
        WhisperFactory? factory = null;
        WhisperProcessor? processor = null;
        try
        {
            var (samples, duration) = LoadAudio(job.AudioPath);

            factory = WhisperFactory.FromPath(_modelPath);
            processor = factory.CreateBuilder()
                .WithLanguage("zh")
                .WithPrompt("以下是一場繁體中文的技術會議，內容可能夾雜英文技術名詞。")
                .WithThreads(Math.Max(1, Environment.ProcessorCount - 2))   // 留 2 核給錄音與系統
                .Build();

            var text = new StringBuilder();
            var srt = new StringBuilder();
            int index = 1;

            await foreach (var seg in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                var line = seg.Text.Trim();
                if (line.Length == 0) continue;

                text.AppendLine($"[{seg.Start:hh\\:mm\\:ss}] {line}");

                srt.AppendLine(index.ToString(CultureInfo.InvariantCulture));
                srt.AppendLine($"{ToSrtTime(seg.Start)} --> {ToSrtTime(seg.End)}");
                srt.AppendLine(line);
                srt.AppendLine();
                index++;

                if (duration.TotalSeconds > 0)
                {
                    job.Percent = Math.Clamp(seg.End.TotalSeconds / duration.TotalSeconds * 100, 0, 100);
                    JobProgress?.Invoke(job);
                }
            }

            var basePath = Path.Combine(
                Path.GetDirectoryName(job.AudioPath)!,
                Path.GetFileNameWithoutExtension(job.AudioPath));

            job.TextPath = basePath + ".txt";
            job.SrtPath = basePath + ".srt";
            File.WriteAllText(job.TextPath, text.ToString(), Encoding.UTF8);
            File.WriteAllText(job.SrtPath, srt.ToString(), Encoding.UTF8);

            job.Percent = 100;
            job.Status = TranscriptionStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = TranscriptionStatus.Failed;
            job.Error = "已取消";
        }
        catch (Exception ex)
        {
            job.Status = TranscriptionStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            job.FinishedAt = DateTime.Now;
            try { processor?.Dispose(); } catch { }
            try { factory?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 讀 WAV 成 Whisper 要的 16kHz 單聲道 float。我們自己錄的檔本來就是這個格式，
    /// 但仍走一次轉換，讓使用者手動丟進來的其他 WAV 也能轉。
    /// </summary>
    private static (float[] Samples, TimeSpan Duration) LoadAudio(string path)
    {
        using var reader = new AudioFileReader(path);
        var duration = reader.TotalTime;

        ISampleProvider sp = reader;
        if (sp.WaveFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (sp.WaveFormat.Channels > 2)
            sp = new MultiplexingSampleProvider(new[] { sp }, 1);

        if (sp.WaveFormat.SampleRate != SampleRate)
            sp = new WdlResamplingSampleProvider(sp, SampleRate);

        var samples = new List<float>((int)(duration.TotalSeconds * SampleRate) + SampleRate);
        var buffer = new float[SampleRate];
        int read;
        while ((read = sp.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read).ToArray());

        return (samples.ToArray(), duration);
    }

    private static string ToSrtTime(TimeSpan t)
        => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}";

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts.Dispose();
    }
}
