using System.Diagnostics;
using System.Text.Json;
using Oroterm.Hosting;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunInputCloseProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        string client=Environment.GetEnvironmentVariable("OROTERM_SMOKE_EXE")!;
        var original=_selected!;
        NewWorkspace($"\"{client}\" --latency-client","Input close"); RemoveWorkspace(original);
        await wait(()=>_focused?.RenderedText.Contains("LATENCY_READY")==true);
        var remote=(RemoteTerminalSession)_selected!.Root.Session!;
        string token=Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(SessionStore.Folder,"input-close.json"),JsonSerializer.Serialize(new { pane=_selected.Root.Id,token }));
        for(int i=0;i<250;i++) { remote.Paste("echo "+token+"_"+i); remote.Key(13,0); }
        check(remote.PendingInput>0,"Closing starts with admitted input still awaiting host acknowledgements");
        Closed+=(_,_)=>File.WriteAllText(Path.Combine(SessionStore.Folder,"input-close-state.json"),JsonSerializer.Serialize(new { _inputClosePending,_inputCloseReady,pending=remote.PendingInput }));
        // The session-probe finally block closes the window immediately.
    }

    private async Task RunInputProbe(Action<bool,string> check,Func<Func<bool>,Task> wait,bool compatibility=false)
    {
        _metadataTimer.Stop();
        string client=Environment.GetEnvironmentVariable("OROTERM_SMOKE_EXE")!;
        var original=_selected!;
        NewWorkspace($"\"{client}\" --latency-client","Input latency"); RemoveWorkspace(original);
        await wait(()=>_focused is { FramesPainted:>0 } ready && ready.RenderedText.Contains("LATENCY_READY") && !ready.ResizePendingForCheck);
        var view=_focused!;
        var remote=(RemoteTerminalSession)_selected!.Root.Session!;
        check(_host!.SupportsOutputWatch!=compatibility,compatibility?"An older host remains usable through the compatibility polling path":"The updated host advertises output notifications through protocol-2 capabilities");
        var results=new List<object>();
        foreach(bool legacy in new[] { true,false,true,false })
        {
            remote.LegacyRefreshForCheck=legacy; view.LegacyRefreshForCheck=legacy;
            await Task.Delay(150);
            var latencies=new List<double>(); var admissions=new List<double>();
            var traces=new List<object>();
            long notifications=view.FrameNotifications;
            for(int i=0;i<(legacy?30:100);i++)
            {
                string token=Guid.NewGuid().ToString("N");
                var drawn=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Painted() { if(view.RenderedText.Contains("ACK_"+token)) drawn.TrySetResult(); }
                view.FramePaintedForCheck+=Painted;
                var timer=Stopwatch.StartNew();
                long origin=Stopwatch.GetTimestamp();
                var stages=new System.Collections.Concurrent.ConcurrentQueue<object>();
                void Stage(string stage,long ticks) => stages.Enqueue(new { stage,ms=Stopwatch.GetElapsedTime(origin,ticks).TotalMilliseconds });
                remote.LatencyStageForCheck+=Stage; view.LatencyStageForCheck+=Stage;
                try
                {
                    remote.Paste("echo "+token); remote.Key(13,0);
                    admissions.Add(timer.Elapsed.TotalMilliseconds);
                    await drawn.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    latencies.Add(timer.Elapsed.TotalMilliseconds);
                }
                finally { view.FramePaintedForCheck-=Painted; remote.LatencyStageForCheck-=Stage; view.LatencyStageForCheck-=Stage; }
                traces.Add(new { sample=i,stages=stages.ToArray() });
                await Task.Delay(5);
            }
            latencies.Sort(); admissions.Sort();
            results.Add(new { cadence=legacy?"reference-33ms-capture-plus-33ms-UI-on-swapchain":compatibility?"compatibility-poll-with-immediate-swapchain":"output-notification-with-immediate-swapchain",samples=latencies.Count,
                requestToPresentMedianMs=latencies[latencies.Count/2],requestToPresentP95Ms=latencies[(int)((latencies.Count-1)*.95)],
                requestToPresentP99Ms=latencies[(int)((latencies.Count-1)*.99)],requestToPresentMaxMs=latencies[^1],within120HzBudget=latencies.Count(ms=>ms<=1000d/120),
                admissionMedianMs=admissions[admissions.Count/2],admissionP95Ms=admissions[(int)((admissions.Count-1)*.95)],
                frameNotifications=view.FrameNotifications-notifications,latencies,traces });
            check(legacy || view.FrameNotifications>notifications,"New terminal frames invalidate the view through dispatcher notifications");
        }
        remote.LegacyRefreshForCheck=false; view.LegacyRefreshForCheck=false;
        check(remote.PendingInput==0,"Input acknowledgements drain after the latency workload");
        File.WriteAllText(Path.Combine(SessionStore.Folder,"input-latency.json"),JsonSerializer.Serialize(new {
            version=BuildInfo.Version,measurements=results,
            meaning="UI admission through ConPTY child response, drawing-session disposal and CanvasSwapChain.Present(0) returning for the response frame. Not physical key hardware latency or measured display scanout. The 33ms timer reference uses the same swapchain and is not a measurement of an older release. Stage traces use Stopwatch timestamps; modern phases each contain 100 samples."
        },new JsonSerializerOptions { WriteIndented=true }));
        view.HoldSnapshotsForCheck=true;
        string heldToken=Guid.NewGuid().ToString("N");
        remote.Paste("echo "+heldToken); remote.Key(13,0);
        await remote.InputDrained;
        await wait(()=>remote.FrameAwaitingReadForCheck);
        long heldCaptures=Interlocked.Read(ref remote.CapturesForCheck);
        await Task.Delay(100);
        check(Interlocked.Read(ref remote.CapturesForCheck)==heldCaptures,"An unread frame stops further captures instead of polling or building a frame backlog");
        view.HoldSnapshotsForCheck=false;
        using(var target=view.CreateRenderTargetForCheck()) view.RenderFrameForCheck(target);
        await wait(()=>view.RenderedText.Contains("ACK_"+heldToken));
        check(true,"Consuming the held frame resumes capture and preserves the latest shell output");
        view.SuspendRendering();
        long pausedNotifications=view.FrameNotifications;
        string pausedToken=Guid.NewGuid().ToString("N");
        remote.Paste("echo "+pausedToken); remote.Key(13,0);
        await remote.InputDrained; await Task.Delay(100);
        check(remote.ViewersForCheck==0 && view.FrameNotifications==pausedNotifications,"Suspended views stop capture notifications while their shells continue processing input");
        view.ResumeRendering();
        await wait(()=>view.RenderedText.Contains("ACK_"+pausedToken));
        check(remote.ViewersForCheck==1,"Resuming restores one active viewer and renders output produced during suspension");
        NewTab($"\"{client}\" --raw-input-client","Raw input correctness");
        await wait(()=>_focused?.RenderedText.Contains("RAW_INPUT_READY")==true);
        view=_focused!; remote=(RemoteTerminalSession)_selected.Root.Session!;
        remote.Key(65,0,"a"); remote.Key(65,0,"a",Terminal.TerminalKeyAction.Repeat);
        remote.Key(65,0,"a",Terminal.TerminalKeyAction.Release);
        remote.Paste("AB"); remote.Focus(false); remote.Focus(true);
        string expected=Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("aa\x1b[97;1:3u\x1b[200~AB\x1b[201~\x1b[O\x1b[I"));
        await wait(()=>view.RenderedText.Contains(expected));
        check(true,"Real ConPTY input preserves key press/repeat/release, bracketed paste and focus-event order");
        check(remote.Mouse(0,4,0,8,8),"Mouse ownership follows upstream mode metadata or the legacy host query");
        await wait(()=>view.RenderedText.Contains(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("\x1b[<64;"))));
        check(true,"Queued mouse wheel input reaches the application's raw input through Ghostty's encoder");
        await remote.InputDrained;
        remote.Select(0,2);
        string before=Hex();
        string copied=await remote.CopyOrKeyAsync(67,2,"c",Terminal.TerminalKeyAction.Press,99,0);
        remote.Key(67,2,"c",Terminal.TerminalKeyAction.Release);
        await remote.InputDrained; await Task.Delay(50);
        check(copied.Length>0 && before==Hex(),"Ctrl+C with a selection copies without leaking a key-release report to the application");
        string empty=await remote.CopyOrKeyAsync(67,2,"c",Terminal.TerminalKeyAction.Press,99,0);
        remote.Key(67,2,"c",Terminal.TerminalKeyAction.Release);
        await wait(()=>Hex()!=before);
        check(empty.Length==0,"Ctrl+C without a selection reaches the application rather than being swallowed");
        string originalPane=view.PaneId;
        view.HoldKeyForCheck(66,"b");
        _saved.Settings.Shell=$"\"{client}\" --latency-client";
        Split(true);
        string releaseBytes=Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("b\x1b[98;1:3u"));
        await wait(()=>_views.Count==2 && _views.Any(v=>v.PaneId==originalPane && v.RenderedText.Contains(releaseBytes)));
        check(true,"Splitting a pane releases a held key through its original session after the model node changes");
        string Hex()=>System.Text.RegularExpressions.Regex.Match(view.RenderedText,@"HEX=([A-F0-9]*)").Groups[1].Value;
    }
}
