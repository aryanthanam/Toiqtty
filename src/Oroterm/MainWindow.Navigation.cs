using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Oroterm.Controls;
using Oroterm.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Oroterm;

public sealed partial class MainWindow
{
    private bool _draggingSessions;
    private bool _draggingTabs;
    private bool _refreshingTabs;
    private bool _tabActivationPending;
    private TerminalTab? _pendingTabActivation;

    private async void WorkspaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_closed || _closing || _dialogOpen || _draggingSessions || _draggedPaneId is not null) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != WorkspaceList; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ListViewItem { Tag: Workspace workspace } || !_saved.Workspaces.Contains(workspace)) continue;
            e.Handled = true;
            await Rename(workspace);
            return;
        }
    }

    private void SessionDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_dialogOpen || e.Items.Count != 1) { e.Cancel = true; return; }
        _draggingSessions = true;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void SessionDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        // WinUI moves the item containers; publish that final order to the host
        // only after the drag has completed, never while hovering or cancelling.
        var order = WorkspaceList.Items.Cast<ListViewItem>().Select(item => (Workspace)item.Tag).ToList();
        _draggingSessions = false;
        CommitSessionOrder(order);
        _sidebarFingerprint = ""; RefreshSidebar();
        RestorePaneFocusAfterDrag();
    }

    private bool CommitSessionOrder(List<Workspace> order)
    {
        if (order.Count != _saved.Workspaces.Count || !order.ToHashSet().SetEquals(_saved.Workspaces)) return false;
        _saved.Workspaces.Clear(); _saved.Workspaces.AddRange(order); Save(); return true;
    }

    private void AddSessionOrderMenu(MenuFlyout menu, Workspace workspace)
    {
        menu.Items.Add(new MenuFlyoutSeparator());
        var up = new MenuFlyoutItem { Text = "Move session up" };
        var down = new MenuFlyoutItem { Text = "Move session down" };
        up.Click += (_, _) => ReorderSession(workspace, -1);
        down.Click += (_, _) => ReorderSession(workspace, 1);
        menu.Opening += (_, _) =>
        {
            int index = _saved.Workspaces.IndexOf(workspace);
            up.IsEnabled = index > 0; down.IsEnabled = index >= 0 && index < _saved.Workspaces.Count - 1;
        };
        menu.Items.Add(up); menu.Items.Add(down);
    }

    private void ReorderSession(Workspace workspace, int direction)
    {
        int index = _saved.Workspaces.IndexOf(workspace), target = index + direction;
        if (index < 0 || target < 0 || target >= _saved.Workspaces.Count) return;
        SessionLayout.Reorder(_saved.Workspaces, workspace, _saved.Workspaces[target], direction > 0);
        RefreshSidebar(); Save();
    }

    private void AddTabOrderMenu(MenuFlyout menu, Workspace workspace, TerminalTab tab)
    {
        menu.Items.Add(new MenuFlyoutSeparator());
        var left = new MenuFlyoutItem { Text = "Move tab left" };
        var right = new MenuFlyoutItem { Text = "Move tab right" };
        void Move(int direction)
        {
            int index = workspace.Tabs.IndexOf(tab), target = index + direction;
            if (index < 0 || target < 0 || target >= workspace.Tabs.Count) return;
            ReorderTab(workspace, tab, workspace.Tabs[target], direction > 0);
        }
        left.Click += (_, _) => Move(-1); right.Click += (_, _) => Move(1);
        menu.Opening += (_, _) =>
        {
            int index = workspace.Tabs.IndexOf(tab);
            left.IsEnabled = index > 0; right.IsEnabled = index >= 0 && index < workspace.Tabs.Count - 1;
        };
        menu.Items.Add(left); menu.Items.Add(right);
    }

    private void ReorderTab(Workspace workspace, TerminalTab source, TerminalTab target, bool after)
    {
        if (!_saved.Workspaces.Contains(workspace) || !SessionLayout.Reorder(workspace.Tabs, source, target, after)) return;
        RefreshTabs(); Save();
    }

    private void TabItemClick(object sender, ItemClickEventArgs e)
    {
        // Explicit ListViewItem containers report their Content as ClickedItem.
        var item = e.ClickedItem as ListViewItem ?? TabStrip.Items.OfType<ListViewItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Content, e.ClickedItem));
        if (item?.Tag is TerminalTab tab) QueueTabActivation(tab);
    }

    private void TabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is ListViewItem { Tag: TerminalTab tab }) QueueTabActivation(tab);
    }

    private void QueueTabActivation(TerminalTab tab)
    {
        if (_closed || _closing || _refreshingTabs || _draggingTabs || _draggedPaneId is not null || _selected?.Tabs.Contains(tab) != true) return;
        _pendingTabActivation = tab;
        if (_tabActivationPending) return;
        // Finish WinUI's click/selection processing before rebuilding containers.
        // Coalesce the two events, keeping the latest requested tab.
        _tabActivationPending = DispatcherQueue.TryEnqueue(() =>
        {
            _tabActivationPending = false;
            var target = _pendingTabActivation; _pendingTabActivation = null;
            if (_closed || _closing || _refreshingTabs || _draggingTabs || _draggedPaneId is not null || target is null || _selected?.Tabs.Contains(target) != true) return;
            if (_selected.ActiveTab == target) { if (_focusedBrowser is { } browser) browser.FocusBrowser(); else _focused?.FocusTerminal(); }
            else SelectTab(target);
        });
    }

    private void TabDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_dialogOpen || _selected is null || e.Items.Count != 1) { e.Cancel = true; return; }
        _pendingTabActivation = null;
        _draggingTabs = true; e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void TabDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        var workspace = _selected;
        var order = TabStrip.Items.Cast<ListViewItem>().Select(item => (TerminalTab)item.Tag).ToList();
        _draggingTabs = false;
        if (workspace is not null) CommitTabOrder(workspace, order);
        _tabsFingerprint = ""; RefreshTabs();
        RestorePaneFocusAfterDrag();
    }

    private bool CommitTabOrder(Workspace workspace, List<TerminalTab> order)
    {
        if (!_saved.Workspaces.Contains(workspace) || order.Count != workspace.Tabs.Count || !order.ToHashSet().SetEquals(workspace.Tabs)) return false;
        workspace.Tabs.Clear(); workspace.Tabs.AddRange(order); Save(); return true;
    }
    private void RestorePaneFocusAfterDrag() => QueuePaneFocus();
    private void MovePaneClick(object sender, RoutedEventArgs e)
    {
        if (ActivePaneId is { } id) _ = MovePaneDialog(id);
    }

    private async Task MovePaneDialog(string paneId)
    {
        if (_dialogOpen || SessionLayout.Find(_saved, paneId) is not { } source) return;
        var tabs = new ComboBox { Header = "Destination tab", HorizontalAlignment = HorizontalAlignment.Stretch };
        var placement = new ComboBox { Header = "Split placement", HorizontalAlignment = HorizontalAlignment.Stretch };
        placement.Items.Add("Right of the focused pane"); placement.Items.Add("Below the focused pane"); placement.SelectedIndex = 0;
        tabs.Items.Add(new ComboBoxItem { Content = "+ New tab" });
        foreach(var tab in source.Workspace.Tabs.Where(t=>t!=source.Tab || !t.Root.IsLeaf))
            tabs.Items.Add(new ComboBoxItem { Content=tab.Name,Tag=tab });
        tabs.SelectionChanged += (_, _) => placement.IsEnabled = tabs.SelectedItem is ComboBoxItem { Tag: TerminalTab };
        tabs.SelectedIndex=0;
        var panel = new StackPanel { Spacing = 14, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Move within "+source.Workspace.Name+". The shell stays in this session.", TextWrapping = TextWrapping.Wrap,
            Foreground = Chrome.Brush(Chrome.Secondary), FontSize = 12 });
        panel.Children.Add(tabs); panel.Children.Add(placement);
        if (await ShowDialog(new ContentDialog { Title = "Move pane", Content = panel, PrimaryButtonText = "Move", CloseButtonText = "Cancel" }) != ContentDialogResult.Primary) return;
        var destinationTab = (tabs.SelectedItem as ComboBoxItem)?.Tag as TerminalTab;
        MovePane(paneId, source.Workspace.Id, destinationTab?.Id, placement.SelectedIndex == 0);
    }

    private bool MovePane(string paneId, string? workspaceId, string? tabId, bool vertical, string? targetPaneId=null, bool before=false)
    {
        if (!SessionLayout.MovePane(_saved, paneId, workspaceId, tabId, vertical, out var destination, out var tab, out var error,targetPaneId,before))
        { SetStatusMessage(error, problem: true); return false; }
        // Rebuilding views only detaches renderers. The same session objects and
        // host pane IDs stay alive within their original owning session.
        _selected = destination; _saved.SelectedWorkspace = destination!.Id; _zoomed = false;
        RefreshSidebar(); RefreshTabs(); RebuildPanes(paneId); Save();
        SetStatusMessage($"Pane moved to {destination.Name} · {tab!.Name}");
        return true;
    }
}
