namespace Toiqtty.Models;

/// <summary>Layout edits never dispose terminal sessions or change pane identities.</summary>
public static class SessionLayout
{
    public const int MaxSessions = 64, MaxTabsPerSession = 64, MaxPanes = 256, MaxPanesPerTab = 16;

    /// <summary>The result of a structural removal. Closed panes still carry their sessions;
    /// the caller owns their explicit closure and any view cleanup.</summary>
    public sealed record Removal(PaneNode[] Closed, TerminalTab? RemovedTab, int TabIndex, Workspace? RemovedWorkspace, int WorkspaceIndex);

    public static int PaneCount(SavedSession layout) => layout.Workspaces.Sum(w => w.AllPanes().Count());

    public static Workspace? AddWorkspace(SavedSession layout, string name, PaneNode root, out string error)
    {
        error = layout.Workspaces.Count >= MaxSessions || PaneCount(layout) >= MaxPanes ? "Session limit reached." : "";
        if (error.Length > 0) return null;
        var workspace = new Workspace { Name = name, Root = root };
        layout.Workspaces.Add(workspace); return workspace;
    }

    public static TerminalTab? AddTab(SavedSession layout, Workspace workspace, string name, PaneNode root, out string error)
    {
        error = workspace.Tabs.Count >= MaxTabsPerSession || PaneCount(layout) >= MaxPanes ? "Session limit reached." : "";
        if (error.Length > 0) return null;
        var tab = new TerminalTab { Name = name, Root = root };
        workspace.Tabs.Add(tab); workspace.ActiveTabId = tab.Id; return tab;
    }

    /// <summary>Splits a pane in the workspace's active tab, keeping the original pane's identity.</summary>
    public static bool SplitPane(SavedSession layout, Workspace workspace, string paneId, bool vertical, string command, out string error)
    {
        error = workspace.Root.Leaves().Count() >= MaxPanesPerTab || PaneCount(layout) >= MaxPanes
            ? $"Pane limit reached ({MaxPanesPerTab} per tab, {MaxPanes} overall)." : "";
        return error.Length == 0 && workspace.Root.Split(paneId, vertical, command);
    }

    /// <summary>Removes one pane from the active tab; an emptied tab and an emptied session go with it.</summary>
    public static Removal? ClosePane(SavedSession layout, Workspace workspace, string paneId)
    {
        var tab = workspace.ActiveTab;
        var pane = tab.Root.Leaves().FirstOrDefault(p => p.Id == paneId);
        if (pane is null) return null;
        var root = PaneNode.Remove(tab.Root, paneId);
        if (root is null) return RemoveTab(layout, workspace, tab);
        tab.Root = root;
        return new([pane], null, -1, null, -1);
    }

    public static Removal? RemoveTab(SavedSession layout, Workspace workspace, TerminalTab tab)
    {
        int index = workspace.Tabs.IndexOf(tab);
        if (index < 0) return null;
        var closed = tab.Root.Leaves().ToArray();
        if (workspace.Tabs.Count == 1)
        {
            var removed = RemoveWorkspace(layout, workspace)!;
            return removed with { RemovedTab = tab, TabIndex = index };
        }
        workspace.Tabs.RemoveAt(index);
        if (workspace.ActiveTabId == tab.Id) workspace.ActiveTabId = workspace.Tabs[Math.Min(index, workspace.Tabs.Count - 1)].Id;
        return new(closed, tab, index, null, -1);
    }

    public static Removal? RemoveWorkspace(SavedSession layout, Workspace workspace)
    {
        int index = layout.Workspaces.IndexOf(workspace);
        if (index < 0) return null;
        var closed = workspace.AllPanes().ToArray();
        layout.Workspaces.RemoveAt(index);
        return new(closed, null, -1, workspace, index);
    }

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
