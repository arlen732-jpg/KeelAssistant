using Microsoft.Web.WebView2.WinForms;

namespace KeelAssistant.Music;

/// <summary>內嵌 WebView2 的音樂播放視窗（線上服務，如 YouTube）。</summary>
public sealed class MusicForm : Form
{
    private readonly WebView2 _web;
    private bool _ready;
    private string? _pendingUrl;

    public MusicForm()
    {
        Text = "🎵 Keel 音樂";
        Width = 960;
        Height = 600;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        // 2026/06/24 - Arlen - 視窗圖示套用 assets\app.ico（原 InitializeComponent 未被呼叫，沿用 WinForms 預設圖示）- Start
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);
        // 2026/06/24 - Arlen - 視窗圖示套用 assets\app.ico - end

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);

        InitAsync();
    }

    private async void InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
            _ready = true;
            if (_pendingUrl is not null)
            {
                _web.CoreWebView2.Navigate(_pendingUrl);
                _pendingUrl = null;
            }
        }
        catch
        {
            // WebView2 Runtime 未安裝時靜默失敗，由呼叫端處理
        }
    }

    /// <summary>導航至指定網址播放。WebView2 尚未就緒時先暫存，初始化完成後補播。</summary>
    public void Play(string url)
    {
        if (_ready && _web.CoreWebView2 is not null)
            _web.CoreWebView2.Navigate(url);
        else
            _pendingUrl = url;

        Show();
        Activate();
    }

    /// <summary>停止播放：導向空白頁中止聲音，並隱藏視窗。</summary>
    public void StopAndHide()
    {
        _pendingUrl = null;
        if (_ready && _web.CoreWebView2 is not null)
        {
            try { _web.CoreWebView2.Navigate("about:blank"); } catch { }
        }
        Hide();
    }

    // 使用者按關閉時不真的銷毀，改為停止 + 隱藏（保留 WebView2 實例）
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            StopAndHide();
            return;
        }
        base.OnFormClosing(e);
    }
}
