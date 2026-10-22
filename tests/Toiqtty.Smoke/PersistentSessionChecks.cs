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
            await HostProtocol.WriteJson(pipe,new HostReply { Protocol=1,Host="legacy",Error="Incompatible session-host protocol." },timeout.Token);
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
            var full=client.CallForTest(new() { Operation="capture",Pane=pane,Numbers=[-1] });
            var cached=client.CallForTest(new() { Operation="capture",Pane=pane,Numbers=[-1],Images=[before.Graphics.Single(g=>g.ImageId==901).Image.Generation] });
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
            try { reconnected!.CallForTest(new() { Protocol=999,Operation="ping" }); } catch (IOException) { rejected=true; }
            Pass(rejected && SessionHostClient.TryConnect(folder) is not null,"incompatible IPC requests are rejected without ending live shells");
            async Task<string?> Answer(HostRequest request)
            {
                try { await reconnected!.CallAsync(request); return null; }
                catch (HostRejectedException ex) { return ex.Message; }
                catch (IOException ex) { return "EOF: "+ex.Message; }
            }
            var nullWorkspace=new SavedSession { Workspaces=[null!] };
            var nullTab=new SavedSession { Workspaces=[new Workspace { Tabs=[null!] }] };
            string? syncError=await Answer(new() { Operation="sync",Layout=nullWorkspace });
            string? createError=await Answer(new() { Operation="create",Pane=Guid.NewGuid().ToString("N"),Text="cmd.exe",Directory=Environment.CurrentDirectory,Layout=nullTab });
            Pass(syncError?.StartsWith("Invalid session")==true && createError?.StartsWith("Invalid session")==true && !second.HasExited
                && reconnected!.CallForTest(new()).Panes.SequenceEqual([pane]),"malformed layouts with null workspaces or tabs get an error reply, not EOF, and change no shells");
            Pass(GhosttyTerminal.Saturate(0)==0 && GhosttyTerminal.Saturate(int.MaxValue)==int.MaxValue && GhosttyTerminal.Saturate((nuint)int.MaxValue+1)==int.MaxValue
                && GhosttyTerminal.Saturate(nuint.MaxValue)==int.MaxValue && second.Find("PERSIST_MARK",0).Count>0,"find saturates match counts beyond int instead of failing the request");
            second.Find("",0); second.Select(-1,-1);
            using (var framed=new MemoryStream())
            {
                bool overflow=false;
                try { await HostProtocol.WriteJson(framed,new HostReply { Text=new string('x',HostProtocol.MaxJsonBytes+1) },CancellationToken.None); }
                catch (HostMessageTooLargeException) { overflow=true; }
                long written=framed.Length;
                foreach (string operation in new[] { "copy","states" }) await HostProtocol.WriteJson(framed,HostProtocol.OversizedReply(operation,"host",1),CancellationToken.None);
                framed.Position=written;
                var copy=await HostProtocol.ReadJson<HostReply>(framed,CancellationToken.None); var states=await HostProtocol.ReadJson<HostReply>(framed,CancellationToken.None);
                Pass(overflow && written==0 && copy.Error==HostProtocol.ReplyTooLarge && states.Error==HostProtocol.MetadataBatchTooLarge && copy.Host=="host",
                    "an oversized reply for any operation is replaced by a framed error before any header is sent; states keeps its batch-split text");
            }
            await HostV2Wire.RunHost(folder,Pass);
            Pass(SessionHostClient.TryConnect(folder) is not null && !second.HasExited,"raw protocol-2 requests leave the live host and its shell intact");
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
