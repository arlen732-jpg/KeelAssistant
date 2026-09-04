using KeelAssistant.Models;

namespace KeelAssistant.Db;

/// <summary>
/// SQL 連線設定視窗（托盤選單開啟）。
///
/// 密碼在此輸入後即以 DPAPI 加密寫入 dbconnections.json，明文不落地、
/// 也不會顯示回畫面上——編輯既有連線時密碼欄一律空白，留空表示沿用原密碼。
/// Claude 只會拿到連線的 Id，看不到主機、帳號與密碼。
/// </summary>
public sealed class DbConnectionForm : Form
{
    private readonly DbConnectionStore _store;
    private readonly SqlExecutor _executor;

    private readonly ListBox _lstConnections = new();
    private readonly TextBox _txtId = new();
    private readonly ComboBox _cboProvider = new();
    private readonly TextBox _txtServer = new();
    private readonly TextBox _txtPort = new();
    private readonly TextBox _txtDatabase = new();
    private readonly TextBox _txtUser = new();
    private readonly TextBox _txtPassword = new() { PasswordChar = '●' };
    private readonly CheckBox _chkReadOnly = new() { Text = "唯讀連線（拒絕一切非 SELECT 語句）" };
    private readonly TextBox _txtDescription = new();
    private readonly Label _lblPasswordHint = new();

    /// <summary>目前正在編輯的既有連線 Id；null 表示正在新增。</summary>
    private string? _editingId;

    /// <summary>載入清單時抑制 SelectedIndexChanged，避免覆寫使用者正在輸入的內容。</summary>
    private bool _loading;

    public DbConnectionForm(DbConnectionStore store, SqlExecutor executor)
    {
        _store = store;
        _executor = executor;

        Text = "SQL 連線設定";
        Width = 720;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimizeBox = false;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        BuildLayout();
        ReloadList();
        ClearEditor();
    }

    // ── 版面 ──────────────────────────────────────────────────────

    private void BuildLayout()
    {
        _cboProvider.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboProvider.Items.AddRange(new object[] { DbProvider.Mssql, DbProvider.Postgresql });
        _cboProvider.SelectedIndex = 0;

        _lstConnections.Dock = DockStyle.Fill;
        _lstConnections.Font = new Font("Microsoft JhengHei UI", 10F);
        _lstConnections.SelectedIndexChanged += (_, _) => OnSelectionChanged();

        _lblPasswordHint.Text = "留空 = 不變更密碼";
        _lblPasswordHint.ForeColor = Color.Gray;
        _lblPasswordHint.Font = new Font("Microsoft JhengHei UI", 8F);
        _lblPasswordHint.Dock = DockStyle.Bottom;
        _lblPasswordHint.Height = 20;
        _lblPasswordHint.Padding = new Padding(112, 0, 0, 0);

        // 右側編輯區：每列一個 Panel（Label 靠左 + 輸入控件填滿），由上往下依序加入
        var editor = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8) };
        var rows = new List<Control>
        {
            // ID 可自行輸入，也可按「隨機」產生——後者讓 AI 無從由代號推測是哪個環境
            MakeRow("連線 ID", _txtId,
                    MakeButton("隨機", (_, _) => _txtId.Text = NewConnectionId(), DockStyle.Right, 70)),
            MakeRow("資料庫類型", _cboProvider),
            MakeRow("主機", _txtServer),
            MakeRow("埠", _txtPort),
            MakeRow("資料庫名稱", _txtDatabase),
            MakeRow("帳號", _txtUser),
            MakeRow("密碼", _txtPassword),
            _lblPasswordHint,
            MakeRow("說明", _txtDescription),
            MakeRow("", _chkReadOnly)
        };
        // Dock.Top 是「後加者貼頂」，故 rows 要反序加入才會呈現由上而下的順序
        for (int i = 0; i <= rows.Count - 1 ; i++) editor.Controls.Add(rows[i]);

        var pnlListButtons = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 4, 8, 8) };
        // 同上：反序加入，呈現為「新增 → 刪除」
        pnlListButtons.Controls.Add(MakeButton("新增", (_, _) => { _lstConnections.ClearSelected(); ClearEditor(); },
                                               DockStyle.Right, 80));
        pnlListButtons.Controls.Add(MakeButton("刪除", DeleteSelected, DockStyle.Right, 80));

        var pnlList = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(8, 8, 4, 8) };
        pnlList.Controls.Add(_lstConnections);
        pnlList.Controls.Add(pnlListButtons);

        var pnlActions = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12, 10, 12, 10) };
        // Dock.Right 是「後加者靠最右」，故由左而右反序加入：測試連線 → 儲存 → 關閉
        pnlActions.Controls.Add(MakeButton("測試連線", TestCurrent, DockStyle.Right, 100));
        pnlActions.Controls.Add(MakeButton("儲存", SaveCurrent, DockStyle.Right, 100));
        pnlActions.Controls.Add(MakeButton("關閉", (_, _) => Close(), DockStyle.Right, 100));

        Controls.Add(editor);
        Controls.Add(pnlActions);
        Controls.Add(pnlList);
    }

    /// <summary>
    /// 建一列「標籤 + 輸入控件」，可選在右側附一顆按鈕。
    /// 標籤固定寬度讓各列輸入框左緣對齊。
    /// </summary>
    private static Panel MakeRow(string label, Control input, Control? trailing = null)
    {
        input.Dock = DockStyle.Fill;
        input.Font = new Font("Microsoft JhengHei UI", 10F);

        var lbl = new Label
        {
            Text = label,
            Dock = DockStyle.Left,
            Width = 100,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft JhengHei UI", 10F)
        };

        var row = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(0, 3, 0, 3) };
        row.Controls.Add(input);
        if (trailing is not null) row.Controls.Add(trailing);
        row.Controls.Add(lbl);
        return row;
    }

    private static Button MakeButton(string text, EventHandler onClick, DockStyle dock, int width)
    {
        var btn = new Button
        {
            Text = text,
            Dock = dock,
            Width = width,
            Font = new Font("Microsoft JhengHei UI", 10F)
        };
        btn.Click += onClick;
        return btn;
    }

    // ── 清單與編輯狀態 ────────────────────────────────────────────

    /// <summary>
    /// 清單項目。連線 ID 是隨機碼、看不出是哪個環境，故清單另外顯示說明或資料庫名稱，
    /// 但實際取值仍用 <see cref="Id"/>。
    /// </summary>
    private sealed record ConnItem(string Id, string Label)
    {
        public override string ToString() => $"{Id}　{Label}";
    }

    private void ReloadList()
    {
        _loading = true;
        try
        {
            _lstConnections.Items.Clear();
            foreach (var entry in _store.List())
            {
                var label = !string.IsNullOrWhiteSpace(entry.Description)
                    ? entry.Description
                    : entry.Database;
                _lstConnections.Items.Add(new ConnItem(entry.Id, label));
            }
        }
        finally { _loading = false; }
    }

    private void OnSelectionChanged()
    {
        if (_loading) return;
        if (_lstConnections.SelectedItem is not ConnItem item) return;

        var entry = _store.Get(item.Id);
        if (entry is null) return;

        _editingId = entry.Id;
        _txtId.Text = entry.Id;
        _cboProvider.SelectedItem = entry.Provider;
        _txtServer.Text = entry.Server;
        _txtPort.Text = entry.Port?.ToString() ?? "";
        _txtDatabase.Text = entry.Database;
        _txtUser.Text = entry.User;
        _txtPassword.Text = "";          // 密碼永不回填畫面
        _chkReadOnly.Checked = entry.ReadOnly;
        _txtDescription.Text = entry.Description;
    }

    private void ClearEditor()
    {
        _editingId = null;
        _txtId.Text = NewConnectionId();
        _cboProvider.SelectedIndex = 0;
        _txtServer.Text = "";
        _txtPort.Text = "";
        _txtDatabase.Text = "";
        _txtUser.Text = "";
        _txtPassword.Text = "";
        _chkReadOnly.Checked = true;     // 預設唯讀，要開放寫入必須是刻意決定
        _txtDescription.Text = "";
    }

    // ── 動作 ──────────────────────────────────────────────────────

    /// <summary>把畫面欄位收成 entry。密碼另外回傳，null 表示沿用原密碼。</summary>
    private (DbConnectionEntry Entry, string? PlainPassword) CollectInput()
    {
        var id = _txtId.Text.Trim();
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("請輸入連線 ID");
        if (string.IsNullOrWhiteSpace(_txtServer.Text)) throw new ArgumentException("請輸入主機");
        if (string.IsNullOrWhiteSpace(_txtDatabase.Text)) throw new ArgumentException("請輸入資料庫名稱");

        int? port = null;
        if (!string.IsNullOrWhiteSpace(_txtPort.Text))
        {
            if (!int.TryParse(_txtPort.Text.Trim(), out var p) || p is <= 0 or > 65535)
                throw new ArgumentException("埠必須是 1~65535 的數字");
            port = p;
        }

        var entry = new DbConnectionEntry
        {
            Id = id,
            Provider = (DbProvider)(_cboProvider.SelectedItem ?? DbProvider.Mssql),
            Server = _txtServer.Text.Trim(),
            Port = port,
            Database = _txtDatabase.Text.Trim(),
            User = _txtUser.Text.Trim(),
            ReadOnly = _chkReadOnly.Checked,
            Description = _txtDescription.Text.Trim()
        };

        // 新增時密碼必填；編輯時留空代表沿用原密碼
        var password = _txtPassword.Text;
        if (string.IsNullOrEmpty(password))
        {
            if (_editingId is null) throw new ArgumentException("請輸入密碼");
            return (entry, null);
        }
        return (entry, password);
    }

    private void SaveCurrent(object? sender, EventArgs e)
    {
        try
        {
            var (entry, password) = CollectInput();

            var renaming = _editingId is not null
                           && !string.Equals(_editingId, entry.Id, StringComparison.OrdinalIgnoreCase);

            // 新增時若撞到既有 ID，會覆寫掉別人的設定，先問過再說
            if (_editingId is null && _store.Get(entry.Id) is not null)
            {
                var overwrite = MessageBox.Show(this,
                    $"連線 ID「{entry.Id}」已存在，要覆寫嗎？", "ID 重複",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (overwrite != DialogResult.Yes) return;
            }

            // 改 ID 時沿用原密碼：Upsert 是依新 ID 查既有密文，查不到會變成空密碼，故先取回舊的
            if (renaming && password is null)
                entry.PasswordEnc = _store.Get(_editingId!)?.PasswordEnc ?? "";

            _store.Upsert(entry, password);

            // 改 ID 視為重新命名而非另存一份，故移除舊項目
            if (renaming) _store.Delete(_editingId!);
            ReloadList();
            _editingId = entry.Id;
            _txtPassword.Text = "";
            MessageBox.Show(this, $"已儲存連線「{entry.Id}」。", "SQL 連線設定",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "儲存失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteSelected(object? sender, EventArgs e)
    {
        if (_lstConnections.SelectedItem is not ConnItem item) return;

        var confirm = MessageBox.Show(this, $"確定刪除連線「{item}」？", "刪除確認",
                                      MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _store.Delete(item.Id);
        ReloadList();
        ClearEditor();
    }

    /// <summary>
    /// 產生 8 碼隨機連線 ID。刻意不用「prod」「vista-db」這類語意名稱——
    /// AI 無法從代號猜出正在操作哪個環境，也無法靠試錯撞出可用的 connId
    /// （工具本身不提供列舉，錯誤訊息也不列出可用代號）。
    /// 格式沿用專案既有的短碼慣例（見 AlarmEngine.NewId）。
    /// </summary>
    private static string NewConnectionId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 測試連線。尚未儲存也能測——用畫面上的值組一個暫時 entry，
    /// 密碼留空時沿用已儲存的密文。
    /// </summary>
    private void TestCurrent(object? sender, EventArgs e)
    {
        try
        {
            var (entry, password) = CollectInput();
            entry.PasswordEnc = password is not null
                ? _store.EncryptPassword(password)
                : _store.Get(_editingId ?? entry.Id)?.PasswordEnc ?? "";

            Cursor = Cursors.WaitCursor;
            try { _executor.TestConnection(entry); }
            finally { Cursor = Cursors.Default; }

            MessageBox.Show(this, "連線成功。", "測試連線",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "連線失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
