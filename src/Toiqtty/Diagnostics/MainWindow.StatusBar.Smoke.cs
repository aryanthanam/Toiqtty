using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunStatusBarChecks(Action<bool, string> check, Func<string, int, UIElement?, Task> capture)
    {
        var original = _saved.Settings.StatusBar;
        var workspace = _selected!;
        var sessions = workspace.AllPanes().Select(p => p.Session).ToArray();
        try
        {
            UpdateChrome(); Root.UpdateLayout();
            check(StatusBar.Visibility == Visibility.Collapsed && Root.RowDefinitions[3].ActualHeight == 0,
                "The default status bar is collapsed and reserves no bottom row");
            double hiddenHeight = WorkspaceBody.ActualHeight;
            _saved.Settings.StatusBar = new StatusBarSettings { Enabled = true, Height = 28, FontSize = 12,
                Left = ["{session}", "{tab}", "{branch}"], Right = ["{panes} panes", "{font_size}px"], Separator = " | " };
            _statusMessageUntil = 0; UpdateChrome(); Root.UpdateLayout();
            check(StatusBar.Visibility == Visibility.Visible && StatusBar.ActualHeight == 28
                && Math.Abs(hiddenHeight - WorkspaceBody.ActualHeight - 28) < 1,
                "Enabling the status bar reserves exactly its configured height from the workspace");
            check(StatusText.Text == $"{workspace.Name} | {workspace.ActiveTab.Name} | {workspace.Branch}"
                && SessionStatus.Text == $"2 panes | {_saved.Settings.FontSize:0.#}px" && StatusText.FontSize == 12,
                "Live status segments show session, tab, branch, pane count and text size in configured order");
            await capture("status-bar-configured", 1280, null);
            var other = _saved.Workspaces.First(w => w != workspace);
            SelectWorkspace(other); _statusMessageUntil = 0; UpdateChrome();
            check(StatusText.Text.StartsWith(other.Name + " | " , StringComparison.Ordinal), "Status segments follow the selected session");
            SelectWorkspace(workspace);
            _saved.Settings.StatusBar.Background = "#102030"; _saved.Settings.StatusBar.Foreground = "#F0E0D0";
            UpdateChrome();
            check(((SolidColorBrush)StatusBar.Background).Color == Chrome.Brush(0xFF102030).Color
                && ((SolidColorBrush)StatusText.Foreground).Color == Chrome.Brush(0xFFF0E0D0).Color,
                "Explicit Lua status colors override the active session palette");
            _saved.Settings.StatusBar.Background = _saved.Settings.StatusBar.Foreground = ""; UpdateChrome();
            check(((SolidColorBrush)StatusBar.Background).Color == Chrome.Brush(Chrome.For(workspace.Theme).Background).Color,
                "Clearing status color overrides restores the active session palette");
            _saved.Settings.StatusBar.Enabled = false; UpdateChrome(); Root.UpdateLayout();
            check(Root.RowDefinitions[3].ActualHeight == 0, "Disabling a live status bar releases its entire layout row");
            SetStatusMessage("Isolated status-bar error check", problem: true);
            check(StatusNotice.IsOpen && StatusNotice.Message.Contains("Isolated") && StatusBar.Visibility == Visibility.Collapsed,
                "Actionable errors remain visible in a dismissible banner while the status bar is hidden");
            StatusNotice.IsOpen = false;
            check(workspace.AllPanes().Select(p => p.Session).SequenceEqual(sessions),
                "Status-bar appearance and visibility changes preserve running shell identities");
        }
        finally
        {
            _saved.Settings.StatusBar = original; _statusMessageUntil = 0;
            StatusNotice.IsOpen = false; SelectWorkspace(workspace); UpdateChrome();
        }
    }
}
