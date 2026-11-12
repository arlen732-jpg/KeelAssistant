using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KeelAssistant.Ssh;

/// <summary>
/// SSH 可視終端視窗：以 WebView2 內嵌 xterm.js，讓遠端 PTY 的輸出（含 ANSI 色彩、
/// 進度條、游標控制）能正確呈現，而不是一堆 <c>ESC[0m</c> 亂碼。
///
/// 兩個設計重點：
///   1. <b>雙向</b>——除了看，Arlen 還能直接在視窗裡打字。哨符機制在遇到互動式程式
///      （nano、apt 的設定檔衝突對話框）時必然失效，那時唯一的出路就是人工接手，
///      所以這個視窗不能是唯讀的日誌檢視器。
///   2. <b>常駐可重複開啟</b>——比照原 MusicForm（已移除）攔截使用者關閉改為隱藏。
///      按 X 就 Dispose 掉 WebView2 的話，下次執行指令要整組重建，終端歷史也沒了。
///
/// 專屬的 webview2-ssh user data folder：與音樂、Python 沙盒分屬不同行程樹，
/// 三者的建立與銷毀互不影響（沿用 CLAUDE.md 對沙盒 WebView2 的既有約束）。
/// </summary>
public sealed class SshTerminalForm : Form
{
    private readonly WebView2 _web;
    private readonly Action<byte[]> _onInput;
    private readonly int _cols;
    private readonly int _rows;
    private readonly int _fontSize;

    private bool _ready;
    private readonly List<byte[]> _pending = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 建立終端視窗。必須在 UI 執行緒呼叫。
    /// </summary>
    /// <param name="onInput">使用者在終端內按鍵時觸發，參數為要送往遠端的原始位元組。</param>
    public SshTerminalForm(string title, int cols, int rows, int fontSize, Action<byte[]> onInput)
    {
        _onInput = onInput;
        _cols = cols;
        _rows = rows;
        _fontSize = fontSize;

        Text = $"🖥️ Keel SSH — {title}";
        // 依欄列數粗估視窗尺寸（等寬字約 0.6 倍字高寬），再讓 xterm 自行微調
        Width = Math.Min(1600, (int)(cols * fontSize * 0.62) + 40);
        Height = Math.Min(1000, rows * (fontSize + 5) + 60);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
    }

    /// <summary>WebView2 與 xterm.js 是否已就緒。未就緒時 <see cref="Write"/> 會先暫存。</summary>
    public bool Ready => _ready;

    /// <summary>
    /// 初始化 WebView2 並載入終端頁面。必須在 UI 執行緒呼叫，且視窗需先 Show
    /// （WebView2 在未顯示的視窗上不會完成初始化）。
    /// </summary>
    public async Task InitAsync()
    {
        var userDataFolder = Path.Combine(AppContext.BaseDirectory, "webview2-ssh");
        var contentRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "ssh");

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        // 用虛擬主機而非 file://：xterm.js 以 module 載入時 file:// 會被 CORS 擋下。
        // 資源全部來自本機資料夾，不會有任何連外請求。
        core.SetVirtualHostNameToFolderMapping(
            "keel-ssh", contentRoot, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate($"https://keel-ssh/index.html?cols={_cols}&rows={_rows}&font={_fontSize}");
    }

    /// <summary>
    /// 把遠端來的位元組寫進終端。必須在 UI 執行緒呼叫。
    /// 尚未就緒時先暫存，等頁面回報 ready 後補寫，避免開視窗瞬間的輸出被吃掉。
    /// </summary>
    public void Write(byte[] data)
    {
        if (data.Length == 0) return;

        if (!_ready)
        {
            _pending.Add(data);
            return;
        }

        Post("data", Convert.ToBase64String(data));
    }

    /// <summary>寫入一行由本程式產生的提示文字（非遠端輸出），用於標示即將執行的指令。</summary>
    public void WriteLocalLine(string text)
    {
        // \r\n 而非 \n：PTY 模式下游標不會自動回到行首
        Write(System.Text.Encoding.UTF8.GetBytes($"\r\n[36m{text}[0m\r\n"));
    }

    private void Post(string kind, string b64)
    {
        try
        {
            _web.CoreWebView2?.PostWebMessageAsString(
                JsonSerializer.Serialize(new { kind, b64 }, JsonOpts));
        }
        catch { /* 視窗正在銷毀，丟棄即可 */ }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.TryGetWebMessageAsString(); }
        catch { return; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;

            if (kind == "ready")
            {
                _ready = true;
                foreach (var buf in _pending) Post("data", Convert.ToBase64String(buf));
                _pending.Clear();
                return;
            }

            if (kind != "input") return;

            var b64 = doc.RootElement.TryGetProperty("b64", out var d) ? d.GetString() : null;
            if (b64 is null) return;

            _onInput(Convert.FromBase64String(b64));
        }
        catch { /* 壞訊息忽略，不讓終端頁面的問題波及主程式 */ }
    }

    /// <summary>
    /// 攔截使用者關閉，改為隱藏並保留 WebView2 實例。
    /// ⚠ 不可移除：按 X 直接 Dispose 會讓下次執行指令時整組重建，終端歷史與捲動緩衝全失。
    /// 真正要關閉請呼叫 <see cref="ForceClose"/>（由 ssh_close 與程式結束時走）。
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>真正關閉並釋放。必須在 UI 執行緒呼叫。</summary>
    public void ForceClose()
    {
        try
        {
            if (_web.CoreWebView2 is not null)
                _web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
        catch { }
        try { _web.Dispose(); } catch { }
        try { Close(); Dispose(); } catch { }
    }
}
