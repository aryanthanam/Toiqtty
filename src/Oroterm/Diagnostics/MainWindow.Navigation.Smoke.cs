using System.Text.Json;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Oroterm.Models;
using Oroterm.Terminal;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunNavigationProbe(string phase, Action<bool, string> check, Func<Func<bool>, Task> wait)
    {
        string identityPath = Path.Combine(SessionStore.Folder, "navigation-identity.json");
        if (phase == "navigation-reconnect")
        {
            using var identity = JsonDocument.Parse(File.ReadAllText(identityPath));
            var value = identity.RootElement;
            string paneId = value.GetProperty("pane").GetString()!;
            var moved = SessionLayout.Find(_saved, paneId)!.Value;
            check(_reattached && _host!.Identity == value.GetProperty("host").GetString(), "reopening a moved layout reconnects to the same host");
            check(_saved.Workspaces.Select(w => w.Id).SequenceEqual(value.GetProperty("sessions").EnumerateArray().Select(i => i.GetString()!)), "drag and menu session order survives reopening");
            check(moved.Workspace.Tabs.Select(t => t.Id).SequenceEqual(value.GetProperty("tabs").EnumerateArray().Select(i => i.GetString()!)), "reordered tabs and the moved pane's destination survive reopening");
            check(_selected == moved.Workspace && _selected.ActiveTab == moved.Tab && _selected.FocusedPaneId == paneId, "moved pane selection and focus survive frontend closure");
            await wait(() => _focused?.PaneId == paneId && _focused.ImageTextureCount == 1);
            var reopenedShell = moved.Pane.Session!;
            string marker = "REOPENED_MOVE=" + value.GetProperty("pid").GetString() + ":" + value.GetProperty("token").GetString();
            reopenedShell.Paste("Write-Output ('REOPENED_MOVE='+$PID+':'+$global:OROTERM_MOVE)"); reopenedShell.Key(13, 0);
            // Soft wrapping in narrow split panes inserts viewport newlines.
            await wait(() => _focused?.TextPeer.DocumentRange.GetText(-1).Replace("\n", "").Contains(marker) == true);
            check(true, "the moved PowerShell retains its PID, global variable, and Ghostty image across reopening");
            foreach (var workspace in _saved.Workspaces.ToArray()) RemoveWorkspace(workspace);
            return;
        }

        var initial = _selected!;
        NewWorkspace(ShellDefaults.Command+" -NoLogo -NoProfile","Source"); RemoveWorkspace(initial);
        var source = _selected!;
        var shell = source.Root.Session!; string pane = source.Root.Id; string token = Guid.NewGuid().ToString("N");
        shell.Paste("$global:OROTERM_MOVE='" + token + "'; 1..40 | ForEach-Object { Write-Output ('MOVE_HISTORY_'+$_) }; [Console]::Write(\"`e_Ga=T,f=32,s=1,v=1,i=901,q=2;/wAA/w==`e\\\"); Write-Output ('MOVE_PID='+$PID)"); shell.Key(13, 0);
        await wait(() => _focused?.RenderedText.Contains("MOVE_PID=") == true && _focused.ImageTextureCount == 1);
        string pid = System.Text.RegularExpressions.Regex.Matches(_focused!.TextPeer.DocumentRange.GetText(-1), @"MOVE_PID=(\d+)").Last().Groups[1].Value;
        _saved.Settings.Shell = "cmd.exe /Q /K";
        Split(true); await Task.Delay(200);
        var sourceTab = source.ActiveTab;
        NewTab(); var destination = source; var destinationTab = source.ActiveTab;
        NewWorkspace("cmd.exe /Q /K", "Other session"); var other = _selected!;
        SelectWorkspace(source);
        var objects = _saved.Workspaces.SelectMany(w => w.AllPanes()).ToDictionary(p => p.Id, p => p.Session);
        var hostPanes = _host!.Call(new Hosting.HostRequest()).Panes.ToHashSet();
        check(WorkspaceList.CanDragItems && WorkspaceList.CanReorderItems && WorkspaceList.AllowDrop, "session list enables WinUI's native drag reordering");
        string ownerLayout = JsonSerializer.Serialize(_saved);
        check(!MovePane(pane, other.Id, other.ActiveTab.Id, false) && JsonSerializer.Serialize(_saved) == ownerLayout,
            "a live shell rejects another session's tab without changing the layout");
        check(!MovePane(pane, null, null, true) && JsonSerializer.Serialize(_saved) == ownerLayout,
            "a live shell rejects a new session without changing the layout");
        check(MovePane(pane, destination.Id, destinationTab.Id, false), "a live PowerShell moves from a split into another tab in its session");
        await AssertLive("same-session split");
        check(sourceTab.Root.IsLeaf && _views.Count == 2 && !destination.Root.Vertical, "source split collapses while destination creates a downward split");
        check(MovePane(pane, destination.Id, null, true), "a running pane moves into a new tab");
        await AssertLive("new tab");
        var movedTab = destination.ActiveTab;
        ReorderTab(destination, movedTab, destination.Tabs[0], false);
        check(destination.Tabs[0] == movedTab && destination.ActiveTab == movedTab, "tab reordering keeps the active tab and shell selected");
        check(CommitTabOrder(destination, destination.Tabs.AsEnumerable().Reverse().ToList()) && destination.Tabs.Last() == movedTab,
            "native tab reorder completion commits the final container order");
        check(!CommitTabOrder(destination, destination.Tabs.Select(_ => movedTab).ToList()) && destination.Tabs.Last() == movedTab,
            "a duplicate or stale native reorder cannot change tab order");
        _tabsFingerprint = ""; RefreshTabs();
        check(TabStrip.CanDragItems && TabStrip.CanReorderItems && TabStrip.AllowDrop
            && TabStrip.Items.OfType<ListViewItem>().All(item => item.Content is Microsoft.UI.Xaml.Controls.Border),
            "shell tabs use the same native item drag mechanism as sidebar sessions without nested label buttons");
        var otherTab = destination.Tabs.First(t => t != movedTab);
        await ActivateNativeTab(otherTab, false);
        await ActivateNativeTab(movedTab, true);
        var firstView=_focused;
        SelectTab(otherTab); SelectTab(movedTab);
        await wait(()=>_focused?.PaneId==pane && _focused.InputHasFocus);
        check(ReferenceEquals(firstView,_focused) && !_focused!.IsDisposedForCheck,"rapid tab switches restore input to the retained view without recreating or disposing it");
        var cancelledPackage=new Windows.ApplicationModel.DataTransfer.DataPackage();
        string unchangedLayout=JsonSerializer.Serialize(_saved);
        check(BeginPaneDrag(pane,cancelledPackage) && OwnPaneDrag(cancelledPackage.GetView())
            && !OwnPaneDrag(new Windows.ApplicationModel.DataTransfer.DataPackage().GetView()),"pane drag uses an authenticated local payload and ignores unrelated data");
        check(_paneDragSurfaces.Values.All(s=>s.Handle.CanDrag && s.HitArea.Visibility==Microsoft.UI.Xaml.Visibility.Visible),
            "pane grab handles enable isolated drop surfaces during internal dragging");
        EndPaneDrag();
        check(JsonSerializer.Serialize(_saved)==unchangedLayout && !OwnPaneDrag(cancelledPackage.GetView())
            && _paneDragSurfaces.Values.All(s=>s.HitArea.Visibility==Microsoft.UI.Xaml.Visibility.Collapsed),"cancelling pane dragging revokes its payload and preserves the entire layout");
        var dropPackage=new Windows.ApplicationModel.DataTransfer.DataPackage();
        check(BeginPaneDrag(pane,dropPackage) && !CompletePaneDrop(dropPackage.GetView(),new(other.Id,null))
            && !CompletePaneDrop(dropPackage.GetView(),new(null,null)) && OwnPaneDrag(dropPackage.GetView())
            && JsonSerializer.Serialize(_saved)==unchangedLayout,
            "pane dragging rejects other and new sessions while leaving the original shell and drag intact");
        check(CompletePaneDrop(dropPackage.GetView(),new(source.Id,null)),
            "dropping onto the owning session's new-tab button schedules a move");
        await wait(()=>_selected==source && ActivePaneId==pane);
        await AssertLive("pane drop into own new tab");
        var backPackage=new Windows.ApplicationModel.DataTransfer.DataPackage();
        string targetPane=destinationTab.Root.Leaves().First().Id;
        check(BeginPaneDrag(pane,backPackage) && CompletePaneDrop(backPackage.GetView(),new(destination.Id,destinationTab.Id,targetPane,PaneEdge.Left)),
            "dropping onto a pane edge schedules a split on the requested side");
        await wait(()=>_selected==destination && ActivePaneId==pane);
        await AssertLive("pane edge drop");
        check(destination.Root.Leaves().First().Id==pane,"left-edge drop puts the moved shell before the target pane");
        check(MovePane(pane, destination.Id, null, true) && SessionLayout.Find(_saved,pane)!.Value.Workspace == source,
            "moving into a new tab retains the original session owner");
        await AssertLive("last-pane move");
        movedTab = destination.ActiveTab;
        ReorderTab(destination, movedTab, destination.Tabs[0], false);
        ReorderSession(destination, 1);
        check(_saved.Workspaces[1] == destination && _selected == destination, "session order changes without changing the selected session");
        check(CommitSessionOrder([other, destination]) && _saved.Workspaces[0] == other,
            "native session reorder completion publishes the final container order");
        check(!CommitSessionOrder([other, other]) && _saved.Workspaces[1] == destination,
            "an incomplete or duplicate reorder result leaves the session layout intact");
        _sidebarFingerprint = ""; RefreshSidebar();

        foreach (var workspace in _saved.Workspaces) { workspace.Attention = false; foreach (var p in workspace.AllPanes()) p.Attention = false; }
        SelectWorkspace(other);
        shell.Paste("[Console]::Write(\"`a\"); Write-Output 'MOVE_ATTENTION'"); shell.Key(13, 0);
        await wait(() => destination.Attention);
        check(!other.Attention && SessionLayout.Find(_saved, pane)!.Value.Pane.Attention, "real shell attention stays in the owning session after tab moves");
        NavigateNotification(other.Id, pane); await wait(() => _focused?.PaneId == pane);
        check(_selected == destination, "notification navigation resolves the pane's owning session");

        NewBrowser("about:blank"); await Task.Delay(500);
        var browserPane = destination.Root; var browser = _browsers[browserPane.Id];
        check(MovePane(browserPane.Id, source.Id, null, true) && ReferenceEquals(_browsers[browserPane.Id], browser), "a browser pane moves between its session's tabs without recreating WebView2");
        await wait(() => _focusedBrowser == browser);
        check(source.FocusedPaneId == browser.PaneId, "a moved browser records focus in its owning session");
        ClosePane(browserPane.Id);
        string surviving=source.Root.Leaves().First().Id;
        NewBrowser("about:blank",split:true);
        await wait(()=>_focusedBrowser is not null);
        ClosePane(_focusedBrowser!.PaneId);
        await wait(()=>_focused?.PaneId==surviving && _focused.InputHasFocus);
        check(source.Root.IsLeaf,"closing the focused split pane restores input to its surviving sibling");
        NavigateNotification(destination.Id, pane); await wait(() => _focused?.PaneId == pane);
        check(hostPanes.SetEquals(_host.Call(new Hosting.HostRequest()).Panes)
            && objects.All(pair => ReferenceEquals(SessionLayout.Find(_saved, pair.Key)!.Value.Pane.Session, pair.Value)),
            "all shell session objects and host pane IDs remain unchanged after moves and reordering");
        check(shell.Find("MOVE_HISTORY_1", 0).Count > 0, "the moved shell retains scrollback from before its first move");
        Save();
        File.WriteAllText(identityPath, JsonSerializer.Serialize(new {
            pane, pid, token, host = _host.Identity,
            sessions = _saved.Workspaces.Select(w => w.Id).ToArray(), tabs = destination.Tabs.Select(t => t.Id).ToArray()
        }));

        async Task AssertLive(string context)
        {
            await wait(() => _focused?.PaneId == pane && _focused.ImageTextureCount == 1);
            check(ReferenceEquals(SessionLayout.Find(_saved, pane)!.Value.Pane.Session, shell), context + " preserves the live terminal session object and image");
            string nonce = Guid.NewGuid().ToString("N"); string marker = nonce + "=" + pid + ":" + token;
            shell.Paste("Write-Output ('" + nonce + "='+$PID+':'+$global:OROTERM_MOVE)"); shell.Key(13, 0);
            await wait(() => _focused?.TextPeer.DocumentRange.GetText(-1).Replace("\n", "").Contains(marker) == true);
            check(true, context + " preserves the PowerShell PID and in-memory variable");
        }
        async Task ActivateNativeTab(TerminalTab tab, bool selection)
        {
            await Task.Delay(200);
            var item = TabStrip.Items.OfType<ListViewItem>().Single(item => item.Tag == tab);
            if (selection) TabStrip.SelectedItem = item;
            else
            {
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item)!;
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            }
            await Task.Delay(300);
            check(destination.ActiveTab == tab && _focused?.PaneId == tab.FocusedPaneId,
                selection ? "native tab selection switches the displayed shell after reorder" : "native tab invocation switches the displayed shell after reorder");
        }
        static IEnumerable<Microsoft.UI.Xaml.DependencyObject> Descendants(Microsoft.UI.Xaml.DependencyObject node)
        {
            yield return node;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i))) yield return child;
        }
    }
}
