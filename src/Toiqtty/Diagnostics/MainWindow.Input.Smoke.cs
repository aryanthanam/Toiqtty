using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunInputCloseProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        string client=Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE")!;
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

    private async Task RunInputProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        _metadataTimer.Stop();
        string client=Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE")!;
        var original=_selected!;
        NewWorkspace($"\"{client}\" --latency-client","Input correctness"); RemoveWorkspace(original);
        await wait(()=>_focused is { FramesPainted:>0 } ready && ready.RenderedText.Contains("LATENCY_READY") && !ready.ResizePendingForCheck);
        var view=_focused!;
        var remote=(RemoteTerminalSession)_selected!.Root.Session!;
        check(!_host!.OutputWatchDegraded,"Output notifications stay on the host watch rather than degraded polling");
        long notifications=view.FrameNotifications;
        string token=Guid.NewGuid().ToString("N");
        remote.Paste("echo "+token); remote.Key(13,0);
        await wait(()=>view.RenderedText.Contains("ACK_"+token));
        await remote.InputDrained;
        check(remote.PendingInput==0 && view.FrameNotifications>notifications,"Input acknowledgements drain and response frames reach the view through dispatcher notifications");
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
        check(remote.Mouse(0,4,0,8,8),"Mouse ownership follows upstream mode metadata in the host's shell state");
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
