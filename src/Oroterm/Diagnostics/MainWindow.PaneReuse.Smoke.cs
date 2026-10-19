using System.Diagnostics;
using System.Text.Json;
using Oroterm.Hosting;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunPaneReuseProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        _metadataTimer.Stop();
        string client=Environment.GetEnvironmentVariable("OROTERM_SMOKE_EXE")!;
        string command=$"\"{client}\" --resize-client";
        _saved.Settings.Shell=command;
        _saved.Settings.Appearance.InactivePaneOpacity=.65;
        var original=_selected!;
        NewWorkspace(command,"Retained views"); RemoveWorkspace(original);
        await wait(()=>_focused is { FramesPainted:>0 } ready && ready.HasImage(901) && !ready.ResizePendingForCheck);
        var workspace=_selected!; var first=workspace.ActiveTab; var firstView=_focused!;
        int firstGeneration=firstView.ResourceGenerations;
        var firstShell=first.Root.Session!;
        NewTab(command,"Second retained tab"); var second=workspace.ActiveTab;
        await wait(()=>_focused is { FramesPainted:>0 } ready && ready.HasImage(901) && !ready.ResizePendingForCheck);
        var secondView=_focused!; int secondGeneration=secondView.ResourceGenerations;
        check(!firstView.IsDisposedForCheck && ((RemoteTerminalSession)firstShell).ViewersForCheck==0 && firstView.ImageTextureCount==1,
            "A hidden tab retains its view and image texture while detaching visible capture");
        int created=_terminalViewsCreated;
        int layoutsCreated=_tabLayoutsCreated;
        var firstLayout=_tabLayouts[first.Id].Root; var secondLayout=_tabLayouts[second.Id].Root;
        var firstTabItem=_tabCards[first.Id].Item; var secondTabItem=_tabCards[second.Id].Item;
        var switches=new List<double>();
        for(int i=0;i<100;i++)
        {
            var tab=i%2==0?first:second; var expected=i%2==0?firstView:secondView;
            var presented=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Painted() { if(ReferenceEquals(_focused,expected) && expected.InputHasFocus && expected.IsLoaded) presented.TrySetResult(); }
            expected.FramePaintedForCheck+=Painted;
            var timer=Stopwatch.StartNew();
            try { SelectTab(tab); await presented.Task.WaitAsync(TimeSpan.FromSeconds(5)); switches.Add(timer.Elapsed.TotalMilliseconds); }
            finally { expected.FramePaintedForCheck-=Painted; }
        }
        check(_terminalViewsCreated==created && firstView.ResourceGenerations==firstGeneration && secondView.ResourceGenerations==secondGeneration,
            "One hundred tab switches reuse both controls, swapchains and resource generations");
        check(_tabLayoutsCreated==layoutsCreated && ReferenceEquals(_tabLayouts[first.Id].Root,firstLayout)
            && ReferenceEquals(_tabLayouts[second.Id].Root,secondLayout),"One hundred switches retain the complete tab layout roots");
        check(ReferenceEquals(_tabCards[first.Id].Item,firstTabItem) && ReferenceEquals(_tabCards[second.Id].Item,secondTabItem),
            "Changing selection retains tab containers, drag targets and close buttons");
        check(ReferenceEquals(first.Root.Session,firstShell) && firstView.ImageTextureCount==1 && secondView.ImageTextureCount==1,
            "Repeated switching preserves the original shell and retained Kitty image resources");
        SelectTab(first); await wait(()=>ReferenceEquals(_focused,firstView) && firstView.InputHasFocus);
        Split(true); await wait(()=>_views.Count==2 && _views.All(v=>v.FramesPainted>0 && !v.ResizePendingForCheck));
        check(_terminalViewsCreated==created+1 && _views.Contains(firstView) && firstView.PaneId==first.Root.First!.Id,
            "Splitting creates exactly one view and rebinds the original view to its stable child identity");
        var splitViews=_views.ToArray(); firstView.FocusTerminal();
        await wait(()=>ReferenceEquals(_focused,firstView));
        ZoomClick(this,new()); await wait(()=>_views.Count==1 && ReferenceEquals(_views[0],firstView));
        ZoomClick(this,new()); await wait(()=>_views.Count==2 && _views.All(v=>splitViews.Contains(v)));
        check(_terminalViewsCreated==created+1,"Zoom and unzoom reparent the existing terminal views");
        check(MovePane(firstView.PaneId,workspace.Id,second.Id,true),"A retained view can move into another tab in its owning session");
        await wait(()=>_views.Contains(firstView) && _views.Contains(secondView) && _focused?.PaneId==firstView.PaneId);
        check(_terminalViewsCreated==created+1 && ReferenceEquals(firstView.Session,firstShell),
            "Moving a pane between tabs preserves its view and original session binding");
        NewWorkspace(command,"Other retained session"); var other=_selected!;
        await wait(()=>_focused is { FramesPainted:>0 });
        SelectWorkspace(workspace);
        await wait(()=>_views.Contains(firstView) && _views.Contains(secondView) && firstView.InputHasFocus);
        check(!firstView.IsDisposedForCheck && firstView.ImageTextureCount==1,"Switching sessions restores the retained view and textures");
        while(_views.Count<4)
        {
            int previous=_views.Count; Split(previous%2==1);
            await wait(()=>_views.Count==previous+1 && _focused?.InputHasFocus==true && _views.All(v=>v.IsLoaded && !v.ResizePendingForCheck));
        }
        check(_views.Count==4 && _views.Contains(firstView) && _views.Contains(secondView),"Repeated nested splits keep all four retained panes loaded and focusable");
        File.WriteAllText(Path.Combine(SessionStore.Folder,"pane-reuse-opacities.json"),JsonSerializer.Serialize(
            _views.Select(view=>new { view.PaneId,view.IsActive,opacity=view.ContentOpacityForCheck }),new JsonSerializerOptions { WriteIndented=true }));
        check(_views.All(view=>Math.Abs(view.ContentOpacityForCheck-(view.IsActive?1:.65))<.000001),
            "Rebinding retained panes restores inactive opacity even when logical focus was already cleared during suspension");
        int evictedBefore=_terminalViewsEvicted;
        for(int i=0;i<12;i++)
        {
            NewTab(command,"Cache pressure "+i);
            await wait(()=>_focused is { FramesPainted:>0 } ready && ready.HasImage(901));
        }
        var visible=_views.ToHashSet();
        var hidden=_terminalViews.Values.Where(item=>!visible.Contains(item.View)).ToArray();
        check(hidden.Length<=MaxHiddenTerminalViews && hidden.Sum(item=>item.View.RetainedGpuBytes)<=MaxHiddenTerminalGpuBytes && _terminalViewsEvicted>evictedBefore,
            "Hidden-view retention obeys both the count limit and estimated GPU-memory budget");
        var live=_host!.Call(new()).Panes;
        check(workspace.AllPanes().All(p=>live.Contains(p.Id)) && other.AllPanes().All(p=>live.Contains(p.Id)),
            "Evicting hidden views leaves all of their shells owned by the session host");
        check(workspace.AllPanes().Select(p=>p.Session).OfType<RemoteTerminalSession>().All(s=>s.ViewersForCheck is 0 or 1 && s.FrameListenersForCheck is 0 or 1),
            "Cached and evicted views never duplicate frame listeners or visible capture attachments");
        switches.Sort();
        File.WriteAllText(Path.Combine(SessionStore.Folder,"pane-reuse.json"),JsonSerializer.Serialize(new {
            version=BuildInfo.Version,switches=switches.Count,switchToPresentMedianMs=switches[switches.Count/2],switchToPresentP95Ms=switches[(int)((switches.Count-1)*.95)],
            viewsCreated=_terminalViewsCreated,viewsEvicted=_terminalViewsEvicted,hiddenViews=hidden.Length,hiddenGpuBytes=hidden.Sum(item=>item.View.RetainedGpuBytes),
            layoutsCreated=_tabLayoutsCreated,retainedLayouts=_tabLayouts.Count,
            meaning="Tab activation through retained view focus and response-frame Present return; not physical display scanout."
        },new JsonSerializerOptions { WriteIndented=true }));
    }
}
