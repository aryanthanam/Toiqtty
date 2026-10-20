using Microsoft.UI.Windowing;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private bool _windowPresentationHidden,_powerSuspended,_renderVisibilityPending;
    private bool WindowRenderingActive=>!_windowPresentationHidden && !_powerSuspended && !_closed && !_closing;
    private void InitializeRenderingVisibility()
    {
        AppWindow.Changed+=RenderingWindowChanged;
        Root.Loaded+=(_,_)=>QueueRenderingVisibility();
        Closed+=(_,_)=>AppWindow.Changed-=RenderingWindowChanged;
    }
    private void RenderingWindowChanged(AppWindow sender,AppWindowChangedEventArgs args)=>QueueRenderingVisibility();
    private void QueueRenderingVisibility()
    {
        if(_closed || _closing || _renderVisibilityPending) return;
        _renderVisibilityPending=DispatcherQueue.TryEnqueue(()=> {
            _renderVisibilityPending=false;
            if(!_closed && !_closing) UpdateRenderingVisibility();
        });
    }
    private void UpdateRenderingVisibility(bool recreate=false)
    {
        var window=WinRT.Interop.WindowNative.GetWindowHandle(this);
        bool hidden=!IsWindowVisible(window) || IsIconic(window);
        if(Environment.GetEnvironmentVariable("TOIQTTY_VIEW_LIFECYCLE_TRACE")=="1")
            File.AppendAllText(Path.Combine(Models.SessionStore.Folder,"window-presentation.jsonl"),System.Text.Json.JsonSerializer.Serialize(new {
                nativeVisible=IsWindowVisible(window),nativeMinimized=IsIconic(window),AppWindow.IsVisible,
                presenter=(AppWindow.Presenter as OverlappedPresenter)?.State,hidden,recreate,ticks=System.Diagnostics.Stopwatch.GetTimestamp()
            })+Environment.NewLine);
        bool changed=hidden!=_windowPresentationHidden;
        _windowPresentationHidden=hidden;
        foreach(var retained in _terminalViews.Values) retained.View.SetWindowRenderingActive(WindowRenderingActive,recreate);
        if(changed && WindowRenderingActive) QueuePaneFocus();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
}
