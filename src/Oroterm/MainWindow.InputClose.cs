using Microsoft.UI.Windowing;
using Oroterm.Hosting;

namespace Oroterm;

public sealed partial class MainWindow
{
    private bool _inputClosePending,_inputCloseReady;
    public new void Close()
    {
        if(_inputCloseReady) base.Close();
        else StartInputClose();
    }
    private void BeginWindowClose(AppWindowClosingEventArgs args)
    {
        _closing=true;
        if(_inputCloseReady) return;
        args.Cancel=true;
        StartInputClose();
    }
    private void StartInputClose()
    {
        _closing=true;
        if(_inputClosePending) return;
        _inputClosePending=true; _metadataTimer.Stop();
        foreach(var view in _views) view.ReleaseInputForClose();
        Save();
        Root.IsHitTestVisible=false;
        var drains=_saved.Workspaces.SelectMany(w=>w.AllPanes()).Select(p=>p.Session)
            .OfType<RemoteTerminalSession>().Select(remote=>remote.CompleteInput()).ToArray();
        // Re-enter Close only after this native Closing callback has returned.
        DispatcherQueue.TryEnqueue(async ()=>
        {
            await Task.WhenAll(drains);
            await ObserveSave(_sessionPersistence.FlushAsync());
            await ObserveSave(_sessionThemes.FlushAsync());
            await _sessionLifetimes.DrainAsync();
            if(_persistent)
            {
                await SessionLifetimeCoordinator.DetachAsync(_saved.Workspaces.SelectMany(w=>w.AllPanes()));
                try { if(_host is not null) await _host.CallAsync(new() { Operation="detach" }); }
                catch(Exception ex) when(ex is IOException or OperationCanceledException) { System.Diagnostics.Debug.WriteLine(ex); }
            }
            _inputCloseReady=true; base.Close();
        });
    }
}
