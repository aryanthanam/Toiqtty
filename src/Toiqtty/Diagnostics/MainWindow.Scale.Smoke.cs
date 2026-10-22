using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Toiqtty.Hosting;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunScaleProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        _metadataTimer.Stop();
        string command=$"\"{Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE")}\" --performance-client";
        _saved.Settings.Shell=command;
        var original=_selected!; NewWorkspace(command,"Sixteen panes"); RemoveWorkspace(original);
        var primary=_selected!;
        var first=primary.Root;
        var leaves=new[] { first }.Concat(Enumerable.Range(1,15).Select(_=>new PaneNode { Command=command,Directory=Environment.CurrentDirectory })).ToArray();
        primary.Root=Balance(leaves,0); RebuildPanes(first.Id); Save();
        await wait(()=>_views.Count==16 && _views.All(v=>v.RenderedText.Contains("PERF_READY") && !v.ResizePendingForCheck));
        string host=_host!.Identity;
        var sessions=leaves.Select(p=>(RemoteTerminalSession)p.Session!).ToArray();
        var others=Enumerable.Range(0,32).Select(i=>new Workspace { Name="Scale session "+i,Tabs=Enumerable.Range(0,2).Select(tab=>new TerminalTab {
            Name="Shell "+tab,Root=new PaneNode { Command=command,Directory=Environment.CurrentDirectory }
        }).ToList() }).ToArray();
        _saved.Workspaces.AddRange(others); Save();
        foreach(var workspace in others) foreach(var pane in workspace.AllPanes()) EnsureShell(pane,workspace);
        await wait(()=>others.SelectMany(w=>w.AllPanes()).All(p=>p.Session!.Performance.BytesRead>0));
        check(_host.CallForCheck(Hosting.HostRequests.Ping()).Panes.Length==80,"One host owns sixteen visible panes and sixty-four shells across thirty-two additional sessions");
        // What a restarted window sends for this layout: one attach, each workspace's own theme.
        var restore=_saved.Workspaces.SelectMany(w=>{ var settings=RemoteTerminalSession.DetachSettings(_saved.Settings.WithTerminalTheme(w.Theme)); return w.AllPanes().Where(p=>!p.IsBrowser).Select(p=>(p.Id,settings)); }).ToArray();
        var heartbeat=new DispatcherHeartbeat(DispatcherQueue); var restoreTimer=Stopwatch.StartNew();
        var restoredStates=await _host.AttachAsync(restore);
        double restoreMs=restoreTimer.Elapsed.TotalMilliseconds, restoreGap=heartbeat.Stop();
        check(restoredStates.Count==80 && restoreGap<100,$"One batched attach reattaches all eighty shells in {restoreMs:0} ms without stalling the UI thread (max dispatcher gap {restoreGap:0} ms)");
        await wait(()=>!_polling); int branchLookups=_branchLookupsForCheck;
        await PollMetadata();
        check(_branchLookupsForCheck-branchLookups==1,"Thirty-three sessions sharing a directory require only one asynchronous Git lookup per metadata pass");
        using var ui=Process.GetCurrentProcess(); using var owner=Process.GetProcessById(_host.ProcessId);
        var (UiCpu, HostCpu, Allocated, UiPrivate, HostPrivate) = Sample(ui,owner); var timer=Stopwatch.StartNew(); await Task.Delay(5000); var idleEnd=Sample(ui,owner);
        var idle=new { seconds=timer.Elapsed.TotalSeconds,uiCpuMs=idleEnd.UiCpu-UiCpu,hostCpuMs=idleEnd.HostCpu-HostCpu,
            uiAllocatedBytes=idleEnd.Allocated-Allocated,uiPrivateBytes=idleEnd.UiPrivate,hostPrivateBytes=idleEnd.HostPrivate };
        var latencies=new List<double>();
        for(int i=0;i<32;i++) latencies.Add((await Probe(sessions[i%16],_views.Single(v=>ReferenceEquals(v.Session,sessions[i%16])))).PresentMs);
        check(latencies.Count==32,"All sixteen visible panes answer timestamped input while sixty-four other shells remain alive");
        foreach(var workspace in others)
        {
            SelectWorkspace(workspace);
            await wait(()=>_focused is { InputHasFocus:true,FramesPainted:>0 } view && view.RenderedText.Contains("PERF_READY"));
            SelectTab(workspace.Tabs[1]);
            await wait(()=>_focused is { InputHasFocus:true,FramesPainted:>0 } view && view.RenderedText.Contains("PERF_READY"));
        }
        check(_terminalViews.Count<=MaxHiddenTerminalViews+1 && _tabLayouts.Count<=MaxHiddenTerminalViews+1,
            "Visiting all sixty-four tabs bounds retained terminal views and complete layouts");
        SelectWorkspace(primary);
        await wait(()=>_views.Count==16 && _views.All(v=>v.FramesPainted>0 && !v.ResizePendingForCheck));
        check(_host.Identity==host && _host.CallForCheck(Hosting.HostRequests.Ping()).Panes.Length==80 && leaves.Select(p=>p.Session).SequenceEqual(sessions),
            "Returning to sixteen panes after cache eviction preserves all eighty shell identities and their host");
        foreach(var shell in sessions) { shell.Paste("scale-image"); shell.Key(13,0); }
        await wait(()=>_views.All(v=>v.HasImage(1002)));
        var generations=_views.Select(v=>v.ResourceGenerations).ToArray();
        foreach(var view in _views) view.InjectRenderDeviceLostForCheck();
        await wait(()=>_views.Select((v,i)=>v.ResourceGenerations>generations[i] && v.HasImage(1002)).All(ready=>ready));
        check(true,"Injected device-loss dispatch rebuilds all sixteen pane renderers and retained images");
        var presenter=(OverlappedPresenter)AppWindow.Presenter;
        presenter.Minimize(false); await wait(()=>_windowPresentationHidden && sessions.All(s=>s.ViewersForCheck==0));
        foreach(var view in _views) view.InjectRenderDeviceLostForCheck();
        await wait(()=>_views.All(v=>!v.HasGraphicsDeviceForCheck));
        _power.SendTestNotification(false); _power.SendTestNotification(true);
        check(_views.All(v=>v.IsSuspendedForCheck),"Power-resume handling while minimized keeps all sixteen panes suspended");
        presenter.Restore(false); await wait(()=>WindowRenderingActive && _views.All(v=>v.HasImage(1002) && !v.IsSuspendedForCheck));
        for(int i=0;i<_views.Count;i++)
            check(await _views[i].CheckRenderedImage(1002,Windows.UI.Color.FromArgb(255,255,0,0),Path.Combine(SessionStore.Folder,"scale-recovery-"+i+".png")),
                "Recovered pane "+i+" retains exact red Kitty image pixels");
        check(sessions.All(s=>s.FrameListenersForCheck==1 && s.ViewersForCheck==1 && s.CaptureFailure.Length==0),
            "Scale and recovery transitions leave one listener and visible attachment per pane without capture errors");
        ui.Refresh(); owner.Refresh();
        File.WriteAllText(Path.Combine(SessionStore.Folder,"scale.json"),JsonSerializer.Serialize(new {
            version=BuildInfo.Version,host,shells=80,sessions=33,tabs=65,visiblePanes=16,idle,input=Distribution(latencies),
            reattach=new { panes=restore.Length,batchedMs=restoreMs,maxDispatcherGapMs=restoreGap },
            cachedViews=_terminalViews.Count,retainedTabLayouts=_tabLayouts.Count,viewsCreated=_terminalViewsCreated,viewsEvicted=_terminalViewsEvicted,
            uiPrivateBytes=ui.PrivateMemorySize64,uiHandles=ui.HandleCount,uiThreads=ui.Threads.Count,hostPrivateBytes=owner.PrivateMemorySize64,hostHandles=owner.HandleCount,
            estimatedGpuBytes=_terminalViews.Values.Sum(item=>item.View.RetainedGpuBytes),
            meaning="Real ConPTY shells, tab/session navigation, cache eviction, simulated shared Win2D device loss and power messages. Does not claim physical driver reset or system sleep."
        },new JsonSerializerOptions { WriteIndented=true }));
    }
}
