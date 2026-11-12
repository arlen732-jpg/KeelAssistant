using System.ComponentModel;
using KeelAssistant.Alarm;
using KeelAssistant.Calendar;
using KeelAssistant.Db;
using KeelAssistant.Models;
using KeelAssistant.Recording;
using KeelAssistant.Sandbox;
// 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - Start
using KeelAssistant.Ssh;
// 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end
using KeelAssistant.Time;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KeelAssistant.Api;

/// <summary>
/// Keel Assistant 對 Claude 開放的 MCP 工具集。
/// 取代原 REST 路由（HttpApiServer）：一條路由對應一個工具，參數帶 schema 與說明。
/// 需操作 WinForms 視窗 / Outlook COM 者，透過 <see cref="UiDispatcher"/> 切回 UI 執行緒。
/// 參數驗證失敗一律拋例外，由 MCP 框架回報為工具錯誤。
/// </summary>
[McpServerToolType]
public static class KeelTools
{
    // ── 時間 ──────────────────────────────────────────────────────

    [McpServerTool(Name = "get_time"), Description("取得當下精準時間、星期與時段（清晨/上午/中午/下午/傍晚/深夜）。")]
    public static object GetTime() => TimeService.Now();

    // ── 鬧鐘 / 計時 ────────────────────────────────────────────────

    [McpServerTool(Name = "list_tasks"), Description("列出目前所有待命的鬧鐘與計時任務。")]
    public static IReadOnlyList<ScheduledTask> ListTasks(AlarmEngine alarm) => alarm.List();

    [McpServerTool(Name = "set_alarm"), Description("設定絕對時間鬧鐘。at 接受 HH:mm（當天，已過則排明天）或完整日期時間字串。")]
    public static ScheduledTask SetAlarm(
        AlarmEngine alarm,
        [Description("時間：HH:mm 或 yyyy/MM/dd HH:mm")] string at,
        [Description("鬧鐘標籤")] string? label = null,
        [Description("是否循環響鈴，預設 true")] bool loop = true)
    {
        if (string.IsNullOrWhiteSpace(at)) throw new ArgumentException("缺少 at（HH:mm 或完整日期時間）");
        if (!TryParseFireAt(at, out var fireAt)) throw new ArgumentException($"無法解析時間：{at}");
        return alarm.AddAlarm(fireAt, (label ?? "鬧鐘").Trim(), loop);
    }

    // 2026/11/10 - Arlen - 移除音樂模組：拿掉 music-stop 動作，只剩響鈴提醒 - Start
    [McpServerTool(Name = "set_timer"), Description("設定相對計時。minutes 分鐘後響鈴提醒。")]
    public static ScheduledTask SetTimer(
        AlarmEngine alarm,
        [Description("幾分鐘後觸發，需 > 0")] double minutes,
        [Description("任務標籤")] string? label = null)
    {
        if (minutes <= 0) throw new ArgumentException("minutes 需 > 0");
        return alarm.AddRelative(minutes, ScheduledAction.Alarm, (label ?? "計時提醒").Trim());
    }
    // 2026/11/10 - Arlen - 移除音樂模組 - end

    [McpServerTool(Name = "stop_alarm"), Description("停止目前正在響的鬧鈴。")]
    public static object StopAlarm(AlarmEngine alarm, UiDispatcher ui)
    {
        ui.Send(() => alarm.StopRinging());
        return new { stopped = true };
    }

    [McpServerTool(Name = "cancel_task"), Description("依任務 id 取消單一鬧鐘或計時任務。")]
    public static object CancelTask(AlarmEngine alarm, [Description("任務 id")] string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("缺少 id");
        var ok = alarm.Cancel(id);
        if (!ok) throw new InvalidOperationException("找不到該任務");
        return new { cancelled = true, id };
    }

    // ── 即時通知 ─────────────────────────────────────────────────

    [McpServerTool(Name = "notify"), Description("立即響鈴一次並彈出訊息視窗，用於通知 Arlen 注意查看 Claude 的重要回覆。")]
    public static object Notify(
        AlarmEngine alarm,
        UiDispatcher ui,
        [Description("通知訊息，例如：「有重要事項請確認」")] string? message = null)
    {
        var label = string.IsNullOrWhiteSpace(message) ? "Keel 有重要訊息！" : message.Trim();
        ui.Send(() => alarm.Notify(label));
        return new { notified = true, message = label };
    }

    // 2026/11/10 - Arlen - 移除音樂工具（play_music/stop_music），已不使用 - Start
    // 2026/11/10 - Arlen - 移除音樂工具 - end

    // ── 會議錄音 ──────────────────────────────────────────────────

    [McpServerTool(Name = "open_recorder"), Description("開啟會議錄音視窗（同時錄麥克風與系統喇叭聲音，適用線上會議）。僅開啟畫面，實際開始/暫停/停止由 Arlen 在視窗上操作。錄音檔存到 {projectPath}\\_Project\\Recordings\\。")]
    public static object OpenRecorder(
        RecordingController recording,
        UiDispatcher ui,
        [Description("專案根目錄絕對路徑（錄音檔會存到其下的 _Project\\Recordings）")] string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("缺少 projectPath");
        var path = projectPath.Trim();
        if (!Directory.Exists(path)) throw new ArgumentException($"找不到專案目錄：{path}");

        ui.Send(() => recording.OpenRecorder(path));
        return new { opened = true, outputDir = recording.OutputDirectory };
    }

    [McpServerTool(Name = "get_recording_status"), Description("查詢錄音狀態：idle（未錄音）／recording（錄音中）／paused（暫停），以及已錄長度與檔案路徑。")]
    public static object GetRecordingStatus(RecordingController recording)
        => new
        {
            state = recording.State.ToString().ToLowerInvariant(),
            elapsed = recording.Elapsed.ToString(@"hh\:mm\:ss"),
            filePath = recording.CurrentFilePath,
            outputDir = recording.OutputDirectory
        };

    // ── 逐字稿（Whisper 事後轉檔）──────────────────────────────────

    [McpServerTool(Name = "list_recordings"), Description("列出專案下已錄好的會議錄音檔（{projectPath}\\_Project\\Recordings\\*.wav）。一場會議最多三件同名檔案：.wav（音訊）、.txt（逐字稿）、.md（會議當下的人工筆記，含決議與重點）。產會議紀錄時 .txt 與 .md 應一起讀。")]
    public static object ListRecordings(
        [Description("專案根目錄絕對路徑")] string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("缺少 projectPath");
        var dir = Path.Combine(projectPath.Trim(), "_Project", "Recordings");
        if (!Directory.Exists(dir)) return Array.Empty<object>();

        return new DirectoryInfo(dir)
            .GetFiles("*.wav")
            .OrderByDescending(f => f.CreationTime)
            .Select(f =>
            {
                var textPath = Path.ChangeExtension(f.FullName, ".txt");
                var notesPath = Path.ChangeExtension(f.FullName, ".md");
                return new
                {
                    fileName = f.Name,
                    filePath = f.FullName,
                    sizeMb = Math.Round(f.Length / 1024.0 / 1024.0, 1),
                    recordedAt = f.CreationTime.ToString("yyyy\\/MM\\/dd HH:mm"),
                    transcribed = File.Exists(textPath),
                    transcriptPath = File.Exists(textPath) ? textPath : null,
                    notesPath = File.Exists(notesPath) ? notesPath : null
                };
            })
            .ToArray();
    }

    [McpServerTool(Name = "transcribe_recording"), Description("把錄音檔轉成正式逐字稿（Whisper medium，繁體中文、含標點、支援中英混雜）。立即回傳 jobId 不等待完成——一小時會議約需 40~60 分鐘，請之後用 get_transcription_status 查進度。完成後會在錄音檔同目錄產生同名的 .txt 與 .srt。")]
    public static object TranscribeRecording(
        TranscriptionService transcription,
        [Description("錄音檔完整路徑（可先用 list_recordings 取得）")] string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("缺少 filePath");

        var job = transcription.Enqueue(filePath.Trim());
        return new { jobId = job.Id, status = job.Status.ToString().ToLowerInvariant(), audioPath = job.AudioPath };
    }

    [McpServerTool(Name = "get_transcription_status"), Description("查詢逐字稿轉檔進度。省略 jobId 則列出所有轉檔工作（最新在前）。狀態：queued／running／completed／failed。")]
    public static object GetTranscriptionStatus(
        TranscriptionService transcription,
        [Description("轉檔工作 id，省略則列出全部")] string? jobId = null)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            var job = transcription.Get(jobId.Trim())
                ?? throw new InvalidOperationException("找不到該轉檔工作");
            return Describe(job);
        }

        return transcription.List().Select(Describe).ToArray();
    }

    // ── Whisper 模型（按需下載）──────────────────────────────────

    [McpServerTool(Name = "get_model_status"), Description("查詢 Whisper 逐字稿模型（medium，約 1.5GB）的安裝狀態與下載進度。模型未安裝時無法轉逐字稿，但不影響錄音。")]
    public static object GetModelStatus(ModelDownloader models)
    {
        var s = models.State;
        return new
        {
            installed = s.Installed,
            downloading = s.Downloading,
            percent = Math.Round(s.Percent, 1),
            path = s.Path,
            error = s.Error
        };
    }

    [McpServerTool(Name = "download_whisper_model"), Description("下載 Whisper 逐字稿模型（medium，約 1.5GB，來源 Hugging Face）。立即回傳不等待完成，請用 get_model_status 查進度。已安裝或下載中則不重複下載。")]
    public static object DownloadWhisperModel(ModelDownloader models)
    {
        var state = models.Start();
        return new { installed = state.Installed, downloading = state.Downloading, path = state.Path };
    }

    /// <summary>把轉檔工作攤平成 MCP 回傳用的匿名物件。</summary>
    private static object Describe(TranscriptionJob job) => new
    {
        jobId = job.Id,
        status = job.Status.ToString().ToLowerInvariant(),
        percent = Math.Round(job.Percent, 1),
        audioPath = job.AudioPath,
        textPath = job.TextPath,
        srtPath = job.SrtPath,
        error = job.Error
    };

    // ── 行事曆（Outlook COM，須於 UI/STA 執行緒）────────────────────

    [McpServerTool(Name = "calendar_list"), Description("列出「起始時間 + 區間偏移量」內的 Outlook 行事曆事件。start 省略為現在，offsetHours 為往後延伸的小時數（預設 24）。例：2026/5/6 09:14 + 24 → 查 2026/5/6 09:14 ~ 2026/5/7 09:14。")]
    public static object CalendarList(
        CalendarService calendar,
        UiDispatcher ui,
        [Description("起始時間 yyyy/MM/dd HH:mm，省略為現在")] string? start = null,
        [Description("區間偏移量（小時），省略為 24")] double offsetHours = 24)
    {
        var from = ParseStartOrNow(start);
        if (offsetHours < 0) offsetHours = 0;
        var to = from.AddHours(offsetHours);
        var events = ui.Send(() => calendar.List(from, to));
        return events.Select(e => new
        {
            e.Id,
            e.Subject,
            start = e.StartText,
            end = e.EndText,
            e.Location,
            e.IsAllDay
        });
    }

    [McpServerTool(Name = "calendar_sync_alarms"), Description("把「起始時間 + 區間偏移量」內的 Outlook 會議同步成鬧鐘：每場於開始前 minutesBefore 分鐘響一次。start 省略為現在，offsetHours 為往後延伸的小時數（預設 24）。跳過整日/已過時間，並以標籤去重。")]
    public static object CalendarSyncAlarms(
        CalendarService calendar,
        AlarmEngine alarm,
        UiDispatcher ui,
        [Description("起始時間 yyyy/MM/dd HH:mm，省略為現在")] string? start = null,
        [Description("區間偏移量（小時），省略為 24")] double offsetHours = 24,
        [Description("會議開始前幾分鐘響鈴，預設 10")] double minutesBefore = 10)
    {
        var from = ParseStartOrNow(start);
        if (offsetHours < 0) offsetHours = 0;
        var to = from.AddHours(offsetHours);
        if (minutesBefore < 0) minutesBefore = 0;
        const string LabelPrefix = "[會議] ";

        return ui.Send(() =>
        {
            var events = calendar.List(from, to);
            var existing = alarm.List()
                .Where(t => t.Label.StartsWith(LabelPrefix, StringComparison.Ordinal))
                .Select(t => t.Label)
                .ToHashSet(StringComparer.Ordinal);

            int added = 0, skippedPast = 0, skippedAllDay = 0, skippedDup = 0;
            var now = DateTime.Now;

            foreach (var e in events)
            {
                if (e.IsAllDay) { skippedAllDay++; continue; }

                var fireAt = e.Start.AddMinutes(-minutesBefore);
                if (fireAt <= now) { skippedPast++; continue; }

                var label = $"{LabelPrefix}{e.Subject}（{e.Start:HH:mm}）";
                if (!existing.Add(label)) { skippedDup++; continue; }

                alarm.AddAlarm(fireAt, label, loop: false);
                added++;
            }

            return new { added, total = events.Count, skippedPast, skippedAllDay, skippedDup, minutesBefore, offsetHours };
        });
    }

    [McpServerTool(Name = "calendar_add"), Description("新增一筆 Outlook 行程。end 省略時預設 start+1 小時；reminder 為提醒分鐘數（省略沿用 Outlook 預設）。")]
    public static object CalendarAdd(
        CalendarService calendar,
        UiDispatcher ui,
        [Description("主旨")] string subject,
        [Description("開始時間 yyyy/MM/dd HH:mm")] string start,
        [Description("結束時間 yyyy/MM/dd HH:mm，省略為 start+1 小時")] string? end = null,
        [Description("地點")] string? location = null,
        [Description("內文")] string? body = null,
        [Description("提醒分鐘數")] int? reminder = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("缺少 subject");
        if (string.IsNullOrWhiteSpace(start) || !DateTime.TryParse(start.Trim(), out var startDt))
            throw new ArgumentException("缺少或無法解析 start（yyyy/MM/dd HH:mm）");

        DateTime endDt;
        if (string.IsNullOrWhiteSpace(end)) endDt = startDt.AddHours(1);
        else if (!DateTime.TryParse(end.Trim(), out endDt))
            throw new ArgumentException($"無法解析 end：{end}");
        if (endDt < startDt) throw new ArgumentException("end 不可早於 start");

        var rem = reminder is >= 0 ? reminder : null;
        var entryId = ui.Send(() => calendar.Add(subject.Trim(), startDt, endDt, location, body, rem));
        return new { entryId };
    }

    // 2026/11/10 - Arlen - 移除生活習慣記憶工具（memory_set/get/list/delete），已不使用 - Start
    // 2026/11/10 - Arlen - 移除生活習慣記憶工具 - end

    // 2026/07/22 - Arlen - 新增 SQL 執行模組：帳密由 KeelAssistant 控管，Claude 只拿得到 SQLConnID - Start

    // ── SQL 執行（MSSQL / PostgreSQL）──────────────────────────────

    [McpServerTool(Name = "sql_query"), Description("以指定的 SQLConnID 執行查詢並回傳結果。connId 由 Arlen 直接告知（無法列舉）。只接受單一 SELECT 語句——含 INSERT/UPDATE/DELETE/DDL 者請改用 sql_exec，多語句（分號分隔）一律拒絕。結果以 columns + rows 陣列回傳，超過 maxRows 會截斷並標記 truncated。")]
    public static object SqlQuery(
        DbConnectionStore connections,
        SqlExecutor executor,
        [Description("連線代號（SQLConnID），由 Arlen 提供")] string connId,
        [Description("單一 SELECT 查詢語句")] string sql,
        [Description("最多回傳幾列，預設 200，上限 1000")] int maxRows = 200,
        [Description("查詢逾時秒數，預設 30，上限 300")] int timeoutSec = 30)
        => Reportable(() =>
        {
            var entry = ResolveConnection(connections, connId);
            var statement = RequireSql(sql);

            var verdict = SqlGuard.Classify(statement);
            if (verdict.Kind == SqlKind.Blocked)
                throw new InvalidOperationException($"語句遭拒：{verdict.Reason}");
            if (verdict.Kind == SqlKind.Write)
                throw new InvalidOperationException(
                    $"這不是純查詢語句（{verdict.Reason}），請改用 sql_exec（會跳出視窗請 Arlen 確認）。");

            var result = executor.Query(entry, statement,
                                        Math.Clamp(maxRows, 1, 1000),
                                        Math.Clamp(timeoutSec, 1, 300));

            return (object)new
            {
                connId = entry.Id,
                columns = result.Columns,
                rows = result.Rows,
                rowCount = result.RowCount,
                truncated = result.Truncated,
                elapsedMs = result.ElapsedMs
            };
        });

    [McpServerTool(Name = "sql_exec"), Description("以指定的 SQLConnID 執行寫入類語句（INSERT/UPDATE/DELETE/DDL），回傳受影響列數。connId 由 Arlen 直接告知（無法列舉）。執行前會彈出視窗請 Arlen 逐字確認 SQL，未核可即中止。唯讀連線一律拒絕，多語句與系統預存程序（sp_/xp_）亦拒絕。")]
    public static object SqlExec(
        DbConnectionStore connections,
        SqlExecutor executor,
        UiDispatcher ui,
        [Description("連線代號（SQLConnID），由 Arlen 提供")] string connId,
        [Description("單一寫入語句")] string sql,
        [Description("執行逾時秒數，預設 60，上限 300")] int timeoutSec = 60)
        => Reportable(() =>
        {
            var entry = ResolveConnection(connections, connId);
            var statement = RequireSql(sql);

            if (entry.ReadOnly)
                throw new InvalidOperationException($"連線「{entry.Id}」為唯讀，不允許寫入。");

            var verdict = SqlGuard.Classify(statement);
            if (verdict.Kind == SqlKind.Blocked)
                throw new InvalidOperationException($"語句遭拒：{verdict.Reason}");

            var reason = verdict.Kind == SqlKind.ReadOnly ? "查詢語句（可改用 sql_query）" : verdict.Reason;
            var approved = ui.Send(() => SqlConfirmForm.Confirm(entry, statement, reason));
            if (!approved)
                throw new InvalidOperationException("Arlen 取消了這次 SQL 執行。");

            var affected = executor.NonQuery(entry, statement, Math.Clamp(timeoutSec, 1, 300));
            return (object)new { connId = entry.Id, affectedRows = affected };
        });

    // 2026/07/22 - Arlen - 新增 SQL 執行模組 - end

    // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組：讓 Claude 能對遠端主機下指令。
    // 與 SQL／Python 沙盒最大的差異是「沒有隔離層」，故每一條都必須經人工確認視窗 - Start

    // ── SSH 遠端執行 ──────────────────────────────────────────────

    [McpServerTool(Name = "ssh_exec"), Description("在指定的遠端主機執行一條 shell 指令並回傳輸出。sshId 由 Arlen 直接告知（無法列舉）。⚠️ 每一條指令都會彈出視窗請 Arlen 逐字確認，未核可即中止——請一次送出完整可執行的指令，不要拆成很多條瑣碎指令連續呼叫。同一 sshId 使用「持續的 shell」，cd 與 export 的效果會延續到下一次呼叫，回傳值的 cwd 即為目前工作目錄。⚠️ 不可執行互動式程式（nano、vim、top、less、需要輸入的 apt 對話框等）：它們不會回到提示符，只會等到逾時。需要分頁輸出請自行加 | cat 或 --no-pager。")]
    public static Task<object> SshExec(
        SshConnectionStore connections,
        SshSessionManager ssh,
        UiDispatcher ui,
        [Description("連線代號（sshId），由 Arlen 提供")] string sshId,
        [Description("要執行的單一 shell 指令（可用管線與 && 串接）")] string command,
        [Description("逾時秒數，上限 1800。省略或給 0 表示採用設定檔的預設值（SshDefaultTimeoutSeconds，通常 120）。長時間作業（apt upgrade、npm ci、next build）請自行加大，否則會在中途被 Ctrl+C 中斷。")] int timeoutSec = 0)
        => ReportableAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("缺少 command");

            var entry = connections.Resolve(sshId);
            var cwd = ssh.CurrentCwd(entry.Id);

            // ShowDialog 內含巢狀訊息迴圈，等待期間 UI 執行緒仍在 pump，
            // 不會餓死鬧鐘 Timer 與錄音 pump，故確認視窗可以走 ui.Send
            var approved = ui.Send(() => SshConfirmForm.Confirm(entry, command, cwd));
            if (!approved)
                throw new InvalidOperationException("Arlen 取消了這次遠端指令執行。");

            var result = await ssh.ExecAsync(entry.Id, command, timeoutSec);

            return (object)new
            {
                sshId = entry.Id,
                exitCode = result.ExitCode,
                cwd = result.Cwd,
                output = result.Output,
                truncated = result.Truncated,
                timedOut = result.TimedOut,
                elapsedMs = result.ElapsedMs
            };
        });

    [McpServerTool(Name = "ssh_status"), Description("查詢指定 sshId 目前的連線狀態與工作目錄。不會主動建立連線。持續 shell 的 cwd 可能已被其他 session 或 Arlen 手動操作改變，執行相對路徑的指令前建議先確認。")]
    public static object SshStatus(
        SshSessionManager ssh,
        [Description("連線代號（sshId），由 Arlen 提供")] string sshId)
        => Reportable(() => ssh.Status(sshId));

    [McpServerTool(Name = "ssh_open"), Description("建立連線並把該主機的終端視窗帶到前景，讓 Arlen 可以自己接手打字。當 ssh_exec 因互動式程式而逾時、需要人工介入時使用。")]
    public static Task<object> SshOpen(
        SshSessionManager ssh,
        [Description("連線代號（sshId），由 Arlen 提供")] string sshId)
        => ReportableAsync(async () =>
        {
            await ssh.OpenTerminalAsync(sshId);
            return (object)new { sshId, opened = true };
        });

    [McpServerTool(Name = "ssh_close"), Description("中斷指定 sshId 的連線並關閉終端視窗。持續 shell 累積的工作目錄與環境變數會一併清除，下次呼叫 ssh_exec 會重新登入。")]
    public static object SshClose(
        SshSessionManager ssh,
        [Description("連線代號（sshId），由 Arlen 提供")] string sshId)
        => Reportable(() => new { sshId, closed = ssh.Close(sshId) });

    // 2026/09/01 - Arlen - 新增 SSH 遠端執行模組 - end

    // 2026/07/23 - Arlen - 新增 Python 沙盒模組：LLM 產生的 Python 一律在 WASM 沙盒內執行 - Start

    // ── Python 沙盒（Pyodide / WebAssembly）────────────────────────

    [McpServerTool(Name = "python_run"), Description("在隔離的 WebAssembly 沙盒內執行 Python 程式碼（CPython 3.14，可用 numpy／pandas／openpyxl／Pillow，其餘只有標準庫）。沙盒沒有網路、碰不到真實檔案系統，因此誤產生的破壞性程式碼不會影響環境。要讀取檔案時用 readPaths 明確列出（可用絕對路徑），會跳出視窗請 Arlen 確認；純計算不會打擾他。程式碼寫檔到 output/ 目錄的內容會被取回並存到沙盒 output 目錄。最後一行若是運算式，其 repr 會一併回傳。")]
    public static Task<object> PythonRun(
        PyodideRunner sandbox,
        SandboxPaths paths,
        UiDispatcher ui,
        [Description("要執行的 Python 程式碼")] string code,
        [Description("要讀取的檔案。可給絕對路徑（例：D:\\報表\\sales.xlsx），或相對於沙盒 input 目錄的路徑（例：sales.csv）。檔案會以「檔名」掛在沙盒的 input/ 之下，程式碼一律用 input/檔名 存取。憑證類檔案（.env、*.key、dbconnections.json 等）一律拒絕。不需讀檔就省略。")] string[]? readPaths = null)
        => ReportableAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("缺少 code");

            var requested = (readPaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToArray();

            var inputs = new List<(string Name, byte[] Data)>();
            if (requested.Length > 0)
            {
                // 先全部解析驗證，再一次顯示確認視窗——不要邊問邊讀，
                // 否則 Arlen 按了幾次「允許」之後才發現其中一個路徑有問題。
                var resolved = requested.Select(paths.ResolveInputFile).ToArray();

                var approved = ui.Send(() => SandboxConfirmForm.Confirm(paths.InputDir, resolved));
                if (!approved)
                    throw new InvalidOperationException("Arlen 拒絕了這次的檔案讀取要求。");

                foreach (var file in resolved)
                {
                    // 沙盒內一律以檔名掛在 input/ 之下，不重現來源的目錄結構——
                    // 絕對路徑帶進虛擬 FS 只會讓程式碼難寫（磁碟機代號在 WASM 無意義）
                    var name = Path.GetFileName(file.FullPath);
                    inputs.Add((name, File.ReadAllBytes(file.FinalTarget)));
                }
            }

            var result = await sandbox.RunAsync(code, inputs);

            // 沙盒寫在虛擬 FS 的 /output 內容，此時才真正落到磁碟（同名不覆蓋）
            var written = new List<object>();
            foreach (var file in result.Files)
            {
                var target = paths.ResolveOutputFile(file.Name);
                var bytes = Convert.FromBase64String(file.B64);
                File.WriteAllBytes(target, bytes);
                written.Add(new { name = Path.GetFileName(target), path = target, bytes = bytes.Length });
            }

            return (object)new
            {
                ok = result.Ok,
                stdout = result.Stdout,
                stderr = result.Stderr,
                value = result.Value,
                error = result.Error,
                truncated = result.Truncated,
                elapsedMs = result.ElapsedMs,
                outputs = written,
                outputsTruncated = result.FilesTruncated
            };
        });

    [McpServerTool(Name = "list_sandbox_files"), Description("列出沙盒 input 目錄下可供讀取的檔案（相對路徑、大小、修改時間）。用來在 python_run 指定 readPaths 之前先確認有哪些檔案，不需人工確認。")]
    public static object ListSandboxFiles(
        SandboxPaths paths,
        [Description("檔名篩選樣式，例如 *.csv；省略則列出全部")] string? pattern = null)
        => Reportable(() =>
        {
            var files = paths.ListInputFiles(pattern)
                .Select(f => new
                {
                    path = f.Path,
                    bytes = f.Bytes,
                    modifiedAt = f.ModifiedAt.ToString("yyyy\\/MM\\/dd HH:mm:ss")
                })
                .ToArray();

            return (object)new { inputDir = paths.InputDir, count = files.Length, files };
        });

    [McpServerTool(Name = "sandbox_info"), Description("查詢 Python 沙盒的目前設定：根目錄、input／output 路徑、逾時與記憶體上限、可用套件。路徑由 Arlen 在 appsettings.json 設定，可能變動，需要用到路徑時先查這裡而不是沿用記憶中的值。")]
    public static object SandboxInfo(PyodideRunner sandbox, SandboxPaths paths)
        => Reportable(() => (object)new
        {
            available = sandbox.Available,
            disabledReason = sandbox.DisabledReason,
            root = paths.Root,
            inputDir = paths.InputDir,
            outputDir = paths.OutputDir,
            readOnly = true,
            network = false,
            timeoutSeconds = sandbox.TimeoutSeconds,
            memoryLimitMb = sandbox.MemoryLimitMb,
            maxFileSizeMb = paths.MaxFileSizeMb,
            python = "3.14 (Pyodide 314.0.2 / WebAssembly)",
            // 2026/10/06 - Arlen - 沙盒新增 Pillow 供圖片處理（如去背） - Start
            packages = new[] { "numpy 2.4.3", "pandas 3.0.2", "openpyxl 3.1.5", "Pillow 12.2.0", "python-dateutil", "pytz", "six" },
            // 2026/10/06 - Arlen - 沙盒新增 Pillow 供圖片處理（如去背） - end
            notes = "沙盒無網路，無法 pip install；未列出的第三方套件一律不可用。寫檔請寫到 output/ 之下才會被取回。"
        });

    // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end

    // ── Claude 用量 ───────────────────────────────────────────────

    // 2026/08/18 - Arlen - 新增 get_usage_5h：讀托盤輪詢的共享快照，不另打官方 usage 端點。
    // 該端點有嚴格 429 桶，而 MCP 是 Stateless、多 session／SubAgent 共用同一行程，
    // 各自即時抓會撞 429 並連帶弄壞托盤輪詢（用量顯示的主要用途）- Start

    /// <summary>
    /// 回報 5 小時（session）額度視窗。資料來自 <see cref="UsageProvider"/> 的共享快照，
    /// 因此一併回傳 snapshotAgeSeconds 揭露陳舊程度——快照最舊可達輪詢間隔（>=180 秒）。
    /// </summary>
    [McpServerTool(Name = "get_usage_5h"), Description("查詢 Claude 5 小時（session）額度用量：已用百分比、剩餘百分比、重置時間與剩餘分鐘。資料取自 Keel 托盤的輪詢快照（間隔至少 180 秒），非即時值——請一併參考回傳的 snapshotAgeSeconds 判斷新舊。尚未輪詢或上次取得失敗時會回報錯誤。")]
    public static object GetUsage5h(UsageProvider usage)
        => Reportable(() =>
        {
            var snapshot = usage.Current
                ?? throw new InvalidOperationException(
                    "目前沒有用量快照（尚未完成第一次輪詢，或上次取得失敗）。請稍後再試，或請 Arlen 從托盤選單手動更新。");

            var window = snapshot.FiveHour
                ?? throw new InvalidOperationException(
                    "用量回應中找不到 5 小時視窗資料（limits[] 的 session 與舊 five_hour 欄位皆無）。");

            var session = snapshot.Limits?.FirstOrDefault(
                l => string.Equals(l.Kind, "session", StringComparison.OrdinalIgnoreCase));

            var at = usage.CurrentAt;

            return (object)new
            {
                window = "5h",
                utilization = Math.Round(window.Utilization, 1),
                remainingPercent = Math.Round(100 - window.Utilization, 1),
                severity = session?.Severity,
                resetsAt = window.ResetsAt?.ToLocalTime().ToString("yyyy\\/MM\\/dd HH:mm"),
                resetsInMinutes = window.ResetsAt is { } r
                    ? Math.Max(0, (int)(r.ToLocalTime() - DateTime.Now).TotalMinutes)
                    : (int?)null,
                snapshotAt = at?.ToString("yyyy\\/MM\\/dd HH:mm:ss"),
                snapshotAgeSeconds = at is { } t ? (int)(DateTime.Now - t).TotalSeconds : (int?)null
            };
        });

    // 2026/08/18 - Arlen - 新增 get_usage_5h - end

    // ── 工具方法 ──────────────────────────────────────────────────

    // 2026/07/22 - Arlen - 新增 SQL 執行模組的共用參數解析 - Start

    /// <summary>
    /// 把工具內拋出的例外訊息轉成 Claude 看得到的錯誤。
    ///
    /// MCP SDK 只會把 <see cref="McpException"/> 的訊息傳給 client，其他例外一律換成
    /// 「An error occurred」以免洩漏內部細節。但本模組的錯誤訊息本身就是要給 Claude 讀的
    /// 判斷依據（為何被拒、該改用哪個工具、該向 Arlen 問什麼），吞掉等於讓工具無法自我修正。
    /// DB 例外的主機名與帳號已於 <see cref="SqlExecutor"/> 內遮蔽，故可安全透出。
    /// </summary>
    private static object Reportable(Func<object> action)
    {
        try
        {
            return action();
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }

    // 2026/07/23 - Arlen - 新增 Python 沙盒模組：非同步工具的錯誤透出 - Start

    /// <summary>
    /// <see cref="Reportable"/> 的非同步版本。沙盒執行最長 30 秒，
    /// 若用同步版包住會佔住一條執行緒池執行緒空等，故獨立一個 async 路徑。
    /// </summary>
    private static async Task<object> ReportableAsync(Func<Task<object>> action)
    {
        try
        {
            return await action();
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }

    // 2026/07/23 - Arlen - 新增 Python 沙盒模組 - end

    /// <summary>
    /// 依 connId 取出連線設定。刻意不提供列舉連線的管道，錯誤訊息也不列出可用代號——
    /// 連線清單屬於 Arlen 控管的資訊，connId 必須由他主動告知。
    /// </summary>
    private static DbConnectionEntry ResolveConnection(DbConnectionStore connections, string connId)
    {
        if (string.IsNullOrWhiteSpace(connId)) throw new ArgumentException("缺少 connId");

        return connections.Get(connId.Trim())
            ?? throw new InvalidOperationException(
                $"找不到連線「{connId}」，請向 Arlen 確認正確的 SQLConnID。");
    }

    /// <summary>驗證並去除 SQL 前後空白。</summary>
    private static string RequireSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("缺少 sql");
        return sql.Trim();
    }

    // 2026/07/22 - Arlen - 新增 SQL 執行模組的共用參數解析 - end

    /// <summary>解析起始時間（yyyy/MM/dd HH:mm 等，保留時間），失敗或空值則回傳現在。</summary>
    private static DateTime ParseStartOrNow(string? s)
        => !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s.Trim(), out var d) ? d : DateTime.Now;

    /// <summary>解析 at：支援「HH:mm」（今天，已過則明天）與完整日期時間字串。</summary>
    private static bool TryParseFireAt(string s, out DateTime fireAt)
    {
        s = s.Trim();
        if (TimeOnly.TryParse(s, out var timeOnly))
        {
            var today = DateTime.Today.Add(timeOnly.ToTimeSpan());
            fireAt = today <= DateTime.Now ? today.AddDays(1) : today;
            return true;
        }
        if (DateTime.TryParse(s, out fireAt)) return true;
        fireAt = default;
        return false;
    }
}
