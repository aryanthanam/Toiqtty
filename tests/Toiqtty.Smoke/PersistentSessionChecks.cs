using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class PersistentSessionChecks
{
    public static async Task RunUpgradeWindow(string appExecutable, Action<bool,string> check)
    {
        string folder = Path.GetFullPath("artifacts/browser-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string saved = "{\"Version\":5,\"Workspaces\":[],\"Settings\":{\"RestoreWorkspaces\":false}}";
        File.WriteAllText(Path.Combine(folder,"session.json"), saved);
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(HostProtocol.PipeName(folder), System.IO.Pipes.PipeDirection.InOut,
            1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var legacy = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            var request = await HostProtocol.ReadJson<HostRequest>(pipe,timeout.Token);
            await HostProtocol.WriteJson(pipe,new HostReply { Protocol=2,Host="legacy",Error="Incompatible session-host protocol." },timeout.Token);
            return request.Operation;
        });
        var start = new ProcessStartInfo(appExecutable) { UseShellExecute=false,WorkingDirectory=Environment.CurrentDirectory,WindowStyle=ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--ui-smoke"); start.Environment["TOIQTTY_DATA_DIR"]=folder;
        using var window = Process.Start(start)!;
        try
        {
            await Wait(()=> { window.Refresh(); return window.MainWindowTitle=="toiqtty update"; });
            check(true,"starting a new release against an older host shows a native WinUI upgrade window");
            check(await legacy=="ping" && File.ReadAllText(Path.Combine(folder,"session.json"))==saved,
                "the upgrade guard preserves saved state and sends no shell creation or layout update to the older host");
            window.CloseMainWindow(); await window.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            check(!File.Exists(Path.Combine(folder,"crash.log")),"the upgrade window closes cleanly without constructing or saving the terminal window");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,checks=3 },new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("Upgrade guard evidence: "+folder);
        }
        finally { timeout.Cancel(); if (!window.HasExited) { window.Kill(); await window.WaitForExitAsync(); } }
    }
    public static async Task RunShellClient()
    {
        Console.OutputEncoding=new System.Text.UTF8Encoding(false);
        Console.WriteLine("PERSIST_READY PID="+Environment.ProcessId);
        int count=0;
        while (Console.ReadLine() is { } line)
        {
            switch (line.Trim())
            {
                case "mark": Console.WriteLine("PERSIST_MARK="+(++count)+" PID="+Environment.ProcessId); break;
                case "seed":
                    for (int i=0;i<50;i++) Console.WriteLine("PERSIST_HISTORY_"+i+" café 中🙂");
                    Console.Write("\x1b_Ga=T,f=32,s=1,v=1,i=901,q=2;/wAA/w==\x1b\\");
                    Console.WriteLine("PERSIST_SEEDED"); break;
                case "background": _=Task.Run(async ()=> { await Task.Delay(700); Console.WriteLine("PERSIST_DETACHED_OUTPUT"); }); break;
                case "animate":
                    Console.Write("\x1b_Ga=T,i=902,f=24,s=1,v=1,C=1,q=2;/wAA\x1b\\"
                        +"\x1b_Ga=f,i=902,f=24,s=1,v=1,z=10,q=2;AAD/\x1b\\"
                        +"\x1b_Ga=a,i=902,r=1,z=10,s=3,v=2,q=2;\x1b\\"); break;
                case "exit": return;
            }
        }
    }
    private static async Task Wait(Func<bool> condition,int seconds=12)
    {
        var until=Stopwatch.StartNew();
        while (!condition()) { if (until.Elapsed.TotalSeconds>seconds) throw new TimeoutException("Persistent session check timed out."); await Task.Delay(50); }
    }
    public static async Task Run(string hostExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/session-host-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var checks=new List<string>();
        void Pass(bool result,string message) { check(result,message); checks.Add(message); }
        SessionHostClient? client=null; RemoteTerminalSession? first=null, second=null;
        try
        {
            var clients=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>Task.Run(()=>SessionHostClient.ConnectOrStart(folder,hostExecutable))));
            client=clients[0];
            Pass(client.IsRunning,"the cached session-host owner is reported alive");
            Pass(clients.All(c=>c.Identity==client.Identity && c.ProcessId==client.ProcessId),"concurrent launches share one session host per data directory");
            string pane=Guid.NewGuid().ToString("N");
            string command="\""+Environment.ProcessPath+"\" --persistent-shell-client";
            first=new(client,pane,command,Environment.CurrentDirectory,new AppSettings()); first.Resize(80,20,8,20); first.AttachView();
            var cells=new TerminalCell[1600];
            string Text(RemoteTerminalSession s)=>string.Concat(cells.Select(c=>c.GetText()));
            TerminalSnapshot Capture(RemoteTerminalSession s)=>s.ReadSnapshot(cells);
            await Wait(()=> { Capture(first); return Text(first).Contains("PERSIST_READY PID="); });
            string pid=System.Text.RegularExpressions.Regex.Match(Text(first),@"PERSIST_READY PID=(\d+)").Groups[1].Value;
            Pass(pid.Length>0,"the host starts a real ConPTY process and returns its terminal cells");
            void Line(RemoteTerminalSession s,string text) { s.Paste(text); s.Key(13,0); }
            Line(first,"mark"); await Wait(()=> { Capture(first); return Text(first).Contains("PERSIST_MARK=1"); });
            Line(first,"seed"); await Wait(()=>Capture(first).Graphics.Any(g=>g.ImageId==901));
            var before=Capture(first); byte[] pixels=before.Graphics.Single(g=>g.ImageId==901).Image.Pixels;
            Pass(pixels.SequenceEqual(new byte[] {255,0,0,255}),"session transport retains exact Ghostty image pixels and placements");
            Pass(Text(first).Contains("café 中🙂"),"session snapshots preserve Unicode and wide-cell text across the process boundary");
            var full=client.CallForTest(HostRequests.Capture(pane));
            var cached=client.CallForTest(HostRequests.Capture(pane,images:[before.Graphics.Single(g=>g.ImageId==901).Image.Generation]));
            Pass(cached.BinaryLength<full.BinaryLength,"cached image generations omit repeated pixel transfers");
            Line(first,"animate");
            Line(first,"background");
            first.Detach(); first=null; client.DetachForTest(); await Task.Delay(1200);
            var reconnected=SessionHostClient.TryConnect(folder)!;
            Pass(reconnected is not null && reconnected.Identity==client.Identity && reconnected.ProcessId==client.ProcessId,"detaching the frontend keeps the same host alive");
            second=new(reconnected!,pane,"invalid-command-must-not-run",Environment.CurrentDirectory,new AppSettings()); second.Resize(80,20,8,20); second.AttachView();
            await Wait(()=> { Capture(second); return Text(second).Contains("PERSIST_DETACHED_OUTPUT"); });
            Pass(true,"the host drains output while no window is attached");
            Pass(Capture(second).Graphics.Any(g=>g.ImageId==901 && g.Image.Pixels.SequenceEqual(pixels)),"graphics remain in Ghostty across frontend detach and reconnect");
            var animated=Capture(second);
            await Wait(()=> { animated=Capture(second); return animated.Frame.AnimationDelay==uint.MaxValue && animated.Graphics.Any(g=>g.ImageId==902 && g.Image.Pixels[2]==255); });
            Pass(true,"finite upstream animations complete while the frontend is detached");
            Line(second,"mark"); await Wait(()=> { Capture(second); return Text(second).Contains("PERSIST_MARK=2 PID="+pid); });
            Pass(true,"reconnect preserves the child PID and in-memory shell state instead of restarting it");
            var found=second.Find("PERSIST_HISTORY_0",0);
            Pass(found.Count>0 && second.CopySelection().Contains("PERSIST_HISTORY_0"),"scrollback search and selection survive reconnect");
            second.Find("",0); second.Select(-1,-1); second.Scroll(0);
            var settingsAttach=new RemoteTerminalSession(reconnected!,pane,"invalid-command-must-not-run",Environment.CurrentDirectory,new AppSettings { ScrollbackLimitBytes=0 });
            settingsAttach.Detach();
            Pass(second.Find("PERSIST_HISTORY_0",0).Count==0,"reattaching applies new scrollback settings over IPC without restarting the shell");
            second.Find("",0);
            Line(second,"mark"); await Wait(()=> { Capture(second); return Text(second).Contains("PERSIST_MARK=3 PID="+pid); });
            Pass(true,"changing scrollback limits preserves the child PID and shell state");
            second.ApplySettings(new AppSettings { ScrollbackLimitBytes=null,ScrollbackLimitLines=null });
            Line(second,"seed"); await Wait(()=>second.Find("PERSIST_HISTORY_0",0).Count>0);
            Pass(second.Find("PERSIST_HISTORY_0",0).Count>0,"unlimited history survives IPC serialization and retains new output after re-enabling scrollback");
            second.Find("",0); second.Select(-1,-1); second.Scroll(0);
            second.Resize(100,16,8,20);
            bool ready=second.TryReadSnapshot(cells,out var reshaped);
            Pass(!ready || (reshaped!.Frame.Columns==100 && reshaped.Frame.Rows==16),"equal-area resizes never expose a stale frame with the previous row geometry");
            second.ReadSnapshot(cells);
            bool rejected=false;
            try { var future=HostRequests.Ping(); future.Protocol=999; reconnected!.CallForTest(future); } catch (HostRejectedException ex) { rejected=ex.Code==HostErrorCode.ProtocolMismatch; }
            Pass(rejected && SessionHostClient.TryConnect(folder) is not null,"incompatible IPC requests are rejected with ProtocolMismatch without ending live shells");
            HostErrorCode lastCode=HostErrorCode.None;
            async Task<string?> Answer(HostRequest request)
            {
                lastCode=HostErrorCode.None;
                try { await reconnected!.CallAsync(request); return null; }
                catch (HostRejectedException ex) { lastCode=ex.Code; return ex.Message; }
                catch (IOException ex) { return "EOF: "+ex.Message; }
            }
            var nullWorkspace=new SavedSession { Workspaces=[null!] };
            var nullTab=new SavedSession { Workspaces=[new Workspace { Tabs=[null!] }] };
            string? syncError=await Answer(HostRequests.Sync(nullWorkspace)); var syncCode=lastCode;
            string? createError=await Answer(HostRequests.Create(Guid.NewGuid().ToString("N"),"cmd.exe",Environment.CurrentDirectory,null,nullTab)); var createCode=lastCode;
            string? missingError=await Answer(HostRequests.Copy("missing-"+Guid.NewGuid().ToString("N"))); var missingCode=lastCode;
            string? unknownError=await Answer(new() { Operation="no-such-op",Pane=pane }); var unknownCode=lastCode;
            string? limitError=await Answer(HostRequests.Create(Guid.NewGuid().ToString("N"),"",Environment.CurrentDirectory,null)); var limitCode=lastCode;
            Pass(missingError=="This shell session no longer exists." && missingCode==HostErrorCode.SessionMissing && unknownError=="Unknown session operation." && unknownCode==HostErrorCode.UnknownOperation
                && limitError=="Shell creation exceeds the session limits." && limitCode==HostErrorCode.LimitExceeded,"host error replies carry structured codes beside their unchanged messages");
            Pass(syncError?.StartsWith("Invalid session")==true && createError?.StartsWith("Invalid session")==true && syncCode==HostErrorCode.InvalidRequest && createCode==HostErrorCode.InvalidRequest && !second.HasExited
                && reconnected!.CallForTest(new()).Panes.SequenceEqual([pane]),"malformed layouts with null workspaces or tabs get an error reply, not EOF, and change no shells");
            Pass(GhosttyTerminal.Saturate(0)==0 && GhosttyTerminal.Saturate(int.MaxValue)==int.MaxValue && GhosttyTerminal.Saturate((nuint)int.MaxValue+1)==int.MaxValue
                && GhosttyTerminal.Saturate(nuint.MaxValue)==int.MaxValue && second.Find("PERSIST_MARK",0).Count>0,"find saturates match counts beyond int instead of failing the request");
            second.Find("",0); second.Select(-1,-1);
            await RoundTrips(reconnected!,pane,Pass);
            using (var framed=new MemoryStream())
            {
                bool overflow=false;
                try { await HostProtocol.WriteJson(framed,new HostReply { Text=new string('x',HostProtocol.MaxJsonBytes+1) },CancellationToken.None); }
                catch (HostMessageTooLargeException) { overflow=true; }
                long written=framed.Length;
                for (int i=0;i<2;i++) await HostProtocol.WriteJson(framed,HostProtocol.OversizedReply("host",1),CancellationToken.None);
                framed.Position=written;
                var copy=await HostProtocol.ReadJson<HostReply>(framed,CancellationToken.None); var states=await HostProtocol.ReadJson<HostReply>(framed,CancellationToken.None);
                Pass(overflow && written==0 && copy.Error==HostProtocol.ReplyTooLarge && states.Error==HostProtocol.ReplyTooLarge && copy.Host=="host"
                    && copy.ErrorCode==HostErrorCode.ReplyTooLarge && states.ErrorCode==HostErrorCode.ReplyTooLarge,
                    "an oversized reply for any operation is replaced by a framed, coded error before any header is sent");
            }
            await HostV2Wire.RunHost(folder,Pass);
            Pass(SessionHostClient.TryConnect(folder) is not null && !second.HasExited,"raw protocol-2 requests, shutdown included, leave the live host and its shell intact");
            using var owner=Process.GetProcessById(client.ProcessId);
            second.Dispose(); second=null; await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Pass(true,"closing the last shell stops the background host");
            Pass(!client.IsRunning,"an exited cached host is detected before creating another shell");
            client=SessionHostClient.ConnectOrStart(folder,hostExecutable);
            using var exitedOwner=Process.GetProcessById(client.ProcessId);
            var exited=new RemoteTerminalSession(client,Guid.NewGuid().ToString("N"),"cmd.exe /c exit 0",Environment.CurrentDirectory,new AppSettings());
            await exitedOwner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); exited.Detach();
            Pass(true,"the host exits after its last child exits naturally");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,checks },new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("Session host evidence: "+folder);
        }
        finally
        {
            first?.Dispose(); second?.Dispose();
            try { SessionHostClient.TryConnect(folder)?.ShutdownForTest(); } catch (IOException) { }
        }
    }
    // Every protocol-3 operation once against the live host, on a disposable cmd pane,
    // plus the payload faults. Sync and shutdown are covered by the scheduling and
    // lifecycle checks; clipboard is skipped so the test never reads the user's clipboard.
    private static async Task RoundTrips(SessionHostClient client,string owned,Action<bool,string> pass)
    {
        string probe=Guid.NewGuid().ToString("N"),missing="missing-"+Guid.NewGuid().ToString("N");
        var replies=new Dictionary<string,HostReply>(); var failures=new List<string>();
        async Task<HostReply?> Call(string label,HostRequest request)
        {
            try { var reply=await client.CallAsync(request,TimeSpan.FromSeconds(10)); replies[label]=reply; return reply; }
            catch (IOException ex) { failures.Add(label+": "+ex.Message); return null; }
        }
        var created=await Call("create",HostRequests.Create(probe,"cmd.exe /d /q /k",Environment.CurrentDirectory,new AppSettings(),columns:120,rows:40));
        var reattached=await Call("create-existing",HostRequests.Create(probe,"invalid-command-must-not-run",Environment.CurrentDirectory,new AppSettings()));
        await Call("ping",HostRequests.Ping());
        await Call("states",HostRequests.States(new() { [probe]=-1 }));
        await Call("watch",HostRequests.Watch(new() { [probe]=-1 }));
        await Call("attach",HostRequests.Attach([new AppSettings()],[new(probe,0),new(owned,-1),new(missing,-1)]));
        await Call("navigate",HostRequests.Navigate(new(null,[])));
        await Call("detach",HostRequests.Detach());
        var deadline=Stopwatch.StartNew(); HostReply? capture;
        // 120x40 exceeds the default 3000-cell buffer: the first capture runs before any resize.
        // Capture never waits on a busy parser; retry until a frame is available.
        while ((capture=await Call("capture",HostRequests.Capture(probe))) is { BinaryLength:0 } && deadline.Elapsed.TotalSeconds<5) await Task.Delay(20);
        await Call("resize",HostRequests.Resize(probe,120,40,8,18));
        await Call("key",HostRequests.Key(probe,65,0,"a"));
        await Call("key-release",HostRequests.Key(probe,65,0,"a",TerminalKeyAction.Release));
        await Call("paste",HostRequests.Paste(probe,"echo ROUND_TRIP"));
        await Call("send-text",HostRequests.SendText(probe,"\r"));
        await Call("paste-checked",HostRequests.PasteChecked(probe,"rem"));
        await Call("scroll",HostRequests.Scroll(probe,-1));
        await Call("scroll-to",HostRequests.ScrollTo(probe,0));
        await Call("focus",HostRequests.Focus(probe,true));
        await Call("select",HostRequests.Select(probe,0,4));
        await Call("copy",HostRequests.Copy(probe));
        var copied=await Call("copy-or-key",HostRequests.CopyOrKey(probe,67,2,"c",TerminalKeyAction.Press,99,0));
        var empty=await Call("copy-or-key-empty",HostRequests.CopyOrKey(probe,67,2,"c",TerminalKeyAction.Press,99,0));
        await Call("gesture",HostRequests.Gesture(probe,0,1.5f,0.5f,false));
        await Call("mouse",HostRequests.Mouse(probe,0,1,0,1.5f,0.5f));
        await Call("drop",HostRequests.DropQuery(probe,2,0,0,1,null));
        await Call("drop-async",HostRequests.DropCommit(probe,0,0,1,TimeSpan.FromMilliseconds(1),new Dictionary<string,byte[]> { ["text/plain"]=[104,105] }));
        await Call("hyperlink",HostRequests.Hyperlink(probe,0,0));
        await Call("find",HostRequests.Find(probe,"ROUND_TRIP",0));
        await Call("settings",HostRequests.Settings(probe,new AppSettings()));
        foreach (var failure in failures) Console.Error.WriteLine("round trip "+failure);
        HostReply R(string label)=>replies.GetValueOrDefault(label) ?? new() { Error="missing" };
        pass(failures.Count==0 && created is { Created:true,State:not null } && reattached is { Created:false,State:not null }
            && R("ping").Panes.Contains(probe) && R("states").States?.ContainsKey(probe)==true && R("watch").OutputRevisions?.ContainsKey(probe)==true && R("watch").OutputRevision>0
            && R("attach").States?.Keys.Order().SequenceEqual(new[] { probe,owned }.Order())==true && R("attach").Missing?.SequenceEqual([missing])==true
            && capture is { BinaryLength:>0,StatusRevision:not null } && R("resize").State is not null && R("key").State is not null && R("settings").State is not null
            && copied?.Copied==(copied?.Text.Length>0) && copied?.State is not null && empty is { Copied:false,Text.Length:0 }
            && R("gesture").Value is not null && R("mouse").Value is not null && R("drop").Value is not null && R("drop-async").Value is not null && R("find").Find is not null,
            "every protocol-3 operation round-trips against a live host with its typed result");
        pass(created is { Created:true } && capture is { BinaryLength:>0 } && !failures.Any(f=>f.StartsWith("capture:")),"a pane created at 120x40, above the default cell buffer, captures before its first resize");
        HostErrorCode Code(string label,HostRequest request)
        {
            try { client.CallForTest(request); return HostErrorCode.None; }
            catch (HostRejectedException ex) { return ex.Code; }
            catch (IOException) { Console.Error.WriteLine("fault EOF: "+label); return HostErrorCode.Disconnected; }
        }
        var badKey=HostRequests.Key(probe,65,0); badKey.Key=badKey.Key! with { Action=(TerminalKeyAction)7 };
        var bare=HostRequests.Key(probe,65,0); bare.Key=null;
        pass(Code("action",badKey)==HostErrorCode.InvalidRequest && Code("resize",HostRequests.Resize(probe,5000,30,8,18))==HostErrorCode.InvalidRequest
            && Code("bare",bare)==HostErrorCode.InvalidRequest && Code("create-size",HostRequests.Create(Guid.NewGuid().ToString("N"),"cmd.exe",Environment.CurrentDirectory,null,columns:1,rows:1))==HostErrorCode.InvalidRequest
            && Code("missing",HostRequests.Copy(missing))==HostErrorCode.SessionMissing && Code("watch",HostRequests.Watch(new()))==HostErrorCode.InvalidRequest,
            "invalid key actions, out-of-range sizes, absent payloads and empty watches are InvalidRequest; an absent pane is SessionMissing");
        string malformed;
        using (var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        await using (var pipe=new System.IO.Pipes.NamedPipeClientStream(".",HostProtocol.PipeName(client.Folder),System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly))
        {
            await pipe.ConnectAsync(timeout.Token);
            byte[] payload=System.Text.Encoding.UTF8.GetBytes("{\"Protocol\":3,\"Host\":\""+client.Identity+"\",\"Operation\":\"key\",\"Pane\":\""+probe+"\",\"Key\":{\"Key\":-1}}");
            byte[] frame=new byte[payload.Length+4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame,payload.Length); payload.CopyTo(frame,4);
            await pipe.WriteAsync(frame,timeout.Token);
            try { var reply=await HostProtocol.ReadJson<HostReply>(pipe,timeout.Token); malformed=reply.ErrorCode.ToString(); }
            catch (IOException ex) { malformed="EOF: "+ex.Message; }
        }
        pass(malformed==nameof(HostErrorCode.InvalidRequest),"a payload of the wrong JSON type gets an InvalidRequest reply instead of a closed pipe");
        await Call("close",HostRequests.Close(probe));
        pass(failures.Count==0 && !(await client.CallAsync(HostRequests.Ping())).Panes.Contains(probe),"close ends the probe shell and leaves the owned pane running");
    }
    public static async Task RunWindows(string appExecutable,Action<bool,string> check,bool crash=false)
    {
        string folder=Path.GetFullPath("artifacts/session-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string phase in new[] { crash?"crash-prepare":"prepare","reconnect" })
            {
                var start=new ProcessStartInfo(appExecutable) { UseShellExecute=false,WorkingDirectory=Environment.CurrentDirectory };
                start.ArgumentList.Add("--ui-smoke");
                start.Environment["TOIQTTY_DATA_DIR"]=folder;
                start.Environment["TOIQTTY_SESSION_UI_PROBE"]=phase;
                start.Environment["TOIQTTY_NOTIFICATION_SMOKE"]="0";
                using var window=Process.Start(start)!;
                if (phase=="crash-prepare")
                {
                    try { await Wait(()=>File.Exists(Path.Combine(folder,"session-ui-crash-prepare.json")),30); }
                    finally { if (!window.HasExited) { window.Kill(); await window.WaitForExitAsync(); } }
                    check(true,"forced termination of the WinUI process does not terminate its independent host");
                }
                try { await window.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
                catch (TimeoutException) { if (!window.HasExited) { window.Kill(); await window.WaitForExitAsync(); } throw; }
                string reportPath=Path.Combine(folder,"session-ui-"+phase+".json");
                if (!File.Exists(reportPath)) throw new IOException("No persistent WinUI report: "+folder);
                using var report=JsonDocument.Parse(File.ReadAllText(reportPath));
                if (!report.RootElement.GetProperty("passed").GetBoolean()) throw new InvalidOperationException(report.RootElement.GetProperty("error").GetString());
                foreach (var item in report.RootElement.GetProperty("checks").EnumerateArray()) check(true,item.GetString()!);
                if (phase is "prepare" or "crash-prepare")
                {
                    var host=SessionHostClient.TryConnect(folder);
                    check(host is not null && host.Panes.Length==4,"closing the entire WinUI process leaves all four shell panes running");
                    await Task.Delay(3500);
                }
            }
            using var identity=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"session-ui-identity.json")));
            int hostPid=identity.RootElement.GetProperty("hostProcessId").GetInt32();
            await Wait(()=> { try { return Process.GetProcessById(hostPid).HasExited; } catch (ArgumentException) { return true; } });
            // The reconnect probe also starts and closes a replacement host.
            await Wait(()=>SessionHostClient.TryConnect(folder) is null);
            check(SessionHostClient.TryConnect(folder) is null,"closing the last session through WinUI shuts down its background host");
            Console.WriteLine("Persistent WinUI evidence: "+folder);
        }
        finally { try { SessionHostClient.TryConnect(folder)?.ShutdownForTest(); } catch (IOException) { } }
    }
}
