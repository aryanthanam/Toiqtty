namespace Oroterm.Models;

/// <summary>Layout edits never dispose terminal sessions or change pane identities.</summary>
public static class SessionLayout
{
    public static bool Reorder<T>(List<T> items, T source, T target, bool after)
    {
        int from = items.IndexOf(source), to = items.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return false;
        if (after) to++;
        if (from < to) to--;
        if (from == to) return false;
        items.RemoveAt(from); items.Insert(to, source); return true;
    }

    public static (Workspace Workspace, TerminalTab Tab, PaneNode Pane)? Find(SavedSession layout, string paneId)
    {
        foreach (var workspace in layout.Workspaces)
            foreach (var tab in workspace.Tabs)
                foreach (var pane in tab.Root.Leaves())
                    if (pane.Id == paneId) return (workspace, tab, pane);
        return null;
    }

    // Panes stay in their owning session; null tab creates a tab there. Validate everything
    // before extracting: rejected moves must leave both trees and live shells intact.
    public static bool MovePane(SavedSession layout, string paneId, string? workspaceId, string? tabId,
        bool vertical, out Workspace? destination, out TerminalTab? destinationTab, out string error,
        string? targetPaneId = null, bool before = false)
        => EditPane(layout, paneId, workspaceId, tabId, vertical, out destination, out destinationTab, out error, targetPaneId, before, false);

    public static bool CanMovePane(SavedSession layout, string paneId, string? workspaceId, string? tabId,
        bool vertical, out string error, string? targetPaneId = null, bool before = false)
        => EditPane(layout, paneId, workspaceId, tabId, vertical, out _, out _, out error, targetPaneId, before, true);

    private static bool EditPane(SavedSession layout, string paneId, string? workspaceId, string? tabId,
        bool vertical, out Workspace? destination, out TerminalTab? destinationTab, out string error,
        string? targetPaneId, bool before, bool validateOnly)
    {
        destination = null; destinationTab = null; error = "";
        if (Find(layout, paneId) is not { } source) { error = "The source pane no longer exists."; return false; }
        var owner = source.Workspace; var oldTab = source.Tab; var pane = source.Pane;
        if (workspaceId != owner.Id) { error = "Panes stay in their owning session. Choose a tab in " + owner.Name + "."; return false; }
        bool emptiesTab = oldTab.Root.IsLeaf;
        var workspace = owner;
        var tab = tabId is null ? new TerminalTab { Name = oldTab.Name, Root = pane, FocusedPaneId = pane.Id }
            : workspace.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) { error = "The destination tab no longer exists."; return false; }
        if (tabId is null && workspace.Tabs.Count - (emptiesTab ? 1 : 0) >= 64)
        { error = "Tab limit reached (64)."; return false; }
        var target = tabId is null ? null : targetPaneId is not null
            ? tab.Root.Leaves().FirstOrDefault(p => p.Id == targetPaneId && p.Id != pane.Id)
            : tab.Root.Leaves().FirstOrDefault(p => p.Id == tab.FocusedPaneId && p.Id != pane.Id)
                ?? tab.Root.Leaves().FirstOrDefault(p => p.Id != pane.Id);
        if (tabId is not null)
        {
            if (target is null) { error = targetPaneId is null ? "This pane is already the only pane in that tab." : "Choose another pane as the split target."; return false; }
            if (tab != oldTab && tab.Root.Leaves().Count() >= 16) { error = "Pane limit reached (16 per tab)."; return false; }
            if (Depth(tab.Root, target.Id) >= 12) { error = "Split depth limit reached (12)."; return false; }
        }

        if (validateOnly) { destination = workspace; destinationTab = tab; return true; }

        var remaining = Extract(oldTab.Root, pane.Id);
        if (remaining is null)
        {
            int index = owner.Tabs.IndexOf(oldTab); owner.Tabs.RemoveAt(index);
            if (owner.Tabs.Count > 0 && owner.ActiveTabId == oldTab.Id)
                owner.ActiveTabId = owner.Tabs[Math.Min(index, owner.Tabs.Count - 1)].Id;
        }
        else
        {
            oldTab.Root = remaining;
            if (oldTab.FocusedPaneId == pane.Id) oldTab.FocusedPaneId = remaining.Leaves().First().Id;
        }
        if (tabId is null) workspace.Tabs.Add(tab);
        else
        {
            tab.Root.Split(target!.Id, vertical, pane.Command, pane);
            if (before) (target.First, target.Second) = (target.Second, target.First);
        }
        workspace.ActiveTabId = tab.Id; tab.FocusedPaneId = pane.Id;
        workspace.Attention = workspace.AllPanes().Any(p => p.Attention);
        layout.SelectedWorkspace = workspace.Id;
        destination = workspace; destinationTab = tab; return true;
    }

    private static int Depth(PaneNode node, string id) => node.IsLeaf ? (node.Id == id ? 0 : -100)
        : 1 + Math.Max(Depth(node.First!, id), Depth(node.Second!, id));

    private static PaneNode? Extract(PaneNode node, string id)
    {
        if (node.IsLeaf) return node.Id == id ? null : node;
        var first = Extract(node.First!, id); var second = Extract(node.Second!, id);
        if (first is null) return second;
        if (second is null) return first;
        node.First = first; node.Second = second; return node;
    }
}
