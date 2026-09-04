namespace KeelAssistant.Alarm;

/// <summary>鬧鈴響起時彈出的置頂視窗，提供「停止」按鈕。</summary>
public sealed class AlarmForm : Form
{
    private readonly Action _onStop;

    public AlarmForm(string label, DateTime firedAt, Action onStop)
    {
        _onStop = onStop;

        Text = "⏰ Keel 鬧鐘";
        Width = 360;
        Height = 200;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        // 2026/06/24 - Arlen - 視窗圖示套用 assets\app.ico（原 InitializeComponent 未被呼叫，沿用 WinForms 預設圖示）- Start
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);
        // 2026/06/24 - Arlen - 視窗圖示套用 assets\app.ico - end

        var lblTitle = new Label
        {
            Text = string.IsNullOrWhiteSpace(label) ? "時間到囉！" : label,
            Dock = DockStyle.Top,
            Height = 70,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft JhengHei UI", 16F, FontStyle.Bold)
        };

        var lblTime = new Label
        {
            Text = firedAt.ToString("yyyy\\/MM\\/dd HH:mm:ss"),
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray
        };

        var btnStop = new Button
        {
            Text = "停止鬧鈴",
            Dock = DockStyle.Bottom,
            Height = 50,
            Font = new Font("Microsoft JhengHei UI", 12F, FontStyle.Bold)
        };
        btnStop.Click += (_, _) => Close();

        // 控制項 Dock 由下往上疊：先加底部按鈕，再加上方標籤
        Controls.Add(btnStop);
        Controls.Add(lblTime);
        Controls.Add(lblTitle);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _onStop();
        base.OnFormClosed(e);
    }
}
