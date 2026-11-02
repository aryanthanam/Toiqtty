using Microsoft.UI.Xaml.Media;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private void RunSidebarChecks(Action<bool, string> check)
    {
        var workspace = new Workspace { Name = "Sidebar regression" };
        workspace.Root.Directory = @"C:\sidebar-first";
        var second = new TerminalTab { Root = new PaneNode { Directory = @"C:\sidebar-second" } };
        workspace.Tabs.Add(second);
        var globalTheme = _saved.Settings.TerminalTheme;
        try
        {
            _saved.Workspaces.Add(workspace); RefreshSidebar();
            var cards = _sidebarCards.ToDictionary(pair => pair.Key, pair => pair.Value);
            var row = cards[workspace];
            workspace.ActiveTabId = second.Id; workspace.Branch = "sidebar-branch"; workspace.Attention = true;
            RefreshSidebar();
            check(cards.All(pair => ReferenceEquals(_sidebarCards[pair.Key], pair.Value))
                && ReferenceEquals(WorkspaceList.Items.Last(), row.Item),
                "Sidebar metadata changes preserve every session card and list container");
            check(row.Directory.Text == second.Root.Directory && row.Metadata.Text.Contains("sidebar-branch")
                && row.Metadata.Text.Contains("Needs attention"),
                "Sidebar directory and metadata follow the active tab without rebuilding rows");
            foreach (var name in new[] { "Cobalt2", "Hot Dog Stand (Mustard)" })
            {
                _saved.Settings.TerminalTheme = SessionThemePresets.Find(name); RefreshSidebar();
                var colors = Chrome.For(_saved.Settings.TerminalTheme);
                check(((SolidColorBrush)row.Directory.Foreground).Color == Chrome.Brush(colors.Secondary).Color
                    && ((SolidColorBrush)row.Title.Foreground).Color == Chrome.Brush(colors.Accent).Color,
                    "Inherited " + name + " colors update retained sidebar labels and attention markers");
            }
            _saved.Workspaces.Remove(workspace); RefreshSidebar();
            check(!_sidebarCards.ContainsKey(workspace) && !WorkspaceList.Items.Contains(row.Item),
                "Removing a session releases its retained sidebar card");
        }
        finally
        {
            _saved.Workspaces.Remove(workspace); _saved.Settings.TerminalTheme = globalTheme; RefreshSidebar();
        }
    }
}
