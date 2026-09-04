namespace KeelAssistant.Notifications;

/// <summary>
/// 命中關鍵人／被 @ 時彈出的置頂提醒視窗。
///
/// 刻意覆寫 <see cref="ShowWithoutActivation"/>：視窗要「浮在最上層讓人看見」，
/// 但不能搶走鍵盤焦點——正在打字或簡報時被奪焦，比漏看訊息更擾人。
/// </summary>
public sealed class MentionAlertForm : Form
{
    private readonly Action _onClose;

    /// <summary>顯示時不啟動（不搶焦點），配合 TopMost 達成「看得見但不打斷」。</summary>
    protected override bool ShowWithoutActivation => true;

    public MentionAlertForm(string reason, string title, string body, DateTime at, Action onClose)
    {
        _onClose = onClose;

        Text = "💬 " + reason;
        Width = 460;
        Height = 300;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        var lblReason = new Label
        {
            Text = reason,
            Dock = DockStyle.Top,
            Height = 34,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Firebrick,
            ForeColor = Color.White,
            Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold)
        };

        var lblTitle = new Label
        {
            Text = string.IsNullOrWhiteSpace(title) ? "（無標題）" : title,
            Dock = DockStyle.Top,
            Height = 44,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft JhengHei UI", 15F, FontStyle.Bold)
        };

        var lblTime = new Label
        {
            Text = at.ToString("yyyy\\/MM\\/dd HH:mm:ss"),
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray
        };

        // 訊息可能很長，用唯讀多行文字框而非 Label，才有捲軸可看全文
        var txtBody = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(body) ? "（無內容預覽，可能已關閉 LINE 的訊息預覽）" : body,
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            ScrollBars = ScrollBars.Vertical,
            TabStop = false,
            Font = new Font("Microsoft JhengHei UI", 11F)
        };

        var btnOk = new Button
        {
            Text = "知道了",
            Dock = DockStyle.Bottom,
            Height = 46,
            Font = new Font("Microsoft JhengHei UI", 12F, FontStyle.Bold)
        };
        btnOk.Click += (_, _) => Close();

        // Dock 由後加者先佔位：Fill 必須最先加（z-order 最底），其餘由外而內依序往上疊
        Controls.Add(txtBody);
        Controls.Add(btnOk);
        Controls.Add(lblTime);
        Controls.Add(lblTitle);
        Controls.Add(lblReason);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _onClose();
        base.OnFormClosed(e);
    }
}
