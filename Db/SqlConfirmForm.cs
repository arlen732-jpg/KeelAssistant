using KeelAssistant.Models;

namespace KeelAssistant.Db;

/// <summary>
/// 寫入類 SQL 的人工確認視窗。Claude 對資料表結構的理解不見得正確
/// （WHERE 少一個條件就可能更新整張表），這是送進 DB 前的最後一道人工閘門。
///
/// 預設焦點放在「取消」、且視窗置頂，避免在無人察覺或誤按 Enter 的情況下放行。
/// 純查詢不會走到這裡（見 <see cref="SqlGuard"/>）。
/// </summary>
public sealed class SqlConfirmForm : Form
{
    private readonly TextBox _txtSql;
    private readonly string _rawSql;
    private readonly string _formattedSql;

    private SqlConfirmForm(DbConnectionEntry entry, string sql, string reason)
    {
        _rawSql = sql;
        _formattedSql = SqlFormatter.Format(sql);

        Text = "⚠️ Keel 要求執行 SQL";
        Width = 640;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        var lblHeader = new Label
        {
            Text = $"連線：{entry.Id}（{entry.Provider} · {entry.Database}）",
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(12, 10, 12, 0),
            Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold)
        };

        var lblReason = new Label
        {
            Text = $"判定：{reason}",
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(12, 4, 12, 0),
            ForeColor = Color.Firebrick,
            Font = new Font("Microsoft JhengHei UI", 9F)
        };

        _txtSql = new TextBox
        {
            Text = _formattedSql,
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.White,
            Font = new Font("Consolas", 10F)
        };

        // 排版只是為了好讀，實際送出的是原文——隨時可切回原始 SQL 核對
        var chkRaw = new CheckBox
        {
            Text = "顯示原始 SQL",
            Dock = DockStyle.Left,
            Width = 140,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft JhengHei UI", 9F)
        };
        chkRaw.CheckedChanged += (_, _) => _txtSql.Text = chkRaw.Checked ? _rawSql : _formattedSql;

        var btnCancel = new Button
        {
            Text = "取消",
            Dock = DockStyle.Right,
            Width = 120,
            DialogResult = DialogResult.Cancel,
            Font = new Font("Microsoft JhengHei UI", 10F)
        };

        var btnRun = new Button
        {
            Text = "執行",
            Dock = DockStyle.Right,
            Width = 120,
            DialogResult = DialogResult.OK,
            Font = new Font("Microsoft JhengHei UI", 10F)
        };

        var pnlButtons = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12, 10, 12, 10) };
        // Dock.Right 是「後加者靠最右」，故由左而右反序加入：執行 → 取消
        pnlButtons.Controls.Add(btnRun);
        pnlButtons.Controls.Add(btnCancel);
        pnlButtons.Controls.Add(chkRaw);

        // Dock 同樣是後加者靠外，故 Fill 的 SQL 內容要最先加
        Controls.Add(_txtSql);
        Controls.Add(pnlButtons);
        Controls.Add(lblReason);
        Controls.Add(lblHeader);

        AcceptButton = null;              // 不讓 Enter 直接放行
        CancelButton = btnCancel;
        ActiveControl = btnCancel;        // 預設焦點在取消，誤按空白鍵不會執行
    }

    /// <summary>
    /// 顯示確認視窗並回傳是否核可。必須在 UI 執行緒呼叫（由 UiDispatcher 切入）。
    /// </summary>
    public static bool Confirm(DbConnectionEntry entry, string sql, string reason)
    {
        using var form = new SqlConfirmForm(entry, sql, reason);
        form.Activate();
        return form.ShowDialog() == DialogResult.OK;
    }
}
