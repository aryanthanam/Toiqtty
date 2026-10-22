using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private sealed record RetainedTabLayout(string Signature, FrameworkElement Root,
        TerminalView[] Terminals, BrowserView[] Browsers,
        Dictionary<string,(Grid Host, Grid HitArea, Border Preview, ContentControl Handle)> DragSurfaces)
    {
        public long LastShown { get; set; }
    }
    private readonly Dictionary<string,RetainedTabLayout> _tabLayouts=[];
    private RetainedTabLayout? _activeLayout;
    private int _tabLayoutsCreated;
    private long _tabLayoutShowSequence;
    private void RecordSplitLayout(Workspace workspace)
    {
        if(_activeLayout is not { } active || !_tabLayouts.TryGetValue(workspace.ActiveTab.Id,out var cached) || !ReferenceEquals(active,cached)) return;
        var updated=active with { Signature=LayoutSignature(workspace.Root)+"|" };
        _tabLayouts[workspace.ActiveTab.Id]=updated; _activeLayout=updated;
    }
    private void TraceTabLayout(string action,string id,string signature="")
    {
        if(Environment.GetEnvironmentVariable("TOIQTTY_VIEW_LIFECYCLE_TRACE")!="1") return;
        File.AppendAllText(Path.Combine(SessionStore.Folder,"tab-layouts.jsonl"),System.Text.Json.JsonSerializer.Serialize(new {
            action,id,signature,active=_activeLayout?.Signature,views=_views.Select(v=>v.PaneId).ToArray()
        })+Environment.NewLine);
    }

    private static string LayoutSignature(PaneNode node) => node.IsLeaf
        ? node.Id+":"+node.Kind
        : node.Id+":"+node.Vertical+":"+node.Ratio.ToString("R",System.Globalization.CultureInfo.InvariantCulture)
            +"("+LayoutSignature(node.First!)+","+LayoutSignature(node.Second!)+")";

    private void SuspendActiveLayout()
    {
        foreach(var view in _views) view.SuspendForReuse();
        if(_activeLayout is { } layout)
        {
            layout.Root.Visibility=Visibility.Collapsed;
            foreach(var browser in layout.Browsers) browser.IsActive=false;
        }
        _activeLayout=null; _views.Clear(); _paneDragSurfaces.Clear(); _focused=null; _focusedBrowser=null;
    }

    private void ForgetTabLayout(string tabId)
    {
        if(!_tabLayouts.Remove(tabId,out var layout)) return;
        TraceTabLayout("forget",tabId,layout.Signature);
        if(ReferenceEquals(_activeLayout,layout)) SuspendActiveLayout();
        // A pane may move to another tab. Release its old wrapper before reuse.
        foreach(var view in layout.Terminals.Cast<FrameworkElement>().Concat(layout.Browsers))
            if(view.Parent is Panel parent) parent.Children.Remove(view);
        PaneHost.Children.Remove(layout.Root);
    }

    private void PruneTabLayouts()
    {
        var tabs=_saved.Workspaces.SelectMany(w=>w.Tabs).ToDictionary(t=>t.Id);
        foreach(var pair in _tabLayouts.ToArray())
        {
            var layout=pair.Value;
            if(!tabs.TryGetValue(pair.Key,out var tab)
                || !layout.Signature.StartsWith(LayoutSignature(tab.Root)+"|",StringComparison.Ordinal)
                || layout.Terminals.Any(v=>v.IsDisposedForCheck)
                || layout.Browsers.Any(v=>!_browsers.TryGetValue(v.PaneId,out var current) || !ReferenceEquals(v,current)))
                ForgetTabLayout(pair.Key);
        }
        // Browser-only roots also need a bound, independent of terminal GPU budgets.
        // Evict the least recently shown roots, not the oldest created.
        foreach(var id in _tabLayouts.Where(p=>!ReferenceEquals(p.Value,_activeLayout)).OrderBy(p=>p.Value.LastShown).Select(p=>p.Key)
            .Take(Math.Max(0,_tabLayouts.Count-1-MaxHiddenTerminalViews)).ToArray()) ForgetTabLayout(id);
    }

    private void ShowTabLayout(Workspace workspace,PaneNode root,string? zoomId)
    {
        SuspendActiveLayout();
        PruneTabLayouts();
        string id=workspace.ActiveTab.Id, signature=LayoutSignature(workspace.Root)+"|"+zoomId;
        if(_tabLayouts.TryGetValue(id,out var cached) && cached.Signature!=signature) ForgetTabLayout(id);
        if(!_tabLayouts.TryGetValue(id,out var layout))
        {
            // Empty-state content is not a retained tab root.
            foreach(var child in PaneHost.Children.Where(child=>!_tabLayouts.Values.Any(item=>ReferenceEquals(item.Root,child))).ToArray())
                PaneHost.Children.Remove(child);
            var element=BuildNode(root,workspace);
            layout=new(signature,element,_views.ToArray(),root.Leaves().Where(p=>p.IsBrowser)
                .Select(p=>_browsers[p.Id]).ToArray(),new(_paneDragSurfaces));
            _tabLayouts.Add(id,layout); _tabLayoutsCreated++;
            PaneHost.Children.Add(element);
        }
        else
        {
            // The entire split tree stays attached to the same XamlRoot.
            // Showing it does not trigger control unload/load or recreate splitters.
            layout.Root.Visibility=Visibility.Visible;
            foreach(var pane in root.Leaves())
            {
                if(pane.IsBrowser) { _browsers[pane.Id].Bind(pane); continue; }
                var view=GetTerminalView(pane,workspace);
                view.ShowFocusMarker=!_zoomed && workspace.Root.Leaves().Skip(1).Any();
                _views.Add(view);
            }
            foreach(var pair in layout.DragSurfaces) _paneDragSurfaces.Add(pair.Key,pair.Value);
        }
        _activeLayout=layout; layout.LastShown=++_tabLayoutShowSequence;
        TraceTabLayout("show",id,signature);
    }
}
