using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Toiqtty.Controls;
using Toiqtty.Models;
using Toiqtty.Terminal;
using Toiqtty.Hosting;
using Windows.System;

namespace Toiqtty;

public sealed partial class MainWindow : Window
{
    private readonly SavedSession _saved;
    private readonly List<TerminalView> _views = [];
    private Workspace? _selected;
    private TerminalView? _focused;
    private bool _refreshing, _zoomed, _dialogOpen;
    private readonly DispatcherTimer _metadataTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _polling;
    private bool _closed, _closing;
    private string _sidebarFingerprint = "";
    private readonly WindowPowerNotifications _power;
    private int _resumeCount;
    private readonly Notifications.DesktopNotifications? _notifications;
    private bool _windowActive;
    private (string Workspace, string Pane)? _pendingNavigation;
    private bool _sessionsRestored;
    private readonly bool _persistent;
    private readonly bool _reattached;
    private readonly HashSet<string> _durablePanes=[];
    // One single-flight connection; _host is whichever client it has resolved.
    private readonly SessionHostConnector _connector = new(SessionStore.Folder);
    private SessionHostClient? _host => _connector.Current;
    private readonly StartupLuaEvaluation? _startupLua;
    private readonly StartupGraphics? _startupGraphics;
    internal MainWindow(Notifications.DesktopNotifications? notifications = null, SessionHostClient? runningHost = null, Task<SessionHostClient?>? startingHost = null, SavedSession? saved = null, StartupLuaEvaluation? startupLua = null, StartupGraphics? startupGraphics = null)
    {
        _startupLua = startupLua;
        _startupGraphics = startupGraphics;
        _saved = saved ?? SessionStartupPolicy.Select(SessionStore.Load(), runningHost is { Panes.Length: > 0 });
        _notifications = notifications;
        StartupTrace.Mark("xaml-begin");
        InitializeComponent();
        StartupTrace.Mark("xaml-end");
        StartupTrace.Mark("browser-runtime-begin");
        _browserRuntime = new(DispatcherQueue);
        StartupTrace.Mark("browser-runtime-end");
        _browserRuntime.Status += (text, problem) => { if (!_closed) SetStatusMessage(text, problem); };
        Activated += (_, args) => {
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (!_windowActive) _bindingState.Reset();
            if (!_windowActive) foreach (var view in _views) view.StopAutoScroll("window-deactivated");
        };
        _power = new(WinRT.Interop.WindowNative.GetWindowHandle(this), resumed =>
        {
            if (_closed) return;
#if DIAGNOSTICS
            if (_hardwareWorkspace is not null) RecordHardwareEvent(resumed ? "power-resume" : "power-suspend", "Window power notification received");
#endif
            if (resumed)
            {
                _resumeCount++;
                _powerSuspended=false; UpdateRenderingVisibility(true);
                _metadataTimer.Start();
            }
            else
            {
                Save(); _metadataTimer.Stop(); _powerSuspended=true; UpdateRenderingVisibility();
            }
        });
        Title = "toiqtty " + BuildInfo.Version;
        UiTasks.Faulted += ReportUiFault;
        var diagnostics=Diagnostics.WindowDiagnosticOptions.Read();
        bool browserSmoke=diagnostics.Browser, configSmoke=diagnostics.Config, designSmoke=diagnostics.Design;
        bool dpiSmoke=diagnostics.Dpi, startupProbe=diagnostics.Startup, uiSmoke=diagnostics.Ui, hardwareCheck=diagnostics.Hardware;
        string? configProbe=diagnostics.ConfigProbe, browserProbe=diagnostics.BrowserProbe, sessionProbe=diagnostics.SessionProbe;
        _persistent=diagnostics.Persistent; _luaEnabled=diagnostics.LuaEnabled;
        StartupTrace.Mark("window-host-probe-begin");
        if (_persistent && runningHost is { } running && running.Panes.Length>0)
        {
            _connector.Adopt(running); _saved=running.Layout??_saved; _reattached=true;
            // Restored from the host's own layout: these panes are already durable there.
            _durablePanes=_saved.Workspaces.SelectMany(w=>w.AllPanes()).Select(p=>p.Id).ToHashSet();
        }
        StartupTrace.Mark("window-host-probe-end");
        // A reconnect after a host exit can meet a newer or older host version.
        _connector.VersionMismatch += ex => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            SetStatusMessage(ex.Message, problem: true);
            (Application.Current as App)?.ShowUpgradeWindow(ex.Message);
        });
        diagnostics.PrepareFixture(_saved);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        InitializeTitleBar();
        InitializeRenderingVisibility();
        InitializePaneDragging();
        AppWindow.Closing+=(_,args)=>BeginWindowClose(args);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 190, 198, 212);
        Root.PreviewKeyDown += GlobalKeyDown;
        var profiles = new List<(string Name, string Command)> { ("Windows PowerShell", "powershell.exe -NoLogo"), ("Command Prompt", "cmd.exe") };
        if (ShellDefaults.FindPowerShell7() is { } pwsh) profiles.Insert(0, ("PowerShell 7", $"\"{pwsh}\" -NoLogo"));
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe"))) profiles.Add(("WSL", "wsl.exe"));
        var shellProfiles=new MenuFlyoutSubItem { Text="New shell tab" }; ShellMenu.Items.Add(shellProfiles);
        foreach (var (Name, Command) in profiles)
        {
            var item = new MenuFlyoutItem { Text = Name };
            item.Click += (_, _) => NewTab(Command, Name);
            shellProfiles.Items.Add(item);
        }
        Root.Loaded += (_, _) => UiTasks.Observe(async () =>
        {
            StartupTrace.Mark("root-loaded");
            StartupTrace.Mark("lua-begin");
            await InitializeLua();
            StartupTrace.Mark("lua-end");
            if (_closed) return;
            // Size once using the loaded root's DPI and evaluated preferences,
            // including baseline preferences when Lua is absent or invalid.
            ApplyWindowSettings(initial: true);
            if (_persistent && startingHost is not null)
            {
                try { if (await startingHost is { } started) _connector.Adopt(started); }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
                { SetStatusMessage("Could not start session host: " + ex.Message, problem: true); }
                if (_closed) return;
            }
            ApplyStartupConfiguration();
            if (_startupGraphics is not null)
            {
                if (_saved.Workspaces.Count == 0 || _saved.Workspaces.SelectMany(w => w.AllPanes()).Any(p => !p.IsBrowser))
                    await _startupGraphics.Install();
                else _startupGraphics.Dispose();
            }
            if (_closed) return;
            _sessionThemes.LoadInto(_saved.Workspaces); _sessionThemesLoaded = true;
            ApplyAppearanceSettings();
            if (_saved.Workspaces.Count == 0) NewWorkspace();
            else
            {
                var selected = _saved.Workspaces.FirstOrDefault(w => w.Id == _saved.SelectedWorkspace) ?? _saved.Workspaces[0];
                // Before any view is built, so the visible panes join the one batched reattach
                // (or lead the creates) instead of each starting alone from BuildNode.
                if (_persistent) EnsureShells(_saved.Workspaces, selected, "Could not restore shell: ");
                RefreshSidebar(); SelectWorkspace(selected);
            }
            UpdateChrome();
            StartupTrace.Mark("sessions-restored");
            if (StartupTrace.Enabled) _ = MarkSessionsReady();
            _metadataTimer.Start();
            _sessionsRestored = true;
            if (_pendingNavigation is { } pending) { _pendingNavigation = null; NavigateNotification(pending.Workspace, pending.Pane); }
            if (startupProbe) _ = RunStartupProbe();
#if DIAGNOSTICS
            else if (dpiSmoke) _ = RunDpiSmoke();
            else if (designSmoke) _ = RunDesignSmoke();
            else if (configSmoke) _ = RunConfigurationSmoke(configProbe);
            else if (browserProbe is not null) _ = RunBrowserPersistenceProbe(browserProbe);
            else if (sessionProbe is not null) _ = RunSessionUiProbe(sessionProbe);
            else if (browserSmoke) _ = RunBrowserSmoke();
            else if (Environment.GetCommandLineArgs().Contains("--directx-smoke")) _ = RunDirectXSmoke();
            else if (uiSmoke) _ = RunUiSmoke();
#endif
            InitializeControlServer();
#if DIAGNOSTICS
            if (hardwareCheck) _ = PrepareHardwareChecks();
#endif
        });
        _metadataTimer.Tick += (_, _) => UiTasks.Observe(PollMetadata());
        _luaPresentationTimer.Tick += (_, _) => QueueLuaPresentation();
        Closed += (_, _) =>
        {
            _closed = true; UiTasks.Faulted -= ReportUiFault;
            _startupLua?.Dispose();
            _startupGraphics?.Dispose();
            if (_controlServer is { } control) _ = control.DisposeAsync();
            _luaWatcher?.Dispose(); _luaReload?.Cancel();
            _luaPresentationTimer.Stop(); _luaRuntime?.Dispose(); _wallpaperImage?.Dispose();
            _power.Dispose();
            _metadataTimer.Stop();
            DisposeViews();
            Controls.DirectXRenderer.ShutdownDevices();
            foreach (var browser in _browsers.Values) browser.Dispose();
            _browsers.Clear(); _browserRuntime.Dispose();
            if (_persistent)
            {
                // StartInputClose already awaited detach and persistence.
            }
            else foreach (var pane in _saved.Workspaces.SelectMany(w => w.AllPanes())) pane.Session?.Dispose();
            if(Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_DEBUG")=="1" && Environment.GetCommandLineArgs().Contains("--directx-smoke"))
            {
                // The isolated debug fixture owns this process-wide shared
                // device. Release Win2D's provider cache before D2D leak checks
                // run during DLL shutdown, after every native pane is disposed.
                Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice().Dispose();
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            }
        };
    }
    // Restore no longer waits for shells; this marks when every restored one has started (or failed).
    private async Task MarkSessionsReady()
    {
        try { await Task.WhenAll(_saved.Workspaces.SelectMany(w => w.AllPanes()).Select(p => p.Session?.Ready).OfType<Task>()); }
        catch (Exception) { }
        StartupTrace.Mark("sessions-ready");
    }
    private static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    internal void NavigateNotification(string workspaceId, string paneId)
    {
        if (_closed) return;
        // Browser-only tabs and empty windows have no terminal views; only defer until restore.
        if (!_sessionsRestored) { _pendingNavigation = (workspaceId, paneId); return; }
        var workspace = SessionLayout.Find(_saved, paneId)?.Workspace ?? _saved.Workspaces.FirstOrDefault(item => item.Id == workspaceId);
        if (workspace is not null)
        {
            var tab=workspace.Tabs.FirstOrDefault(t=>t.Root.Leaves().Any(p=>p.Id==paneId));
            if (tab is not null) { workspace.ActiveTabId=tab.Id; tab.FocusedPaneId=paneId; }
            SelectWorkspace(workspace);
        }
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindowAsync(handle, 9); SetForegroundWindow(handle); Activate();
    }
    [System.Runtime.InteropServices.DllImport("user32")] private static extern bool ShowWindowAsync(nint window, int command);
    [System.Runtime.InteropServices.DllImport("user32")] private static extern bool SetForegroundWindow(nint window);
    private void NewWorkspace(string? shell = null, string? name = null, string? directory = null)
    {
        var root = new PaneNode { Command = shell ?? _saved.Settings.Shell, Directory = directory ?? _selected?.Root.Leaves().FirstOrDefault(p => p.Id == _focused?.PaneId)?.Session?.WorkingDirectory ?? _saved.Settings.StartDirectory };
        if (SessionLayout.AddWorkspace(_saved, name ?? $"Session {_saved.Workspaces.Count + 1}", root, out string error) is not { } workspace)
        { SetStatusMessage(error, problem: true); return; }
        RefreshSidebar(); SelectWorkspace(workspace); Save();
    }
    private void RefreshSidebar()
    {
        UpdateChrome();
        if (_closed || _closing || _draggingSessions || _draggedPaneId is not null) return;
        var fingerprint = string.Join("|", _saved.Workspaces.Select(w =>
            w.Id + ":" + w.Name + ":" + w.Theme?.Key + ":" + w.Branch + ":" + w.Attention + ":" + w.AllPanes().Count() + ":" + w.Tabs.Count + ":" +
            (w.Root.Leaves().FirstOrDefault()?.Session?.Directory ?? w.Root.Leaves().FirstOrDefault()?.Directory)));
        if (_sidebarFingerprint == fingerprint) { SyncSidebarSelection(); return; }
        _sidebarFingerprint = fingerprint;
        _refreshing = true;
        WorkspaceList.Items.Clear();
        foreach (var workspace in _saved.Workspaces)
        {
            var leaves = workspace.AllPanes().ToList();
            var directory = leaves.FirstOrDefault()?.Session?.Directory ?? leaves.FirstOrDefault()?.Directory ?? "";
            bool selected = workspace == _selected;
            var colors = Chrome.For(ThemeFor(workspace));
            var stack = new StackPanel { Spacing = 3, Padding = new Thickness(10, 7, 10, 7) };
            var title = new TextBlock { Text = workspace.Name, FontSize = 12,
                FontWeight = selected?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Chrome.Brush(workspace.Attention ? colors.Accent : colors.Text) };
            var heading = new Grid { ColumnSpacing = workspace.Attention ? 8 : 0 };
            heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            heading.Children.Add(new Border { Width=4,Height=4,CornerRadius=new CornerRadius(2),Background=Chrome.Brush(colors.Accent),
                Visibility=workspace.Attention?Visibility.Visible:Visibility.Collapsed,VerticalAlignment=VerticalAlignment.Center });
            Grid.SetColumn(title, 1); heading.Children.Add(title); stack.Children.Add(heading);
            stack.Children.Add(new TextBlock { Text = DisplayDirectory(directory), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Chrome.Brush(workspace.Theme is null ? Chrome.Muted : colors.Secondary) });
            var meta = new List<string>();
            if (workspace.Tabs.Count > 1) meta.Add($"{workspace.Tabs.Count} tabs");
            if (workspace.Branch.Length > 0) meta.Add("git · " + workspace.Branch);
            if (leaves.Count > 1) meta.Add($"{leaves.Count} panes");
            if (workspace.Attention) meta.Add("Needs attention");
            if (meta.Count > 0) stack.Children.Add(new TextBlock { Text = string.Join("  ·  ", meta), FontSize = 10, Foreground = Chrome.Brush(workspace.Attention ? colors.Accent : colors.Secondary), TextTrimming = TextTrimming.CharacterEllipsis });
            var card = new Border { Child = stack, Background = Chrome.Brush(SessionCardBackground(workspace, selected)),
                BorderBrush = Chrome.Brush(selected || ThemeFor(workspace) is not null ? colors.Accent : 0x00000000), BorderThickness = new Thickness(2,0,0,0) };
            var item = new ListViewItem { Content = card, Tag = workspace };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, "Session " + workspace.Name + ", " + leaves.Count + " panes" + (workspace.Attention ? ", needs attention" : ""));
            ToolTipService.SetToolTip(item, directory);
            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "Rename session" }; rename.Click += (_, _) => UiTasks.Observe(Rename(workspace));
            var theme = new MenuFlyoutItem { Text = "Session theme…" }; theme.Click += (_, _) => UiTasks.Observe(EditSessionTheme(workspace));
            var close = new MenuFlyoutItem { Text = "Close session" }; close.Click += (_, _) => UiTasks.Observe(CloseWorkspace(workspace));
            var openDirectory = new MenuFlyoutItem { Text = "Open in Windows Explorer" };
            openDirectory.Click += (_, _) => UiTasks.Observe(OpenDirectory(workspace));
            menu.Items.Add(openDirectory); menu.Items.Add(theme); menu.Items.Add(rename); menu.Items.Add(close); item.ContextFlyout = menu;
            AddSessionOrderMenu(menu, workspace);
            WorkspaceList.Items.Add(item);
            if (workspace == _selected) WorkspaceList.SelectedItem = item;
        }
        _refreshing = false;
    }
    private void SyncSidebarSelection()
    {
        _refreshing = true;
        foreach (var value in WorkspaceList.Items)
        {
            var item = (ListViewItem)value;
            if (item.Tag is not Workspace workspace || item.Content is not Border card) continue;
            bool selected = workspace == _selected;
            var colors = Chrome.For(ThemeFor(workspace));
            card.Background = Chrome.Brush(SessionCardBackground(workspace, selected));
            card.BorderBrush = Chrome.Brush(selected || ThemeFor(workspace) is not null ? colors.Accent : 0x00000000);
            if (card.Child is StackPanel stack && stack.Children[0] is Grid heading && heading.Children[1] is TextBlock title)
                title.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            if (selected) WorkspaceList.SelectedItem = item;
        }
        _refreshing = false;
    }
    private void WorkspaceSelectionChanged(object _1, SelectionChangedEventArgs _2)
    {
        if (!_closed && !_closing && !_refreshing && _draggedPaneId is null && WorkspaceList.SelectedItem is ListViewItem { Tag: Workspace workspace }) SelectWorkspace(workspace);
    }
    private void SelectWorkspace(Workspace workspace)
    {
        _selected = workspace; _saved.SelectedWorkspace = workspace.Id;
        workspace.Attention = false; _zoomed = false;
        RefreshSidebar(); RefreshTabs(); RebuildPanes(); SaveNavigation();
    }
    private void RebuildPanes(string? focusId = null)
    {
        if (_selected is null) return;
        focusId ??= _selected.FocusedPaneId;
        var leaves = _selected.Root.Leaves().ToList();
        if (!leaves.Any(p=>p.Id==focusId)) focusId=leaves.FirstOrDefault()?.Id;
        UpdateChrome();
        RefreshTabs();
        PaneNode root = _selected.Root;
        if (_zoomed) root = leaves.FirstOrDefault(p => p.Id == focusId) ?? leaves[0];
        ShowTabLayout(_selected,root,_zoomed?focusId:null);
        PruneTerminalViews();
        _selected.FocusedPaneId=focusId ?? leaves.FirstOrDefault()?.Id;
        QueuePaneFocus();
    }
    private FrameworkElement BuildNode(PaneNode node, Workspace workspace)
    {
        if (node.IsLeaf)
        {
            if (node.IsBrowser) return WrapPane(BuildBrowser(node, workspace),node);
            try
            {
                // A remote shell starts asynchronously; its failure later swaps in this panel.
                EnsureShell(node,workspace);
                if (node.Session is null && _shellFailures.TryGetValue(node.Id,out var failure)) return ShellFailurePanel(node,workspace,failure);
                var view=GetTerminalView(node,workspace);
                view.ShowFocusMarker=!_zoomed && workspace.Root.Leaves().Skip(1).Any();
                _views.Add(view); return WrapPane(view,node);
            }
            catch (Exception ex)
            {
                // Local (diagnostic) sessions still spawn synchronously.
                TraceTabLayout("build-error",node.Id,ex.ToString());
                return ShellFailurePanel(node,workspace,ex.Message);
            }
        }
        var grid = new Grid();
        var first = BuildNode(node.First!, workspace); var second = BuildNode(node.Second!, workspace);
        var splitter = new Thumb
        {
            Background = Chrome.Brush(Chrome.Background),
            MinWidth = 5,
            MinHeight = 5,
            Template = (ControlTemplate)Root.Resources[node.Vertical ? "ColumnSplitter" : "RowSplitter"]
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(splitter, node.Vertical ? "Resize side-by-side panes" : "Resize stacked panes");
        ToolTipService.SetToolTip(splitter, "Drag to resize panes");
        splitter.PointerEntered += (_, _) => splitter.Background = Chrome.Brush(Chrome.Accent);
        splitter.PointerExited += (_, _) => splitter.Background = Chrome.Brush(Chrome.Background);
        if (node.Vertical)
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(5) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1 - node.Ratio, GridUnitType.Star) });
            Grid.SetColumn(splitter, 1); Grid.SetColumn(second, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new() { Height = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new() { Height = new GridLength(5) });
            grid.RowDefinitions.Add(new() { Height = new GridLength(1 - node.Ratio, GridUnitType.Star) });
            Grid.SetRow(splitter, 1); Grid.SetRow(second, 2);
        }
        splitter.DragDelta += (_, e) =>
        {
            var size = node.Vertical ? grid.ActualWidth : grid.ActualHeight;
            if (size < 10) return;
            node.Ratio = Math.Clamp(node.Ratio + (node.Vertical ? e.HorizontalChange : e.VerticalChange) / (size - 5), .1, .9);
            if (node.Vertical) { grid.ColumnDefinitions[0].Width = new(node.Ratio, GridUnitType.Star); grid.ColumnDefinitions[2].Width = new(1 - node.Ratio, GridUnitType.Star); }
            else { grid.RowDefinitions[0].Height = new(node.Ratio, GridUnitType.Star); grid.RowDefinitions[2].Height = new(1 - node.Ratio, GridUnitType.Star); }
        };
        splitter.DragCompleted += (_, _) => { RecordSplitLayout(workspace); Save(); };
        grid.Children.Add(first); grid.Children.Add(splitter); grid.Children.Add(second); return grid;
    }
    private void FocusView(TerminalView view)
    {
        if(_closed || _closing || !_views.Contains(view)) return;
        var focusedPane=_selected?.Root.Leaves().FirstOrDefault(p=>p.Id==view.PaneId);
        bool attention=focusedPane?.Attention==true;
        if(ReferenceEquals(_focused,view) && _focusedBrowser is null && view.IsActive && !attention) return;
        _focused = view; _focusedBrowser = null;
        foreach (var browser in _browsers.Values) browser.IsActive = false;
        if (_selected is not null)
        {
            _selected.FocusedPaneId = view.PaneId;
            focusedPane?.Attention = false;
            if(attention) { _selected.Attention = _selected.AllPanes().Any(p => p.Attention); RefreshSidebar(); RefreshTabs(); }
        }
        foreach (var pane in _views) pane.IsActive = pane == view;
        UpdateStatus();
    }
    private void Split(bool vertical)
    {
        if (_selected is null || (ActivePaneId ?? _selected.FocusedPaneId ?? _selected.Root.Leaves().FirstOrDefault()?.Id) is not { } id) return;
        if (!SessionLayout.SplitPane(_saved, _selected, id, vertical, _saved.Settings.Shell, out string error))
        { if (error.Length > 0) SetStatusMessage(error, problem: true); return; }
        _zoomed = false;
        var leaves = _selected.Root.Leaves().ToList();
        var oldIndex = leaves.FindIndex(p => p.Id == id);
        RebuildPanes(leaves[Math.Min(oldIndex + 1, leaves.Count - 1)].Id); RefreshSidebar(); Save();
    }
    private void ClosePane(string id)
    {
        if (_selected is null) return;
        // Suspend views before disposing sessions they may still draw on this frame.
        // Only layouts containing the closed pane are forgotten; other tabs keep theirs.
        SuspendActiveLayout();
        var workspace = _selected;
        if (SessionLayout.ClosePane(_saved, workspace, id) is { } removal) ApplyRemoval(workspace, removal, activeTab: true);
        else RebuildPanes();
    }
    /// <summary>View, shell and selection follow-up for a structural removal already applied to the model.</summary>
    private void ApplyRemoval(Workspace owner, SessionLayout.Removal removal, bool activeTab)
    {
        ForgetTerminalViews(removal.Closed);
        foreach (var pane in removal.Closed) _shellFailures.Remove(pane.Id);
        _sessionLifetimes.ClosePanes(removal.Closed);
        if (removal.RemovedWorkspace is { } removed)
        {
            if (_saved.Workspaces.Count == 0) { _selected = null; ShowEmptySessions(); }
            else if (_selected == removed) SelectWorkspace(_saved.Workspaces[Math.Clamp(removal.WorkspaceIndex - 1, 0, _saved.Workspaces.Count - 1)]);
            else RefreshSidebar();
            Save();
            if (_saved.Workspaces.Count == 0) _connector.Reset();
        }
        else if (removal.RemovedTab is not null)
        {
            if (_selected == owner && activeTab) { SelectTab(owner.ActiveTab); Save(); }
            else { RefreshTabs(); RefreshSidebar(); Save(); }
        }
        else { _zoomed = false; RebuildPanes(); RefreshSidebar(); Save(); }
    }
    private void RemoveWorkspace(Workspace workspace)
    {
        if (_selected == workspace) SuspendActiveLayout();
        if (SessionLayout.RemoveWorkspace(_saved, workspace) is { } removal) ApplyRemoval(workspace, removal, activeTab: false);
    }
    private async Task CloseWorkspace(Workspace workspace)
    {
        var result = await ShowDialog(new ContentDialog { Title = $"Close {workspace.Name}?", Content = "All tabs and running shells in this session will be stopped.", PrimaryButtonText = "Close session", CloseButtonText = "Cancel" });
        if (result == ContentDialogResult.Primary) RemoveWorkspace(workspace);
    }
    private async Task Rename(Workspace workspace)
    {
        var input = new TextBox { Text = workspace.Name, MaxLength = 60, PlaceholderText = "Session name" };
        var dialog = new ContentDialog { Title = "Rename session", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        var result = await ShowDialog(dialog);
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text)) { workspace.Name = input.Text.Trim(); RefreshSidebar(); Save(); }
    }
    private async Task<ContentDialogResult> ShowDialog(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true; dialog.XamlRoot = Root.XamlRoot; dialog.RequestedTheme = Root.RequestedTheme;
        try { return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; if (_focusedBrowser is { } browser) browser.FocusBrowser(); else _focused?.FocusTerminal(); }
    }
    private void Save()
    {
        PruneBrowsers();
        RequestSave(false);
    }
    private void SaveNavigation()
    {
        RequestSave(true);
    }
    private void RequestSave(bool navigationOnly)
    {
        try
        {
            // Shell closure is explicitly owned by SessionLifetimeCoordinator.
            // Browser-only layouts therefore retire their host reference at
            // the layout transition, rather than waiting for an IPC failure.
            if(_persistent && _saved.Workspaces.SelectMany(w=>w.AllPanes()).All(p=>p.IsBrowser)) _connector.Reset();
            SavePreferences();
            if (_sessionThemesLoaded) _ = ObserveSave(_sessionThemes.SaveAsync(_saved.Workspaces));
            ObservePersistence(_sessionPersistence.Enqueue(_saved,navigationOnly,_persistent?PersistenceHost():null));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        { if(!_closed) SetStatusMessage("Could not save session: "+ex.Message, problem: true); }
    }
    // The connected host owns the layout, and so does the one a starting shell is still
    // connecting to. Otherwise the file is written directly; a save never starts a host.
    private Task<SessionHostClient>? PersistenceHost()
        => _connector.Current is { } current ? Task.FromResult(current)
            : _saved.Workspaces.Any(w => w.AllPanes().Any(p => p.Session is RemoteTerminalSession { Ready.IsCompleted: false })) ? _connector.ConnectAsync() : null;
    private readonly SessionPersistenceQueue _sessionPersistence=new();
    private readonly SessionLifetimeCoordinator _sessionLifetimes=new();
    // Coalesced saves share one commit task; report each failed write once.
    private Task? _observedPersistence;
    private void ObservePersistence(Task commit)
    {
        if (ReferenceEquals(commit,_observedPersistence)) return;
        _observedPersistence=commit; _=ObserveSave(commit);
    }
    private async Task ObserveSave(Task save)
    {
        try
        {
            await save;
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        { if(!_closed) SetStatusMessage("Could not save session: "+ex.Message, problem: true); }
    }
    private void ReportUiFault(Exception ex)
        => DispatcherQueue.TryEnqueue(() => { if (!_closed) SetStatusMessage(ex.Message, problem: true); });
    private void UpdateStatus()
    {
        UpdateChrome();
    }
    private async Task PollMetadata()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var branches=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var workspace in _saved.Workspaces.ToList())
            {
                // A move/close can remove a session while the previous Git
                // lookup awaits. Its captured workspace can now have no tabs.
                if (!_saved.Workspaces.Contains(workspace) || workspace.Tabs.Count == 0) continue;
                var directory = workspace.Root.Leaves().FirstOrDefault()?.Session?.LocalDirectory;
                if (directory is null) { workspace.Branch = ""; continue; }
                if(!branches.TryGetValue(directory,out var branch))
                { branch=await ReadBranch(directory); branches.Add(directory,branch); }
                if (_closed) return;
                // The shell may change directory or its session may close while
                // Git is running. Do not publish a result for an older identity.
                if(_saved.Workspaces.Contains(workspace) && workspace.Tabs.Count>0
                    && workspace.Root.Leaves().FirstOrDefault()?.Session?.LocalDirectory==directory) workspace.Branch=branch;
            }
            RefreshSidebar(); UpdateStatus();
        }
        finally { _polling = false; }
    }
    private int _branchLookupsForCheck;
    private async Task<string> ReadBranch(string directory)
    {
        _branchLookupsForCheck++;
        try
        {
            using var process = new System.Diagnostics.Process { StartInfo = new() { FileName = "git", WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            process.StartInfo.ArgumentList.Add("rev-parse"); process.StartInfo.ArgumentList.Add("--abbrev-ref"); process.StartInfo.ArgumentList.Add("HEAD");
            process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false); }
            catch(TimeoutException) { process.Kill(); await process.WaitForExitAsync().ConfigureAwait(false); return ""; }
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? (await output.ConfigureAwait(false)).Trim() : "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return ""; }
    }
    private void NewWorkspaceClick(object _1, RoutedEventArgs _2) => NewWorkspace();
    private void SplitRightClick(object _1, RoutedEventArgs _2) => Split(true);
    private void SplitDownClick(object _1, RoutedEventArgs _2) => Split(false);
    private void ZoomClick(object _1, RoutedEventArgs _2) { if ((ActivePaneId??_selected?.FocusedPaneId) is not { } id) return; _zoomed = !_zoomed; RebuildPanes(id); }
    private void SettingsClick(object sender, RoutedEventArgs e) => UiTasks.Observe(SettingsClickAsync(sender, e));
    private async Task SettingsClickAsync(object sender, RoutedEventArgs e)
    {
        var shell = new TextBox { Header = "Default shell command", Text = _saved.Settings.Shell, PlaceholderText = "pwsh.exe -NoLogo" };
        var directory = new TextBox { Header = "Starting directory", Text = _saved.Settings.StartDirectory };
        var font = new TextBox { Header = "Monospace font", Text = _saved.Settings.FontFamily,
            Description = $"Bundled: {TerminalFont.BundledFamily} (includes Starship icons)." };
        var size = new NumberBox { Header = "Font size", Minimum = 9, Maximum = 32, Value = _saved.Settings.FontSize, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var clipboardWrite = new ToggleSwitch { Header = "Allow applications to write the clipboard", IsOn = _saved.Settings.AllowClipboardWrite };
        var clipboardRead = new ToggleSwitch { Header = "Allow applications to read the clipboard", IsOn = _saved.Settings.AllowClipboardRead };
        var imageFiles = new ToggleSwitch { Header = "Allow applications to display local image files", IsOn = _saved.Settings.AllowImageFiles };
        var ligatures = new ToggleSwitch { Header = "Font ligatures", IsOn = _saved.Settings.Ligatures };
        var framePacing = new ToggleSwitch { Header = "Limit output redraws to the display refresh rate", IsOn = _saved.Settings.FramePacing };
        ToolTipService.SetToolTip(framePacing, "Uses less CPU during heavy output. Responses to typing are never delayed.");
        var notifications = new ToggleSwitch { Header = "Desktop notifications for background terminals", IsOn = _saved.Settings.DesktopNotifications };
        var bindings = new TextBox { Header = "Keyboard shortcuts", AcceptsReturn = true, MaxHeight = 180,
            Description = "action = shortcut, one per line. Leave a shortcut empty to disable it.",
            Text = string.Join(Environment.NewLine, Keybindings.Defaults.Select(item => item.Key + " = " + _saved.Settings.Keybindings.GetValueOrDefault(item.Key, item.Value))) };
        var error = new TextBlock { Foreground = new SolidColorBrush(Colors.Salmon), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
        foreach (var (control, property) in new (Control, string)[] { (shell,"Shell"), (directory,"StartDirectory"), (font,"FontFamily"), (size,"FontSize"),
            (ligatures,"Ligatures"), (framePacing,"FramePacing"), (notifications,"DesktopNotifications"), (clipboardWrite,"AllowClipboardWrite"),
            (clipboardRead,"AllowClipboardRead"), (imageFiles,"AllowImageFiles"), (bindings,"Keybindings") }) control.IsEnabled = !LuaOwns(property);
        panel.Children.Add(new TextBlock { Text="Defaults apply to new shells. Appearance and input settings also apply to current terminals.",FontSize=12,Foreground=Chrome.Brush(Chrome.Secondary),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4) });
        if (_luaConfig.Overrides.Length>0) panel.Children.Add(new TextBlock { Text="Some settings are managed by your Lua configuration.",FontSize=12,Foreground=Chrome.Brush(Chrome.Accent),TextWrapping=TextWrapping.Wrap });
        Section("Shells and sessions",true,shell,directory);
        Section("Appearance",false,font,size,ligatures,framePacing);
        Section("Notifications and clipboard",false,notifications,clipboardWrite,clipboardRead,imageFiles);
        Section("Keyboard shortcuts",false,bindings);
        Section("Lua configuration",false,LuaSettingsControls());
        panel.Children.Add(error);
        void Section(string title,bool expanded,params UIElement[] elements)
        {
            var content=new StackPanel { Spacing=14,Padding=new Thickness(0,4,0,4) };
            foreach (var element in elements) content.Children.Add(element);
            panel.Children.Add(new Expander { Header=title,IsExpanded=expanded,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=content });
        }
        Dictionary<string, string>? parsedBindings = null;
        var dialog = new ContentDialog { Title = "Settings", Content = new ScrollViewer { Content = panel, MaxHeight = 520 }, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(shell.Text) || string.IsNullOrWhiteSpace(font.Text) || !Directory.Exists(Environment.ExpandEnvironmentVariables(directory.Text)))
            { error.Text = "Enter a shell command, a font, and an existing directory."; args.Cancel = true; }
            try { parsedBindings = Keybindings.ParseOverrides(bindings.Text); }
            catch (FormatException ex) { error.Text = ex.Message; args.Cancel = true; }
        };
        if (await ShowDialog(dialog) != ContentDialogResult.Primary) return;
        _saved.Settings.Shell = shell.Text.Trim(); _saved.Settings.StartDirectory = Environment.ExpandEnvironmentVariables(directory.Text);
        _saved.Settings.FontFamily = font.Text.Trim(); _saved.Settings.FontSize = (float)(double.IsFinite(size.Value) ? Math.Clamp(size.Value, 9, 32) : 14);
        _saved.Settings.AllowClipboardWrite = clipboardWrite.IsOn; _saved.Settings.AllowClipboardRead = clipboardRead.IsOn;
        _saved.Settings.AllowImageFiles = imageFiles.IsOn; _saved.Settings.FramePacing = framePacing.IsOn;
        _saved.Settings.Ligatures = ligatures.IsOn; _saved.Settings.Keybindings = parsedBindings ?? [];
        _saved.Settings.DesktopNotifications = notifications.IsOn;
        foreach (var workspace in _saved.Workspaces) foreach (var pane in workspace.AllPanes()) pane.Session?.ApplySettings(_saved.Settings.WithTerminalTheme(workspace.Theme));
        foreach (var view in _views) view.ApplySettings(); UpdateStatusBar(); Save();
    }
    private void PaletteClick(object _1, RoutedEventArgs _2) => UiTasks.Observe(ShowCommandPalette());
    private async Task ShowCommandPalette()
    {
            if (_dialogOpen) return;
        var search = new TextBox { PlaceholderText = "Type a command or workspace…" };
        var list = new ListView { MaxHeight = 330, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
        string ShortcutLabel(string label, string action) => label + "    " + _saved.Settings.Keybindings.GetValueOrDefault(action, Keybindings.Defaults[action]);
        var commands = new List<(string Name, Action Action)> {
            (ShortcutLabel("New session", "newWorkspace"), () => NewWorkspace()),
            (ShortcutLabel("New tab", "newTab"), () => NewTab()),
            (ShortcutLabel("New browser tab", "newBrowser"), () => NewBrowser()),
            ("Split browser right", () => NewBrowser(split: true)),
            ("Split browser down", () => NewBrowser(split: true, vertical: false)),
            ("Browser profiles and cookie sharing", () => UiTasks.Observe(BrowserSettingsDialog())),
            ("Close current tab", () => { if (_selected is { } s) RemoveTab(s,s.ActiveTab); }),
            ("Quit toiqtty and stop all sessions", () => UiTasks.Observe(QuitSessions())),
            (ShortcutLabel("Split right", "splitRight"), () => Split(true)),
            (ShortcutLabel("Split down", "splitDown"), () => Split(false)),
            (ShortcutLabel("Zoom focused pane", "zoom"), () => ZoomClick(this, new())),
            (ShortcutLabel("Close focused pane", "closePane"), () => { if (ActivePaneId is { } id) ClosePane(id); }),
            (ShortcutLabel("Move focused pane…", "movePane"), () => { if (ActivePaneId is { } id) UiTasks.Observe(MovePaneDialog(id)); }),
            (ShortcutLabel("Open current folder in Windows Explorer", "openDirectory"), () => _ = OpenDirectory()),
            (ShortcutLabel("Toggle sidebar", "sidebar"), ToggleSidebar),
            ("Settings", () => SettingsClick(this, new())) };
        commands.AddRange([("Next wallpaper",()=>CycleWallpaper(1)),("Previous wallpaper",()=>CycleWallpaper(-1)),
            ("Random wallpaper",()=>CycleWallpaper(1,true)),("Toggle wallpaper focus mode",ToggleWallpaper),("Choose wallpaper",()=>UiTasks.Observe(ChooseWallpaper()))]);
        commands.Add(("Session theme…", () => { if (_selected is { } workspace) UiTasks.Observe(EditSessionTheme(workspace)); }));
        commands.Add(("Edit Lua configuration", () => UiTasks.Observe(OpenLuaConfiguration())));
        commands.Add((ShortcutLabel("Switch session", "switchSession"), () => UiTasks.Observe(SwitchSession())));
        commands.Add(("Toggle fullscreen", ToggleFullscreen));
        commands.Add(("Reload Lua configuration", () => UiTasks.Observe(ReloadLua())));
        commands.Add(("Lua configuration status", () => UiTasks.Observe(ShowLuaStatus())));
        commands.Add(("Create configured startup sessions", CreateConfiguredSessions));
        commands.Add((ShortcutLabel("Find in terminal", "find"), () => FindClick(this, new())));
        commands.Add(("Terminal activity and notifications", () => ActivityClick()));
        commands.AddRange(_saved.Workspaces.Select(w => ("Switch to " + w.Name, (Action)(() => SelectWorkspace(w)))));
        var empty = new TextBlock { Text="No matching commands",Foreground=Chrome.Brush(Chrome.Muted),Margin=new Thickness(8,12,8,12),Visibility=Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 }; panel.Children.Add(search); panel.Children.Add(list); panel.Children.Add(empty);
        var dialog = new ContentDialog { Title = "Command palette", Content = panel, CloseButtonText = "Close" };
        Action? selected = null;
        void Filter()
        {
            list.Items.Clear();
            foreach (var command in commands.Where(c=>c.Name.Contains(search.Text,StringComparison.OrdinalIgnoreCase)))
            {
                int split=command.Name.LastIndexOf("    ",StringComparison.Ordinal);
                string label=split<0?command.Name:command.Name[..split], shortcut=split<0?"":command.Name[(split+4)..];
                var row=new Grid { ColumnSpacing=18,Padding=new Thickness(4,7,4,7) };
                row.ColumnDefinitions.Add(new() { Width=new GridLength(1,GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
                row.Children.Add(new TextBlock { Text=label,FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis });
                var hint=new TextBlock { Text=shortcut,FontSize=11,Foreground=Chrome.Brush(Chrome.Muted),VerticalAlignment=VerticalAlignment.Center }; Grid.SetColumn(hint,1); row.Children.Add(hint);
                var item=new ListViewItem { Content=row,Tag=command.Action,HorizontalContentAlignment=HorizontalAlignment.Stretch };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item,command.Name);
                list.Items.Add(item);
            }
            empty.Visibility=list.Items.Count==0?Visibility.Visible:Visibility.Collapsed;
            if (list.Items.Count>0) list.SelectedIndex=0;
        }
        void Execute() { if (list.SelectedItem is ListViewItem { Tag: Action action }) { selected = action; dialog.Hide(); } }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { Execute(); args.Handled = true; } else if (args.Key is VirtualKey.Down or VirtualKey.Up) { list.SelectedIndex = Math.Clamp(list.SelectedIndex + (args.Key == VirtualKey.Down ? 1 : -1), 0, Math.Max(0, list.Items.Count - 1)); args.Handled = true; } };
        list.ItemClick += (_, args) => { if (args.ClickedItem is ListViewItem { Tag: Action action }) { selected = action; dialog.Hide(); } };
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        Filter(); await ShowDialog(dialog); selected?.Invoke();
    }
    private void ToggleSidebar()
    {
        var show = Sidebar.Visibility != Visibility.Visible;
        Sidebar.Visibility = SidebarHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = TitleSidebarColumn.Width = new GridLength(show ? SidebarWidth : 0);
        UpdateChrome(); QueueTitleRegions();
    }
    private void FindClick(object _1, RoutedEventArgs _2) => UiTasks.Observe(ShowFind());
    private async Task ShowFind()
    {
        if (_dialogOpen || _selected is null || _focused is null) return;
        var session = _selected.Root.Leaves().FirstOrDefault(p => p.Id == _focused.PaneId)?.Session;
        if (session is null) return;
        var input = new TextBox { PlaceholderText = "Search terminal history", MinWidth = 380 };
        var count = new TextBlock { Text = "Type to search", FontSize = 12 };
        var next = new Button { Content = "Older ↑" }; var previous = new Button { Content = "Newer ↓" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(next); buttons.Children.Add(previous);
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(input); panel.Children.Add(count); panel.Children.Add(buttons);
        int generation = 0;
        async Task Search(int direction)
        {
            int current = ++generation; string text = input.Text;
            if (direction == 0) await Task.Delay(180);
            if (current != generation) return;
            try
            {
                var result = await session.FindAsync(text,direction);
                if (current != generation) return;
                count.Text = result.Count == 0 ? "No matches" : $"{result.Index + 1} of {result.Count} matches";
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            { if (current == generation) count.Text = "Search unavailable: " + ex.Message; }
        }
        input.TextChanged += (_, _) => UiTasks.Observe(Search(0));
        input.KeyDown += (_, args) => UiTasks.Observe(async () => { if (args.Key == VirtualKey.Enter) { args.Handled = true; await Search(TerminalView.Down(VirtualKey.Shift) ? -1 : 1); } });
        next.Click += (_, _) => UiTasks.Observe(Search(1)); previous.Click += (_, _) => UiTasks.Observe(Search(-1));
        var dialog = new ContentDialog { Title = "Find in terminal", Content = panel, CloseButtonText = "Done" };
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        await ShowDialog(dialog);
        generation++;
        try { await session.FindAsync("",0); session.Select(-1,-1); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { System.Diagnostics.Debug.WriteLine(ex); }
    }
    private void ActivityClick() => UiTasks.Observe(ActivityClickAsync());
    private async Task ActivityClickAsync()
    {
        if (_dialogOpen || _selected is null || _focused is null) return;
        var session = _selected.Root.Leaves().FirstOrDefault(p => p.Id == _focused.PaneId)?.Session;
        if (session is null) return;
        TerminalStatusSnapshot status;
        try { status = await Task.Run(() => session.Status); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        { SetStatusMessage("Terminal activity is unavailable: " + ex.Message, true); return; }
        var lines = new List<string>();
        lines.AddRange(status.Programs.Select(item => $"{item.App} · {item.Title} · {item.Body}"));
        lines.AddRange(status.Notices.Reverse().Select(item => $"{item.Time:t} · {item.Title}\n{item.Body}"));
        lines.AddRange(status.Commands.AsEnumerable().Reverse().Select(item => $"{item.Started:t} · {(item.Finished is null ? "running" : $"exit {item.ExitCode} · {item.Duration?.TotalSeconds:0.00}s")} · {item.Text}"));
        var text = new TextBlock { Text = lines.Count == 0 ? "No terminal activity yet." : string.Join("\n\n", lines), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 540 };
        await ShowDialog(new ContentDialog { Title = "Terminal activity", Content = new ScrollViewer { Content = text, MaxHeight = 440 }, CloseButtonText = "Done" });
    }
    private void GlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dialogOpen || e.Handled || _draggedPaneId is not null) return;
        if (_focusedBrowser is not null && e.Key == VirtualKey.L && TerminalView.Down(VirtualKey.Control) && !TerminalView.Down(VirtualKey.Menu))
        { _focusedBrowser.FocusAddress(); e.Handled = true; return; }
        if (_focused is { InputHasFocus: true, IsComposing: true } || WindowsKeyboard.IsImeKey((uint)e.Key) || WindowsKeyboard.IsImeKey((uint)e.OriginalKey)) return;
        if (TerminalView.Down(VirtualKey.RightMenu) && TerminalView.Down(VirtualKey.Control)) return;
        uint modifiers = (TerminalView.Down(VirtualKey.Shift) ? 1u : 0) | (TerminalView.Down(VirtualKey.Control) ? 2u : 0)
            | (TerminalView.Down(VirtualKey.Menu) ? 4u : 0) | (TerminalView.Down(VirtualKey.LeftWindows) || TerminalView.Down(VirtualKey.RightWindows) ? 8u : 0);
        if (TryHandleActionKey((uint)e.Key, modifiers)) e.Handled = true;
    }
    private void FocusDirection(VirtualKey direction)
    {
        FrameworkElement? focused = (FrameworkElement?)_focused ?? _focusedBrowser;
        if (focused is null) return;
        Windows.Foundation.Point Center(FrameworkElement view) => view.TransformToVisual(Root).TransformPoint(new(view.ActualWidth / 2, view.ActualHeight / 2));
        var origin = Center(focused);
        var candidates = _views.Cast<FrameworkElement>().Concat(_browsers.Values.Where(b => b.Parent is not null));
        var target = candidates.Where(v => v != focused).Select(v => (View: v, Point: Center(v)))
            .Select(v => (v.View, DX: v.Point.X - origin.X, DY: v.Point.Y - origin.Y))
            .Where(v => direction switch { VirtualKey.Left => v.DX < -1, VirtualKey.Right => v.DX > 1, VirtualKey.Up => v.DY < -1, _ => v.DY > 1 })
            .OrderBy(v => direction is VirtualKey.Left or VirtualKey.Right ? Math.Abs(v.DX) + 2 * Math.Abs(v.DY) : Math.Abs(v.DY) + 2 * Math.Abs(v.DX))
            .Select(v => v.View).FirstOrDefault();
        if (target is TerminalView terminal) terminal.FocusTerminal();
        else if (target is BrowserView browser) browser.FocusBrowser();
    }
}
