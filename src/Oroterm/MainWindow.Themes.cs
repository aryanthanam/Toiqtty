using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Oroterm.Controls;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private readonly SessionThemeStore _sessionThemes = new(SessionStore.Folder);
    private bool _sessionThemesLoaded;
    private Chrome.Palette? _appliedSessionPalette;
    private SessionTheme? ThemeFor(Workspace? workspace) => workspace?.Theme ?? _saved.Settings.TerminalTheme;

    private void ApplySessionChrome()
    {
        bool oldColors = _host is { SupportsPaneThemes: false } && (_saved.Settings.TerminalTheme is not null || _saved.Workspaces.Any(w => w.Theme is not null));
        bool oldAppearance = _host is { SupportsAdvancedAppearance: false } && (_saved.Settings.Cursor.Style != "block" || _saved.Settings.Cursor.Blink
            || _saved.Workspaces.Any(w => ThemeFor(w) is { Indexed.Length: > 0 }));
        PaneThemeHostNotice.IsOpen = oldColors || oldAppearance;
        PaneThemeHostNotice.Title = oldAppearance ? "Cursor defaults and indexed colors need the new session host" : "Pane colors need the new session host";
        var colors = Chrome.For(ThemeFor(_selected));
        if (_appliedSessionPalette == colors) return;
        _appliedSessionPalette = colors;
        Root.Background = TitleBar.Background = Chrome.Brush(colors.Background);
        Sidebar.Background = SidebarHeader.Background = Chrome.Brush(ThemeFor(_selected) is null ? Chrome.Sidebar : colors.Background);
        Sidebar.BorderBrush = SidebarHeader.BorderBrush = TitleBar.BorderBrush = Chrome.Brush(colors.Border);
        SessionCount.Foreground = Chrome.Brush(colors.Secondary);
        SessionsLabel.Foreground = Chrome.Brush(colors.Text);
        Root.Resources["TextFillColorPrimaryBrush"] = Chrome.Brush(colors.Text);
        Root.Resources["ButtonBackgroundPointerOver"] = Chrome.Brush(colors.Selected);
        Root.Resources["ButtonBackgroundPressed"] = Chrome.Brush(colors.Border);
        foreach (var button in TerminalActions.Children.OfType<Button>()) button.Foreground = Chrome.Brush(colors.Text);
        NewSessionButton.Foreground = SidebarToggleButton.Foreground = SidebarRestoreButton.Foreground = Chrome.Brush(colors.Text);
        foreach (var button in SidebarActions.Children.OfType<Button>()) button.Foreground = Chrome.Brush(colors.Text);
        double brightness = (((colors.Background >> 16) & 255) * .2126 + ((colors.Background >> 8) & 255) * .7152 + (colors.Background & 255) * .0722) / 255;
        Root.RequestedTheme = brightness > .55 ? ElementTheme.Light : ElementTheme.Dark;
        ApplyWindowBackground();
        AppWindow.TitleBar.ButtonForegroundColor = Chrome.Brush(colors.Text).Color;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Chrome.Brush(colors.Selected).Color;
        AppWindow.TitleBar.ButtonHoverForegroundColor = Chrome.Brush(colors.Text).Color;
    }

    private uint SessionCardBackground(Workspace workspace, bool selected)
    {
        var colors = Chrome.For(ThemeFor(workspace));
        if (selected) return colors.Selected;
        if (ThemeFor(workspace) is not null) return colors.Background;
        // Keep unthemed cards readable on a bright active-session backdrop.
        return ThemeFor(_selected) is null ? 0x00000000 : Chrome.Sidebar;
    }

    private void SetSessionTheme(Workspace workspace, SessionTheme? theme)
    {
        workspace.Theme = theme;
        uint accent = Chrome.For(theme).Accent;
        foreach (var pane in workspace.AllPanes())
        {
            pane.Session?.ApplySettings(_saved.Settings.WithTerminalTheme(theme));
            if (_terminalViews.TryGetValue(pane.Id, out var retained)) retained.View.SetSessionTheme(ThemeFor(workspace));
            if (_browsers.TryGetValue(pane.Id, out var browser)) browser.SetSessionTheme(ThemeFor(workspace));
            if (_paneDragSurfaces.TryGetValue(pane.Id, out var drag))
            {
                drag.Preview.BorderBrush = Chrome.Brush(accent);
                drag.Preview.Background = Chrome.Brush(0x33000000 | (accent & 0xffffff));
            }
        }
        RefreshSidebar(); RefreshTabs(); Save();
    }

    private async Task EditSessionTheme(Workspace workspace)
    {
        if (_dialogOpen) return;
        SessionTheme? selected = null;
        var gallery = new Grid { ColumnSpacing = 8, RowSpacing = 8, MinWidth = 400 };
        gallery.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        gallery.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Choose a theme for this session's interface and terminal panes. Applications can still set their own colors.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        panel.Children.Add(gallery);
        var dialog = new ContentDialog { Title = "Session theme · " + workspace.Name,
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 }, SecondaryButtonText = "Use default", CloseButtonText = "Cancel" };
        int index = 0;
        foreach (var theme in SessionThemePresets.All.Concat(_saved.Settings.ColorSchemes.Values))
        {
            if (index % 2 == 0) gallery.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var colors = Chrome.For(theme);
            bool active = workspace.Theme?.Name == theme.Name;
            var label = new TextBlock { Text = theme.Name + (active ? "  ✓" : ""), Foreground = Chrome.Brush(colors.Text),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            var tile = new Border { Child = label, Background = Chrome.Brush(colors.Background), BorderBrush = Chrome.Brush(colors.Accent),
                BorderThickness = new Thickness(4,0,0,0), Padding = new Thickness(12,8,10,8), MinHeight = 56 };
            var button = new Button { Content = tile, Tag = theme, Padding = new Thickness(0), BorderThickness = new Thickness(1),
                BorderBrush = Chrome.Brush(active ? colors.Accent : colors.Border), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, theme.Name + (active ? ", current theme" : ""));
            button.Click += (_, _) => { selected = theme; dialog.Hide(); };
            Grid.SetRow(button, index / 2); Grid.SetColumn(button, index % 2); gallery.Children.Add(button); index++;
        }
        var result = await ShowDialog(dialog);
        if (selected is not null) SetSessionTheme(workspace, selected);
        else if (result == ContentDialogResult.Secondary) SetSessionTheme(workspace, null);
    }
}
