using System.Media;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeelAssistant.Models;

namespace KeelAssistant.Alarm;

/// <summary>
/// 純記憶體計時引擎：以 WinForms Timer 每秒檢查到期任務並執行。
/// 任務狀態持久化於 tasks.json，重啟後復原（已過期者啟動時補觸發一次）。
/// 採固定檔名 + 唯一任務 Id，先刪後建不誤殺待命任務（一次性任務觸發後自動移除）。
/// </summary>
public sealed class AlarmEngine : IDisposable
{
    private readonly string _tasksPath;
    private readonly string _baseDir;
    private readonly AppSettings _settings;
    private readonly Action _stopMusic;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<ScheduledTask> _tasks = new();
    private readonly object _gate = new();

    // 目前響鈴中的資源（同時間僅保留一組）
    private SoundPlayer? _player;
    private AlarmForm? _alarmForm;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 中文 label 直出，避免 tasks.json 滿是 \uXXXX
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public AlarmEngine(AppSettings settings, string baseDir, Action stopMusic)
    {
        _settings = settings;
        _baseDir = baseDir;
        _stopMusic = stopMusic;
        _tasksPath = Path.Combine(baseDir, "tasks.json");

        Load();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => CheckDue();
        _timer.Start();

        // 啟動即檢查一次（補觸發離線期間到期的任務）
        CheckDue();
    }

    // ── 對外操作 ──────────────────────────────────────────────────

    /// <summary>新增絕對時間鬧鐘。</summary>
    public ScheduledTask AddAlarm(DateTime fireAt, string label, bool loop = true)
        => Add(new ScheduledTask
        {
            Id = NewId(),
            FireAt = fireAt,
            Label = label,
            Action = ScheduledAction.Alarm,
            Loop = loop,
            Enabled = true
        });

    /// <summary>新增相對時間任務（N 分鐘後）。</summary>
    public ScheduledTask AddRelative(double minutes, ScheduledAction action, string label)
        => Add(new ScheduledTask
        {
            Id = NewId(),
            FireAt = DateTime.Now.AddMinutes(minutes),
            Label = label,
            Action = action,
            Loop = action == ScheduledAction.Alarm,
            Enabled = true
        });

    private ScheduledTask Add(ScheduledTask task)
    {
        lock (_gate)
        {
            _tasks.Add(task);
            Save();
        }
        return task;
    }

    public IReadOnlyList<ScheduledTask> List()
    {
        lock (_gate)
            return _tasks.OrderBy(t => t.FireAt).ToList();
    }

    public bool Cancel(string id)
    {
        lock (_gate)
        {
            int removed = _tasks.RemoveAll(t => t.Id == id);
            if (removed > 0) Save();
            return removed > 0;
        }
    }

    /// <summary>立即響鈴一次（非循環），用於即時通知 Arlen 注意訊息。</summary>
    public void Notify(string message)
    {
        var task = new ScheduledTask
        {
            Id = NewId(),
            FireAt = DateTime.Now,
            Label = string.IsNullOrWhiteSpace(message) ? "Keel 有重要訊息！" : message.Trim(),
            Action = ScheduledAction.Alarm,
            Loop = false,
            Enabled = true
        };
        Ring(task);
    }

    /// <summary>停止目前響鈴（音效 + 視窗）。</summary>
    public void StopRinging()
    {
        if (_player is not null)
        {
            try { _player.Stop(); } catch { }
            _player.Dispose();
            _player = null;
        }

        if (_alarmForm is not null && !_alarmForm.IsDisposed)
        {
            var f = _alarmForm;
            _alarmForm = null;
            try { f.Close(); } catch { }
        }
    }

    // ── 內部：到期檢查與觸發 ──────────────────────────────────────

    private void CheckDue()
    {
        List<ScheduledTask> due;
        lock (_gate)
        {
            var now = DateTime.Now;
            due = _tasks.Where(t => t.Enabled && t.FireAt <= now).ToList();
            if (due.Count > 0)
            {
                foreach (var t in due) _tasks.Remove(t);
                Save();
            }
        }

        // Timer.Tick 已在 UI 執行緒，可直接操作 UI
        foreach (var t in due) Fire(t);
    }

    private void Fire(ScheduledTask task)
    {
        switch (task.Action)
        {
            case ScheduledAction.MusicStop:
                try { _stopMusic(); } catch { }
                break;

            case ScheduledAction.Alarm:
            default:
                Ring(task);
                break;
        }
    }

    private void Ring(ScheduledTask task)
    {
        // 先停掉前一個響鈴，確保資源唯一
        StopRinging();

        var soundPath = Path.IsPathRooted(_settings.AlarmSoundPath)
            ? _settings.AlarmSoundPath
            : Path.Combine(_baseDir, _settings.AlarmSoundPath);

        if (File.Exists(soundPath))
        {
            try
            {
                _player = new SoundPlayer(soundPath);
                if (task.Loop) _player.PlayLooping();
                else _player.Play();
            }
            catch
            {
                SystemSounds.Exclamation.Play();
            }
        }
        else
        {
            // 無音檔則退回系統音
            SystemSounds.Exclamation.Play();
        }

        _alarmForm = new AlarmForm(task.Label, DateTime.Now, StopRinging);
        _alarmForm.Show();
        _alarmForm.Activate();
    }

    // ── 持久化 ────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_tasksPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ScheduledTask>>(
                File.ReadAllText(_tasksPath), JsonOpts);
            if (loaded is not null)
            {
                _tasks.Clear();
                _tasks.AddRange(loaded);
            }
        }
        catch { /* 壞檔忽略，視為無任務 */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_tasksPath, JsonSerializer.Serialize(_tasks, JsonOpts));
        }
        catch { /* 寫入失敗不影響執行期 */ }
    }

    // 唯一 Id：以 ticks 為基礎，避免 Math.Random / Guid 亦可，這裡用 Guid 短碼
    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    // 2026/06/25 - Arlen - 修「結束無法關閉」：應用程式結束時停掉每秒 Timer 並釋放響鈴資源（SoundPlayer / AlarmForm）- Start
    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        StopRinging();
    }
    // 2026/06/25 - Arlen - 修「結束無法關閉」- end
}
