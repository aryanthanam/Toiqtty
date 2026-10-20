using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;

internal static class ColdStartupChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var old=new SavedSession { Workspaces=[new Workspace { Name="Old session" }], Settings=new() { RestoreWorkspaces=true,FontSize=19 } };
        old.SelectedWorkspace=old.Workspaces[0].Id;
        old.Workspaces[0].Root.Split(old.Workspaces[0].Root.Id,true,"cmd.exe");
        var cold=SessionStartupPolicy.Select(old,false);
        check(cold.Workspaces.Count==0 && cold.SelectedWorkspace is null,"Cold startup discards saved sessions, tabs, splits and selection even with legacy restoration enabled");
        check(cold.Settings.FontSize==19 && !ReferenceEquals(cold.Settings,old.Settings),"Cold startup retains an isolated copy of user preferences");
        check(old.Workspaces.Count==1 && old.Workspaces[0].Root.Leaves().Count()==2,"Choosing a cold startup does not mutate the saved layout object");
        check(ReferenceEquals(SessionStartupPolicy.Select(old,true),old),"A live-host reconnect retains its authoritative layout and identities");
    }

    internal static async Task RunWindows(string executable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/startup-check-cold-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string shell=$"\"{Environment.ProcessPath}\" --persistent-shell-client";
        string config=Path.Combine(folder,"toiqtty.lua");
        var stale=new SavedSession { Settings=new() { Shell=shell,FontSize=18,RestoreWorkspaces=true },
            Workspaces=[new Workspace { Name="Old A" },new Workspace { Name="Old B" }] };
        stale.Workspaces[0].Root.Split(stale.Workspaces[0].Root.Id,true,"cmd.exe /D /Q /K echo STALE_LAYOUT_REPLAYED");
        var staleIds=stale.Workspaces.SelectMany(w=>w.AllPanes()).Select(p=>p.Id).ToHashSet();
        void Seed() => File.WriteAllText(Path.Combine(folder,"session.json"),JsonSerializer.Serialize(stale));
        async Task StopHost()
        {
            var host=SessionHostClient.TryConnect(folder);
            if(host is null) return;
            using var process=Process.GetProcessById(host.ProcessId);
            await host.CallAsync(new() { Operation="shutdown" });
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        async Task<JsonElement> Launch(string phase,string expected="PERSIST_READY")
        {
            var start=new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute=false,WorkingDirectory=Environment.CurrentDirectory };
            start.ArgumentList.Add("--startup-probe");
            start.Environment["TOIQTTY_DATA_DIR"]=folder;
            start.Environment["TOIQTTY_CONFIG"]=config;
            start.Environment["TOIQTTY_STARTUP_TRACE"]="1";
            start.Environment["TOIQTTY_STARTUP_EXPECT"]=expected;
            using var window=Process.Start(start)!;
            try { await window.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
            catch { if(!window.HasExited) window.Kill(); throw; }
            using var trace=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"startup-trace.json")));
            if(!trace.RootElement.GetProperty("passed").GetBoolean()) throw new InvalidOperationException(trace.RootElement.ToString());
            string json=File.ReadAllText(Path.Combine(folder,"startup-layout.json"));
            File.WriteAllText(Path.Combine(folder,phase+".json"),json);
            using var report=JsonDocument.Parse(json); return report.RootElement.Clone();
        }
        static string[] Panes(JsonElement report) => report.GetProperty("sessions").EnumerateArray()
            .SelectMany(session=>session.GetProperty("tabs").EnumerateArray())
            .SelectMany(tab=>tab.GetProperty("panes").EnumerateArray()).Select(pane=>pane.GetProperty("Id").GetString()!).ToArray();
        static int Tabs(JsonElement report) => report.GetProperty("sessions").EnumerateArray().Sum(s=>s.GetProperty("tabs").GetArrayLength());
        try
        {
            Seed(); var fresh=await Launch("default");
            check(!fresh.GetProperty("reattached").GetBoolean() && fresh.GetProperty("sessions").GetArrayLength()==1 && Tabs(fresh)==1 && Panes(fresh).Length==1,
                "A real cold launch with a saved split layout opens exactly one default session, tab and shell");
            check(!Panes(fresh).Any(staleIds.Contains) && fresh.GetProperty("fontSize").GetDouble()==18 && fresh.GetProperty("shell").GetString()==shell,
                "Cold launch preserves preferences while giving the default shell a new identity");
            check(SessionHostClient.TryConnect(folder)!.Panes.Order().SequenceEqual(Panes(fresh).Order()),
                "The fresh host owns only the default shell and no hidden shells from the old layout");
            File.WriteAllText(config,"return {startup={sessions={{name='Do not replay on attach',tabs={{}}}}}}");
            var attached=await Launch("reattach");
            check(attached.GetProperty("reattached").GetBoolean() && attached.GetProperty("host").GetString()==fresh.GetProperty("host").GetString()
                && Panes(attached).SequenceEqual(Panes(fresh)),"Reopening a window preserves its live host and pane identities instead of running startup actions again");
            await StopHost(); Seed();
            File.WriteAllText(config,"return {startup={mode='when_empty',sessions={{name='Configured primary',tabs={{},{root={split='right',first={},second={}}}}},{name='Configured secondary',tabs={{}}}}}}");
            var configured=await Launch("configured");
            check(configured.GetProperty("sessions").GetArrayLength()==2 && Tabs(configured)==3 && Panes(configured).Length==4
                && configured.GetProperty("sessions")[0].GetProperty("Name").GetString()=="Configured primary" && !Panes(configured).Any(staleIds.Contains),
                "After host exit, Lua when_empty startup creates only its configured sessions, tabs and splits despite an old disk layout");
            check(SessionHostClient.TryConnect(folder)!.Panes.Order().SequenceEqual(Panes(configured).Order()),
                "The configured cold host owns exactly its declared shells");
            await StopHost(); Seed();
            File.WriteAllText(config,"return { startup =");
            var invalid=await Launch("invalid-config");
            check(invalid.GetProperty("configError").ValueKind==JsonValueKind.String && Panes(invalid).Length==1 && !Panes(invalid).Any(staleIds.Contains),
                "Invalid startup logic reports an error and opens a default shell without resurrecting old panes");
            await StopHost(); Seed();
            File.WriteAllText(config,"return {startup={sessions={{name='Configured browser',tabs={{kind='browser',url='about:blank'}}}}}}");
            var browser=await Launch("browser-only");
            check(browser.GetProperty("sessions")[0].GetProperty("Name").GetString()=="Configured browser" && Panes(browser).Length==1
                && browser.GetProperty("host").ValueKind==JsonValueKind.Null && SessionHostClient.TryConnect(folder) is null,
                "Browser-only Lua startup ignores old shell layouts and does not start a shell host");
            Console.WriteLine("Cold startup evidence: "+folder);
        }
        finally { await StopHost(); }
    }
}
