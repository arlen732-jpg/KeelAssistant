using System.Text;

namespace KeelAssistant.Sandbox;

/// <summary>
/// 沙盒讀取實體檔案前的人工確認視窗。
///
/// 只在 read_paths 非空時彈出——純計算不彈。理由是沙盒（WASM）本身已隔離，
/// 誤傷風險為零，逐次確認只會養成不看內容就按確認的習慣，讓真正該看的那次也被略過。
/// 會落到磁碟的只有讀檔這一步（檔案內容會進入對話 context 並上傳），所以只守這裡。
///
/// 刻意不顯示 Python 程式碼：程式碼通常很長，你不會逐行看，
/// 只會稀釋對「它要讀哪些檔」這件事的注意力。
/// </summary>
public sealed class SandboxConfirmForm : Form
{
    private SandboxConfirmForm(string inputDir, IReadOnlyList<SandboxInputFile> files, string requestedAt)
    {
        // 沙盒 input 目錄以外的檔案＝Arlen 沒有事先表態要給沙盒用的檔案，視為需要特別警示
        var outside = files.Where(f => f.OutsideInputDir).ToArray();

        Text = "🐍 Keel Python 沙盒 — 檔案讀取確認";
        Width = 700;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        var total = files.Sum(f => f.Bytes);

        var lblHeader = new Label
        {
            Text = $"沙盒程式要求讀取 {files.Count} 個檔案（共 {total / 1024.0 / 1024.0:N1} MB）",
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(12, 10, 12, 0),
            Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold)
        };

        // 標上時間：SubAgent 在背景執行時，你需要判斷這個彈窗是誰、什麼時候要求的
        var lblWhen = new Label
        {
            Text = $"要求時間：{requestedAt}　·　沙盒 input：{inputDir}",
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(12, 4, 12, 0),
            ForeColor = Color.DimGray,
            Font = new Font("Microsoft JhengHei UI", 9F)
        };

        // 沙盒 input 以外的路徑用大紅字獨立警示：讀檔本身不會破壞任何東西，
        // 真正的風險是內容進入對話後被上傳，這一列就是要讓那件事無法被忽略。
        var lblWarn = new Label
        {
            Text = outside.Length == 0
                ? "全部來自沙盒 input 目錄。"
                : $"⚠ 其中 {outside.Length} 個檔案在沙盒目錄之外！",
            Dock = DockStyle.Top,
            Height = outside.Length == 0 ? 30 : 44,
            Padding = new Padding(12, 4, 12, 0),
            ForeColor = outside.Length == 0 ? Color.SeaGreen : Color.Red,
            Font = new Font("Microsoft JhengHei UI", outside.Length == 0 ? 10F : 14F, FontStyle.Bold)
        };

        var sb = new StringBuilder();
        foreach (var f in files)
        {
            var mark = f.OutsideInputDir ? "⚠ 沙盒外" : "  沙盒內";
            sb.AppendLine($"{mark}  {f.FullPath}");
            if (!string.Equals(f.FinalTarget, f.FullPath, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"          ↳ 實際指向 {f.FinalTarget}");
            sb.AppendLine($"          {f.Bytes / 1024.0:N1} KB");
        }

        var txtFiles = new TextBox
        {
            Text = sb.ToString(),
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.White,
            Font = new Font("Consolas", 10F)
        };

        var lblNote = new Label
        {
            Text = "檔案以唯讀方式複製進沙盒記憶體，原檔不會被修改或刪除。\r\n"
                 + "但檔案內容會出現在對話中並上傳到 Anthropic，請確認以上都不是機敏資料。",
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(12, 6, 12, 0),
            ForeColor = Color.Firebrick,
            Font = new Font("Microsoft JhengHei UI", 9F)
        };

        var btnDeny = new Button
        {
            Text = "拒絕",
            Dock = DockStyle.Right,
            Width = 120,
            DialogResult = DialogResult.Cancel,
            Font = new Font("Microsoft JhengHei UI", 10F)
        };

        var btnAllow = new Button
        {
            Text = "允許",
            Dock = DockStyle.Right,
            Width = 120,
            DialogResult = DialogResult.OK,
            Font = new Font("Microsoft JhengHei UI", 10F)
        };

        var pnlButtons = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12, 10, 12, 10) };
        // Dock.Right 後加者靠最右，故由左而右反序加入：允許 → 拒絕
        pnlButtons.Controls.Add(btnAllow);
        pnlButtons.Controls.Add(btnDeny);

        // Dock 同樣是後加者靠外，Fill 的清單要最先加
        Controls.Add(txtFiles);
        Controls.Add(pnlButtons);
        Controls.Add(lblNote);
        Controls.Add(lblWarn);
        Controls.Add(lblWhen);
        Controls.Add(lblHeader);

        AcceptButton = null;          // 不讓 Enter 直接放行
        CancelButton = btnDeny;
        ActiveControl = btnDeny;      // 預設焦點在拒絕，誤按空白鍵不會放行
    }

    /// <summary>顯示確認視窗並回傳是否核可。必須在 UI 執行緒呼叫（由 UiDispatcher 切入）。</summary>
    public static bool Confirm(string inputDir, IReadOnlyList<SandboxInputFile> files)
    {
        using var form = new SandboxConfirmForm(inputDir, files, DateTime.Now.ToString("yyyy\\/MM\\/dd HH:mm:ss"));
        form.Activate();
        return form.ShowDialog() == DialogResult.OK;
    }
}
