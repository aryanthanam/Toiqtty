using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Oroterm.Controls;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunSessionThemeChecks(Action<bool,string> check, Func<string,int,UIElement?,Task> capture)
    {
        var workspace = _selected!;
        var shells = workspace.AllPanes().Select(p => p.Session).ToArray();
        var views = _views.ToArray();
        foreach (var theme in SessionThemePresets.All)
        {
            SetSessionTheme(workspace, theme);
            var colors = Chrome.For(theme);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (_views.Any(v => v.FrameForCheck.Background != colors.Background || v.FrameForCheck.Foreground != colors.Text) && DateTime.UtcNow < until) await Task.Delay(25);
            check(((SolidColorBrush)TitleBar.Background).Color == Chrome.Brush(colors.Background).Color
                && ((SolidColorBrush)Sidebar.Background).Color == Chrome.Brush(colors.Background).Color
                && SidebarActions.Children.OfType<Button>().All(button => ((SolidColorBrush)button.Foreground).Color == Chrome.Brush(colors.Text).Color)
                && ((SolidColorBrush)StatusText.Foreground).Color == Chrome.Brush(colors.Secondary).Color
                && _views.All(v => v.SessionAccent == colors.Accent)
                && ((SolidColorBrush)_tabCards[workspace.ActiveTab.Id].Card.BorderBrush).Color == Chrome.Brush(colors.Accent).Color,
                "The " + theme.Name + " preset reaches the sidebar backdrop, footer icons, title bar, status text, active tab and live pane focus markers");
            check(_views.All(v => v.FrameForCheck.Background == colors.Background && v.FrameForCheck.Foreground == colors.Text),
                "The " + theme.Name + " preset changes the actual Ghostty frames in every split pane");
        }
        SetSessionTheme(workspace, SessionThemePresets.Find("Hot Dog Stand (Mustard)"));
        check(Root.RequestedTheme == ElementTheme.Light, "Bright session palettes switch native controls to the light theme");
        await capture("theme-mustard",960,null);
        var other = _saved.Workspaces.First(w => w != workspace);
        SetSessionTheme(other, SessionThemePresets.Find("Alien Blood"));
        SelectWorkspace(other);
        check(((SolidColorBrush)TitleBar.Background).Color == Chrome.Brush(Chrome.For(other.Theme).Background).Color,
            "Switching sessions changes the active interface to that session's own palette");
        SelectWorkspace(workspace);
        var editing = EditSessionTheme(workspace); await Task.Delay(150);
        var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).SelectMany(p => Walk(p.Child)).OfType<ContentDialog>().First();
        var choices = Walk(dialog).OfType<Button>().Where(b => b.Tag is SessionTheme).ToArray();
        check(choices.Length == 10 && !Walk(dialog).OfType<ColorPicker>().Any() && !Walk(dialog).OfType<TextBox>().Any(),
            "The session-theme gallery offers exactly ten visual presets without color pickers or hex inputs");
        await capture("session-theme-gallery",960,dialog);
        var cobalt = choices.Single(b => ((SessionTheme)b.Tag).Name == "Cobalt2");
        ((IInvokeProvider)new ButtonAutomationPeer(cobalt).GetPattern(PatternInterface.Invoke)).Invoke();
        await editing.WaitAsync(TimeSpan.FromSeconds(5));
        check(workspace.Theme == SessionThemePresets.Find("Cobalt2"), "Choosing a native theme tile applies its preset immediately");
        foreach (var view in _views) view.ApplySettings();
        var settingsUntil = DateTime.UtcNow.AddSeconds(5);
        while (_views.Any(v => v.FrameForCheck.Background != Chrome.For(workspace.Theme).Background) && DateTime.UtcNow < settingsUntil) await Task.Delay(25);
        check(_views.All(v => v.FrameForCheck.Background == Chrome.For(workspace.Theme).Background),
            "Applying font and global terminal settings preserves each pane's owning-session palette");
        await _sessionThemes.FlushAsync();
        var legacy = new Workspace { Id = workspace.Id };
        new SessionThemeStore(SessionStore.Folder).LoadInto([legacy]);
        check(legacy.Theme == workspace.Theme, "The selected theme is durably saved independently of the shell host");
        check(workspace.AllPanes().Select(p => p.Session).SequenceEqual(shells) && _views.SequenceEqual(views),
            "Theme changes preserve live shell objects and retained terminal views");
        SetSessionTheme(workspace, null);
        check(Root.RequestedTheme == ElementTheme.Dark && ((SolidColorBrush)TitleBar.Background).Color == Chrome.Brush(Chrome.Background).Color
            && ((SolidColorBrush)Sidebar.Background).Color == Chrome.Brush(Chrome.Sidebar).Color,
            "Reset to default restores the normal interface without replacing shells");
        SetSessionTheme(workspace, SessionThemePresets.Find("Cobalt2"));
        SetSessionTheme(_saved.Workspaces.Last(w => w != workspace && w != other), SessionThemePresets.Find("Dracula"));
        static IEnumerable<DependencyObject> Walk(DependencyObject? node)
        {
            if (node is null) yield break;
            yield return node;
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) foreach(var child in Walk(VisualTreeHelper.GetChild(node,i))) yield return child;
        }
    }
}
