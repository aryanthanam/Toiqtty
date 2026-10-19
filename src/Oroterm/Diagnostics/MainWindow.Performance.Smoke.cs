using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Oroterm.Controls;
using Oroterm.Hosting;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private sealed record LoadLatency(string Pane,double ChildReadMs,double ChildWriteMs,double? HostStateMs,double PresentMs,double UiContinuationMs,double DrawStartMs,double DrawDurationMs,int Draws);

    private async Task RunPerformanceProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        if(!Path.GetFileName(SessionStore.Folder).StartsWith("performance-",StringComparison.Ordinal))
            throw new InvalidOperationException("Performance checks require an isolated performance-* directory.");
        _metadataTimer.Stop();
        _host!.DisablePaneOutputWatchForCheck=Environment.GetEnvironmentVariable("OROTERM_PERFORMANCE_WATCH_MODE")=="legacy";
        string client=Environment.GetEnvironmentVariable("OROTERM_SMOKE_EXE")??throw new InvalidOperationException("Performance client missing.");
        string command=$"\"{client}\" --performance-client";
        var original=_selected!; _saved.Settings.Shell=command;
        NewWorkspace(command,"Performance"); RemoveWorkspace(original);
        await wait(()=>_focused?.RenderedText.Contains("PERF_READY")==true);
        var measurements=new List<object>();
        var profiles=new List<object>();
        bool profileRendering=Environment.GetEnvironmentVariable("OROTERM_RENDER_PROFILE")=="1";
        foreach(int paneCount in Environment.GetEnvironmentVariable("OROTERM_PERFORMANCE_SCALE")=="16"?new[] { 1,4,8,16 }:new[] { 1,4,8 })
        {
            while(_views.Count<paneCount)
            {
                int previous=_views.Count;
                var current=_selected!; var target=current.Root.Leaves().Last();
                current.Root.Split(target.Id,previous%2==1,_saved.Settings.Shell);
                var leaves=current.Root.Leaves().ToArray();
                current.Root=Balance(leaves,0); RebuildPanes(leaves[^1].Id); Save();
                await wait(()=>_views.Count==previous+1 && _focused?.InputHasFocus==true && _views.All(v=>v.IsLoaded && !v.ResizePendingForCheck));
            }
            if(paneCount>1) { _selected!.Root=Balance(_selected.Root.Leaves().ToArray(),0); RebuildPanes(); }
            await wait(()=>_views.Count==paneCount && _views.All(v=>v.FramesPainted>0 && !v.ResizePendingForCheck));
            var panes=_selected!.Root.Leaves().ToArray();
            var shells=panes.Select(p=>(RemoteTerminalSession)p.Session!).ToArray();
            var views=panes.Select(p=>_views.Single(v=>v.PaneId==p.Id)).ToArray();
            check(views.All(v=>int.Parse(v.ViewportLabel.Split(' ')[0])>=24),"The balanced "+paneCount+"-pane viewport fits the latency response marker");
            await Task.Delay(500);
            using var ui=Process.GetCurrentProcess(); using var host=Process.GetProcessById(_host!.ProcessId);
            var idleStart=Sample(ui,host); var timer=Stopwatch.StartNew(); await Task.Delay(2000); var idleEnd=Sample(ui,host);
            measurements.Add(new { phase="idle",paneCount,seconds=timer.Elapsed.TotalSeconds,
                uiCpuMs=idleEnd.UiCpu-idleStart.UiCpu,hostCpuMs=idleEnd.HostCpu-idleStart.HostCpu,
                uiAllocatedBytes=idleEnd.Allocated-idleStart.Allocated,uiPrivateBytes=idleEnd.UiPrivate,hostPrivateBytes=idleEnd.HostPrivate });
            foreach(string phase in new[] { "quiet","controlled-output","saturated-output","mixed-output" })
            {
                var samples=new List<LoadLatency>();
                long bytesBefore=shells.Sum(s=>s.Performance.BytesRead),framesBefore=views.Sum(v=>v.FramesPainted),rowsBefore=views.Sum(v=>v.RowsPainted);
                int runsBefore=views.Sum(v=>v.ShapedRuns); long spacingBefore=views.Sum(v=>v.CharacterSpacingCalls);
                var gpuBefore=views.Select(v=>v.NativeRenderStatsForCheck).ToArray();
                Profile("start",phase,paneCount,views);
                var start=Sample(ui,host); timer.Restart();
                long watchesBefore=_host.WatchRequestsForCheck,notificationsBefore=_host.WatchNotificationsForCheck;
                var finished=new List<Task>(); var subscriptions=new List<(RemoteTerminalSession Shell,Action<ShellState,long> Handler)>();
                long kibPerPane=phase=="controlled-output"?32768/paneCount:phase=="mixed-output"?32768:0;
                string run=Guid.NewGuid().ToString("N");
                try
                {
                    if(phase!="quiet")
                    {
                        foreach(var shell in phase=="mixed-output"?shells[..1]:shells)
                        {
                            var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            void State(ShellState state,long ticks) { if(state.Title.StartsWith("FLOOD_DONE_"+run+"_",StringComparison.Ordinal)) done.TrySetResult(); }
                            shell.StateObservedForCheck+=State; subscriptions.Add((shell,State)); finished.Add(done.Task);
                            shell.Paste("flood2 5000 "+kibPerPane+" "+run); shell.Key(13,0);
                        }
                        await Task.WhenAll(shells.Select(s=>s.InputDrained));
                    }
                    int count=phase=="quiet"?64:32;
                    for(int i=0;i<count;i++)
                    {
                        samples.Add(await Probe(shells[i%paneCount],views[i%paneCount]));
                        await Task.Delay(phase=="quiet"?5:35);
                    }
                    if(finished.Count>0) await Task.WhenAll(finished).WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally { foreach(var subscription in subscriptions) subscription.Shell.StateObservedForCheck-=subscription.Handler; }
                double seconds=timer.Elapsed.TotalSeconds; var end=Sample(ui,host);
                var gpuAfter=views.Select(v=>v.NativeRenderStatsForCheck).ToArray();
                Profile("end",phase,paneCount,views);
                check(phase=="quiet" || seconds>=4.9,"Output measurements wait for their own flood completion in "+phase+" with "+paneCount+" panes");
                check(samples.Count==(phase=="quiet"?64:32),"Timestamped input and response-frame presentation complete during "+phase+" with "+paneCount+" panes");
                measurements.Add(new { phase,paneCount,seconds,requestedKibPerPane=kibPerPane,
                    busyPanes=phase=="quiet"?0:phase=="mixed-output"?1:paneCount,
                    watchRequests=_host.WatchRequestsForCheck-watchesBefore,watchNotifications=_host.WatchNotificationsForCheck-notificationsBefore,
                    bytesRead=shells.Sum(s=>s.Performance.BytesRead)-bytesBefore,frames=views.Sum(v=>v.FramesPainted)-framesBefore,rowsPainted=views.Sum(v=>v.RowsPainted)-rowsBefore,
                    shapedRuns=views.Sum(v=>v.ShapedRuns)-runsBefore,characterSpacingCalls=views.Sum(v=>v.CharacterSpacingCalls)-spacingBefore,
                    nativeGpu=new { drawCalls=gpuAfter.Sum(s=>(long)s.DrawCalls)-gpuBefore.Sum(s=>(long)s.DrawCalls),vertices=gpuAfter.Sum(s=>(long)s.Vertices)-gpuBefore.Sum(s=>(long)s.Vertices),
                        atlasHits=gpuAfter.Sum(s=>(long)s.AtlasHits)-gpuBefore.Sum(s=>(long)s.AtlasHits),atlasMisses=gpuAfter.Sum(s=>(long)s.AtlasMisses)-gpuBefore.Sum(s=>(long)s.AtlasMisses),
                        textureBytes=gpuAfter.Sum(s=>(long)s.TextureBytes),vertexBufferBytes=gpuAfter.Sum(s=>(long)s.VertexBufferBytes),shapeCacheBytes=gpuAfter.Sum(s=>(long)s.ShapeCacheBytes) },
                    childRead=Distribution(samples.Select(s=>s.ChildReadMs)),childWrite=Distribution(samples.Select(s=>s.ChildWriteMs)),
                    hostState=Distribution(samples.Where(s=>s.HostStateMs.HasValue).Select(s=>s.HostStateMs!.Value)),requestToPresent=Distribution(samples.Select(s=>s.PresentMs)),uiContinuation=Distribution(samples.Select(s=>s.UiContinuationMs)),
                    responseDraw=Distribution(samples.Select(s=>s.DrawDurationMs)),responseDrawStart=Distribution(samples.Select(s=>s.DrawStartMs)),
                    uiCpuMs=end.UiCpu-start.UiCpu,hostCpuMs=end.HostCpu-start.HostCpu,uiAllocatedBytes=end.Allocated-start.Allocated,
                    uiPrivateBytes=end.UiPrivate,hostPrivateBytes=end.HostPrivate,samples });
                SaveReport();
            }
        }
        SaveReport();
        foreach(var workspace in _saved.Workspaces.ToArray()) RemoveWorkspace(workspace);
        void Profile(string stage,string phase,int paneCount,TerminalView[] views)
        {
            if(!profileRendering)return;
            profiles.Add(new {stage,phase,paneCount,renderers=views.Select(v=>v.NativeRenderProfileForCheck).ToArray()});
            if(stage=="end")File.WriteAllText(Path.Combine(SessionStore.Folder,"performance-render-profile.json"),JsonSerializer.Serialize(profiles,new JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
        }
        void SaveReport()=>File.WriteAllText(Path.Combine(SessionStore.Folder,"performance.json"),JsonSerializer.Serialize(new {
            version=BuildInfo.Version,benchmarkVersion=3,watchMode=_host!.DisablePaneOutputWatchForCheck?"legacy":"pane-interest",timestamp=DateTimeOffset.Now,measurements,
            supportsOutputWatch=_host.SupportsOutputWatch,supportsPaneOutputWatch=_host.SupportsPaneOutputWatch,
            meaning="Stopwatch/QPC timestamps: input admission to child ReadLine, response emission, host state arrival and response-marker frame Present(0) return. UI continuation timing is separate. No polling is used for latency samples. Display scanout is not measured. Controlled output targets 32MiB/s aggregate; actual bytes and duration are recorded."
        },new JsonSerializerOptions { WriteIndented=true }));
    }
    private async Task<LoadLatency> Probe(RemoteTerminalSession shell,TerminalView view)
    {
        string token=Guid.NewGuid().ToString("N")[..6],prefix="PONG_"+token+"_";
        long origin=Stopwatch.GetTimestamp(),hostTicks=0,drawBegin=0,drawEnd=0; int draws=0;
        var presented=new TaskCompletionSource<(long Present,long ReadUs,long WriteUs,long DrawBegin,long DrawEnd,int Draws)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Stage(string stage,long ticks) { if(stage=="draw-begin") { drawBegin=ticks; draws++; } else if(stage=="draw-end") drawEnd=ticks; }
        void State(ShellState state,long ticks) { if(state.Title=="PING_"+token) Interlocked.CompareExchange(ref hostTicks,ticks,0); }
        void Painted()
        {
            long ticks=Stopwatch.GetTimestamp(); string row=view.RenderedRowForCheck(1);
            int start=row.IndexOf(prefix,StringComparison.Ordinal); if(start<0) return;
            var fields=row[(start+prefix.Length)..].Trim().Split('_');
            if(fields.Length>=2 && long.TryParse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out long read)
                && long.TryParse(fields[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out long write)) presented.TrySetResult((ticks,read,write,drawBegin,drawEnd,draws));
        }
        shell.StateObservedForCheck+=State; view.FramePaintedForCheck+=Painted; view.LatencyStageForCheck+=Stage;
        try
        {
            shell.Paste("ping2 "+token+" "+origin); shell.Key(13,0);
            var result=await WaitForProbePresentation(presented.Task);
            return new(view.PaneId,result.ReadUs/1000d,result.WriteUs/1000d,hostTicks==0?null:Stopwatch.GetElapsedTime(origin,hostTicks).TotalMilliseconds,
                Stopwatch.GetElapsedTime(origin,result.Present).TotalMilliseconds,Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                Stopwatch.GetElapsedTime(origin,result.DrawBegin).TotalMilliseconds,Stopwatch.GetElapsedTime(result.DrawBegin,result.DrawEnd).TotalMilliseconds,result.Draws);
        }
        catch(TimeoutException)
        {
            var reply=await shell.CaptureDiagnosticsForCheck();
            var (snapshot,cells)=HostSnapshotCodec.Decode(reply.Binary,[]);
            File.WriteAllText(Path.Combine(SessionStore.Folder,"performance-timeout.json"),JsonSerializer.Serialize(new {
                token,view.PaneId,hostState=reply.State,view.FramesPainted,view.IsSuspendedForCheck,
                view.SuspensionForCheck,WindowRenderingActive,_windowPresentationHidden,_powerSuspended,
                nativeVisible=IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(this)),nativeMinimized=IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(this)),
                cachedRow=view.RenderedRowForCheck(1),hostRows=Enumerable.Range(0,(int)snapshot.Frame.Rows)
                    .Select(row=>string.Concat(cells.Skip(row*(int)snapshot.Frame.Columns).Take((int)snapshot.Frame.Columns).Select(cell=>cell.GetText()))).ToArray()
            },new JsonSerializerOptions { WriteIndented=true }));
            throw;
        }
        finally { shell.StateObservedForCheck-=State; view.FramePaintedForCheck-=Painted; view.LatencyStageForCheck-=Stage; }
    }
    private static object Distribution(IEnumerable<double> source)
    {
        double[] values=source.Order().ToArray();
        if(values.Length==0) return new { samples=0 };
        return new { samples=values.Length,medianMs=values[values.Length/2],p95Ms=values[(int)((values.Length-1)*.95)],p99Ms=values[(int)((values.Length-1)*.99)],
            maxMs=values[^1],within8_33ms=values.Count(value=>value<=1000d/120) };
    }
    private static PaneNode Balance(PaneNode[] leaves,int depth)
    {
        if(leaves.Length==1) return leaves[0]; int middle=leaves.Length/2;
        return new() { Vertical=depth%2==0,First=Balance(leaves[..middle],depth+1),Second=Balance(leaves[middle..],depth+1) };
    }
    private static (double UiCpu,double HostCpu,long Allocated,long UiPrivate,long HostPrivate) Sample(Process ui,Process host)
    { ui.Refresh(); host.Refresh(); return (ui.TotalProcessorTime.TotalMilliseconds,host.TotalProcessorTime.TotalMilliseconds,GC.GetTotalAllocatedBytes(),ui.PrivateMemorySize64,host.PrivateMemorySize64); }
}
