using System.Text.Json;
using Microsoft.UI.Windowing;
using Toiqtty.Hosting;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunBackgroundProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        _metadataTimer.Stop(); string client=Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE")!;
        string command=$"\"{client}\" --performance-client";
        var original=_selected!; NewWorkspace(command,"Background rendering"); RemoveWorkspace(original);
        await wait(()=>_focused is { FramesPainted:>0 } ready && ready.RenderedText.Contains("PERF_READY") && !ready.ResizePendingForCheck);
        var view=_focused!; var remote=(RemoteTerminalSession)view.Session!; var first=_selected!.ActiveTab;
        var presenter=AppWindow.Presenter as OverlappedPresenter??throw new InvalidOperationException("The background fixture requires an overlapped window.");
        string host=_host!.Identity,run=Guid.NewGuid().ToString("N"),token=Guid.NewGuid().ToString("N")[..6];
        remote.Paste("flood2 2500 8192 "+run); remote.Key(13,0);
        await remote.InputDrained;
        presenter.Minimize(false);
        await wait(()=>_windowPresentationHidden && view.IsSuspendedForCheck && remote.ViewersForCheck==0);
        long frames=view.FramesPainted,captures=remote.CapturesForCheck,bytes=remote.Performance.BytesRead;
        long origin=System.Diagnostics.Stopwatch.GetTimestamp(); remote.Paste("ping2 "+token+" "+origin); remote.Key(13,0);
        await wait(()=>remote.Performance.BytesRead>bytes+1048576 && remote.Title.StartsWith("FLOOD_DONE_"+run+"_",StringComparison.Ordinal));
        check(view.FramesPainted==frames && remote.CapturesForCheck<=captures+1,"Minimizing stops drawing and visible captures while the shell continues parsing megabytes of output");
        NewTab(command,"Created while minimized");
        await wait(()=>_selected!.ActiveTab.Root.Session?.Performance.BytesRead>0);
        check(_views.All(v=>v.IsSuspendedForCheck && (v.Session as RemoteTerminalSession)?.ViewersForCheck==0),
            "A terminal created while minimized remains suspended without a capture attachment");
        SelectTab(first);
        check(ReferenceEquals(_views.Single(),view) && view.IsSuspendedForCheck,"Rebinding a retained tab while minimized does not resume presentation");
        presenter.Restore(false);
        await wait(()=>!_windowPresentationHidden && !view.IsSuspendedForCheck && remote.ViewersForCheck==1 && view.FramesPainted>frames && view.InputHasFocus);
        await wait(()=>view.RenderedRowForCheck(1).Contains("PONG_"+token,StringComparison.Ordinal));
        check(_host!.Identity==host && ReferenceEquals(view.Session,remote),"Restoring resumes the same view and shell and renders output produced while minimized");
        frames=view.FramesPainted; captures=remote.CapturesForCheck;
        AppWindow.Hide(); await wait(()=>_windowPresentationHidden && remote.ViewersForCheck==0);
        await Task.Delay(250);
        check(view.FramesPainted<=frames+1 && remote.CapturesForCheck<=captures+1,"Explicit window hiding also stops presentation and visible capture");
        AppWindow.Show(false);
        await wait(()=>!_windowPresentationHidden && remote.ViewersForCheck==1 && view.InputHasFocus);
        check(remote.FrameListenersForCheck==1,"Visibility transitions retain exactly one frame subscription");
        File.WriteAllText(Path.Combine(SessionStore.Folder,"background.json"),JsonSerializer.Serialize(new {
            version=BuildInfo.Version,host,bytesReadWhileMinimized=remote.Performance.BytesRead-bytes,preservedView=true,viewerCount=remote.ViewersForCheck,frameListeners=remote.FrameListenersForCheck
        },new JsonSerializerOptions { WriteIndented=true }));
    }
}
