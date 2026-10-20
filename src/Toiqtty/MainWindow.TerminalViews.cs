using Microsoft.UI.Xaml.Controls;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private const int MaxHiddenTerminalViews=8;
    private const long MaxHiddenTerminalGpuBytes=128L*1024*1024;
    private sealed record RetainedTerminal(TerminalView View,long LastUsed);
    private readonly Dictionary<string,RetainedTerminal> _terminalViews=[];
    private long _terminalUseSequence;
    private int _terminalViewsCreated,_terminalViewsEvicted;
    private bool _syntheticInputOnlyForCheck;

    private TerminalView GetTerminalView(PaneNode node,Workspace workspace)
    {
        if(_terminalViews.TryGetValue(node.Id,out var retained))
        {
            if(ReferenceEquals(retained.View.Session,node.Session))
            {
                retained.View.Bind(node,_saved.Settings);
                retained.View.SetSessionTheme(ThemeFor(workspace));
                retained.View.SetWallpaper(_wallpaperImage,_saved.Settings.Wallpaper);
                retained.View.SetWindowRenderingActive(WindowRenderingActive);
                _terminalViews[node.Id]=retained with { LastUsed=++_terminalUseSequence };
                return retained.View;
            }
            retained.View.Dispose(); _terminalViews.Remove(node.Id);
        }
        var view=new TerminalView(node,_saved.Settings);
        view.SetSessionTheme(ThemeFor(workspace));
        view.SetWallpaper(_wallpaperImage,_saved.Settings.Wallpaper);
        if(_syntheticInputOnlyForCheck) view.UseSyntheticInputOnlyForCheck();
        view.SetWindowRenderingActive(WindowRenderingActive);
        if(workspace==_hardwareWorkspace) view.ValidationObserved+=(kind,text)=>RecordHardwareEvent(kind,text);
        view.Activated+=FocusView;
        view.HyperlinkRequested+=uri=> { if(_saved.Settings.Browser.OpenTerminalLinks) NewBrowser(uri.AbsoluteUri); else _=Windows.System.Launcher.LaunchUriAsync(uri); };
        view.FontSizeChanged+=()=> { foreach(var pane in _views) pane.ApplySettings(); UpdateStatusBar(); Save(); };
        _terminalViews.Add(node.Id,new(view,++_terminalUseSequence)); _terminalViewsCreated++;
        return view;
    }
    private void DetachViews()
    {
        SuspendActiveLayout();
        foreach(var id in _tabLayouts.Keys.ToArray()) ForgetTabLayout(id);
        _paneDragSurfaces.Clear();
        foreach(var view in _views)
        {
            view.SuspendForReuse();
            if(view.Parent is Panel parent) parent.Children.Remove(view);
        }
        _views.Clear(); _focused=null; _focusedBrowser=null;
        foreach(var browser in _browsers.Values) if(browser.Parent is Panel parent) parent.Children.Remove(browser);
        PaneHost.Children.Clear(); PruneBrowsers();
    }
    private void ForgetTerminalViews(IEnumerable<PaneNode> panes)
    {
        var ids=panes.Select(p=>p.Id).ToHashSet();
        foreach(var id in _tabLayouts.Where(p=>p.Value.Terminals.Any(v=>ids.Contains(v.PaneId))
            || p.Value.Browsers.Any(v=>ids.Contains(v.PaneId))).Select(p=>p.Key).ToArray()) ForgetTabLayout(id);
        foreach(var id in ids)
            if(_terminalViews.Remove(id,out var retained)) retained.View.Dispose();
    }
    private void PruneTerminalViews()
    {
        var identities=_saved.Workspaces.SelectMany(w=>w.AllPanes()).Where(p=>!p.IsBrowser).Select(p=>p.Id).ToHashSet();
        foreach(var id in _terminalViews.Keys.Where(id=>!identities.Contains(id)).ToArray())
            if(_terminalViews.Remove(id,out var stale)) stale.View.Dispose();
        var visible=_views.ToHashSet();
        var hidden=_terminalViews.Where(pair=>!visible.Contains(pair.Value.View)).OrderBy(pair=>pair.Value.LastUsed).ToArray();
        int count=hidden.Length; long bytes=hidden.Sum(pair=>pair.Value.View.RetainedGpuBytes);
        foreach(var pair in hidden)
        {
            if(count<=MaxHiddenTerminalViews && bytes<=MaxHiddenTerminalGpuBytes) break;
            bytes-=pair.Value.View.RetainedGpuBytes; count--;
            _terminalViews.Remove(pair.Key); pair.Value.View.Dispose(); _terminalViewsEvicted++;
        }
        PruneTabLayouts();
    }
    private void DisposeViews()
    {
        DetachViews();
        foreach(var retained in _terminalViews.Values) retained.View.Dispose();
        _terminalViews.Clear();
    }
}
