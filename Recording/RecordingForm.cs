namespace KeelAssistant.Recording;

/// <summary>
/// 會議錄音操作視窗。MCP 只負責把這個視窗叫出來，開始 / 暫停 / 停止一律由使用者按下，
/// 錄音不會在無人知情下進行。
///
/// 兩條音量條是刻意設計的：線上會議錄音最常見的失敗是「錄完才發現只有自己的聲音」，
/// 開錄前掃一眼「系統音」那條有沒有跳動，就能當場確認對方聲音錄得到。
///
/// 下半部的筆記框與逐字稿職責不同：逐字稿是「誰講了什麼」的流水帳，筆記是「什麼是重點、
/// 決議了什麼」。兩者同名存在錄音檔旁（.md 與 .txt），GenMeetingMinutes 一起吃。
/// 按 Ctrl+T 可插入當下錄音時間，讓筆記成為逐字稿的索引。
/// </summary>
public sealed class RecordingForm : Form
{
    /// <summary>停止打字後多久落檔。逐鍵寫檔會是幾萬次磁碟 I/O，故用 debounce。</summary>
    private const int SaveDebounceMs = 1500;

    private readonly RecordingController _controller;

    private readonly Label _lblPath;
    private readonly TextBox _txtLabel;
    private readonly Label _lblTime;
    private readonly ProgressBar _barMic;
    private readonly ProgressBar _barSystem;
    private readonly TextBox _txtNotes;
    private readonly Button _btnStart;
    private readonly Button _btnPause;
    private readonly Button _btnStop;

    private readonly System.Windows.Forms.Timer _saveTimer;

    /// <summary>目前筆記已落檔的路徑。未開始錄音前為 null（內容先留在記憶體）。</summary>
    private string? _notesPath;
    private bool _dirty;

    public RecordingForm(RecordingController controller)
    {
        _controller = controller;

        Text = "🎙️ Keel 會議錄音";
        Width = 600;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(520, 520);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        var font = new Font("Microsoft JhengHei UI", 9F);

        _saveTimer = new System.Windows.Forms.Timer { Interval = SaveDebounceMs };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNotes(); };

        // 先建立，下方按鈕的 Click 處理器會用到
        _txtLabel = new TextBox { Dock = DockStyle.Top, Font = font, PlaceholderText = "會議名稱（作為檔名，可留空）" };
        _txtNotes = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsTab = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10F),
            PlaceholderText = "會議筆記（Markdown）… 記重點與決議即可，逐字內容交給逐字稿。Ctrl+T 插入時間戳。"
        };
        _txtNotes.TextChanged += (_, _) =>
        {
            _dirty = true;
            _saveTimer.Stop();    // 每次輸入都重新計時，停手 1.5 秒才真的寫檔
            _saveTimer.Start();
        };
        _txtNotes.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.T)
            {
                InsertTimestamp();
                e.SuppressKeyPress = true;   // 不要讓 't' 被打進去
            }
        };

        // ── 底部：操作按鈕 ──
        var pnlButtons = new Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(10) };
        _btnStart = new Button
        {
            Text = "● 開始",
            Dock = DockStyle.Left,
            Width = 145,
            Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold),
            ForeColor = Color.Firebrick
        };
        _btnPause = new Button
        {
            Text = "⏸ 暫停",
            Dock = DockStyle.Left,
            Width = 145,
            Font = new Font("Microsoft JhengHei UI", 11F)
        };
        _btnStop = new Button
        {
            Text = "⏹ 停止",
            Dock = DockStyle.Left,
            Width = 145,
            Font = new Font("Microsoft JhengHei UI", 11F)
        };
        _btnStart.Click += (_, _) => Guard(() =>
        {
            // 換一場會議：先把上一場的筆記落檔，再清空重來
            SaveNotes();
            _txtNotes.Clear();
            _notesPath = null;

            _controller.Start(_txtLabel.Text);

            // 錄音一開始才有檔名。會前先打的內容（若有）此刻一併寫入。
            _notesPath = Path.ChangeExtension(_controller.CurrentFilePath!, ".md");
            _dirty = true;
            SaveNotes();
        });
        _btnPause.Click += (_, _) => Guard(() =>
        {
            if (_controller.State == RecordingState.Recording) _controller.Pause();
            else _controller.Resume();
        });
        _btnStop.Click += (_, _) => Guard(() =>
        {
            var r = _controller.Stop();
            SaveNotes();

            var notes = _notesPath is not null && File.Exists(_notesPath)
                ? $"\n筆記：{Path.GetFileName(_notesPath)}"
                : "";
            MessageBox.Show(this,
                $"已儲存：\n{r.FilePath}{notes}\n\n長度：{r.Duration:hh\\:mm\\:ss}\n大小：{r.SizeBytes / 1024.0 / 1024.0:N1} MB\n\n" +
                "逐字稿請透過 Claude 呼叫 transcribe_recording 轉檔。",
                "錄音完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        // Dock.Left 由後加的排在左邊，因此逆序加入才會呈現「開始 / 暫停 / 停止」
        pnlButtons.Controls.Add(_btnStop);
        pnlButtons.Controls.Add(_btnPause);
        pnlButtons.Controls.Add(_btnStart);

        // ── 筆記抬頭（含插入時間戳按鈕）──
        var btnTimestamp = new Button
        {
            Text = "⏱ 插入時間戳 (Ctrl+T)",
            Dock = DockStyle.Right,
            Width = 170,
            Font = new Font("Microsoft JhengHei UI", 8.5F)
        };
        btnTimestamp.Click += (_, _) => InsertTimestamp();
        var lblNotesHead = new Label
        {
            Text = "會議筆記（自動存檔，與錄音檔同名 .md）",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft JhengHei UI", 8.5F),
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var pnlNotesHead = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(4, 2, 4, 2) };
        pnlNotesHead.Controls.Add(lblNotesHead);
        pnlNotesHead.Controls.Add(btnTimestamp);

        // ── 音量條 ──
        var pnlLevels = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(12, 4, 12, 6) };
        _barSystem = NewLevelBar();
        var lblSystem = new Label { Text = "系統音（對方聲音）", Dock = DockStyle.Top, Height = 18, Font = font };
        _barMic = NewLevelBar();
        var lblMic = new Label { Text = "麥克風（我的聲音）", Dock = DockStyle.Top, Height = 18, Font = font };
        // Dock.Top 由後加的排在最上面，因此逆序加入
        pnlLevels.Controls.Add(_barSystem);
        pnlLevels.Controls.Add(lblSystem);
        pnlLevels.Controls.Add(_barMic);
        pnlLevels.Controls.Add(lblMic);

        // ── 計時 ──
        _lblTime = new Label
        {
            Text = "00:00:00",
            Dock = DockStyle.Top,
            Height = 56,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft JhengHei UI", 26F, FontStyle.Bold),
            ForeColor = Color.Gray
        };

        // ── 頂部：輸出路徑 + 會議名稱 ──
        _lblPath = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            Font = new Font("Microsoft JhengHei UI", 8F),
            ForeColor = Color.DimGray,
            Padding = new Padding(2, 2, 2, 6),
            AutoEllipsis = true
        };
        var pnlTop = new Panel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(12, 8, 12, 4) };
        pnlTop.Controls.Add(_txtLabel);
        pnlTop.Controls.Add(_lblPath);

        // Dock 佈局：Fill 必須最先加入 Controls 才會被其他 Dock 元件擠壓
        Controls.Add(_txtNotes);
        Controls.Add(pnlNotesHead);
        Controls.Add(pnlLevels);
        Controls.Add(_lblTime);
        Controls.Add(pnlButtons);
        Controls.Add(pnlTop);

        _controller.Changed += OnControllerChanged;
        UpdateUi();
    }

    private static ProgressBar NewLevelBar() => new()
    {
        Dock = DockStyle.Top,
        Height = 14,
        Maximum = 100,
        Style = ProgressBarStyle.Continuous
    };

    /// <summary>顯示視窗（重複呼叫只是把既有視窗帶到前景，不重建，保留錄音與筆記狀態）。</summary>
    public void ShowRecorder()
    {
        UpdateUi();
        Show();
        Activate();
    }

    // ── 筆記 ──────────────────────────────────────────────────────

    /// <summary>
    /// 在游標處插入當下的錄音時間。日後看筆記寫「[00:23:10] 這裡有爭議」，
    /// 就能直接跳到逐字稿的同一段——這是把人工筆記與逐字稿黏起來的關鍵。
    /// </summary>
    private void InsertTimestamp()
    {
        var stamp = $"[{_controller.Elapsed:hh\\:mm\\:ss}] ";
        int pos = _txtNotes.SelectionStart;
        _txtNotes.Text = _txtNotes.Text.Insert(pos, stamp);
        _txtNotes.SelectionStart = pos + stamp.Length;
        _txtNotes.Focus();
    }

    /// <summary>
    /// 落檔。尚未開始錄音（沒有檔名）時不寫，內容先留在記憶體，
    /// 按下「開始」拿到檔名後會一併寫入——這樣會前先打的議程不會遺失。
    /// </summary>
    private void SaveNotes()
    {
        if (!_dirty || _notesPath is null) return;

        try
        {
            File.WriteAllText(_notesPath, _txtNotes.Text, System.Text.Encoding.UTF8);
            _dirty = false;
        }
        catch
        {
            // 磁碟寫入失敗不該打斷錄音，也不該跳窗打斷會議；下次 debounce 會再試
        }
    }

    /// <summary>把操作錯誤（如重複按開始、無可用裝置）以對話框呈現，不讓例外炸掉 UI 執行緒。</summary>
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "錄音", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnControllerChanged()
    {
        if (IsDisposed) return;
        UpdateUi();
    }

    private void UpdateUi()
    {
        var state = _controller.State;

        _lblPath.Text = "輸出：" + (_controller.OutputDirectory ?? "（尚未指定）");
        _lblTime.Text = _controller.Elapsed.ToString(@"hh\:mm\:ss");
        _lblTime.ForeColor = state switch
        {
            RecordingState.Recording => Color.Firebrick,
            RecordingState.Paused => Color.DarkOrange,
            _ => Color.Gray
        };

        _barMic.Value = LevelToPercent(_controller.MicLevel);
        _barSystem.Value = LevelToPercent(_controller.SystemLevel);

        _btnStart.Enabled = state == RecordingState.Idle;
        _btnPause.Enabled = state != RecordingState.Idle;
        _btnStop.Enabled = state != RecordingState.Idle;
        _btnPause.Text = state == RecordingState.Paused ? "▶ 繼續" : "⏸ 暫停";
        _txtLabel.Enabled = state == RecordingState.Idle;
    }

    private static int LevelToPercent(float level)
        => Math.Clamp((int)(level * 100), 0, 100);

    // 視窗失焦時先落檔：切去別的程式很可能就是會議中途，不要讓筆記只活在記憶體裡
    protected override void OnDeactivate(EventArgs e)
    {
        SaveNotes();
        base.OnDeactivate(e);
    }

    // 錄音中按 X 不放行：錄音必須是使用者明確停止的，不能讓視窗一關就在背景偷錄。
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveNotes();

        if (e.CloseReason == CloseReason.UserClosing)
        {
            if (_controller.State != RecordingState.Idle)
            {
                e.Cancel = true;
                MessageBox.Show(this, "錄音進行中，請先按「停止」再關閉視窗。",
                    "錄音", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 未錄音：隱藏保留實例，下次 open_recorder 直接重用（筆記內容也保留）
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SaveNotes();
            _saveTimer.Dispose();
            _controller.Changed -= OnControllerChanged;
        }
        base.Dispose(disposing);
    }
}
