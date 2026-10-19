using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Oroterm.Controls;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private double SidebarWidth=>Math.Clamp(Root.ActualWidth*.2,160,220);
    private void UpdateSidebarWidth()
    {
        if(Sidebar.Visibility==Visibility.Visible) SidebarColumn.Width=TitleSidebarColumn.Width=new GridLength(SidebarWidth);
    }
    private string Shortcut(string action) => _saved.Settings.Keybindings.GetValueOrDefault(action, Keybindings.Defaults[action]);
    private void UpdateChrome()
    {
        if(_closed || _closing) return;
        ApplySessionChrome();
        SessionCount.Text = _saved.Workspaces.Count.ToString();
        SidebarRestoreButton.Visibility=Sidebar.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
        Hint(SidebarRestoreButton,"Show sidebar","sidebar");
        UpdateStatusBar();
        QueueLuaPresentation();
        SplitRightButton.KeyboardAcceleratorTextOverride=Shortcut("splitRight"); SplitDownButton.KeyboardAcceleratorTextOverride=Shortcut("splitDown");
        MovePaneMenuItem.KeyboardAcceleratorTextOverride=Shortcut("movePane");
        ZoomButton.KeyboardAcceleratorTextOverride=Shortcut("zoom"); ClosePaneMenuItem.KeyboardAcceleratorTextOverride=Shortcut("closePane");
        Hint(NewSessionButton, "New session", "newWorkspace"); Hint(NewTabButton, "New tab", "newTab");
        OpenDirectoryButton.IsEnabled = _selected is not null;
        Hint(OpenDirectoryButton, "Open current folder in Windows Explorer", "openDirectory");
        Hint(SplitRightButton, "Split right", "splitRight"); Hint(SplitDownButton, "Split down", "splitDown");
        Hint(ZoomButton, _zoomed ? "Restore all panes" : "Zoom focused pane", "zoom");
        Hint(PaletteButton, "Command palette", "palette");
        ZoomButton.Text=_zoomed?"Restore all panes":"Zoom focused pane";
        string title=_selected is null?"Oroterm":_selected.Name+" — "+_selected.ActiveTab.Name+(_zoomed?" (zoomed)":"")+" — Oroterm";
        if (Title!=title) Title=title;

        void Hint(FrameworkElement element, string label, string action)
        {
            string key = Shortcut(action);
            ToolTipService.SetToolTip(element, key.Length == 0 ? label : label + " (" + key + ")");
        }
    }
    private static string DisplayDirectory(string directory)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        return directory.Equals(home, StringComparison.OrdinalIgnoreCase) ? "~"
            : directory.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "~" + directory[home.Length..] : directory;
    }
    private void SidebarClick(object sender, RoutedEventArgs e) => ToggleSidebar();
    private void CloseFocusedPaneClick(object sender,RoutedEventArgs e) { if (ActivePaneId is { } id) ClosePane(id); }
}
