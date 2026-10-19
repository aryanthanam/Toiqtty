using System.Text.Json;
using Oroterm.Hosting;
using Oroterm.Models;
using Windows.Graphics;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunFrameDeltaProbe(Action<bool,string> check,Func<Func<bool>,Task> wait,bool compatibility)
    {
        _metadataTimer.Stop(); string client=Environment.GetEnvironmentVariable("OROTERM_SMOKE_EXE")!;
        var original=_selected!; NewWorkspace($"\"{client}\" --delta-client","Row deltas"); RemoveWorkspace(original);
        await wait(()=>_focused is { FramesPainted:>0 } ready && ready.HasImage(902) && !ready.ResizePendingForCheck);
        var view=_focused!; var remote=(RemoteTerminalSession)view.Session!;
        string identity=_host!.Identity;
        check(_host.SupportsFrameDeltas!=compatibility,compatibility?"An older host remains on the full-frame protocol":"The updated host negotiates row-delta transport");
        long startBytes=remote.FrameBytesForCheck,startDelta=remote.DeltaFramesForCheck,startFull=remote.FullFramesForCheck;
        for(int i=0;i<20;i++)
        {
            string token=Guid.NewGuid().ToString("N")[..12]; int row=3+i%3;
            remote.Paste("set "+row+" "+token); remote.Key(13,0);
            await wait(()=>view.RenderedRowForCheck(row-1).Contains("DELTA_"+token,StringComparison.Ordinal));
        }
        long deltaFrames=remote.DeltaFramesForCheck-startDelta,fullFrames=remote.FullFramesForCheck-startFull,bytes=remote.FrameBytesForCheck-startBytes;
        check(compatibility?deltaFrames==0 && fullFrames>0:deltaFrames>=20,"Sparse updates reach the renderer through the negotiated snapshot path");
        check(view.ImageTextureCount==1 && remote.FullFrameRecoveriesForCheck==0,"Sparse updates preserve Kitty image generations without recovery failures");
        if(!compatibility)
        {
            long fullBefore=remote.FullFramesForCheck;
            _host.Call(new() { Operation="capture",Pane=view.PaneId,Numbers=[-1] });
            string token=Guid.NewGuid().ToString("N")[..12];
            remote.Paste("set 4 "+token); remote.Key(13,0);
            await wait(()=>view.RenderedRowForCheck(3).Contains("DELTA_"+token) && remote.FullFramesForCheck>fullBefore);
            check(true,"An intervening reader invalidates the delta base and receives a full replacement frame");
            int corrupted=0; fullBefore=remote.FullFramesForCheck; long recoveries=remote.FullFrameRecoveriesForCheck;
            void Corrupt(HostReply reply) { if(reply.DeltaBaseRevision is not null && Interlocked.CompareExchange(ref corrupted,1,0)==0) reply.DeltaBaseRevision=-9000; }
            remote.CaptureReplyForCheck+=Corrupt;
            try
            {
                token=Guid.NewGuid().ToString("N")[..12]; remote.Paste("set 5 "+token); remote.Key(13,0);
                await wait(()=>Volatile.Read(ref corrupted)==1 && remote.FullFrameRecoveriesForCheck==recoveries+1 && remote.FullFramesForCheck>fullBefore
                    && view.RenderedRowForCheck(4).Contains("DELTA_"+token));
            }
            finally { remote.CaptureReplyForCheck-=Corrupt; }
            check(_host.Identity==identity && !remote.StatusSummary.Contains("disconnected",StringComparison.OrdinalIgnoreCase),
                "A deliberately mismatched delta base recovers a full frame without replacing the host or replaying input");
            check(view.ImageTextureCount==1 && await view.CheckRenderedImage(902,Windows.UI.Color.FromArgb(255,255,0,0),Path.Combine(SessionStore.Folder,"delta-recovery-image.png")),
                "Full-frame recovery restores image cache ownership and correct retained pixels");
            fullBefore=remote.FullFramesForCheck;
            AppWindow.Resize(new SizeInt32(AppWindow.Size.Width-40,AppWindow.Size.Height-30));
            await wait(()=>!view.ResizePendingForCheck && remote.FullFramesForCheck>fullBefore);
            check(true,"Viewport resizing replaces the delta baseline with a full frame");
        }
        File.WriteAllText(Path.Combine(SessionStore.Folder,"frame-delta.json"),JsonSerializer.Serialize(new {
            version=BuildInfo.Version,compatibility,deltaFrames,fullFrames,bytes,averagePayloadBytes=bytes/(double)Math.Max(1,deltaFrames+fullFrames),
            recoveries=remote.FullFrameRecoveriesForCheck,host=identity,meaning="Twenty sparse row updates through real ConPTY, host snapshot transport and Win2D presentation. One baseline mismatch is deliberately injected only in the updated-host fixture."
        },new JsonSerializerOptions { WriteIndented=true }));
    }
}
