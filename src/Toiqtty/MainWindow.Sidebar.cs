using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private sealed record SidebarState(string Name, string Directory, string Branch, int Tabs, int Panes,
        bool Attention, Chrome.Palette Colors, bool Themed);
    private sealed class SidebarCard
    {
        internal readonly ListViewItem Item = new();
        internal readonly Border Card = new() { BorderThickness = new Thickness(2, 0, 0, 0) };
        internal readonly Grid Heading = new();
        internal readonly Border Attention = new() { Width = 4, Height = 4, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center };
        internal readonly TextBlock Title = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        internal readonly TextBlock Directory = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
        internal readonly TextBlock Metadata = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
        internal SidebarState? State;
        internal (bool Selected, uint Background, uint Border)? Selection;
        internal SidebarCard()
        {
            Heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            Heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Heading.Children.Add(Attention); Grid.SetColumn(Title, 1); Heading.Children.Add(Title);
            var stack = new StackPanel { Spacing = 3, Padding = new Thickness(10, 7, 10, 7) };
            stack.Children.Add(Heading); stack.Children.Add(Directory); stack.Children.Add(Metadata);
            Card.Child = stack; Item.Content = Card;
        }
    }
    private readonly Dictionary<Workspace, SidebarCard> _sidebarCards = [];

    // Keep directory text and branch polling tied to the same terminal. A browser
    // tab falls back to a shell elsewhere in its session.
    private static PaneNode? SidebarPane(Workspace workspace) =>
        workspace.Root.Leaves().FirstOrDefault(p => !p.IsBrowser) ?? workspace.AllPanes().FirstOrDefault(p => !p.IsBrowser);

    private void RefreshSidebar()
    {
        UpdateChrome();
        if (_closed || _closing || _draggingSessions || _draggedPaneId is not null) return;
        _refreshing = true;
        try
        {
            var live = _saved.Workspaces.ToHashSet();
            foreach (var workspace in _sidebarCards.Keys.Where(w => !live.Contains(w)).ToArray())
            {
                WorkspaceList.Items.Remove(_sidebarCards[workspace].Item);
                _sidebarCards.Remove(workspace);
            }
            for (int index = 0; index < _saved.Workspaces.Count; index++)
            {
                var workspace = _saved.Workspaces[index];
                if (!_sidebarCards.TryGetValue(workspace, out var row))
                {
                    row = new SidebarCard(); row.Item.Tag = workspace;
                    var menu = new MenuFlyout();
                    var rename = new MenuFlyoutItem { Text = "Rename session" }; rename.Click += (_, _) => UiTasks.Observe(Rename(workspace));
                    var theme = new MenuFlyoutItem { Text = "Session theme…" }; theme.Click += (_, _) => UiTasks.Observe(EditSessionTheme(workspace));
                    var close = new MenuFlyoutItem { Text = "Close session" }; close.Click += (_, _) => UiTasks.Observe(CloseWorkspace(workspace));
                    var open = new MenuFlyoutItem { Text = "Open in Windows Explorer" }; open.Click += (_, _) => UiTasks.Observe(OpenDirectory(workspace));
                    menu.Items.Add(open); menu.Items.Add(theme); menu.Items.Add(rename); menu.Items.Add(close);
                    AddSessionOrderMenu(menu, workspace); row.Item.ContextFlyout = menu;
                    _sidebarCards.Add(workspace, row);
                }
                // Only structural changes touch the item collection. Metadata updates
                // preserve containers, menus, scroll position, and pointer interaction.
                if (index >= WorkspaceList.Items.Count || !ReferenceEquals(WorkspaceList.Items[index], row.Item))
                {
                    WorkspaceList.Items.Remove(row.Item);
                    WorkspaceList.Items.Insert(index, row.Item);
                }
                var pane = SidebarPane(workspace);
                var themeForRow = ThemeFor(workspace);
                var state = new SidebarState(workspace.Name, pane?.Session?.Directory ?? pane?.Directory ?? "",
                    workspace.Branch, workspace.Tabs.Count, workspace.AllPanes().Count(), workspace.Attention,
                    Chrome.For(themeForRow), themeForRow is not null);
                if (row.State == state) continue;
                row.State = state;
                var colors = state.Colors;
                row.Title.Text = state.Name;
                row.Title.Foreground = Chrome.Brush(state.Attention ? colors.Accent : colors.Text);
                row.Heading.ColumnSpacing = state.Attention ? 8 : 0;
                row.Attention.Background = Chrome.Brush(colors.Accent);
                row.Attention.Visibility = state.Attention ? Visibility.Visible : Visibility.Collapsed;
                row.Directory.Text = DisplayDirectory(state.Directory);
                row.Directory.Foreground = Chrome.Brush(state.Themed ? colors.Secondary : Chrome.Muted);
                var metadata = new List<string>();
                if (state.Tabs > 1) metadata.Add($"{state.Tabs} tabs");
                if (state.Branch.Length > 0) metadata.Add("git · " + state.Branch);
                if (state.Panes > 1) metadata.Add($"{state.Panes} panes");
                if (state.Attention) metadata.Add("Needs attention");
                row.Metadata.Text = string.Join("  ·  ", metadata);
                row.Metadata.Visibility = metadata.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                row.Metadata.Foreground = Chrome.Brush(state.Attention ? colors.Accent : colors.Secondary);
                AutomationProperties.SetName(row.Item, "Session " + state.Name + ", " + state.Panes + " panes" + (state.Attention ? ", needs attention" : ""));
                ToolTipService.SetToolTip(row.Item, state.Directory);
            }
            SyncSidebarSelection();
        }
        finally { _refreshing = false; }
    }
    private void SyncSidebarSelection()
    {
        bool refreshing = _refreshing; _refreshing = true;
        try
        {
            foreach (var (workspace, row) in _sidebarCards)
            {
                bool selected = workspace == _selected;
                var theme = ThemeFor(workspace);
                var selection = (selected, SessionCardBackground(workspace, selected), selected || theme is not null ? Chrome.For(theme).Accent : 0u);
                if (row.Selection != selection)
                {
                    row.Selection = selection;
                    row.Card.Background = Chrome.Brush(selection.Item2);
                    row.Card.BorderBrush = Chrome.Brush(selection.Item3);
                    row.Title.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                }
                if (selected && !ReferenceEquals(WorkspaceList.SelectedItem, row.Item)) WorkspaceList.SelectedItem = row.Item;
            }
        }
        finally { _refreshing = refreshing; }
    }
}
