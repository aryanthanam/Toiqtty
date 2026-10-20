using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;

internal static class NavigationChecks
{
    public static void Run(Action<bool, string> check)
    {
        Keybindings.ParseOverrides("");
        check(Keybindings.Match(new AppSettings(),'M',3)=="movePane","Ctrl+Shift+M provides a conflict-free keyboard alternative to pane dragging");
        var order = new List<int> { 1, 2, 3, 4 };
        check(SessionLayout.Reorder(order, 1, 3, true) && order.SequenceEqual([2, 3, 1, 4]), "reorder inserts after a later target without an off-by-one error");
        check(SessionLayout.Reorder(order, 4, 2, false) && order.SequenceEqual([4, 2, 3, 1]), "reorder inserts before an earlier target");
        check(!SessionLayout.Reorder(order, 4, 4, true) && !SessionLayout.Reorder(order, 9, 1, false), "self and missing reorder targets are harmless");
        check(!SessionLayout.Reorder(order, 2, 3, false), "dropping at the existing boundary leaves order intact");

        var source = new Workspace { Name = "Owner" }; var other = new Workspace { Name = "Other" };
        var sourceTab = source.ActiveTab; var destination = new TerminalTab { Name = "Destination" };
        source.Tabs.Add(destination);
        var layout = new SavedSession { Workspaces = [source, other], SelectedWorkspace = source.Id };
        string id = source.Root.Id;
        source.Root.Split(id, true, "cmd.exe");
        var pane = source.Root.Leaves().First(); var sibling = source.Root.Leaves().Last();
        source.FocusedPaneId = pane.Id; pane.Attention = true; source.Attention = true;
        string targetId = destination.Root.Id;
        string before = JsonSerializer.Serialize(layout);
        check(!Move(layout, id, other.Id, other.ActiveTab.Id, true) && JsonSerializer.Serialize(layout) == before, "another session's tab rejects without changing ownership or layout");
        check(!Move(layout, id, other.Id, null, true) && JsonSerializer.Serialize(layout) == before, "another session's new-tab target rejects atomically");
        check(!Move(layout, id, null, null, true) && JsonSerializer.Serialize(layout) == before, "a new-session destination rejects atomically");
        check(!SessionLayout.CanMovePane(layout,id,other.Id,other.ActiveTab.Id,true,out _) && JsonSerializer.Serialize(layout)==before, "drag previews also reject cross-session targets without mutation");
        check(Move(layout, pane.Id, source.Id, destination.Id, false), "a split pane moves into another tab in its owning session");
        check(sourceTab.Root == sibling && sourceTab.FocusedPaneId == sibling.Id, "source split collapses and focus moves to its remaining sibling");
        check(ReferenceEquals(SessionLayout.Find(layout, pane.Id)!.Value.Pane, pane) && destination.Root.Leaves().Select(p => p.Id).SequenceEqual([targetId, id]), "moving preserves the exact source leaf and all pane identities");
        check(!destination.Root.Vertical && destination.FocusedPaneId == id && layout.SelectedWorkspace == source.Id, "destination split direction and focus follow the moved pane within its session");
        check(source.Attention && !other.Attention, "attention stays with the owning session");
        check(Move(layout, id, source.Id, null, true) && source.Tabs.Count == 3, "moving to a new tab detaches the pane without closing it");
        var movedTab = source.ActiveTab;
        check(Move(layout, id, source.Id, sourceTab.Id, true) && !source.Tabs.Contains(movedTab) && layout.Workspaces.Count == 2, "moving the last pane removes only its empty tab and retains its session");
        before = JsonSerializer.Serialize(layout);
        check(!Move(layout, id, "gone", null, true) && JsonSerializer.Serialize(layout) == before, "a stale session target rejects the move without changing the layout");
        check(!Move(layout, id, source.Id, "gone", true) && JsonSerializer.Serialize(layout) == before, "a stale tab target rejects the move without extracting the shell");
        check(Move(layout, id, source.Id, source.ActiveTab.Id, false) && !source.Root.Vertical, "a pane can move within its own split tab");
        var only = new SavedSession { Workspaces = [new()] }; string onlyId = only.Workspaces[0].Root.Id;
        before = JsonSerializer.Serialize(only);
        check(!Move(only, onlyId, only.Workspaces[0].Id, only.Workspaces[0].ActiveTab.Id, true) && before == JsonSerializer.Serialize(only), "moving a lone pane into its own tab is a no-op rejection");
        var onlyOwner = only.Workspaces[0];
        check(Move(only, onlyId, onlyOwner.Id, null, true) && only.Workspaces.Single() == onlyOwner && onlyOwner.Tabs.Count == 1 && onlyOwner.AllPanes().Single().Id == onlyId, "moving the last shell to a new tab retains the same session");

        var full = new Workspace(); for (int i = 1; i < 16; i++) full.Root.Split(full.Root.Leaves().Last().Id, true, "cmd.exe");
        var donor = new TerminalTab(); full.Tabs.Add(donor);
        var limits = new SavedSession { Workspaces = [full] };
        before = JsonSerializer.Serialize(limits);
        check(!Move(limits, donor.Root.Id, full.Id, full.ActiveTab.Id, true) && JsonSerializer.Serialize(limits) == before, "a full destination rejects atomically at sixteen panes");
        var deep = new Workspace(); for (int i = 0; i < 12; i++) deep.Root.Split(deep.Root.Leaves().Last().Id, true, "cmd.exe");
        deep.FocusedPaneId = deep.Root.Leaves().Last().Id;
        deep.Tabs.Add(donor); limits.Workspaces[0] = deep; before = JsonSerializer.Serialize(limits);
        check(!Move(limits, donor.Root.Id, deep.Id, deep.ActiveTab.Id, true) && JsonSerializer.Serialize(limits) == before, "split depth rejects before mutating either source or destination");
        var tabs = new Workspace { Tabs = Enumerable.Range(0, 64).Select(_ => new TerminalTab()).ToList() };
        tabs.Root.Split(tabs.Root.Id,true,"cmd.exe");
        limits.Workspaces[0] = tabs; before = JsonSerializer.Serialize(limits);
        check(!Move(limits, tabs.Root.Leaves().First().Id, tabs.Id, null, true) && JsonSerializer.Serialize(limits) == before, "a full session rejects a sixty-fifth tab atomically");
        string replaceId = tabs.Tabs[1].Root.Id;
        check(Move(limits, replaceId, tabs.Id, null, true) && tabs.Tabs.Count == 64, "replacing a sole-pane tab at the tab limit is allowed");
        var browser = new PaneNode { Kind = "browser", BrowserUrl = "https://example.com", BrowserSourceId = "profile-1" };
        source.Tabs.Add(new TerminalTab { Root = browser });
        check(Move(layout, browser.Id, source.Id, destination.Id, true) && ReferenceEquals(SessionLayout.Find(layout, browser.Id)!.Value.Pane, browser)
            && browser.BrowserSourceId == "profile-1", "browser panes move with the same identity, URL, and profile");
        foreach (var (vertical, beforeTarget) in new[] { (true,true), (true,false), (false,true), (false,false) })
        {
            var to=new Workspace(); var from=new TerminalTab(); var moving=from.Root;
            var targetTab=to.ActiveTab; to.Tabs.Add(from);
            string explicitTargetId=targetTab.Root.Id; targetTab.Root.Split(explicitTargetId,false,"cmd.exe");
            var explicitTarget=targetTab.Root.Leaves().First(); var untouched=targetTab.Root.Leaves().Last();
            targetTab.FocusedPaneId=untouched.Id;
            var directional=new SavedSession { Workspaces=[to], SelectedWorkspace=to.Id };
            string serialized=JsonSerializer.Serialize(directional);
            check(SessionLayout.CanMovePane(directional,moving.Id,to.Id,targetTab.Id,vertical,out _,explicitTargetId,beforeTarget)
                && JsonSerializer.Serialize(directional)==serialized,"drop preview validates without changing layout or selection");
            check(SessionLayout.MovePane(directional,moving.Id,to.Id,targetTab.Id,vertical,out _,out _,out _,explicitTargetId,beforeTarget)
                && explicitTarget.Vertical==vertical && explicitTarget.First!.Id==(beforeTarget?moving.Id:explicitTargetId)
                && explicitTarget.Second!.Id==(beforeTarget?explicitTargetId:moving.Id),"directional drop inserts on the requested side of the exact target pane");
            check(ReferenceEquals(SessionLayout.Find(directional,moving.Id)!.Value.Pane,moving)
                && ReferenceEquals(to.Root.Second,untouched) && to.FocusedPaneId==moving.Id,"directional drop preserves moving identity and untouched sibling, then focuses the moved pane");
        }
        before=JsonSerializer.Serialize(layout);
        check(!SessionLayout.MovePane(layout,id,source.Id,source.ActiveTab.Id,true,out _,out _,out _,"stale-target",true)
            && JsonSerializer.Serialize(layout)==before,"a stale exact drop target rejects atomically");
        var roundtrip = JsonSerializer.Deserialize<SavedSession>(JsonSerializer.Serialize(layout))!;
        check(roundtrip.Workspaces.Select(w => w.Id).SequenceEqual(layout.Workspaces.Select(w => w.Id))
            && roundtrip.Workspaces.SelectMany(w => w.Tabs).Select(t => t.Id).SequenceEqual(layout.Workspaces.SelectMany(w => w.Tabs).Select(t => t.Id))
            && roundtrip.Workspaces.SelectMany(w => w.AllPanes()).Select(p => p.Id).SequenceEqual(layout.Workspaces.SelectMany(w => w.AllPanes()).Select(p => p.Id)),
            "serialized layouts retain reordered sessions, tabs, and moved pane identities");
    }

    private static bool Move(SavedSession layout, string id, string? workspace, string? tab, bool vertical)
        => SessionLayout.MovePane(layout, id, workspace, tab, vertical, out _, out _, out _);

    public static async Task RunWindows(string executable, Action<bool, string> check)
    {
        string folder = Path.GetFullPath("artifacts/navigation-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string phase in new[] { "navigation-prepare", "navigation-reconnect" })
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
                start.ArgumentList.Add("--ui-smoke");
                start.Environment["TOIQTTY_DATA_DIR"] = folder;
                start.Environment["TOIQTTY_SESSION_UI_PROBE"] = phase;
                start.Environment["TOIQTTY_NOTIFICATION_SMOKE"] = "0";
                start.Environment.Remove("NO_COLOR");
                using var window = Process.Start(start)!;
                try { await window.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
                catch (TimeoutException) { if (!window.HasExited) { window.Kill(); await window.WaitForExitAsync(); } throw; }
                if (window.ExitCode != 0) throw new InvalidOperationException($"Navigation window {phase} exited abnormally ({window.ExitCode}).");
                string reportPath = Path.Combine(folder, "session-ui-" + phase + ".json");
                using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
                if (!report.RootElement.GetProperty("passed").GetBoolean()) throw new InvalidOperationException(report.RootElement.GetProperty("error").GetString());
                foreach (var item in report.RootElement.GetProperty("checks").EnumerateArray()) check(true, item.GetString()!);
                if (phase == "navigation-prepare")
                    check(SessionHostClient.TryConnect(folder) is { Panes.Length: 4 }, "closing the moved layout's frontend leaves all four shells running");
            }
            var until = DateTime.UtcNow.AddSeconds(12);
            while (SessionHostClient.TryConnect(folder) is not null && DateTime.UtcNow < until) await Task.Delay(100);
            check(SessionHostClient.TryConnect(folder) is null, "closing the isolated moved sessions ends their host cleanly");
            Console.WriteLine("Navigation WinUI evidence: " + folder);
        }
        finally { try { SessionHostClient.TryConnect(folder)?.Shutdown(); } catch (IOException) { } }
    }
}
