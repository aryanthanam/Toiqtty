using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Toiqtty.Hosting;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunEnduranceProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        if(!Path.GetFileName(SessionStore.Folder).StartsWith("endurance-",StringComparison.Ordinal))
            throw new InvalidOperationException("Endurance requires an isolated endurance-* directory.");
        int seconds=int.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_SESSION_SOAK_SECONDS"),out int requested)?Math.Clamp(requested,30,86400):3600;
        _metadataTimer.Stop();
        string command=$"\"{Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE")}\" --performance-client";
        _saved.Settings.Shell=command;
        var original=_selected!; NewWorkspace(command,"Session endurance"); RemoveWorkspace(original);
        await wait(()=>_focused?.RenderedText.Contains("PERF_READY")==true);
        while(_views.Count<4) { Split(_views.Count%2==1); await Task.Delay(100); }
        _selected!.Root=Balance(_selected.Root.Leaves().ToArray(),0); RebuildPanes();
        var workspace=_selected; var primary=workspace.ActiveTab;
        await wait(()=>_views.Count==4 && _views.All(v=>v.FramesPainted>0 && !v.ResizePendingForCheck));
        var panes=primary.Root.Leaves().ToArray(); var shells=panes.Select(p=>(RemoteTerminalSession)p.Session!).ToArray();
        var retained=_views.ToArray(); string hostIdentity=_host!.Identity;
        NewTab(command,"Retained alternate"); var alternate=workspace.ActiveTab;
        await wait(()=>_focused?.RenderedText.Contains("PERF_READY")==true);
        var alternateView=_focused!;
        SelectTab(primary); await wait(()=>_views.Count==4 && _focused?.InputHasFocus==true);
        int created=_terminalViewsCreated; var presenter=(OverlappedPresenter)AppWindow.Presenter;
        using var process=Process.GetCurrentProcess();
        using var hostProcess=Process.GetProcessById(_host.ProcessId);
        process.Refresh(); long coldPrivate=process.PrivateMemorySize64; int coldHandles=process.HandleCount;
        // Include text-cache, fallback-font and WARP initialization in the warmup,
        // then measure growth across repeated use of those same resources.
        string warmup=Guid.NewGuid().ToString("N");
        foreach(var shell in shells) { shell.Paste("flood2 3000 4096 "+warmup); shell.Key(13,0); }
        await wait(()=>shells.All(s=>s.Title.StartsWith("FLOOD_DONE_"+warmup+"_",StringComparison.Ordinal)));
        foreach(var shell in shells) { shell.Paste("scene 0"); shell.Key(13,0); }
        await wait(()=>retained.All(v=>v.RenderedText.Contains("SOAK_0") && v.HasImage(1001)));
        var warmGenerations=retained.Select(v=>v.ResourceGenerations).ToArray();
        foreach(var view in retained) view.ReplaceRenderDeviceForCheck(true);
        await wait(()=>retained.Select((v,i)=>v.ResourceGenerations>warmGenerations[i] && v.HasImage(1001)).All(ready=>ready));
        warmGenerations=retained.Select(v=>v.ResourceGenerations).ToArray();
        foreach(var view in retained) view.ReplaceRenderDeviceForCheck(false);
        await wait(()=>retained.Select((v,i)=>v.ResourceGenerations>warmGenerations[i] && v.HasImage(1001)).All(ready=>ready));
        // Device recreation intentionally clears shaped layouts. Populate the
        // restored hardware caches before taking the steady-state baseline.
        warmup=Guid.NewGuid().ToString("N");
        foreach(var shell in shells) { shell.Paste("flood2 3000 4096 "+warmup); shell.Key(13,0); }
        await wait(()=>shells.All(s=>s.Title.StartsWith("FLOOD_DONE_"+warmup+"_",StringComparison.Ordinal)));
        foreach(var shell in shells) { shell.Paste("scene 0"); shell.Key(13,0); }
        await wait(()=>shells.All(s=>s.Title=="SCENE_0") && retained.All(v=>v.RenderedText.Contains("SOAK_0") && v.HasImage(1001)));
        if(Environment.GetEnvironmentVariable("TOIQTTY_ENDURANCE_PAUSE_CHECK")=="1") await CheckEndurancePause(check,wait);
        process.Refresh();
        long initialPrivate=process.PrivateMemorySize64; int initialHandles=process.HandleCount;
        var timer=_enduranceClock=Stopwatch.StartNew(); var wall=Stopwatch.StartNew();
        int cycle=0; long inputSamples=0,interruptedInputSamples=0; double worstPresent=0; int lossInjections=0;
        void Save(bool finished)
        {
            process.Refresh(); hostProcess.Refresh();
            var (Bytes, Buffers, Created, Reused) = Terminal.TerminalCellPool.Shared.Statistics;
            var sample=new { timestamp=DateTimeOffset.Now,elapsedSeconds=timer.Elapsed.TotalSeconds,wallElapsedSeconds=wall.Elapsed.TotalSeconds,
                pausedSeconds=_endurancePausedSeconds,unplannedPauses=_endurancePauseCount,requestedSeconds=seconds,cycle,inputSamples,interruptedInputSamples,worstPresentMs=worstPresent,
                cellBufferPool=new { retainedBytes=Bytes,buffers=Buffers,created=Created,reused=Reused },
                host=hostIdentity,viewsCreated=_terminalViewsCreated,viewsEvicted=_terminalViewsEvicted,cachedViews=_terminalViews.Count,
                coldPrivateBytes=coldPrivate,coldHandles,warmedPrivateBytes=initialPrivate,warmedHandles=initialHandles,
                privateBytes=process.PrivateMemorySize64,handles=process.HandleCount,threads=process.Threads.Count,
                managedBytes=GC.GetTotalMemory(false),gcHeapBytes=GC.GetGCMemoryInfo().HeapSizeBytes,
                cachedLayouts=retained.Sum(v=>v.CachedTextLayouts),imageTextures=retained.Sum(v=>v.ImageTextureCount),
                estimatedGpuBytes=retained.Sum(v=>v.RetainedGpuBytes),resourceGenerations=retained.Sum(v=>v.ResourceGenerations),
                hostPrivateBytes=hostProcess.PrivateMemorySize64,hostHandles=hostProcess.HandleCount,hostThreads=hostProcess.Threads.Count,
                bytesRead=shells.Sum(s=>s.Performance.BytesRead),deltaFrames=shells.Sum(s=>s.DeltaFramesForCheck),fullFrames=shells.Sum(s=>s.FullFramesForCheck),
                recoveryCount=shells.Sum(s=>s.FullFrameRecoveriesForCheck),lossInjections,powerResumeMessages=_resumeCount,retainedTabLayouts=_tabLayouts.Count,tabLayoutsCreated=_tabLayoutsCreated,finished };
            string path=Path.Combine(SessionStore.Folder,"endurance.json");
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(sample,new JsonSerializerOptions { WriteIndented=true })); File.Move(path+".tmp",path,true);
            File.AppendAllText(Path.Combine(SessionStore.Folder,"endurance-samples.jsonl"),JsonSerializer.Serialize(sample)+Environment.NewLine);
        }
        while(timer.Elapsed.TotalSeconds<seconds)
        {
            await WaitForEndurancePresentation();
            cycle++; string run=Guid.NewGuid().ToString("N");
            foreach(var shell in shells) { shell.Paste("flood2 6000 "+(cycle%10==0?"0":"512")+" "+run); shell.Key(13,0); }
            await Task.WhenAll(shells.Select(s=>s.InputDrained));
            for(int i=0;i<8;i++)
            {
                int pauses=_endurancePauseCount;
                var result=await Probe(shells[i%4],retained.Single(v=>ReferenceEquals(v.Session,shells[i%4])));
                inputSamples++; if(_endurancePauseCount!=pauses) interruptedInputSamples++;
                else worstPresent=Math.Max(worstPresent,result.PresentMs);
                await Task.Delay(35);
            }
            // Burst actual nested split layout changes while real ConPTY output is arriving.
            var splits=Descendants(PaneHost).OfType<Grid>().Where(g=>g.Children.Count==3 && g.Children[1] is Thumb).ToArray();
            for(int i=0;i<60;i++)
            {
                foreach(var grid in splits) SetRatio(grid,.2+(i*17%61)/100d);
                Root.UpdateLayout(); if(i%4==0) await Task.Delay(16);
            }
            foreach(var grid in splits) SetRatio(grid,.5); Root.UpdateLayout();
            await wait(()=>_views.All(v=>!v.ResizePendingForCheck));
            SelectTab(alternate); await wait(()=>ReferenceEquals(_focused,alternateView) && alternateView.InputHasFocus);
            SelectTab(primary); await wait(()=>_views.Count==4 && _focused?.InputHasFocus==true);
            if(cycle%3==0)
            {
                _enduranceExpectedHidden=true;
                try
                {
                presenter.Minimize(false); await wait(()=>_windowPresentationHidden && shells.All(s=>s.ViewersForCheck==0));
                await Task.Delay(300); presenter.Restore(false);
                await wait(()=>!_windowPresentationHidden && shells.All(s=>s.ViewersForCheck==1) && _focused?.InputHasFocus==true);
                }
                finally { _enduranceExpectedHidden=false; }
            }
            if(cycle%5==0)
            {
                var focus=_focused!; ZoomClick(this,new()); await wait(()=>_views.Count==1);
                ZoomClick(this,new()); await wait(()=>_views.Count==4 && ReferenceEquals(_focused,focus));
            }
            // Slow probes can finish after the flood and replace its OSC title.
            // Ask the client for its sticky completion state after the probes.
            foreach(var shell in shells) { shell.Paste("flood-status "+run); shell.Key(13,0); }
            await wait(()=>shells.All(s=>s.Title.StartsWith("FLOOD_DONE_"+run+"_",StringComparison.Ordinal)));
            foreach(var shell in shells) { shell.Paste("scene "+cycle); shell.Key(13,0); }
            await wait(()=>retained.All(v=>v.RenderedText.Contains("SOAK_"+cycle) && v.HasImage(1001)));
            if(cycle%20==0)
            {
                var generations=retained.Select(v=>v.ResourceGenerations).ToArray();
                foreach(var view in retained) view.ReplaceRenderDeviceForCheck(true);
                await wait(()=>retained.Select((v,i)=>v.ResourceGenerations>generations[i] && v.HasImage(1001)).All(ready=>ready));
                generations=retained.Select(v=>v.ResourceGenerations).ToArray();
                foreach(var view in retained) view.ReplaceRenderDeviceForCheck(false);
                await wait(()=>retained.Select((v,i)=>v.ResourceGenerations>generations[i] && v.HasImage(1001)).All(ready=>ready));
            }
            if(cycle%30==0)
            {
                _enduranceExpectedHidden=true;
                try
                {
                presenter.Minimize(false); await wait(()=>_windowPresentationHidden && shells.All(s=>s.ViewersForCheck==0));
                foreach(var view in retained) view.InjectRenderDeviceLostForCheck();
                lossInjections+=retained.Length;
                await wait(()=>retained.All(v=>!v.HasGraphicsDeviceForCheck));
                _power.SendTestNotification(false); _power.SendTestNotification(true);
                presenter.Restore(false);
                await wait(()=>WindowRenderingActive && retained.All(v=>v.HasGraphicsDeviceForCheck && v.HasImage(1001) && !v.IsSuspendedForCheck));
                }
                finally { _enduranceExpectedHidden=false; }
            }
            if(_terminalViewsCreated!=created || _views.Any(v=>!retained.Contains(v)) || _host.Identity!=hostIdentity
                || shells.Any(s=>s.ViewersForCheck!=1 || s.FrameListenersForCheck!=1 || s.CaptureFailure.Length>0))
                throw new InvalidOperationException("Endurance changed a retained view, host identity or capture subscription.");
            Save(false); await Task.Delay(150);
        }
        process.Refresh();
        check(timer.Elapsed.TotalSeconds>=seconds,"Sessionized endurance completed its requested active stress duration, excluding external suspension");
        check(_terminalViewsCreated==created && _terminalViewsEvicted==0,"Repeated tab/split/zoom/minimize transitions retained all five views without recreating controls");
        check(shells.All(s=>s.DeltaFramesForCheck>0 && s.FullFrameRecoveriesForCheck==0),"Changed-row transfers remained synchronized without unexpected full-frame recovery");
        check(_host.Identity==hostIdentity && _host.CallForCheck(Hosting.HostRequests.Ping()).Panes.Length==5,"The original host and all five shell panes survived the endurance run");
        check(process.PrivateMemorySize64-initialPrivate<256L*1024*1024 && process.HandleCount-initialHandles<300,
            "Private-memory and handle growth stayed within the explicit endurance guardrails (256MiB and 300 handles)");
        foreach(var view in retained)
            check(await view.CheckRenderedImage(1001,Windows.UI.Color.FromArgb(255,255,0,0),Path.Combine(SessionStore.Folder,"endurance-image-"+view.PaneId+".png")),
                "Kitty image pixels remain correct after endurance: "+view.PaneId);
        Save(true);

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            { var child=VisualTreeHelper.GetChild(parent,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
        }
        static void SetRatio(Grid grid,double ratio)
        {
            if(grid.ColumnDefinitions.Count==3) { grid.ColumnDefinitions[0].Width=new(ratio,GridUnitType.Star); grid.ColumnDefinitions[2].Width=new(1-ratio,GridUnitType.Star); }
            else if(grid.RowDefinitions.Count==3) { grid.RowDefinitions[0].Height=new(ratio,GridUnitType.Star); grid.RowDefinitions[2].Height=new(1-ratio,GridUnitType.Star); }
        }
    }
}
