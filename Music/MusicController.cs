namespace KeelAssistant.Music;

/// <summary>音樂播放總管：維護單一 MusicForm，提供播放 / 停止。</summary>
public sealed class MusicController : IDisposable
{
    private MusicForm? _form;

    public bool IsPlaying { get; private set; }
    public string? CurrentUrl { get; private set; }

    public void Play(string url)
    {
        if (_form is null || _form.IsDisposed)
            _form = new MusicForm();

        _form.Play(url);
        IsPlaying = true;
        CurrentUrl = url;
    }

    public void Stop()
    {
        if (_form is not null && !_form.IsDisposed)
            _form.StopAndHide();
        IsPlaying = false;
        CurrentUrl = null;
    }

    // 2026/06/25 - Arlen - 修「結束無法關閉」：MusicForm 內含 WebView2，會孳生 msedgewebview2 子行程；
    // 結束時直接 Dispose（繞過 OnFormClosing 對 UserClosing 的取消）以真正釋放，避免背景殘留 - Start
    public void Dispose()
    {
        if (_form is not null && !_form.IsDisposed)
        {
            var f = _form;
            _form = null;
            try { f.Dispose(); } catch { }
        }
        IsPlaying = false;
        CurrentUrl = null;
    }
    // 2026/06/25 - Arlen - 修「結束無法關閉」- end
}
