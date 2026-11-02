namespace Toiqtty.Models;

internal static class SavedSessionSnapshot
{
    internal static SavedSession Capture(SavedSession layout)
    {
        // Saves run on the UI thread and reconnect captures hold the host's
        // layout lock. Copy the layout directly rather than encode it as JSON,
        // parse it, then walk it twice more to restore live directories.
        return new SavedSession
        {
            Version = layout.Version,
            SelectedWorkspace = layout.SelectedWorkspace,
            Settings = Configuration.LuaConfigResult.Clone(layout.Settings),
            Workspaces = layout.Workspaces.Select(workspace => new Workspace
            {
                Id = workspace.Id, Name = workspace.Name, ActiveTabId = workspace.ActiveTabId,
                Theme = workspace.Theme is { } theme ? theme with { } : null,
                Tabs = workspace.Tabs.Select(tab => new TerminalTab
                {
                    Id = tab.Id, Name = tab.Name, FocusedPaneId = tab.FocusedPaneId,
                    Root = CapturePane(tab.Root)
                }).ToList()
            }).ToList()
        };
    }

    private static PaneNode CapturePane(PaneNode pane) => new()
    {
        Id = pane.Id, Command = pane.Command,
        Directory = pane.IsLeaf ? pane.Session?.WorkingDirectory ?? pane.Directory : pane.Directory,
        Kind = pane.Kind, BrowserUrl = pane.BrowserUrl, BrowserSourceId = pane.BrowserSourceId,
        Vertical = pane.Vertical, Ratio = pane.Ratio,
        First = pane.First is { } first ? CapturePane(first) : null,
        Second = pane.Second is { } second ? CapturePane(second) : null
    };
}
