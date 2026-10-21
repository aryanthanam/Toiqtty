using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toiqtty.Browser;
using Toiqtty.Controls;
using Toiqtty.Models;
using Windows.System;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private readonly BrowserRuntime _browserRuntime;
    private readonly Dictionary<string, BrowserView> _browsers = [];
    private BrowserView? _focusedBrowser;
    private string? ActivePaneId => _focused?.PaneId ?? _focusedBrowser?.PaneId;
    private void PruneBrowsers()
    {
        var retained = _saved.Workspaces.SelectMany(w => w.AllPanes()).Where(p => p.IsBrowser).Select(p => p.Id).ToHashSet();
        foreach (var id in _browsers.Keys.Where(id => !retained.Contains(id)).ToArray()) { _browsers[id].Dispose(); _browsers.Remove(id); }
    }
    private BrowserView BuildBrowser(PaneNode node, Workspace workspace)
    {
        if (_browsers.TryGetValue(node.Id, out var existing)) { existing.Bind(node); existing.SetSessionTheme(ThemeFor(workspace)); return existing; }
        var view = new BrowserView(node, _browserRuntime);
        view.SetSessionTheme(ThemeFor(workspace));
        view.Activated += browser =>
        {
            if(_closed || _closing || _activeLayout?.Browsers.Contains(browser)!=true) return;
            _focusedBrowser = browser; _focused = null;
            var current = SessionLayout.Find(_saved, browser.PaneId);
            if (current is null) return;
            current.Value.Tab.FocusedPaneId = browser.PaneId;
            foreach (var terminal in _views) terminal.IsActive = false;
            foreach (var item in _browsers.Values) item.IsActive = item == browser;
            UpdateChrome();
        };
        view.CloseRequested += ClosePane;
        view.NewWindowRequested += url => NewBrowser(url, sourceId: view.SourceId);
        view.LocationChanged += Save;
        view.ProfileRequested += () => _ = ChangeBrowserProfile(view);
        view.RecoveryRequested += () => RecreateBrowser(view);
        view.PermissionRequested += async (uri, kind) => !_closed && await ShowDialog(new ContentDialog {
            Title = "Allow site permission?", Content = $"{uri}\n\nRequested permission: {kind}", PrimaryButtonText = "Allow once", CloseButtonText = "Block"
        }) == ContentDialogResult.Primary;
        _browsers.Add(node.Id, view); return view;
    }
    private void RecreateBrowser(BrowserView view, string? source = null)
    {
        var pane = _saved.Workspaces.SelectMany(w => w.AllPanes()).FirstOrDefault(p => p.Id == view.PaneId);
        if (pane is null) return;
        view.Dispose(); _browsers.Remove(view.PaneId);
        if (source is not null) pane.BrowserSourceId = source;
        if (_selected?.Root.Leaves().Any(p => p.Id == pane.Id) == true) RebuildPanes(pane.Id);
        Save();
    }
    private async Task ChangeBrowserProfile(BrowserView view)
    {
        if (_dialogOpen) return;
        var choices = new ComboBox { Header = "Browser profile", MinWidth = 360 };
        choices.Items.Add(new ComboBoxItem { Content = "toiqtty · independent profile", Tag = "" });
        foreach (var source in _browserRuntime.Bridge?.Sources ?? []) choices.Items.Add(new ComboBoxItem { Content = source.Name, Tag = source.Id });
        choices.SelectedItem = choices.Items.Cast<ComboBoxItem>().FirstOrDefault(c => (string)c.Tag == view.SourceId) ?? choices.Items[0];
        if (await ShowDialog(new ContentDialog { Title = "Change browser profile", Content = choices, PrimaryButtonText = "Switch and reload", CloseButtonText = "Cancel" }) == ContentDialogResult.Primary && choices.SelectedItem is ComboBoxItem { Tag: string id } && id != view.SourceId)
            RecreateBrowser(view, id);
    }
    private void NewBrowserClick(object _1, RoutedEventArgs _2) => NewBrowser();
    private void SplitBrowserRightClick(object _1, RoutedEventArgs _2) => NewBrowser(split: true);
    private void SplitBrowserDownClick(object _1, RoutedEventArgs _2) => NewBrowser(split: true, vertical: false);
    private async void BrowserSettingsClick(object _1, RoutedEventArgs _2) => await BrowserSettingsDialog();
    private void NewBrowser(string? url = null, bool split = false, bool vertical = true, string? sourceId = null)
    {
        if (_selected is null) NewWorkspace();
        if (_selected is null) return;
        if (_saved.Workspaces.SelectMany(w => w.AllPanes()).Count() >= 256 || (!split && _selected.Tabs.Count >= 64) ||
            (split && _selected.Root.Leaves().Count() >= 16)) { SetStatusMessage("Session pane limit reached.", problem: true); return; }
        var pane = new PaneNode { Kind = "browser", BrowserUrl = BrowserAddress.Resolve(url ?? _saved.Settings.Browser.HomePage),
            BrowserSourceId = sourceId ?? _saved.Settings.Browser.DefaultSourceId, Directory = _saved.Settings.StartDirectory };
        if (split && (_selected.FocusedPaneId ?? _selected.Root.Leaves().FirstOrDefault()?.Id) is { } focused)
        {
            _selected.Root.Split(focused, vertical, _saved.Settings.Shell, pane);
            _selected.FocusedPaneId = pane.Id; _zoomed = false; RebuildPanes(pane.Id); RefreshSidebar(); Save();
        }
        else
        {
            var tab = new TerminalTab { Name = "Browser", Root = pane, FocusedPaneId = pane.Id };
            _selected.Tabs.Add(tab); SelectTab(tab); Save();
        }
    }
    private async Task BrowserSettingsDialog()
    {
        if (_dialogOpen) return;
        var bridge = _browserRuntime.Bridge;
        var home = new TextBox { Header = "Browser home page", Text = _saved.Settings.Browser.HomePage };
        var links = new ToggleSwitch { Header = "Open terminal web links inside toiqtty", IsOn = _saved.Settings.Browser.OpenTerminalLinks };
        var choices = new ComboBox { Header = "Default profile for new browser panes", HorizontalAlignment = HorizontalAlignment.Stretch };
        home.IsEnabled = !LuaOwns("Browser.HomePage"); links.IsEnabled = !LuaOwns("Browser.OpenTerminalLinks"); choices.IsEnabled = !LuaOwns("Browser.DefaultSourceId");
        var connections = new ComboBox { Header = "Connected browser profile", HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "Profile name", MaxLength = 100 };
        var origins = new TextBox { Header = "Share cookies for these sites", AcceptsReturn = true, MinHeight = 95, MaxHeight = 180,
            PlaceholderText = "https://github.com\nhttp://localhost:3000", Description = "One site origin per line. Cookies apply across ports on the same host. Sharing goes into toiqtty." };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var apply = new Button { Content = "Save site selection" };
        var disconnect = new Button { Content = "Disconnect profile" };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; controls.Children.Add(apply); controls.Children.Add(disconnect);
        string selectedDefault = _saved.Settings.Browser.DefaultSourceId;
        void Refresh()
        {
            string? selected = (connections.SelectedItem as ComboBoxItem)?.Tag as string;
            choices.Items.Clear(); choices.Items.Add(new ComboBoxItem { Content = "toiqtty · independent profile", Tag = "" });
            connections.Items.Clear();
            foreach (var source in bridge?.Sources ?? [])
            {
                choices.Items.Add(new ComboBoxItem { Content = source.Name, Tag = source.Id });
                connections.Items.Add(new ComboBoxItem { Content = source.Name, Tag = source.Id });
            }
            choices.SelectedItem = choices.Items.Cast<ComboBoxItem>().FirstOrDefault(c => (string)c.Tag == selectedDefault) ?? choices.Items[0];
            connections.SelectedItem = connections.Items.Cast<ComboBoxItem>().FirstOrDefault(c => (string)c.Tag == selected) ?? connections.Items.Cast<ComboBoxItem>().FirstOrDefault();
        }
        connections.SelectionChanged += (_, _) =>
        {
            var source = bridge?.Sources.FirstOrDefault(s => s.Id == ((connections.SelectedItem as ComboBoxItem)?.Tag as string));
            name.Text = source?.Name ?? ""; origins.Text = string.Join("\n", source?.Origins ?? []);
            name.IsEnabled = origins.IsEnabled = apply.IsEnabled = disconnect.IsEnabled = source is not null;
            if (source is not null) status.Text = source.LastSync is { } synced ? $"Last sync: {synced.LocalDateTime:t}" : "Waiting for the companion extension to sync.";
        };
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is ComboBoxItem { Tag: string id }) selectedDefault = id; };
        Refresh();
        var pair = new Button { Content = "Connect another browser profile", IsEnabled = bridge is not null };
        var code = new TextBox { Header = "Pairing code · expires after 10 minutes", IsReadOnly = true, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, MaxHeight = 90 };
        pair.Click += (_, _) => { code.Text = bridge!.BeginPairing(); code.Visibility = Visibility.Visible; status.Text = "Paste this code into the toiqtty companion extension in the browser profile you want to connect."; };
        var folder = new Button { Content = "Open companion extension folder" };
        folder.Click += async (_, _) => await Launcher.LaunchFolderPathAsync(Path.Combine(AppContext.BaseDirectory, "BrowserCompanion"));
        apply.Click += (_, _) =>
        {
            try
            {
                if (connections.SelectedItem is not ComboBoxItem { Tag: string id }) return;
                bridge!.Configure(id, name.Text, origins.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                status.Text = "Saved. Open the companion extension and choose Sync now to approve access to new sites.";
            }
            catch (ArgumentException ex) { status.Text = ex.Message; }
        };
        disconnect.Click += (_, _) =>
        {
            if (connections.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            bridge!.Disconnect(id); if (selectedDefault == id) selectedDefault = "";
            status.Text = "Disconnected. Cookies already imported remain until cleared from the browser pane menu.";
        };
        void Changed() => DispatcherQueue.TryEnqueue(() => { Refresh(); code.Text = ""; code.Visibility = Visibility.Collapsed; });
        bridge?.SourcesChanged += Changed;
        var panel = new StackPanel { Spacing = 14, MinWidth = 400, MaxWidth = 520 };
        foreach (var item in new UIElement[] { home, links, choices, connections, name, origins, controls, pair, code, folder,
            new TextBlock { Text = "Install chromium/ in Edge, Chrome, Brave, Vivaldi, or another Chromium browser using Load unpacked. For Firefox and compatible browsers, use firefox/. See README.txt in that folder. Cookies sync while toiqtty and the source browser are open. Passwords, history, local storage, and partitioned cookies are not copied.", TextWrapping = TextWrapping.Wrap, FontSize = 12 }, status }) panel.Children.Add(item);
        if (_browserRuntime.BridgeError is { } error) status.Text = error;
        var dialog = new ContentDialog { Title = "Browser profiles and cookie sharing", Content = new ScrollViewer { Content = panel, MaxHeight = 570 }, PrimaryButtonText = "Save defaults", CloseButtonText = "Done" };
        string resolved = "about:blank";
        dialog.PrimaryButtonClick += (_, args) => { try { resolved = BrowserAddress.Resolve(home.Text); } catch (ArgumentException ex) { status.Text = ex.Message; args.Cancel = true; } };
        try
        {
            if (await ShowDialog(dialog) == ContentDialogResult.Primary)
            { _saved.Settings.Browser.HomePage = resolved; _saved.Settings.Browser.DefaultSourceId = selectedDefault; _saved.Settings.Browser.OpenTerminalLinks = links.IsOn; Save(); }
        }
        finally { if (bridge is not null) { bridge.SourcesChanged -= Changed; bridge.EndPairing(); } }
    }
}
