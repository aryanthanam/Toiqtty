using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;

internal static class HostSchedulingChecks
{
    internal static async Task AcceptStorm(string hostExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/host-accept-storm-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var client=HostTestCalls.ConnectOrStartForTest(folder,hostExecutable);
        string pane=Guid.NewGuid().ToString("N");
        try
        {
            await client.CallAsync(HostRequests.Create(pane,$"\"{Environment.ProcessPath}\" --persistent-shell-client",Environment.CurrentDirectory,new()));
            string identity=client.Identity; long retries=0;
            for(int round=0;round<16;round++)
            {
                await Task.WhenAll(Enumerable.Range(0,64).Select(async _=> {
                    using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",HostProtocol.PipeName(folder),System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
                    using var stop=new CancellationTokenSource(1000);
                    try { await pipe.ConnectAsync(stop.Token); } catch(Exception ex) when(ex is IOException or OperationCanceledException) { }
                }));
                var reply=await client.CallAsync(HostRequests.Ping()); retries=reply.TransportAcceptRetries;
                if(reply.Host!=identity || !reply.Panes.Contains(pane)) throw new IOException("The accept storm lost the original host or shell.");
            }
            check(true,"One thousand twenty-four clients disconnecting around pipe acceptance preserve the original host and shell");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,connections=1024,transportAcceptRetries=retries,host=identity },new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("Host accept storm evidence: "+folder);
        }
        finally { try { client.ShutdownForTest(); } catch(IOException) { } }
    }
    // Every spawn on this host sleeps first, so each check runs while a create is visibly in flight.
    internal static async Task Creates(string hostExecutable,Action<bool,string> check)
    {
        const int delay=2000; const string variable="TOIQTTY_HOST_SPAWN_DELAY_MS";
        string folder=Path.GetFullPath("artifacts/host-creates-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var checks=new List<string>();
        void Pass(bool value,string description) { check(value,description); checks.Add(description); }
        Environment.SetEnvironmentVariable(variable,delay.ToString());
        SessionHostClient client;
        try { client=HostTestCalls.ConnectOrStartForTest(folder,hostExecutable); }
        finally { Environment.SetEnvironmentVariable(variable,null); }
        string command=$"\"{Environment.ProcessPath}\" --persistent-shell-client",epoch=Guid.NewGuid().ToString("N");
        var workspace=new Workspace { Tabs=[new(),new(),new()] };
        var layout=new SavedSession { Workspaces=[workspace],SelectedWorkspace=workspace.Id };
        string a=workspace.Tabs[0].Root.Id,b=workspace.Tabs[1].Root.Id,c=workspace.Tabs[2].Root.Id;
        SavedSession Named(string name) { var copy=JsonSerializer.Deserialize<SavedSession>(JsonSerializer.Serialize(layout))!; copy.Workspaces[0].Name=name; return copy; }
        Task<HostReply> Create(string pane,SavedSession? with=null,long revision=0)
            =>client.CallAsync(HostRequests.Create(pane,command,Environment.CurrentDirectory,new(),with,with is null && revision==0 ? null : epoch,revision),TimeSpan.FromSeconds(60));
        async Task<string[]> Panes()=>(await client.CallAsync(HostRequests.Ping())).Panes;
        try
        {
            var spawning=Create(a,layout,1);
            await Task.Delay(300); var clock=Stopwatch.StartNew();
            await client.CallAsync(HostRequests.Navigate(new(workspace.Id,[new(workspace.Id,workspace.Tabs[1].Id,new())]))); long navigateMs=clock.ElapsedMilliseconds;
            await client.CallAsync(HostRequests.Sync(layout)); long syncMs=clock.ElapsedMilliseconds-navigateMs;
            await client.CallAsync(HostRequests.Close(Guid.NewGuid().ToString("N"))); long closeMs=clock.ElapsedMilliseconds-navigateMs-syncMs;
            bool inFlight=!spawning.IsCompleted; var created=await spawning;
            Console.WriteLine($"During a {delay} ms spawn: navigate {navigateMs} ms, sync {syncMs} ms, close {closeMs} ms");
            Pass(inFlight && created is { Created:true,State.HasExited:false } && clock.ElapsedMilliseconds>=delay/2 && navigateMs+syncMs+closeMs<delay/2,
                "A spawning create does not delay navigate, sync or close");
            var twins=await Task.WhenAll(Create(b),Create(b));
            Pass(twins.Count(r=>r.Created==true)==1 && twins.All(r=>r.State is { HasExited:false }) && (await Panes()).Count(p=>p==b)==1,
                "Concurrent creates of one pane share a single shell");
            var stale=Create(c,Named("older"),10);
            await Task.Delay(300); await client.CallAsync(HostRequests.Sync(Named("newer"),epoch,11));
            var staleReply=await stale; var saved=await client.CallAsync(HostRequests.Ping());
            Pass(staleReply.Created==true && saved.Layout!.Workspaces.Single().Name=="newer" && saved.Panes.Contains(c),
                "An older-revision create layout does not overwrite a newer sync that landed during its spawn");
            string kept=Guid.NewGuid().ToString("N"),dropped=Guid.NewGuid().ToString("N"),closed=Guid.NewGuid().ToString("N");
            var keeping=Create(kept,null,30);
            await Task.Delay(300); await client.CallAsync(HostRequests.Sync(Named("newer"),epoch,29)); await keeping;
            Pass((await Panes()).Contains(kept),"A sync stamped before a racing create keeps the shell it does not list (CreatedAfter)");
            var dropping=Create(dropped,null,40); var closing=Create(closed);
            await Task.Delay(300); await client.CallAsync(HostRequests.Sync(Named("newer"),epoch,41)); await client.CallAsync(HostRequests.Close(closed));
            var droppedReply=await dropping; var closedReply=await closing; var remaining=await Panes();
            Pass(droppedReply.Created==true && closedReply.Created==true && !remaining.Contains(dropped) && !remaining.Contains(closed) && remaining.Contains(a),
                "A newer sync or a close that lands during a spawn ends that shell once it starts");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,checks },new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("Host create evidence: "+folder);
        }
        finally { try { client.ShutdownForTest(); } catch(IOException) { } }
    }
    internal static async Task Run(string hostExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/host-scheduling-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var checks=new List<string>();
        void Pass(bool value,string description) { check(value,description); checks.Add(description); }
        var client=HostTestCalls.ConnectOrStartForTest(folder,hostExecutable);
        var workspace=new Workspace { Tabs=[new(),new()] };
        var layout=new SavedSession { Workspaces=[workspace],SelectedWorkspace=workspace.Id };
        string a=workspace.Tabs[0].Root.Id,b=workspace.Tabs[1].Root.Id;
        string command=$"\"{Environment.ProcessPath}\" --persistent-shell-client";
        async Task Line(string pane,string text)
        {
            await client.CallAsync(HostRequests.Paste(pane,text));
            await client.CallAsync(HostRequests.Key(pane,13,0));
        }
        async Task<string> Text(string pane)
        {
            var reply=await client.CallAsync(HostRequests.Capture(pane));
            // Capture is intentionally nonblocking while the terminal parser
            // owns its snapshot lock. WaitText retries an unavailable frame.
            if(reply.BinaryLength==0) return "";
            var (_,cells)=HostSnapshotCodec.Decode(reply.Binary,[]); return string.Concat(cells.Select(cell=>cell.GetText()));
        }
        async Task WaitText(string pane,string marker)
        {
            var timer=Stopwatch.StartNew();
            while(!(await Text(pane)).Contains(marker)) { if(timer.Elapsed.TotalSeconds>10) throw new TimeoutException(marker); await Task.Delay(10); }
        }
        HostRequest Watch(Dictionary<string,long> revisions)=>HostRequests.Watch(revisions);
        try
        {
            foreach(var pane in new[] { a,b }) await client.CallAsync(HostRequests.Create(pane,command,Environment.CurrentDirectory,new(),layout));
            await WaitText(a,"PERSIST_READY"); await WaitText(b,"PERSIST_READY");
            var initial=await client.CallAsync(Watch(new() { [a]=-1,[b]=-1 }));
            var seen=initial.OutputRevisions!;
            Pass(seen.Count==2 && initial.OutputRevision>=seen.Values.Max(),"Initial watches return independent output revisions for both panes and the global revision");
            await Line(a,"mark"); await WaitText(a,"PERSIST_MARK=1");
            var clock=Stopwatch.StartNew();
            var idle=await client.CallAsync(Watch(new() { [b]=seen[b] }));
            Pass(idle.OutputRevisions is null && clock.ElapsedMilliseconds>=150,"Output in an excluded busy pane does not wake the idle-pane watch");
            var rejoined=await client.CallAsync(Watch(new() { [a]=seen[a] }));
            Pass(rejoined.OutputRevisions?.Keys.SequenceEqual([a])==true && rejoined.OutputRevisions[a]>seen[a],
                "Rejoining a pane observes its pending output even after the global revision advanced past it");
            await Line(b,"mark"); await WaitText(b,"PERSIST_MARK=1");
            var updated=await client.CallAsync(Watch(new() { [b]=seen[b] }));
            Pass(updated.OutputRevisions?.Keys.SequenceEqual([b])==true,"The watched idle pane wakes when its own output changes");
            for(int i=0;i<80;i++)
            {
                using var stop=new CancellationTokenSource(10);
                try { await client.CallAsync(Watch(new() { [b]=updated.OutputRevisions![b] }),cancellation:stop.Token); }
                catch(Exception ex) when(ex is OperationCanceledException or IOException) { }
            }
            clock.Restart(); var live=await client.CallAsync(HostRequests.Ping());
            Pass(clock.ElapsedMilliseconds<500 && live.Panes.Length==2,"Eighty canceled watches release host connections promptly without ending shells");
            var writes=new List<Task>();
            for(int i=0;i<100;i++) { workspace.ActiveTabId=workspace.Tabs[i%2].Id; writes.Add(client.SaveNavigationAsync(layout)); }
            await Task.WhenAll(writes);
            var saved=await client.CallAsync(HostRequests.Ping());
            Pass(saved.Layout!.Workspaces.Single().ActiveTabId==workspace.Tabs[1].Id && saved.Panes.Length==2,
                "Rapid coalesced navigation persists the latest tab without replacing shell ownership");
            Pass(saved.Layout.Workspaces.Single().AllPanes().All(pane=>pane.Directory==Environment.CurrentDirectory),
                "Navigation persistence records the host's current shell directories");
            // Small syncs reach the layout lock almost at once; each then holds it
            // through a write and fsync. Once one finished, the rest are queued.
            var syncs=new Task<HostReply>[24];
            for(int i=0;i<syncs.Length;i++) syncs[i]=client.CallAsync(HostRequests.Sync(layout),TimeSpan.FromSeconds(60));
            await Task.WhenAny(syncs);
            clock.Restart();
            var reattached=await client.CallAsync(HostRequests.Create(a,"invalid-command-must-not-run",Environment.CurrentDirectory,new()),TimeSpan.FromSeconds(60));
            long reattachMs=clock.ElapsedMilliseconds; int pendingSyncs=syncs.Count(t=>!t.IsCompleted);
            await Task.WhenAll(syncs); long queueMs=clock.ElapsedMilliseconds;
            Console.WriteLine($"Reattach create {reattachMs} ms with {pendingSyncs} syncs pending; sync queue drained after {queueMs} ms");
            Pass(reattached.State is { HasExited: false } && reattached.Created==false && pendingSyncs>=syncs.Length/2 && reattachMs<queueMs,"A reattach create returns without waiting behind in-flight layout syncs and their fsyncs");
            await client.CallAsync(HostRequests.Navigate(new("missing",[new(workspace.Id,"removed-tab",new() { [workspace.Tabs[0].Id]="removed-pane" })])));
            var stale=await client.CallAsync(HostRequests.Ping());
            Pass(stale.Layout!.SelectedWorkspace==workspace.Id && stale.Layout.Workspaces.Single().ActiveTabId==workspace.Tabs[1].Id && stale.Panes.Length==2,
                "Stale navigation cannot select removed identities, create panes or stop existing shells");
            workspace.ActiveTabId=workspace.Tabs[0].Id; var pending=client.SaveNavigationAsync(layout);
            workspace.Tabs.RemoveAt(0); workspace.ActiveTabId=workspace.Tabs[0].Id; client.SaveForTest(layout); await pending;
            var structural=await client.CallAsync(HostRequests.Ping());
            Pass(structural.Panes.SequenceEqual([b]) && structural.Layout!.Workspaces.Single().Tabs.Count==1
                && structural.Layout.Workspaces.Single().ActiveTabId==workspace.Tabs[0].Id,
                "A structural save supersedes pending navigation without resurrecting a removed tab");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,checks },new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("Host scheduling evidence: "+folder);
        }
        finally { try { client.ShutdownForTest(); } catch(IOException) { } }
    }
}
