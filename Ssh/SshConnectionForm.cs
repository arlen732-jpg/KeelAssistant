using KeelAssistant.Models;

namespace KeelAssistant.Ssh;

/// <summary>
/// SSH 連線設定視窗（托盤選單「SSH 連線設定」）。
///
/// 這是維護 sshconnections.json 的<b>唯一</b>途徑——手動編輯該檔無法產生正確的 DPAPI 密文。
/// 密碼／passphrase 欄位永遠顯示為空白，留空即代表「沿用原值」；
/// 已儲存的密文不回填到畫面上，避免密碼在記憶體與畫面上多停留一份。
/// </summary>
public sealed class SshConnectionForm : Form
{
    private readonly SshConnectionStore _store;

    private readonly ListBox _list = new();
    private readonly TextBox _txtId = new();
    private readonly TextBox _txtHost = new();
    private readonly TextBox _txtPort = new();
    private readonly TextBox _txtUser = new();
    private readonly ComboBox _cmbAuth = new();
    private readonly TextBox _txtKeyPath = new();
    private readonly TextBox _txtSecret = new();
    private readonly TextBox _txtDesc = new();
    private readonly Label _lblFingerprint = new();

    private bool _loading;

    public SshConnectionForm(SshConnectionStore store)
    {
        _store = store;

        Text = "🔐 SSH 連線設定";
        Width = 760;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        var font = new Font("Microsoft JhengHei UI", 9.5F);
        Font = font;

        // ── 左側清單 ──
        _list.Dock = DockStyle.Left;
        _list.Width = 220;
        _list.IntegralHeight = false;
        _list.SelectedIndexChanged += (_, _) => LoadSelected();

        // ── 右側編輯區 ──
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10) };

        int y = 8;
        AddRow(panel, "代號 (sshId)", _txtId, ref y);
        AddRow(panel, "主機位址", _txtHost, ref y);
        AddRow(panel, "連接埠", _txtPort, ref y);
        AddRow(panel, "登入帳號", _txtUser, ref y);

        _cmbAuth.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAuth.Items.AddRange(new object[] { "私鑰檔（建議）", "密碼" });
        _cmbAuth.SelectedIndexChanged += (_, _) => SyncAuthMode();
        AddRow(panel, "認證方式", _cmbAuth, ref y);

        AddRow(panel, "私鑰檔路徑", _txtKeyPath, ref y);
        AddRow(panel, "密碼／passphrase", _txtSecret, ref y);
        _txtSecret.UseSystemPasswordChar = true;
        AddRow(panel, "說明", _txtDesc, ref y);

        _lblFingerprint.SetBounds(120, y + 6, 560, 40);
        _lblFingerprint.ForeColor = Color.DimGray;
        panel.Controls.Add(_lblFingerprint);

        // ── 底部按鈕 ──
        var btnNew = MakeButton("新增", (_, _) => ClearFields());
        var btnSave = MakeButton("儲存", (_, _) => Save());
        var btnDelete = MakeButton("刪除", (_, _) => Delete());
        var btnForget = MakeButton("重置主機指紋", (_, _) => ForgetFingerprint());
        btnForget.Width = 140;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(14, 8, 14, 8)
        };
        buttons.Controls.AddRange(new Control[] { btnNew, btnSave, btnDelete, btnForget });

        Controls.Add(panel);
        Controls.Add(buttons);
        Controls.Add(_list);

        Refresh();
        SyncAuthMode();
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var btn = new Button { Text = text, Width = 100, Height = 34 };
        btn.Click += onClick;
        return btn;
    }

    private static void AddRow(Control parent, string label, Control input, ref int y)
    {
        parent.Controls.Add(new Label
        {
            Text = label,
            Bounds = new Rectangle(0, y + 4, 115, 22)
        });
        input.SetBounds(120, y, 380, 24);
        parent.Controls.Add(input);
        y += 32;
    }

    private void SyncAuthMode()
    {
        var isKey = _cmbAuth.SelectedIndex == 0;
        _txtKeyPath.Enabled = isKey;
    }

    private new void Refresh()
    {
        _list.Items.Clear();
        foreach (var e in _store.List())
            _list.Items.Add($"{e.Id}  —  {e.Display}");
    }

    private void LoadSelected()
    {
        if (_list.SelectedIndex < 0) return;

        var entries = _store.List();
        if (_list.SelectedIndex >= entries.Count) return;

        _loading = true;
        var e = entries[_list.SelectedIndex];
        _txtId.Text = e.Id;
        _txtHost.Text = e.Host;
        _txtPort.Text = e.Port.ToString();
        _txtUser.Text = e.User;
        _cmbAuth.SelectedIndex = e.AuthMode == SshAuthMode.PrivateKey ? 0 : 1;
        _txtKeyPath.Text = e.KeyPath;
        _txtSecret.Text = "";          // 密文不回填，留空＝沿用原值
        _txtDesc.Text = e.Description;
        _lblFingerprint.Text = string.IsNullOrEmpty(e.HostKeyFingerprint)
            ? "主機指紋：尚未連線過（首次連線時記錄，TOFU）"
            : $"主機指紋：SHA256:{e.HostKeyFingerprint}";
        _loading = false;
        SyncAuthMode();
    }

    private void ClearFields()
    {
        _list.SelectedIndex = -1;
        _txtId.Text = _txtHost.Text = _txtUser.Text = _txtKeyPath.Text = _txtSecret.Text = _txtDesc.Text = "";
        _txtPort.Text = "22";
        _cmbAuth.SelectedIndex = 0;
        _lblFingerprint.Text = "主機指紋：尚未連線過（首次連線時記錄，TOFU）";
    }

    private void Save()
    {
        if (_loading) return;

        try
        {
            if (!int.TryParse(_txtPort.Text.Trim(), out var port) || port is < 1 or > 65535)
                throw new ArgumentException("連接埠必須是 1~65535 的數字");

            var entry = new SshConnectionEntry
            {
                Id = _txtId.Text.Trim(),
                Host = _txtHost.Text.Trim(),
                Port = port,
                User = _txtUser.Text.Trim(),
                AuthMode = _cmbAuth.SelectedIndex == 0 ? SshAuthMode.PrivateKey : SshAuthMode.Password,
                KeyPath = _txtKeyPath.Text.Trim(),
                Description = _txtDesc.Text.Trim(),
                // 保留既有指紋：改主機位址以外的欄位不該讓 TOFU 記錄失效
                HostKeyFingerprint = _store.Get(_txtId.Text.Trim())?.HostKeyFingerprint ?? ""
            };

            // 留空＝沿用原密文（傳 null），有輸入才重新加密
            var secret = _txtSecret.Text.Length == 0 ? null : _txtSecret.Text;

            _store.Upsert(entry, secret);
            _txtSecret.Text = "";
            Refresh();
            MessageBox.Show(this, $"已儲存連線「{entry.Id}」。", "SSH 連線設定",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "儲存失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Delete()
    {
        var id = _txtId.Text.Trim();
        if (id.Length == 0) return;

        if (MessageBox.Show(this, $"確定刪除連線「{id}」？", "SSH 連線設定",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _store.Delete(id);
        ClearFields();
        Refresh();
    }

    private void ForgetFingerprint()
    {
        var id = _txtId.Text.Trim();
        if (id.Length == 0) return;

        if (MessageBox.Show(this,
                $"確定重置「{id}」的主機指紋？\n\n只有在你確知對端主機真的換過（重灌、換機）時才該這麼做。" +
                "若不是，指紋不符代表連線可能遭攔截。",
                "重置主機指紋", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        _store.ForgetHostKey(id);
        LoadSelected();
    }
}
