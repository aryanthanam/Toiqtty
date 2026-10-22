using Microsoft.UI.Xaml;
using Toiqtty.Models;
using Windows.System;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static string? ExplorerDirectory(Workspace? workspace, TerminalTab? tab = null, string? paneId = null)
    {
        if (workspace is null || workspace.Tabs.Count == 0) return null;
        tab ??= workspace.ActiveTab;
        var panes = tab.Root.Leaves().ToArray();
        var pane = panes.FirstOrDefault(p => p.Id == (paneId ?? tab.FocusedPaneId)) ?? panes.First();
        // Browser panes have no shell cwd. Use a shell in their tab/session,
        // or the browser's configured starting directory in a browser-only session.
        if (pane.IsBrowser) pane = panes.FirstOrDefault(p => !p.IsBrowser)
            ?? workspace.AllPanes().FirstOrDefault(p => !p.IsBrowser) ?? pane;
        // A remote cwd is display metadata, not a local folder. Do not silently
        // substitute the shell's original launch directory for it.
        return pane.Session is { } session ? session.LocalDirectory : pane.Directory;
    }

    private async Task OpenDirectory(Workspace? workspace = null, TerminalTab? tab = null)
    {
        workspace ??= _selected;
        string? directory = ExplorerDirectory(workspace, tab, workspace == _selected && tab is null ? ActivePaneId : null);
        if (string.IsNullOrWhiteSpace(directory))
        {
            SetStatusMessage(workspace is null ? "Select a session to open its folder." : "The current directory is not accessible from Windows Explorer.", problem: true);
            return;
        }
        try
        {
            // Pass the path directly to Windows; no shell command or quoting.
            if (!await Launcher.LaunchFolderPathAsync(directory))
                SetStatusMessage("Could not open folder in Windows Explorer: " + directory, problem: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        { SetStatusMessage("Could not open folder in Windows Explorer: " + ex.Message, problem: true); }
    }

    private void OpenDirectoryClick(object _1, RoutedEventArgs _2) => UiTasks.Observe(OpenDirectory());
}
