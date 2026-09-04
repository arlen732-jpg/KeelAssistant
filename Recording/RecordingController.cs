using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KeelAssistant.Recording;

/// <summary>錄音狀態機。</summary>
public enum RecordingState
{
    Idle,
    Recording,
    Paused
}

/// <summary>
/// 線上會議錄音引擎：同時擷取「麥克風」與「系統喇叭輸出（WASAPI loopback）」並混成單一 WAV。
///
/// 為何要雙軌：線上會議（Teams/Meet/Zoom）中，對方的聲音是從本機喇叭播出，麥克風錄不到，
/// 只錄麥克風會得到「只有自己在講話」的檔案。
///
/// 時間軸為何以時鐘驅動：<see cref="WasapiLoopbackCapture"/> 在系統完全無聲時「不會」觸發
/// DataAvailable，該段時間是「沒有資料」而非「一段靜音」。若直接把兩軌 buffer 依序混合，
/// 兩軌會逐漸錯位（對方聲音愈錄愈超前）。因此改以 <see cref="Stopwatch"/> 為唯一時鐘：
/// 每次 pump 依「已經過的時間」算出應產生的總樣本數，不足者由 BufferedWaveProvider 的
/// ReadFully 行為自動補 0（靜音），時間軸恆定不漂移。
///
/// 本類別僅由 UI 執行緒操作（經 UiDispatcher），內部 pump 用 WinForms Timer 亦在 UI 執行緒。
/// </summary>
public sealed class RecordingController : IDisposable
{
    /// <summary>
    /// 錄音輸出格式：16kHz / 單聲道。人聲會議夠用，一小時約 115MB，
    /// 而且正好是 Whisper 的原生輸入格式，事後轉逐字稿不必再重取樣。
    /// </summary>
    private const int SampleRate = 16000;

    /// <summary>錄音時長硬上限（分鐘），避免忘了按停止把磁碟錄滿。</summary>
    private const int MaxMinutes = 180;

    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    private readonly System.Windows.Forms.Timer _pump;
    private readonly Stopwatch _clock = new();

    private RecordingForm? _form;

    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _system;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _systemBuffer;
    private PeakSampleProvider? _micPeak;
    private PeakSampleProvider? _systemPeak;
    private IWaveProvider? _mixed;
    private WaveFileWriter? _writer;
    private byte[] _pumpBuffer = new byte[8192];
    private long _samplesWritten;

    /// <summary>狀態或計量變動時觸發，供托盤（圖示變紅）與錄音視窗更新顯示。</summary>
    public event Action? Changed;

    public RecordingState State { get; private set; } = RecordingState.Idle;

    /// <summary>本次錄音的輸出資料夾（{專案路徑}\_Project\Recordings）。</summary>
    public string? OutputDirectory { get; private set; }

    /// <summary>錄製中/剛完成的 WAV 檔完整路徑。</summary>
    public string? CurrentFilePath { get; private set; }

    /// <summary>已錄製長度（不含暫停區段，與檔案實際時長一致）。</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds((double)_samplesWritten / SampleRate);

    /// <summary>麥克風音量峰值 0~1，供畫面音量條顯示（錄音前確認訊號是否進得來）。</summary>
    public float MicLevel => _micPeak?.Peak ?? 0f;

    /// <summary>系統音（喇叭輸出）峰值 0~1。線上會議中這條沒動＝錄不到對方聲音。</summary>
    public float SystemLevel => _systemPeak?.Peak ?? 0f;

    public RecordingController()
    {
        _pump = new System.Windows.Forms.Timer { Interval = 100 };
        _pump.Tick += (_, _) => Pump();
    }

    // ── 視窗 ──────────────────────────────────────────────────────

    /// <summary>
    /// 開啟錄音操作視窗並鎖定輸出資料夾。MCP 只做到這一步，實際開始/暫停/停止由使用者在畫面上操作，
    /// 避免 Claude（或任何能打本機 MCP 埠的程式）靜默開啟麥克風。
    /// </summary>
    public void OpenRecorder(string projectPath)
    {
        var dir = Path.Combine(projectPath, "_Project", "Recordings");
        Directory.CreateDirectory(dir);
        OutputDirectory = dir;

        if (_form is null || _form.IsDisposed)
            _form = new RecordingForm(this);

        _form.ShowRecorder();
        Changed?.Invoke();
    }

    // ── 錄音控制 ──────────────────────────────────────────────────

    /// <summary>開始錄音。label 供檔名辨識（如會議名稱），省略則用「會議」。</summary>
    public void Start(string? label = null)
    {
        if (State != RecordingState.Idle) throw new InvalidOperationException("目前已在錄音中");
        if (string.IsNullOrWhiteSpace(OutputDirectory)) throw new InvalidOperationException("尚未指定輸出資料夾");

        var name = $"{DateTime.Now:yyyyMMdd-HHmm}-{SanitizeFileName(label)}.wav";
        var path = Path.Combine(OutputDirectory, name);

        var mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };

        // 兩個來源都是「可缺席」的：沒麥克風也能純錄系統音，反之亦然。
        (_mic, _micBuffer, _micPeak) = TryOpenSource(() => new WasapiCapture(
            new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)), mixer);
        (_system, _systemBuffer, _systemPeak) = TryOpenSource(() => new WasapiLoopbackCapture(), mixer);

        if (_mic is null && _system is null)
        {
            Cleanup();
            throw new InvalidOperationException("找不到可用的麥克風或系統音訊裝置");
        }

        _mixed = new SampleToWaveProvider16(mixer);
        _writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        CurrentFilePath = path;
        _samplesWritten = 0;

        _mic?.StartRecording();
        _system?.StartRecording();

        _clock.Restart();
        _pump.Start();
        State = RecordingState.Recording;
        Changed?.Invoke();
    }

    /// <summary>暫停：時鐘停走，該區段不寫入檔案（時間軸直接接起來，正是開會暫停要的行為）。</summary>
    public void Pause()
    {
        if (State != RecordingState.Recording) throw new InvalidOperationException("目前不在錄音中");
        _clock.Stop();
        State = RecordingState.Paused;
        Changed?.Invoke();
    }

    /// <summary>繼續錄音。先清空兩軌暫存，避免把暫停期間積壓的舊聲音寫進去。</summary>
    public void Resume()
    {
        if (State != RecordingState.Paused) throw new InvalidOperationException("目前不是暫停狀態");
        _micBuffer?.ClearBuffer();
        _systemBuffer?.ClearBuffer();
        _clock.Start();
        State = RecordingState.Recording;
        Changed?.Invoke();
    }

    /// <summary>停止並關檔，回傳這次錄到的檔案資訊。</summary>
    public RecordingResult Stop()
    {
        if (State == RecordingState.Idle) throw new InvalidOperationException("目前沒有在錄音");

        if (State == RecordingState.Recording) Pump();   // 收尾：把最後一段寫進去

        var result = new RecordingResult(
            CurrentFilePath ?? string.Empty,
            Elapsed,
            File.Exists(CurrentFilePath) ? new FileInfo(CurrentFilePath!).Length : 0);

        Cleanup();
        State = RecordingState.Idle;
        Changed?.Invoke();
        return result;
    }

    // ── 內部 ──────────────────────────────────────────────────────

    /// <summary>
    /// 以時鐘為準補足樣本：算出「到現在為止該產生多少樣本」，差多少就從混音器讀多少。
    /// 無聲時來源不送資料，缺口由 BufferedWaveProvider 的 ReadFully 補 0，因此時間軸不會漂移。
    /// </summary>
    private void Pump()
    {
        if (_writer is null || _mixed is null) return;
        if (State == RecordingState.Paused) return;

        if (_clock.Elapsed.TotalMinutes >= MaxMinutes)
        {
            Stop();
            return;
        }

        long target = (long)(_clock.Elapsed.TotalSeconds * SampleRate);
        int need = (int)Math.Min(target - _samplesWritten, SampleRate);   // 單次上限 1 秒，避免瞬間暴衝
        if (need <= 0) return;

        int bytes = need * 2;   // 16-bit
        if (_pumpBuffer.Length < bytes) _pumpBuffer = new byte[bytes];

        int read = _mixed.Read(_pumpBuffer, 0, bytes);
        if (read <= 0) return;

        _writer.Write(_pumpBuffer, 0, read);
        _samplesWritten += read / 2;
        Changed?.Invoke();
    }

    /// <summary>
    /// 建立一路擷取來源並接進混音器：擷取 → 暫存 → 轉單聲道 → 重取樣至 16kHz → 峰值計量。
    /// 裝置不存在（如無麥克風）時回傳三個 null，讓另一路仍可單獨錄。
    /// </summary>
    private static (T?, BufferedWaveProvider?, PeakSampleProvider?) TryOpenSource<T>(
        Func<T> factory, MixingSampleProvider mixer) where T : WasapiCapture
    {
        T capture;
        try { capture = factory(); }
        catch { return (null, null, null); }

        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true   // pump 慢於裝置時丟舊資料，不讓記憶體無限長
        };
        capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        ISampleProvider sp = buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (sp.WaveFormat.Channels > 2)
            sp = new MultiplexingSampleProvider(new[] { sp }, 1);   // 多聲道裝置只取第一聲道

        if (sp.WaveFormat.SampleRate != SampleRate)
            sp = new WdlResamplingSampleProvider(sp, SampleRate);

        var peak = new PeakSampleProvider(sp);
        mixer.AddMixerInput(peak);
        return (capture, buffer, peak);
    }

    private void Cleanup()
    {
        _pump.Stop();
        _clock.Reset();

        try { _mic?.StopRecording(); } catch { }
        try { _system?.StopRecording(); } catch { }
        try { _mic?.Dispose(); } catch { }
        try { _system?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }

        _mic = null;
        _system = null;
        _micBuffer = null;
        _systemBuffer = null;
        _micPeak = null;
        _systemPeak = null;
        _mixed = null;
        _writer = null;
    }

    /// <summary>清掉檔名非法字元，空白則用預設名。</summary>
    private static string SanitizeFileName(string? label)
    {
        var s = string.IsNullOrWhiteSpace(label) ? "會議" : label.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Length > 60 ? s[..60] : s;
    }

    public void Dispose()
    {
        if (State != RecordingState.Idle)
        {
            // 結束程式時仍在錄音：先關檔保住已錄內容，不讓 WAV header 停留在未完成狀態。
            try { Stop(); } catch { }
        }
        Cleanup();
        _pump.Dispose();

        if (_form is not null && !_form.IsDisposed)
        {
            var f = _form;
            _form = null;
            try { f.Dispose(); } catch { }
        }
    }
}

/// <summary>一次錄音的結果摘要。</summary>
public sealed record RecordingResult(string FilePath, TimeSpan Duration, long SizeBytes);

/// <summary>
/// 透通的取樣包裝：讀取時順手記下這一段的音量峰值，供畫面音量條顯示。
/// 放在重取樣之後、混音之前，因此格式無關，兩軌都能各自計量。
/// </summary>
internal sealed class PeakSampleProvider(ISampleProvider source) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>最近一次讀取區塊的峰值（0~1）。</summary>
    public float Peak { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        float peak = 0f;
        for (int i = 0; i < read; i++)
        {
            float abs = Math.Abs(buffer[offset + i]);
            if (abs > peak) peak = abs;
        }
        Peak = peak;
        return read;
    }
}
