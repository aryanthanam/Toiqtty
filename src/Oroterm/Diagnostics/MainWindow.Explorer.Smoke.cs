using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunExplorerChecks(Action<bool, string> check)
    {
        var workspace = _selected!;
        var panes = workspace.Root.Leaves().ToArray();
        var first = panes[0]; var second = panes[1];
        string original = first.Session!.LocalDirectory!;
        string folder = Path.Combine(SessionStore.Folder, "Explorer folder & 日本語");
        Directory.CreateDirectory(folder);
        first.Session.Paste("Set-Location -LiteralPath '" + folder.Replace("'", "''") + "'"); first.Session.Key(13, 0);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (first.Session.LocalDirectory != folder && DateTime.UtcNow < deadline) await Task.Delay(25);
        check(first.Session.LocalDirectory == folder && ExplorerDirectory(workspace, paneId: ActivePaneId) == folder && first.Directory != folder,
            "Explorer follows a real shell's live directory change, preserving spaces, Unicode and punctuation instead of using its launch directory");
        FocusView(_views.Single(v => v.PaneId == second.Id));
        check(ExplorerDirectory(workspace, paneId: ActivePaneId) == second.Session!.LocalDirectory
            && ExplorerDirectory(workspace, paneId: ActivePaneId) != folder,
            "Changing focused split panes changes the Explorer target independently");
        FocusView(_views.Single(v => v.PaneId == first.Id));
        var otherTab = workspace.Tabs.First(t => t != workspace.ActiveTab);
        check(ExplorerDirectory(workspace, otherTab) == otherTab.Root.Leaves().First().Session!.LocalDirectory
            && ExplorerDirectory(workspace, otherTab) != folder,
            "An inactive tab's Explorer action uses that tab's directory instead of the active pane");
        var otherWorkspace = _saved.Workspaces.First(w => w != workspace);
        SelectWorkspace(otherWorkspace);
        check(ExplorerDirectory(workspace) == folder && _selected == otherWorkspace,
            "An inactive session's Explorer action resolves its focused directory without changing the selected session");
        SelectWorkspace(workspace);
        check(OpenDirectoryButton.IsEnabled && OpenDirectoryButton.ActualWidth > 0
            && AutomationProperties.GetName(OpenDirectoryButton) == "Open current folder in Windows Explorer"
            && ToolTipService.GetToolTip(OpenDirectoryButton)?.ToString()?.Contains("Ctrl+Shift+O") == true,
            "The toolbar exposes an accessible folder button with the configured Explorer shortcut");
        check(WorkspaceList.Items.OfType<ListViewItem>().All(item => item.ContextFlyout is MenuFlyout menu
            && menu.Items.OfType<MenuFlyoutItem>().Any(action => action.Text == "Open in Windows Explorer"))
            && TabStrip.Items.OfType<ListViewItem>().All(item => item.ContextFlyout is MenuFlyout menu
            && menu.Items.OfType<MenuFlyoutItem>().Any(action => action.Text == "Open in Windows Explorer")),
            "Session and tab context menus expose the Explorer action");
        var browserOnly = new Workspace { Root = new PaneNode { Kind = "browser", Directory = folder } };
        check(ExplorerDirectory(browserOnly) == folder,
            "A browser-only session uses its configured starting directory as its Explorer target");
        first.Session.Paste("Set-Location -LiteralPath '" + original.Replace("'", "''") + "'"); first.Session.Key(13, 0);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (first.Session.LocalDirectory != original && DateTime.UtcNow < deadline) await Task.Delay(25);
    }
}
