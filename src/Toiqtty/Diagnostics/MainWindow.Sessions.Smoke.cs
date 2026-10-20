using System.Text.Json;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunSessionUiProbe(string phase)
    {
        var checks=new List<string>(); string? error=null; bool interrupted=false;
        void Check(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        async Task Wait(Func<bool> condition)
        {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if(phase=="endurance" && !_enduranceExpectedHidden && !WindowRenderingActive)
                { timer.Stop(); await WaitForEndurancePresentation(); timer.Start(); }
                if(timer.Elapsed.TotalSeconds>15) throw new TimeoutException("Persistent window probe timed out.");
                await Task.Delay(50);
            }
        }
        try
        {
            if(phase is "performance" or "endurance" or "scale" or "pane-reuse")
            {
                _syntheticInputOnlyForCheck=true;
                foreach(var retained in _terminalViews.Values) retained.View.UseSyntheticInputOnlyForCheck();
            }
            await Task.Delay(1000);
            if (phase == "performance") await RunPerformanceProbe(Check, Wait);
            else if (phase == "pane-reuse") await RunPaneReuseProbe(Check, Wait);
            else if (phase == "scale") await RunScaleProbe(Check,Wait);
            else if (phase is "frame-delta" or "frame-delta-compat") await RunFrameDeltaProbe(Check,Wait,phase=="frame-delta-compat");
            else if (phase == "background") await RunBackgroundProbe(Check,Wait);
            else if (phase == "endurance") await RunEnduranceProbe(Check,Wait);
            else if (phase == "scrolling") await RunScrollingProbe(Check, Wait);
            else if (phase == "resize") await RunResizeProbe(Check, Wait);
            else if (phase == "input") await RunInputProbe(Check, Wait);
            else if (phase == "input-compat") await RunInputProbe(Check, Wait,true);
            else if (phase is "input-close" or "input-close-native") await RunInputCloseProbe(Check, Wait);
            else if (phase is "navigation-prepare" or "navigation-reconnect") await RunNavigationProbe(phase, Check, Wait);
            else if (phase is "prepare" or "crash-prepare")
            {
                // This checks persistence, independently of user profile startup
                // and Starship directory scanning. Profile rendering has its own fixture.
                var previous=_selected!;
                _saved.Settings.Shell=ShellDefaults.Command+" -NoProfile";
                NewWorkspace(_saved.Settings.Shell,"Persistent session"); RemoveWorkspace(previous);
                var session=_selected!;
                await Wait(()=>_focused is { FramesPainted:>0 } ready && ready.RenderedText.Contains("PS "));
                var first=session.ActiveTab; first.Name="PowerShell";
                NewTab("cmd.exe /Q /K","Split shells"); await Task.Delay(200);
                Split(true); await Task.Delay(200); Split(false); await Task.Delay(200);
                var second=session.ActiveTab;
                Check(second.Root.Leaves().Count()==3,"a tab supports nested splits with three independent shells");
                SelectTab(first); await Task.Delay(400);
                string token=Guid.NewGuid().ToString("N");
                string command="$global:TOIQTTY_PERSIST='"+token+"'; Write-Output ('PERSIST_PID='+$PID); Write-Output 'PERSIST_READY'; [Console]::Write(\"`e_Ga=T,f=32,s=1,v=1,i=901,q=2;/wAA/w==`e\\\"); Start-Sleep -Seconds 3; Write-Output 'DETACHED_OUTPUT'";
                first.Root.Leaves().Single().Session!.Paste(command); first.Root.Leaves().Single().Session!.Key(13,0);
                await Wait(()=>_focused is { } view && view.RenderedText.Contains("PERSIST_READY") && view.ImageTextureCount==1);
                string visible=_focused!.TextPeer.DocumentRange.GetText(-1);
                var pid=System.Text.RegularExpressions.Regex.Matches(visible,@"PERSIST_PID=(\d+)").Last().Groups[1].Value;
                SelectTab(second); await Task.Delay(100);
                File.WriteAllText(Path.Combine(SessionStore.Folder,"session-ui-identity.json"),JsonSerializer.Serialize(new {
                    token,pid,host=_host!.Identity,hostProcessId=_host.ProcessId,session=session.Id,first=first.Id,second=second.Id,
                    panes=second.Root.Leaves().Select(p=>p.Id).ToArray(),focus=second.FocusedPaneId }));
                Save();
                Check(_persistent && _host is not null,"WinUI uses the independent session host");
            }
            else if (phase=="reconnect")
            {
                using var expected=JsonDocument.Parse(File.ReadAllText(Path.Combine(SessionStore.Folder,"session-ui-identity.json")));
                var value=expected.RootElement;
                Check(_reattached && _host!.Identity==value.GetProperty("host").GetString(),"a new WinUI process reconnects to the existing host");
                var session=_selected!;
                Check(session.Id==value.GetProperty("session").GetString() && session.Tabs.Count==2,"session and tab identities survive closing the window");
                Check(session.ActiveTab.Id==value.GetProperty("second").GetString() && _views.Count==3,"active tab and nested pane layout are restored");
                Check(_focused?.PaneId==value.GetProperty("focus").GetString(),"focused split pane is restored on reconnect");
                var first=session.Tabs.Single(t=>t.Id==value.GetProperty("first").GetString()); SelectTab(first);
                await Wait(()=>_focused?.RenderedText.Contains("DETACHED_OUTPUT")==true);
                Check(true,"output produced while the window is closed reaches the reattached terminal");
                await Wait(()=>_focused?.ImageTextureCount==1);
                Check(true,"Ghostty graphics survive window detach and rebuild Win2D textures on reconnect");
                var shell=first.Root.Leaves().Single().Session!;
                shell.Paste("Write-Output ('RECONNECTED_PID='+$PID+' TOKEN='+$global:TOIQTTY_PERSIST)"); shell.Key(13,0);
                string marker="RECONNECTED_PID="+value.GetProperty("pid").GetString()+" TOKEN="+value.GetProperty("token").GetString();
                await Wait(()=>_focused?.TextPeer.DocumentRange.GetText(-1).Contains(marker)==true);
                Check(true,"the same PowerShell PID and in-memory variable survive window closure");
                var second=session.Tabs.Single(t=>t.Id==value.GetProperty("second").GetString());
                RemoveTab(session,second);
                var live=_host!.Call(new Hosting.HostRequest()).Panes;
                Check(second.Root.Leaves().All(p=>!live.Contains(p.Id)) && live.Contains(first.Root.Id),"closing a tab ends only its split shells");
                string previousHost = _host.Identity;
                using var previousOwner = System.Diagnostics.Process.GetProcessById(_host.ProcessId);
                RemoveWorkspace(session);
                Check(_saved.Workspaces.Count==0 && _selected is null,"closing the last session leaves an empty window instead of creating another shell");
                await Wait(()=>previousOwner.HasExited);
                NewWorkspace("cmd.exe /Q /K","After host exit");
                Check(_host is not null && _host.Identity != previousHost,"creating a new session after the cached host exits starts a fresh host");
                var restarted = _selected!.Root.Leaves().Single().Session!;
                restarted.Paste("echo HOST_RESTART_READY"); restarted.Key(13,0);
                await Wait(()=>_focused is { } restartedView && restartedView.RenderedText.Contains("HOST_RESTART_READY"));
                Check(true,"the replacement host renders a usable shell in the same window");
                RemoveWorkspace(_selected!);
            }
            else throw new InvalidOperationException("Unknown session probe phase.");
        }
        catch (Exception ex)
        {
            error=ex.ToString();
            interrupted=ex is EnduranceInterruptedException;
            File.WriteAllText(Path.Combine(SessionStore.Folder, "probe-diagnostics.json"), JsonSerializer.Serialize(new {
                selected=_selected?.Id, focused=_focused?.PaneId, active=ActivePaneId,
                WindowRenderingActive,_windowPresentationHidden,_powerSuspended,
                nativeVisible=IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(this)),nativeMinimized=IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(this)),
                hostWidth=PaneHost.ActualWidth, hostHeight=PaneHost.ActualHeight, tabWidth=TabStrip.ActualWidth, tabHeight=TabStrip.ActualHeight,
                views=_views.Select(v=>new { id=v.PaneId, width=v.ActualWidth, height=v.ActualHeight, images=v.ImageTextureCount, frames=v.FramesPainted,
                    text=v.RenderedText,title=v.Session?.Title, focused=v.InputHasFocus,loaded=v.IsLoaded,suspended=v.IsSuspendedForCheck,resizePending=v.ResizePendingForCheck,viewport=v.ViewportLabel,
                    geometry=v.DebugGeometryForCheck,cache=(v.Session as Hosting.RemoteTerminalSession)?.DebugCacheForCheck,
                    suspension=v.SuspensionForCheck,
                    pendingInput=(_saved.Workspaces.SelectMany(w=>w.AllPanes()).FirstOrDefault(p=>p.Id==v.PaneId)?.Session as Hosting.RemoteTerminalSession)?.PendingInput,
                    captureFailure=(_saved.Workspaces.SelectMany(w=>w.AllPanes()).FirstOrDefault(p=>p.Id==v.PaneId)?.Session as Hosting.RemoteTerminalSession)?.CaptureFailure,
                    inputStatus=_saved.Workspaces.SelectMany(w=>w.AllPanes()).FirstOrDefault(p=>p.Id==v.PaneId)?.Session?.StatusSummary }).ToArray()
            }));
        }
        finally
        {
            string report=Path.Combine(SessionStore.Folder,"session-ui-"+phase+".json");
            File.WriteAllText(report+".tmp",JsonSerializer.Serialize(new { passed=error is null,checks,error,interrupted },new JsonSerializerOptions { WriteIndented=true }));
            File.Move(report+".tmp",report,true);
            if (phase is not ("crash-prepare" or "input-close-native") || error is not null) Close();
        }
    }
}
