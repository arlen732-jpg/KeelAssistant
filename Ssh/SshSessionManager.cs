using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KeelAssistant.Api;
using KeelAssistant.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace KeelAssistant.Ssh;

/// <summary>
/// SSH 連線與遠端指令執行的核心。每個 sshId 對應一條<b>持續的</b> shell（PTY），
/// cwd 與環境變數會跨指令延續——這是刻意的設計選擇，讓操作貼近人在終端機前的直覺。
///
/// ## 怎麼知道一條指令跑完了
///
/// PTY 是一條連續的位元組流，不像 <c>SshCommand</c> 有明確的 ExitStatus，因此必須自己框。
/// 作法是把哨符放進 <b>PS1</b>（而非在指令後面串 printf）：
///
///   PS1='\n[keel] \u@\h:\w\n{MARKER}$?|${PWD}{MARKER}\n'
///
/// 好處是哨符只會出現在提示符，指令本身完全不必包裝——包裝過的指令會被 PTY 回音，
/// 在畫面上留下一長串雜訊，而且要從輸出裡剝掉它非常容易出錯。
/// 每個 session 的 MARKER 含隨機碼，避免指令輸出剛好含有哨符字串造成誤判。
///
/// ## 已知邊界
///
/// 指令若啟動互動式程式（nano、top、apt 的設定檔衝突對話框），就不會回到提示符，
/// 哨符永遠等不到。因此<b>逾時是必要機制而非保險</b>：逾時後送 Ctrl+C 並回報 timedOut，
/// 同時終端視窗允許 Arlen 直接接手操作——這是唯一的出路，故該視窗不可做成唯讀日誌。
///
/// ## 併發
///
/// MCP 是 Stateless=true，多個 Claude Code session 與 SubAgent 共用同一行程，沒有身分可區分。
/// 持續 shell 是共享狀態，因此：每個 session 一個 <see cref="SemaphoreSlim"/> 序列化執行，
/// 且每次回傳都帶上 cwd，讓「狀態被別人動過」這件事是看得見的，而不是隱性耦合。
/// </summary>
public sealed class SshSessionManager : IDisposable
{
    private const int QueueWaitMs = 120_000;     // 排隊等候上限（含前一條指令的執行時間）

    private readonly SshConnectionStore _store;
    private readonly AppSettings _settings;
    private readonly UiDispatcher _ui;
    private readonly ConcurrentDictionary<string, SshSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _idleTimer;
    private bool _disposed;

    public SshSessionManager(SshConnectionStore store, AppSettings settings, SynchronizationContext uiContext)
    {
        _store = store;
        _settings = settings;
        _ui = new UiDispatcher(uiContext);

        // 閒置檢查跑在執行緒池的 Timer（非 WinForms Timer）：斷線動作不需要 UI 執行緒，
        // 沒必要為此佔用它。
        _idleTimer = new System.Threading.Timer(_ => SweepIdle(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public bool Enabled => _settings.SshEnabled;

    /// <summary>模組停用時給呼叫端的說明；啟用時為 null。</summary>
    public string? DisabledReason => _settings.SshEnabled
        ? null
        : "SSH 模組未啟用（appsettings.json 的 SshEnabled = false）。";

    // ── 對外操作 ──────────────────────────────────────────────────

    /// <summary>
    /// 在遠端執行一條指令並等待完成。呼叫端須<b>先</b>取得人工確認再呼叫本方法——
    /// 確認邏輯放在 MCP 工具層，這裡只負責執行。
    /// </summary>
    public async Task<SshExecResult> ExecAsync(string sshId, string command, int timeoutSec)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("缺少 command");

        // 0（或負數）表示「用設定檔的預設值」——MCP 工具的參數預設值必須是編譯期常數，
        // 拿不到 appsettings.json，因此把哨兵值的解讀放在這裡
        if (timeoutSec <= 0) timeoutSec = _settings.SshDefaultTimeoutSeconds;

        var session = await EnsureSessionAsync(sshId).ConfigureAwait(false);

        if (!await session.Gate.WaitAsync(QueueWaitMs).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"sshId「{sshId}」忙碌中（已等候 {QueueWaitMs / 1000} 秒仍未輪到），請稍後再試。");

        try
        {
            return await session.ExecuteAsync(command, timeoutSec, _settings.SshMaxOutputChars)
                                .ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>
    /// 把<b>已連線</b>的終端視窗帶到前景。不會建立連線，因此可以安全地直接在 UI 執行緒呼叫
    /// （托盤選單用）；未連線時回 false。
    /// </summary>
    public bool ShowTerminalIfConnected(string sshId)
    {
        if (!_sessions.TryGetValue(sshId, out var session) || !session.Connected) return false;
        session.ShowTerminal();
        return true;
    }

    /// <summary>建立（或沿用）連線並把終端視窗帶到前景，供 Arlen 手動接手操作。</summary>
    public async Task OpenTerminalAsync(string sshId)
    {
        EnsureEnabled();
        var session = await EnsureSessionAsync(sshId).ConfigureAwait(false);
        session.ShowTerminal();
    }

    /// <summary>目前狀態。未連線時不會主動建立連線。</summary>
    public object Status(string sshId)
    {
        EnsureEnabled();
        var entry = _store.Resolve(sshId);

        if (!_sessions.TryGetValue(entry.Id, out var session) || !session.Connected)
            return new { sshId = entry.Id, connected = false, cwd = (string?)null, idleSeconds = (int?)null };

        return new
        {
            sshId = entry.Id,
            connected = true,
            cwd = session.Cwd,
            idleSeconds = (int)(DateTime.Now - session.LastActivity).TotalSeconds
        };
    }

    /// <summary>
    /// 目前的遠端工作目錄；未連線時為空字串。供確認視窗顯示「這條指令將在哪裡執行」——
    /// 持續 shell 的 cwd 是隱含狀態，讓它在按下允許之前就看得見。
    /// </summary>
    public string CurrentCwd(string sshId) =>
        _sessions.TryGetValue(sshId, out var session) && session.Connected ? session.Cwd : "";

    /// <summary>主動斷線並關閉終端視窗，一併清掉持續 shell 累積的 cwd 與環境變數。</summary>
    public bool Close(string sshId)
    {
        if (!_sessions.TryRemove(sshId, out var session)) return false;
        session.Dispose();
        return true;
    }

    /// <summary>目前已連線的 sshId 清單（供托盤選單顯示，不對 Claude 開放）。</summary>
    public IReadOnlyList<string> ConnectedIds() =>
        _sessions.Where(kv => kv.Value.Connected).Select(kv => kv.Key).OrderBy(x => x).ToList();

    // ── 連線建立 ──────────────────────────────────────────────────

    private void EnsureEnabled()
    {
        if (!_settings.SshEnabled)
            throw new InvalidOperationException(DisabledReason!);
    }

    private async Task<SshSession> EnsureSessionAsync(string sshId)
    {
        var entry = _store.Resolve(sshId);

        if (_sessions.TryGetValue(entry.Id, out var existing))
        {
            if (existing.Connected) return existing;
            // 斷線的殘骸先清掉再重建，否則會拿到一個永遠不會有回應的 stream
            _sessions.TryRemove(entry.Id, out _);
            existing.Dispose();
        }

        var session = new SshSession(entry, _store, _settings, _ui);
        try
        {
            await session.ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        _sessions[entry.Id] = session;
        return session;
    }

    private void SweepIdle()
    {
        if (_disposed) return;

        var limit = _settings.SshIdleShutdownMinutes;
        if (limit <= 0) return;

        foreach (var kv in _sessions.ToArray())
        {
            if ((DateTime.Now - kv.Value.LastActivity).TotalMinutes < limit) continue;
            if (!_sessions.TryRemove(kv.Key, out var session)) continue;
            session.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _idleTimer.Dispose(); } catch { }
        foreach (var kv in _sessions.ToArray())
        {
            if (_sessions.TryRemove(kv.Key, out var session)) session.Dispose();
        }
    }
}

/// <summary>
/// 單一 sshId 的連線狀態：SshClient + 持續 ShellStream + 終端視窗 + 讀取執行緒。
/// 生命週期由 <see cref="SshSessionManager"/> 管理，外部不直接建立。
/// </summary>
internal sealed class SshSession : IDisposable
{
    /// <summary>哨符前綴。顯示過濾與完成偵測都靠它，不可與遠端可能出現的字串重複。</summary>
    private const string MarkerPrefix = "__KEEL_";

    private readonly SshConnectionEntry _entry;
    private readonly SshConnectionStore _store;
    private readonly AppSettings _settings;
    private readonly UiDispatcher _ui;
    private readonly string _marker;
    private readonly Regex _promptRegex;

    private SshClient? _client;
    private ShellStream? _stream;
    private Thread? _reader;
    private SshTerminalForm? _terminal;
    private volatile bool _closing;

    // 擷取狀態：只有在執行指令期間才作用，由 Gate 保證同時只有一條指令
    private readonly object _captureGate = new();
    private StringBuilder? _capture;
    private TaskCompletionSource<bool>? _captureDone;

    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly List<byte> _displayBuf = new();

    public SemaphoreSlim Gate { get; } = new(1, 1);
    public DateTime LastActivity { get; private set; } = DateTime.Now;
    public string Cwd { get; private set; } = "";
    public bool Connected => _client?.IsConnected == true && _stream is not null && !_closing;

    public SshSession(SshConnectionEntry entry, SshConnectionStore store, AppSettings settings, UiDispatcher ui)
    {
        _entry = entry;
        _store = store;
        _settings = settings;
        _ui = ui;
        _marker = $"{MarkerPrefix}{Guid.NewGuid().ToString("N")[..8]}__";

        // 提示符格式：{MARKER}{exit code}|{cwd}{MARKER}
        _promptRegex = new Regex($"{Regex.Escape(_marker)}(-?\\d+)\\|(.*?){Regex.Escape(_marker)}",
            RegexOptions.Compiled | RegexOptions.Singleline);
    }

    // ── 連線 ──────────────────────────────────────────────────────

    public async Task ConnectAsync()
    {
        var info = BuildConnectionInfo();
        _client = new SshClient(info);
        _client.HostKeyReceived += OnHostKeyReceived;

        // SSH.NET 的 Connect 是同步阻塞；丟到執行緒池，別佔住呼叫端
        await Task.Run(() => _client.Connect()).ConfigureAwait(false);

        _stream = _client.CreateShellStream(
            "xterm-256color",
            (uint)Math.Clamp(_settings.SshTerminalCols, 40, 500),
            (uint)Math.Clamp(_settings.SshTerminalRows, 10, 200),
            0, 0, 64 * 1024);

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"ssh-read-{_entry.Id}" };
        _reader.Start();

        await EnsureTerminalAsync().ConfigureAwait(false);
        await InitShellAsync().ConfigureAwait(false);
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        var secret = _store.RevealSecret(_entry);
        AuthenticationMethod[] auths;

        if (_entry.AuthMode == SshAuthMode.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(_entry.KeyPath) || !File.Exists(_entry.KeyPath))
                throw new InvalidOperationException($"找不到私鑰檔：{_entry.KeyPath}");

            var keyFile = string.IsNullOrEmpty(secret)
                ? new PrivateKeyFile(_entry.KeyPath)
                : new PrivateKeyFile(_entry.KeyPath, secret);

            auths = new AuthenticationMethod[]
            {
                new PrivateKeyAuthenticationMethod(_entry.User, keyFile)
            };
        }
        else
        {
            // 兩種都掛上：多數 Linux 發行版的密碼登入實際走 keyboard-interactive，
            // 只給 PasswordAuthenticationMethod 會出現「密碼明明正確卻拒絕」的假象。
            var ki = new KeyboardInteractiveAuthenticationMethod(_entry.User);
            ki.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = secret;
            };

            auths = new AuthenticationMethod[]
            {
                new PasswordAuthenticationMethod(_entry.User, secret),
                ki
            };
        }

        return new ConnectionInfo(_entry.Host, _entry.Port, _entry.User, auths)
        {
            Timeout = TimeSpan.FromSeconds(SshSessionManagerConstants.ConnectTimeoutSec)
        };
    }

    /// <summary>
    /// 主機金鑰驗證（TOFU）。首次連線記下指紋，之後每次比對。
    /// 指紋不符一律拒絕，<b>不詢問</b>——這種提示跳出來時人幾乎都會直接按確定，
    /// 問了等於沒問，反而讓人以為有把關。
    /// </summary>
    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var fingerprint = Convert.ToBase64String(SHA256.HashData(e.HostKey));

        if (string.IsNullOrEmpty(_entry.HostKeyFingerprint))
        {
            e.CanTrust = true;
            _store.TrustHostKey(_entry.Id, fingerprint);
            _entry.HostKeyFingerprint = fingerprint;
            return;
        }

        e.CanTrust = string.Equals(_entry.HostKeyFingerprint, fingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// 設定 PS1 哨符並等待第一個提示符出現，確認 shell 已就緒。
    /// PS2 清空、PROMPT_COMMAND 取消，避免它們插入額外輸出干擾比對。
    /// </summary>
    private async Task InitShellAsync()
    {
        var init =
            "unset PROMPT_COMMAND\n" +
            "export PS2=''\n" +
            "export TERM=xterm-256color\n" +
            $"export PS1='\\n[keel] \\u@\\h:\\w\\n{_marker}$?|${{PWD}}{_marker}\\n'\n";

        var done = BeginCapture();
        WriteRaw(Encoding.UTF8.GetBytes(init));

        var finished = await Task.WhenAny(done, Task.Delay(SshSessionManagerConstants.PromptSyncMs))
                                 .ConfigureAwait(false);
        EndCapture(out _);

        if (finished != done)
            throw new InvalidOperationException(
                $"連上 {_entry.Display} 後等不到 shell 提示符（{SshSessionManagerConstants.PromptSyncMs / 1000} 秒）。" +
                "遠端登入 shell 可能不是 bash，或有互動式的登入訊息尚未結束。");
    }

    // ── 指令執行 ──────────────────────────────────────────────────

    public async Task<SshExecResult> ExecuteAsync(string command, int timeoutSec, int maxChars)
    {
        if (!Connected) throw new InvalidOperationException($"sshId「{_entry.Id}」的連線已中斷。");

        LastActivity = DateTime.Now;
        var sw = Stopwatch.StartNew();

        // 在終端上標示這條指令的來源，讓畫面上分得出「Claude 下的」與「Arlen 自己打的」
        ShowTerminal();
        _ui.Send(() => _terminal?.WriteLocalLine($"[Keel] $ {command}"));

        var done = BeginCapture();
        WriteRaw(Encoding.UTF8.GetBytes(command.TrimEnd('\n') + "\n"));

        var finished = await Task.WhenAny(done, Task.Delay(Math.Clamp(timeoutSec, 1, 1800) * 1000))
                                 .ConfigureAwait(false);

        var timedOut = finished != done;
        if (timedOut)
        {
            // Ctrl+C：指令若還在跑就中止它。互動式程式（nano/top）不見得吃這招，
            // 那時只能由 Arlen 在終端視窗裡自己處理。
            try { WriteRaw(new byte[] { 0x03 }); } catch { }
            await Task.Delay(300).ConfigureAwait(false);
        }

        EndCapture(out var raw);
        sw.Stop();
        LastActivity = DateTime.Now;

        var (output, exitCode, cwd) = ParseCapture(raw, command);
        if (!string.IsNullOrEmpty(cwd)) Cwd = cwd;

        var truncated = output.Length > maxChars;
        if (truncated) output = output[..maxChars];

        return new SshExecResult
        {
            Output = output,
            ExitCode = timedOut ? null : exitCode,
            Cwd = Cwd,
            Truncated = truncated,
            TimedOut = timedOut,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// 從擷取到的原始文字取出「指令輸出／離開碼／cwd」三者。
    ///
    /// 擷取內容的形狀是：{指令的 PTY 回音}\n{實際輸出}{提示符哨符}。
    /// 回音要剝掉——它是終端行為而非指令產出，留著會讓 Claude 以為輸出多了一行。
    /// </summary>
    private (string Output, int? ExitCode, string Cwd) ParseCapture(string raw, string command)
    {
        int? exitCode = null;
        var cwd = "";

        var match = _promptRegex.Match(raw);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var code)) exitCode = code;
            cwd = match.Groups[2].Value.Trim();
            raw = raw[..match.Index];
        }

        // 剝掉哨符所在提示符的前導裝飾行（PS1 的 "[keel] user@host:path"）
        var text = StripAnsi(raw).Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = text.Split('\n').ToList();

        // PS1 開頭的 \n 是在「上一輪」比對到哨符之後才送達的，因此每次擷取都會以空行起頭。
        // 這是提示符的裝飾，不是指令輸出。
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);

        // 接著才可能是指令回音，內容與送出的指令相同時才剝，避免誤刪真正的輸出
        var firstLineOfCommand = command.TrimEnd('\n').Split('\n')[0].Trim();
        if (lines.Count > 0 && lines[0].Trim() == firstLineOfCommand) lines.RemoveAt(0);

        // 尾端 PS1 的人類可讀行（"[keel] ..."）不屬於指令輸出
        while (lines.Count > 0 && (lines[^1].StartsWith("[keel] ") || lines[^1].Trim().Length == 0))
            lines.RemoveAt(lines.Count - 1);

        // 首尾空行一律去除。上面的逐行剝除已處理已知的幾種來源（PS1 前導換行、指令回音、
        // 提示符裝飾行），但實測仍會殘留一個開頭換行，且來源未能完全定位。
        // 指令輸出的首尾空行沒有語意，統一在出口正規化，比在入口猜測來源可靠。
        return (string.Join("\n", lines).Trim('\n'), exitCode, cwd);
    }

    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[@-Z\\-_]",
        RegexOptions.Compiled);

    /// <summary>
    /// 移除 ANSI 控制序列。終端視窗需要它們才有色彩，但回給 Claude 的文字留著只是雜訊，
    /// 還會吃掉 context 額度。
    /// </summary>
    private static string StripAnsi(string text) => AnsiRegex.Replace(text, "");

    // ── 擷取（只在執行指令期間作用）────────────────────────────────

    private Task<bool> BeginCapture()
    {
        lock (_captureGate)
        {
            _capture = new StringBuilder();
            _captureDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _captureDone.Task;
        }
    }

    private void EndCapture(out string raw)
    {
        lock (_captureGate)
        {
            raw = _capture?.ToString() ?? "";
            _capture = null;
            _captureDone = null;
        }
    }

    // ── 讀取迴圈 ──────────────────────────────────────────────────

    /// <summary>
    /// 持續讀取 ShellStream。刻意用阻塞式 Read 的專屬執行緒，而非 DataReceived 事件——
    /// 事件參數的型別在 SSH.NET 各版本間變動過，Stream.Read 則是穩定介面。
    /// </summary>
    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        var stream = _stream;
        if (stream is null) return;

        while (!_closing)
        {
            int n;
            try { n = stream.Read(buffer, 0, buffer.Length); }
            catch { break; }          // 連線中斷或已釋放
            if (n <= 0) break;

            var chunk = new byte[n];
            Array.Copy(buffer, chunk, n);

            PushToTerminal(chunk);
            PushToCapture(chunk);
        }

        _closing = true;
    }

    private void PushToCapture(byte[] chunk)
    {
        // 解碼器跨 chunk 保留半個 UTF-8 字元的狀態，避免中文被切成兩半變成亂碼
        var chars = new char[_decoder.GetCharCount(chunk, 0, chunk.Length, false)];
        _decoder.GetChars(chunk, 0, chunk.Length, chars, 0, false);
        var text = new string(chars);

        lock (_captureGate)
        {
            if (_capture is null) return;
            _capture.Append(text);

            // 提示符出現即代表上一條指令已結束
            if (_captureDone is not null && _promptRegex.IsMatch(_capture.ToString()))
                _captureDone.TrySetResult(true);
        }
    }

    /// <summary>
    /// 把資料送到終端視窗，途中濾掉哨符所在的那一行。
    ///
    /// 過濾必須是串流式的：進度條只用 \r 不換行，若為了找換行而整段緩衝，畫面會一頓一頓。
    /// 作法是只保留「有可能是哨符開頭」的尾段位元組（最多 <see cref="MarkerPrefix"/> 長度），
    /// 其餘立刻送出——使用者打字時不會因此延遲回顯。
    /// </summary>
    private void PushToTerminal(byte[] chunk)
    {
        var emit = new List<byte>();

        lock (_displayBuf)
        {
            _displayBuf.AddRange(chunk);

            int idx;
            while ((idx = _displayBuf.IndexOf((byte)'\n')) >= 0)
            {
                var line = _displayBuf.GetRange(0, idx + 1).ToArray();
                _displayBuf.RemoveRange(0, idx + 1);
                if (!ContainsMarkerPrefix(line)) emit.AddRange(line);
            }

            // 未完成的一行：留下可能構成哨符開頭的尾段，其餘先送出
            var keep = ContainsMarkerPrefix(_displayBuf.ToArray())
                ? _displayBuf.Count                       // 已經看到哨符開頭，整段扣住等換行
                : LongestMarkerPrefixSuffix(_displayBuf);

            if (_displayBuf.Count > keep)
            {
                emit.AddRange(_displayBuf.GetRange(0, _displayBuf.Count - keep));
                _displayBuf.RemoveRange(0, _displayBuf.Count - keep);
            }
        }

        if (emit.Count == 0) return;

        var data = emit.ToArray();
        var form = _terminal;
        if (form is null) return;

        try
        {
            // BeginInvoke 而非 Send：這裡跑在讀取執行緒，用同步等待會讓遠端輸出的節奏
            // 受 UI 執行緒忙碌程度牽制，快速輸出時尤其明顯
            if (form.IsHandleCreated) form.BeginInvoke(() => form.Write(data));
        }
        catch { /* 視窗正在銷毀 */ }
    }

    private static bool ContainsMarkerPrefix(byte[] data)
    {
        var prefix = Encoding.ASCII.GetBytes(MarkerPrefix);
        for (int i = 0; i + prefix.Length <= data.Length; i++)
        {
            var hit = true;
            for (int j = 0; j < prefix.Length; j++)
            {
                if (data[i + j] == prefix[j]) continue;
                hit = false;
                break;
            }
            if (hit) return true;
        }
        return false;
    }

    /// <summary>緩衝尾端有多少位元組可能是哨符前綴的開頭（0 表示完全不可能）。</summary>
    private static int LongestMarkerPrefixSuffix(List<byte> buf)
    {
        var prefix = Encoding.ASCII.GetBytes(MarkerPrefix);
        var max = Math.Min(prefix.Length - 1, buf.Count);

        for (int len = max; len > 0; len--)
        {
            var ok = true;
            for (int j = 0; j < len; j++)
            {
                if (buf[buf.Count - len + j] == prefix[j]) continue;
                ok = false;
                break;
            }
            if (ok) return len;
        }
        return 0;
    }

    private void WriteRaw(byte[] data)
    {
        var stream = _stream ?? throw new InvalidOperationException("SSH 連線尚未建立。");
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    // ── 終端視窗 ──────────────────────────────────────────────────

    private async Task EnsureTerminalAsync()
    {
        var init = _ui.Send(() =>
        {
            if (_terminal is { IsDisposed: false }) return Task.CompletedTask;

            _terminal = new SshTerminalForm(
                $"{_entry.Id}（{_entry.Display}）",
                Math.Clamp(_settings.SshTerminalCols, 40, 500),
                Math.Clamp(_settings.SshTerminalRows, 10, 200),
                Math.Clamp(_settings.SshTerminalFontSize, 8, 32),
                // 使用者在終端裡打的字直接進遠端；這是哨符失效時的唯一出路
                onInput: bytes =>
                {
                    LastActivity = DateTime.Now;
                    try { WriteRaw(bytes); } catch { }
                });

            _terminal.Show();
            return _terminal.InitAsync();
        });

        var finished = await Task.WhenAny(init, Task.Delay(SshSessionManagerConstants.TerminalInitMs))
                                 .ConfigureAwait(false);
        if (finished != init)
            throw new InvalidOperationException("SSH 終端視窗啟動逾時（WebView2 未就緒）。");

        await init.ConfigureAwait(false);
    }

    public void ShowTerminal() => _ui.Send(() =>
    {
        if (_terminal is not { IsDisposed: false }) return;
        if (!_terminal.Visible) _terminal.Show();
        _terminal.Activate();
    });

    public void Dispose()
    {
        _closing = true;

        try { _stream?.Dispose(); } catch { }
        try
        {
            if (_client is not null)
            {
                _client.HostKeyReceived -= OnHostKeyReceived;
                _client.Disconnect();
                _client.Dispose();
            }
        }
        catch { }

        try { _ui.Send(() => _terminal?.ForceClose()); } catch { }
        try { Gate.Dispose(); } catch { }

        _stream = null;
        _client = null;
        _terminal = null;
    }
}

/// <summary>兩個類別共用的逾時常數（C# 不允許巢狀存取外部類別的 private const）。</summary>
internal static class SshSessionManagerConstants
{
    public const int ConnectTimeoutSec = 15;
    public const int PromptSyncMs = 10_000;
    public const int TerminalInitMs = 30_000;
}
