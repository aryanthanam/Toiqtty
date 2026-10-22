using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private string _tabsFingerprint="";
    private readonly Dictionary<string,(ListViewItem Item,Border Card,TextBlock Label,Button Close)> _tabCards=[];
    private string? _tabSelection;
    private readonly HashSet<string> _hoveredTabs=[];
    private void UpdateTabOverflow()
    {
        TabOverflowButton.Visibility=TabScroller.ScrollableWidth>1?Visibility.Visible:Visibility.Collapsed;
        QueueTitleRegions();
        ApplyLuaTabTitles();
    }
    private void TabOverflowOpening(object _1,object _2)
    {
        TabOverflowMenu.Items.Clear();
        if(_selected is not { } workspace) return;
        foreach(var tab in workspace.Tabs)
        {
            var item=new ToggleMenuFlyoutItem { Text=tab.Name,IsChecked=tab==workspace.ActiveTab };
            AutomationProperties.SetName(item,tab.Name+(tab.Root.Leaves().Any(p=>p.Attention)?", needs attention":""));
            item.Click+=(_,_)=> { if(_selected==workspace && workspace.Tabs.Contains(tab)) SelectTab(tab); };
            TabOverflowMenu.Items.Add(item);
        }
    }
    // Shells whose start failed, by pane, until Retry. BuildNode shows these instead of a view.
    private readonly Dictionary<string,string> _shellFailures=[];
    private (Task Commit,HashSet<string> Panes)? _shellLayoutCommit;
    private void EnsureShell(PaneNode node,Workspace workspace)
    {
        if (node.IsBrowser || node.Session is not null || _shellFailures.ContainsKey(node.Id)) return;
        StartupTrace.Mark("shell-connect-begin");
        if (_persistent)
        {
            // Returns at once. The session's input queue connects (single-flight, off this
            // thread), waits until a layout naming the pane is durable, then creates or
            // reattaches the shell; input typed meanwhile follows it in order. A pane restored
            // from the live host's layout is already durable there and reattaches without a save.
            var host=_connector.ConnectAsync();
            var committed=_durablePanes.Contains(node.Id)?Task.CompletedTask:ShellLayoutCommit(node,host);
            var remote=RemoteTerminalSession.Start(host,node.Id,node.Command,node.Directory,_saved.Settings.WithTerminalTheme(workspace.Theme),committed);
            node.Session=remote;
            UiTasks.Observe(ObserveShellStart(node,remote));
        }
        else node.Session=new TerminalSession(node.Command,node.Directory,_saved.Settings.WithTerminalTheme(workspace.Theme));
        StartupTrace.Mark("shell-connect-end");
        WireShell(node.Session);
    }
    /// <summary>Starts the shells of many panes at once, the selected tab's first. Panes the live
    /// host already owns (restored from its layout) reattach through one batched attach, off this
    /// thread, instead of a create each; the rest start as EnsureShell starts them.</summary>
    private void EnsureShells(IEnumerable<Workspace> workspaces,Workspace? selected,string problem)
    {
        var visible=selected?.ActiveTab.Root.Leaves().ToHashSet();
        var panes=workspaces.SelectMany(w=>w.AllPanes().Select(p=>(Pane:p,Workspace:w))).OrderBy(item=>visible?.Contains(item.Pane)==true?0:1).ToList();
        List<(PaneNode Pane,Workspace Workspace)>? attach=null;
        foreach (var item in panes)
            if (_persistent && _durablePanes.Contains(item.Pane.Id) && !item.Pane.IsBrowser && item.Pane.Session is null && !_shellFailures.ContainsKey(item.Pane.Id)) (attach??=[]).Add(item);
            else try { EnsureShell(item.Pane,item.Workspace); } catch (Exception ex) { SetStatusMessage(problem+ex.Message, problem: true); }
        if (attach is null) return;
        StartupTrace.Mark("shell-connect-begin");
        // One detached settings copy per distinct theme; the batch sends each copy once.
        var themed=new Dictionary<object,AppSettings>(); object noTheme=new();
        var batch=new (string Pane,AppSettings Settings)[attach.Count];
        for (int i=0;i<attach.Count;i++)
        {
            var workspace=attach[i].Workspace; object key=(object?)workspace.Theme??noTheme;
            if (!themed.TryGetValue(key,out var settings)) themed.Add(key,settings=RemoteTerminalSession.DetachSettings(_saved.Settings.WithTerminalTheme(workspace.Theme)));
            batch[i]=(attach[i].Pane.Id,settings);
        }
        var host=_connector.ConnectAsync();
        var attached=AttachShells(host,batch);
        for (int i=0;i<attach.Count;i++)
        {
            var (node,_)=attach[i];
            // Restored from the live host's layout: durable there, so a missing pane's create needs no save.
            var remote=RemoteTerminalSession.Attach(host,node.Id,attached,node.Command,node.Directory,batch[i].Settings,Task.CompletedTask);
            node.Session=remote;
            UiTasks.Observe(ObserveShellStart(node,remote));
            WireShell(remote);
        }
        StartupTrace.Mark("shell-connect-end");
    }
    private static async Task<Dictionary<string,ShellState>> AttachShells(Task<SessionHostClient> host,(string Pane,AppSettings Settings)[] batch)
        => await (await host.ConfigureAwait(false)).AttachAsync(batch).ConfigureAwait(false);
    private void WireShell(ITerminalSession shell)
    {
        shell.Notification+=notice=>DispatcherQueue.TryEnqueue(()=>
        {
            if (_closed || !_saved.Settings.DesktopNotifications) return;
            var owner=FindShellOwner(shell);
            if (owner is not { } current || (_windowActive && _selected==current.Workspace && _focused?.PaneId==current.Pane.Id)) return;
            _notifications?.Show(current.Workspace.Id,current.Pane.Id,current.Workspace.Name,notice);
        });
        shell.Attention+=()=>DispatcherQueue.TryEnqueue(()=>
        {
            if (_closed || FindShellOwner(shell) is not { } current) return;
            current.Pane.Attention=true;
            current.Workspace.Attention=true; RefreshSidebar(); RefreshTabs();
            SetStatusMessage($"{current.Workspace.Name} needs attention");
        });
    }
    /// <summary>The structural save a new shell waits for before its create is sent. Every shell
    /// requested in one dispatcher turn shares one save (one fsync) when its snapshot holds the pane.</summary>
    private Task ShellLayoutCommit(PaneNode node,Task<SessionHostClient> host)
    {
        if (_shellLayoutCommit is { } pending && pending.Panes.Contains(node.Id)) return pending.Commit;
        var panes=_saved.Workspaces.SelectMany(w=>w.AllPanes()).Select(p=>p.Id).ToHashSet();
        Task commit;
        try { commit=_sessionPersistence.Enqueue(_saved,false,host); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatusMessage("Could not save session: "+ex.Message, problem: true); return Task.CompletedTask; }
        ObservePersistence(commit);
        if (_shellLayoutCommit is null) DispatcherQueue.TryEnqueue(()=>_shellLayoutCommit=null);
        _shellLayoutCommit=(commit,panes);
        return commit;
    }
    private async Task ObserveShellStart(PaneNode node,RemoteTerminalSession remote)
    {
        try { await remote.Ready; }
        // Closed or detached before it was created.
        catch (OperationCanceledException) when (remote.Ready.IsCanceled) { return; }
        catch (Exception ex)
        {
            // A pane closed meanwhile has no panel to show.
            if (_closed || !ReferenceEquals(node.Session,remote) || FindShellOwner(remote) is null) return;
            ShowShellFailure(node,remote,ex);
            return;
        }
        if (_closed || !ReferenceEquals(node.Session,remote) || !remote.PendingAttention || FindShellOwner(remote) is not { } owner) return;
        // A reattached shell that rang while no window was open: mark it, as restore always has.
        node.Attention=true; owner.Workspace.Attention=true; RefreshSidebar(); RefreshTabs();
    }
    /// <summary>A shell's start failed after its view was built: swap in the failure panel.</summary>
    private void ShowShellFailure(PaneNode node,RemoteTerminalSession remote,Exception ex)
    {
        TraceTabLayout("start-error",node.Id,ex.ToString());
        if (ex is SessionHostVersionException) (Application.Current as App)?.ShowUpgradeWindow(ex.Message);
        _shellFailures[node.Id]=ex.Message;
        node.Session=null; remote.Detach();
        ForgetTerminalViews([node]);
        SetStatusMessage("Couldn’t start shell: "+ex.Message, problem: true);
        if (_selected?.ActiveTab.Root.Leaves().Contains(node)==true) RebuildPanes();
    }
    private FrameworkElement ShellFailurePanel(PaneNode node,Workspace workspace,string message)
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(28), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(panel,"ShellFailure-"+node.Id);
        panel.Children.Add(new TextBlock { Text = "Couldn’t start this shell", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = message + "\n\n" + node.Command, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        // Retry rebuilds this pane's session in place, with the same identity.
        var retry = new Button { Content = "Retry" };
        retry.Click += (_, _) => { _shellFailures.Remove(node.Id); _connector.Retry(); ForgetTabLayout(workspace.ActiveTab.Id); RebuildPanes(node.Id); };
        panel.Children.Add(retry);
        var settings = new Button { Content = "Edit shell settings" }; settings.Click += SettingsClick; panel.Children.Add(settings);
        return panel;
    }
    private (Workspace Workspace, PaneNode Pane)? FindShellOwner(ITerminalSession shell)
    {
        foreach (var owner in _saved.Workspaces)
            foreach (var pane in owner.AllPanes())
                if (pane.Session == shell) return (owner, pane);
        return null;
    }
    private void NewTabClick(object _1,RoutedEventArgs _2)=>NewTab();
    private void NewTab(string? command=null,string? name=null,string? directory=null)
    {
        if (_selected is null) { NewWorkspace(command,name,directory); return; }
        var root=new PaneNode { Command=command??_saved.Settings.Shell,
            Directory=directory??_selected.Root.Leaves().FirstOrDefault(p=>p.Id==_focused?.PaneId)?.Session?.WorkingDirectory??_saved.Settings.StartDirectory };
        if (SessionLayout.AddTab(_saved,_selected,name??$"Shell {_selected.Tabs.Count+1}",root,out string error) is not { } tab)
        { SetStatusMessage(error, problem: true); return; }
        SelectTab(tab); Save();
    }
    private void SelectTab(TerminalTab tab)
    {
        if (_selected is null || !_selected.Tabs.Contains(tab)) return;
        _selected.ActiveTabId=tab.Id; _zoomed=false;
        RefreshTabs(); RebuildPanes(tab.FocusedPaneId); RefreshSidebar(); SaveNavigation();
    }
    private void CycleTab(int direction)
    {
        if (_selected is null) return;
        int index=_selected.Tabs.IndexOf(_selected.ActiveTab);
        SelectTab(_selected.Tabs[(index+direction+_selected.Tabs.Count)%_selected.Tabs.Count]);
    }
    private void RefreshTabs()
    {
        if (_draggingTabs || _draggedPaneId is not null) return;
        string fingerprint=_selected?.Id+":"+_selected?.Theme?.Key+":"+string.Join("|",_selected?.Tabs.Select(t=>t.Id+":"+t.Name+":"+t.Root.Leaves().Any(p=>p.Attention)+":"+t.Root.Leaves().Count()+":"+t.Root.Leaves().All(p=>p.IsBrowser))??[]);
        _refreshingTabs=true;
        try
        {
            if(_tabsFingerprint!=fingerprint) { _tabsFingerprint=fingerprint; RebuildTabStrip(); _tabSelection=null; }
            SyncTabSelection();
        }
        finally { _refreshingTabs=false; }
        QueueTitleRegions();
        DispatcherQueue.TryEnqueue(UpdateTabOverflow);
    }
    private void SyncTabSelection()
    {
        string? id=_selected?.ActiveTab.Id;
        if(_tabSelection==id) return;
        string? previous=_tabSelection; _tabSelection=id;
        var colors = Controls.Chrome.For(ThemeFor(_selected));
        foreach(var key in new[] { previous,id }.OfType<string>().Distinct())
        {
            if(!_tabCards.TryGetValue(key,out var card)) continue;
            var pair=new KeyValuePair<string,(ListViewItem Item,Border Card,TextBlock Label,Button Close)>(key,card);
            bool active=key==id;
            pair.Value.Card.BorderBrush=Controls.Chrome.Brush(active?colors.Accent:0x00000000);
            pair.Value.Card.Background=Controls.Chrome.Brush(active?colors.Selected:colors.Background);
            pair.Value.Label.Foreground=Controls.Chrome.Brush(active?colors.Text:colors.Secondary);
            pair.Value.Label.FontWeight=active?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal;
            pair.Value.Close.Opacity=active || _hoveredTabs.Contains(pair.Key) || pair.Value.Close.FocusState!=FocusState.Unfocused?1:0;
            AutomationProperties.SetItemStatus(pair.Value.Item,active?"Selected":"");
            if(active)
            {
                TabStrip.SelectedItem=pair.Value.Item;
                var item=pair.Value.Item;
                DispatcherQueue.TryEnqueue(()=> { if(!_closed && !_draggingTabs && _selected?.ActiveTab.Id==id && item.IsLoaded) item.StartBringIntoView(); });
            }
        }
    }
    private void RebuildTabStrip()
    {
        TabStrip.Items.Clear(); _tabCards.Clear(); _hoveredTabs.Clear();
        if (_selected is null) return;
        var session=_selected;
        var colors = Controls.Chrome.For(ThemeFor(session));
        foreach (var tab in session.Tabs)
        {
            bool active=tab==session.ActiveTab;
            var leaves=tab.Root.Leaves().ToList();
            var content=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
            if (leaves.All(p=>p.IsBrowser)) content.Children.Add(new FontIcon { Glyph="\uE774",FontSize=11,Foreground=Controls.Chrome.Brush(Controls.Chrome.Muted) });
            var label=new TextBlock { Text=tab.Name,FontSize=12,MaxWidth=145,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Controls.Chrome.Brush(active?colors.Text:colors.Secondary),
                FontWeight=active?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal };
            content.Children.Add(label);
            if (leaves.Any(p=>p.Attention)) content.Children.Add(new Border { Width=5,Height=5,CornerRadius=new CornerRadius(3),Background=Controls.Chrome.Brush(colors.Accent),VerticalAlignment=VerticalAlignment.Center });
            var row=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center };
            var select=new ListViewItem { Tag=tab };
            AutomationProperties.SetName(select,"Tab "+tab.Name+(leaves.Any(p=>p.Attention)?", needs attention":"")); AutomationProperties.SetAutomationId(select,"Tab-"+tab.Id);
            AutomationProperties.SetItemStatus(select,active?"Selected":"");
            AutomationProperties.SetHelpText(select,leaves.Count+" pane"+(leaves.Count==1?"":"s"));
            ToolTipService.SetToolTip(select,tab.Name+" · "+leaves.Count+" pane"+(leaves.Count==1?"":"s"));
            var menu=new MenuFlyout(); var rename=new MenuFlyoutItem { Text="Rename tab" };
            rename.Click+=(_,_) => UiTasks.Observe(async () =>
            {
                var input=new TextBox { Text=tab.Name,MaxLength=60 };
                if (await ShowDialog(new ContentDialog { Title="Rename tab",Content=input,PrimaryButtonText="Save",CloseButtonText="Cancel" })==ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
                { tab.Name=input.Text.Trim(); RefreshTabs(); Save(); }
            });
            menu.Items.Add(rename);
            var move=new MenuFlyoutItem { Text="Move focused pane…" };
            move.Click+=(_,_)=> { var id=tab.FocusedPaneId??tab.Root.Leaves().First().Id; UiTasks.Observe(MovePaneDialog(id)); };
            var openDirectory = new MenuFlyoutItem { Text = "Open in Windows Explorer" };
            openDirectory.Click += (_, _) => UiTasks.Observe(OpenDirectory(session, tab));
            menu.Items.Add(move); menu.Items.Add(openDirectory); AddTabOrderMenu(menu, session, tab); select.ContextFlyout=menu;
            var close = new Button
            {
                Content = new FontIcon { Glyph = "\uE8BB", FontSize = 8 },
                Style = (Style)Root.Resources["ToolButton"],
                Width = 23,
                MinWidth = 23,
                Height = 25,
                Padding = new Thickness(0),
                Foreground = Controls.Chrome.Brush(colors.Text)
            };
            AutomationProperties.SetName(close,"Close tab "+tab.Name); close.Click+=(_,_)=>RemoveTab(session,tab);
            ToolTipService.SetToolTip(close,"Close tab "+tab.Name);
            close.Opacity=active?1:0;
            select.PointerEntered+=(_,_)=> { _hoveredTabs.Add(tab.Id); close.Opacity=1; };
            select.PointerExited+=(_,_)=> { _hoveredTabs.Remove(tab.Id); close.Opacity=tab==_selected?.ActiveTab || close.FocusState!=FocusState.Unfocused?1:0; };
            close.GotFocus+=(_,_)=>close.Opacity=1;
            close.LostFocus+=(_,_)=>close.Opacity=tab==_selected?.ActiveTab || _hoveredTabs.Contains(tab.Id)?1:0;
            content.Margin=new Thickness(10,3,5,3); row.Children.Add(content); row.Children.Add(close);
            var card=new Border { Child=row,BorderThickness=new Thickness(0,0,0,1),
                Background=Controls.Chrome.Brush(active?colors.Selected:colors.Background),
                BorderBrush=Controls.Chrome.Brush(active?colors.Accent:0x00000000) };
            select.Content=card; TabStrip.Items.Add(select);
            _tabCards.Add(tab.Id,(select,card,label,close));
            WirePaneDestination(select,()=>new(session.Id,tab.Id),"Split into "+tab.Name);
            if (active) TabStrip.SelectedItem=select;
            if (active)
            {
                bool brought=false;
                void BringActiveTabIntoView()
                {
                    if (brought || _closed || _draggingTabs || !card.IsLoaded || _selected?.ActiveTab!=tab || !TabStrip.Items.Contains(select)) return;
                    brought=true; card.StartBringIntoView();
                }
                card.Loaded+=(_,_)=>BringActiveTabIntoView();
                DispatcherQueue.TryEnqueue(BringActiveTabIntoView);
            }
        }
    }
    private void RemoveTab(Workspace session,TerminalTab tab)
    {
        if (!session.Tabs.Contains(tab)) return;
        bool selected=_selected==session && session.ActiveTab==tab;
        if (_selected==session && (selected || session.Tabs.Count==1)) SuspendActiveLayout();
        if (SessionLayout.RemoveTab(_saved,session,tab) is { } removal) ApplyRemoval(session,removal,selected);
    }
    private void ShowEmptySessions()
    {
        DetachViews(); PruneTerminalViews(); TabStrip.Items.Clear(); _tabsFingerprint="";
        Title="toiqtty";
        SetStatusMessage("Ready for a new session");
        var panel=new StackPanel { Spacing=10,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text="No sessions",FontSize=14,Foreground=Controls.Chrome.Brush(Controls.Chrome.Secondary) });
        var create=new Button { Content="+ New session",Style=(Style)Root.Resources["ToolButton"],HorizontalAlignment=HorizontalAlignment.Left }; create.Click+=(_,_)=>NewWorkspace(); panel.Children.Add(create);
        string shortcut=Shortcut("newWorkspace");
        if (shortcut.Length>0) panel.Children.Add(new TextBlock { Text=shortcut,FontSize=11,Foreground=Controls.Chrome.Brush(Controls.Chrome.Muted) });
        PaneHost.Children.Add(panel); RefreshSidebar();
    }
    private async Task QuitSessions()
    {
        if (await ShowDialog(new ContentDialog { Title="Stop all sessions and quit?",Content="Every running shell and command will be stopped. Closing the window normally keeps them running.",PrimaryButtonText="Stop sessions and quit",CloseButtonText="Cancel" })!=ContentDialogResult.Primary) return;
        Save();
        await ObserveSave(_sessionPersistence.FlushAsync());
        await SessionLifetimeCoordinator.DetachAsync(_saved.Workspaces.SelectMany(w=>w.AllPanes()));
        try { if(_host is not null) await _host.CallAsync(HostRequests.Shutdown()); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { SetStatusMessage(ex.Message, problem: true); return; }
        _connector.Reset();
        Close();
    }
}
