using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;

internal static class HostSchedulingChecks
{
    internal static async Task AcceptStorm(string hostExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/host-accept-storm-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var client=SessionHostClient.ConnectOrStart(folder,hostExecutable);
        string pane=Guid.NewGuid().ToString("N");
        try
        {
            await client.CallAsync(new() { Operation="create",Pane=pane,Text=$"\"{Environment.ProcessPath}\" --persistent-shell-client",Directory=Environment.CurrentDirectory,Settings=new() });
            string identity=client.Identity; long retries=0;
            for(int round=0;round<16;round++)
            {
                await Task.WhenAll(Enumerable.Range(0,64).Select(async _=> {
                    using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",HostProtocol.PipeName(folder),System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
                    using var stop=new CancellationTokenSource(1000);
                    try { await pipe.ConnectAsync(stop.Token); } catch(Exception ex) when(ex is IOException or OperationCanceledException) { }
                }));
                var reply=await client.CallAsync(new()); retries=reply.TransportAcceptRetries;
                if(reply.Host!=identity || !reply.Panes.Contains(pane)) throw new IOException("The accept storm lost the original host or shell.");
            }
            check(true,"One thousand twenty-four clients disconnecting around pipe acceptance preserve the original host and shell");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,connections=1024,transportAcceptRetries=retries,host=identity },new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("Host accept storm evidence: "+folder);
        }
        finally { try { client.ShutdownForTest(); } catch(IOException) { } }
    }
    internal static async Task Run(string hostExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/host-scheduling-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var checks=new List<string>();
        void Pass(bool value,string description) { check(value,description); checks.Add(description); }
        var client=SessionHostClient.ConnectOrStart(folder,hostExecutable);
        var workspace=new Workspace { Tabs=[new(),new()] };
        var layout=new SavedSession { Workspaces=[workspace],SelectedWorkspace=workspace.Id };
        string a=workspace.Tabs[0].Root.Id,b=workspace.Tabs[1].Root.Id;
        string command=$"\"{Environment.ProcessPath}\" --persistent-shell-client";
        async Task Line(string pane,string text)
        {
            await client.CallAsync(new() { Operation="paste",Pane=pane,Text=text,Numbers=[0] });
            await client.CallAsync(new() { Operation="key",Pane=pane,Numbers=[13,0,1,-1,0] });
        }
        async Task<string> Text(string pane)
        {
            var reply=await client.CallAsync(new() { Operation="capture",Pane=pane,Numbers=[-1] });
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
        HostRequest Watch(string[] panes,Dictionary<string,long> revisions,long global)=>new() { Operation="watch",WatchPanes=panes,WatchRevisions=revisions,Numbers=[global] };
        try
        {
            foreach(var pane in new[] { a,b }) await client.CallAsync(new() { Operation="create",Pane=pane,Text=command,Directory=Environment.CurrentDirectory,Settings=new(),Layout=layout });
            await WaitText(a,"PERSIST_READY"); await WaitText(b,"PERSIST_READY");
            Pass(client.SupportsPaneOutputWatch && client.SupportsNavigationSave,"The host negotiates per-pane watches and navigation-only saves");
            var initial=await client.CallAsync(Watch([a,b],new() { [a]=-1,[b]=-1 },-1));
            var seen=initial.OutputRevisions!;
            Pass(initial.Panes.Length==2 && seen.Count==2,"Initial watches return independent output revisions for both panes");
            await Line(a,"mark"); await WaitText(a,"PERSIST_MARK=1");
            var clock=Stopwatch.StartNew();
            var idle=await client.CallAsync(Watch([b],new() { [b]=seen[b] },initial.Numbers[0]));
            Pass(idle.Panes.Length==0 && clock.ElapsedMilliseconds>=150,"Output in an excluded busy pane does not wake the idle-pane watch");
            var rejoined=await client.CallAsync(Watch([a],new() { [a]=seen[a] },idle.Numbers[0]));
            Pass(rejoined.Panes.SequenceEqual([a]) && rejoined.OutputRevisions![a]>seen[a],
                "Rejoining a pane observes its pending output even after the global revision advanced past it");
            await Line(b,"mark"); await WaitText(b,"PERSIST_MARK=1");
            var updated=await client.CallAsync(Watch([b],new() { [b]=seen[b] },idle.Numbers[0]));
            Pass(updated.Panes.SequenceEqual([b]),"The watched idle pane wakes when its own output changes");
            for(int i=0;i<80;i++)
            {
                using var stop=new CancellationTokenSource(10);
                try { await client.CallAsync(Watch([b],new() { [b]=updated.OutputRevisions![b] },updated.Numbers[0]),cancellation:stop.Token); }
                catch(Exception ex) when(ex is OperationCanceledException or IOException) { }
            }
            clock.Restart(); var live=await client.CallAsync(new());
            Pass(clock.ElapsedMilliseconds<500 && live.Panes.Length==2,"Eighty canceled watches release host connections promptly without ending shells");
            var writes=new List<Task>();
            for(int i=0;i<100;i++) { workspace.ActiveTabId=workspace.Tabs[i%2].Id; writes.Add(client.SaveNavigationAsync(layout)); }
            await Task.WhenAll(writes);
            var saved=await client.CallAsync(new());
            Pass(saved.Layout!.Workspaces.Single().ActiveTabId==workspace.Tabs[1].Id && saved.Panes.Length==2,
                "Rapid coalesced navigation persists the latest tab without replacing shell ownership");
            Pass(saved.Layout.Workspaces.Single().AllPanes().All(pane=>pane.Directory==Environment.CurrentDirectory),
                "Navigation persistence records the host's current shell directories");
            await client.CallAsync(new() { Operation="navigate",Navigation=new("missing",[new(workspace.Id,"removed-tab",new() { [workspace.Tabs[0].Id]="removed-pane" })]) });
            var stale=await client.CallAsync(new());
            Pass(stale.Layout!.SelectedWorkspace==workspace.Id && stale.Layout.Workspaces.Single().ActiveTabId==workspace.Tabs[1].Id && stale.Panes.Length==2,
                "Stale navigation cannot select removed identities, create panes or stop existing shells");
            workspace.ActiveTabId=workspace.Tabs[0].Id; var pending=client.SaveNavigationAsync(layout);
            workspace.Tabs.RemoveAt(0); workspace.ActiveTabId=workspace.Tabs[0].Id; client.SaveForTest(layout); await pending;
            var structural=await client.CallAsync(new());
            Pass(structural.Panes.SequenceEqual([b]) && structural.Layout!.Workspaces.Single().Tabs.Count==1
                && structural.Layout.Workspaces.Single().ActiveTabId==workspace.Tabs[0].Id,
                "A structural save supersedes pending navigation without resurrecting a removed tab");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,checks },new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("Host scheduling evidence: "+folder);
        }
        finally { try { client.ShutdownForTest(); } catch(IOException) { } }
    }
}
