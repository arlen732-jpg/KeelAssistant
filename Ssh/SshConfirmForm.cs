using KeelAssistant.Models;

namespace KeelAssistant.Ssh;

/// <summary>
/// 遠端指令的人工確認視窗。<b>每一條</b> ssh_exec 都會經過這裡。
///
/// 為什麼不像 Python 沙盒那樣只在特定情況才問：沙盒的隔離來自 WASM，誤傷風險本來就趨近於零；
/// SQL 也還有唯讀帳號與唯讀交易兩層兜底。SSH 沒有任何隔離層——送過去就是遠端主機上的真實指令，
/// <c>sudo rm -rf</c> 打過去就是真的執行。這個視窗是唯一的防線，因此不做白名單、不做分級。
///
/// 也刻意不嘗試用啟發式規則判斷「這條指令危不危險」：shell 的語法（管線、$()、&amp;&amp;、別名、eval）
/// 比 SQL 難分類一個數量級，寫出來的分類器只會給人一種有把關的錯覺。
///
/// 預設焦點放在「取消」、視窗置頂，避免在無人察覺或誤按 Enter 的情況下放行。
/// </summary>
public sealed class SshConfirmForm : Form
{
    private SshConfirmForm(SshConnectionEntry entry, string command, string cwd)
    {
        Text = "⚠️ Keel 要求在遠端執行指令";
        Width = 700;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        var lblTarget = new Label
        {
            Text = $"目標主機：{entry.Id}　（{entry.Display}）\n" +
                   $"工作目錄：{(string.IsNullOrEmpty(cwd) ? "（尚未連線，將使用登入預設目錄）" : cwd)}",
            Dock = DockStyle.Top,
            Height = 60,
            Padding = new Padding(14, 12, 14, 0),
            Font = new Font("Microsoft JhengHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(160, 40, 40)
        };

        var lblHint = new Label
        {
            Text = "遠端指令沒有任何隔離層，送出即在該主機上真實執行。請逐字確認：",
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(14, 6, 14, 0),
            Font = new Font("Microsoft JhengHei UI", 9F)
        };

        var txtCommand = new TextBox
        {
            Text = command,
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Font = new Font("Cascadia Mono", 11F),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle
        };

        var panelCommand = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 8) };
        panelCommand.Controls.Add(txtCommand);

        var btnDeny = new Button
        {
            Text = "取消（不執行）",
            Dock = DockStyle.Right,
            Width = 160,
            Height = 46,
            DialogResult = DialogResult.Cancel,
            Font = new Font("Microsoft JhengHei UI", 10F)
        };

        var btnAllow = new Button
        {
            Text = "允許執行",
            Dock = DockStyle.Right,
            Width = 140,
            Height = 46,
            DialogResult = DialogResult.OK,
            Font = new Font("Microsoft JhengHei UI", 10F)
        };

        var panelButtons = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(14, 6, 14, 8) };
        // Dock.Right 的加入順序決定由右到左的排列：先加的在最右邊
        panelButtons.Controls.Add(btnDeny);
        panelButtons.Controls.Add(btnAllow);

        // 控制項 Dock 由下往上疊：先加底部按鈕，再加中央內容，最後加頂部標籤
        Controls.Add(panelCommand);
        Controls.Add(panelButtons);
        Controls.Add(lblHint);
        Controls.Add(lblTarget);

        AcceptButton = null;          // 不讓 Enter 直接放行
        CancelButton = btnDeny;
        ActiveControl = btnDeny;      // 預設焦點在取消，誤按空白鍵不會放行
    }

    /// <summary>顯示確認視窗並回傳是否核可。必須在 UI 執行緒呼叫（由 UiDispatcher 切入）。</summary>
    public static bool Confirm(SshConnectionEntry entry, string command, string cwd)
    {
        using var form = new SshConfirmForm(entry, command, cwd);
        form.Activate();
        return form.ShowDialog() == DialogResult.OK;
    }
}
