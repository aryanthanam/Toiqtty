using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Toiqtty;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;
    private Window? _upgradeWindow;
    private Models.InstanceCoordinator? _instance;
    private Notifications.DesktopNotifications? _notifications;
    private (string Workspace, string Pane)? _pendingNotification;
    private readonly TaskCompletionSource<(string Workspace, string Pane)> _notificationActivation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _notificationGate = new();

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        Models.StartupTrace.Mark("app-constructor");
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Models.SessionStore.Folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(Models.SessionStore.Folder, "crash.log"), DateTimeOffset.Now + " " + args.Exception + Environment.NewLine);
            }
            catch { /* Preserve the original exception if logging fails. */ }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Models.StartupTrace.Mark("on-launched");
        if (Diagnostics.WindowDiagnosticOptions.Unavailable() is { } unavailable)
        {
            // Validation scripts look for crash.log when no report appears.
            try { System.IO.Directory.CreateDirectory(Models.SessionStore.Folder); System.IO.File.WriteAllText(System.IO.Path.Combine(Models.SessionStore.Folder, "crash.log"), unavailable); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
            Console.Error.WriteLine(unavailable); Environment.ExitCode = 3; Exit(); return;
        }
#if DIAGNOSTICS
        if (Environment.GetCommandLineArgs().Contains("--notification-probe"))
        {
            await RunNotificationProbe(); Exit(); return;
        }
#endif
        var diagnostics=Diagnostics.WindowDiagnosticOptions.Read();
        if(diagnostics.Ui && Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_DEBUG")=="1")
            Microsoft.Graphics.Canvas.CanvasDevice.DebugLevel=Microsoft.Graphics.Canvas.CanvasDebugLevel.Warning;
        bool smoke=diagnostics.Ui, startupProbe=diagnostics.Startup, hardware=diagnostics.Hardware;
        _instance = Models.InstanceCoordinator.TryAcquire(Models.SessionStore.Folder);
        if (_instance is null)
        {
            // A COM-launched process can race an already running instance.
            // Receive the toast target before forwarding and exiting.
            if (Environment.GetCommandLineArgs().Any(arg => arg.Contains("AppNotificationActivated", StringComparison.Ordinal)))
            {
                using var notifications = new Notifications.DesktopNotifications(true, DispatcherNotification);
                notifications.ReadInitialActivation();
                try
                {
                    var (Workspace, Pane) = await _notificationActivation.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await Models.InstanceCoordinator.ActivateExisting(Models.SessionStore.Folder, Workspace, Pane);
                }
                catch (TimeoutException) { await Models.InstanceCoordinator.ActivateExisting(Models.SessionStore.Folder); }
            }
            else await Models.InstanceCoordinator.ActivateExisting(Models.SessionStore.Folder);
            Exit(); return;
        }
        var startupGraphics = new StartupGraphics();
        Models.StartupTrace.Mark("host-probe-begin");
        Hosting.SessionHostClient? runningHost;
        try { runningHost = await Task.Run(() => Hosting.SessionHostClient.TryConnect(Models.SessionStore.Folder)); }
        catch (Hosting.SessionHostVersionException ex)
        {
            startupGraphics.Dispose();
            var panel = new StackPanel { Spacing = 20, Margin = new Thickness(28), RequestedTheme = ElementTheme.Dark };
            panel.Children.Add(new TextBlock { Text = "Finish the toiqtty upgrade", FontSize = 22 });
            panel.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
            var close = new Button { Content = "Close" }; panel.Children.Add(close);
            _upgradeWindow = new Window { Title = "toiqtty update", Content = new Border {
                RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 25, 28, 34)),
                Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            } };
            _upgradeWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 420));
            close.Click += (_, _) => _upgradeWindow.Close();
            _instance.Activated += () => _upgradeWindow.DispatcherQueue.TryEnqueue(_upgradeWindow.Activate);
            _upgradeWindow.Closed += (_, _) => _instance.Dispose();
            _upgradeWindow.Activate(); return;
        }
        Models.StartupTrace.Mark("host-probe-end");
        bool usesHost=diagnostics.Persistent;
        var saved = Models.SessionStartupPolicy.Select(runningHost?.Layout ?? Models.SessionStore.Load(),
            runningHost is { Panes.Length: > 0 });
        bool needsShell = saved.Workspaces.Count == 0 || saved.Workspaces.SelectMany(w => w.AllPanes()).Any(p => !p.IsBrowser);
        // Lua may replace a cold layout with browser-only sessions. Decide after
        // evaluating it rather than starting an unnecessary shell host.
        var startupLua = diagnostics.LuaEnabled ? new StartupLuaEvaluation(saved.Settings) : null;
        Task<Hosting.SessionHostClient?>? startingHost = usesHost && runningHost is null
            ? Task.Run(async () =>
            {
                bool launchShell = needsShell;
                if (startupLua is not null)
                {
                    var config = await startupLua.Result.ConfigureAwait(false);
                    if (config.Error is null && config.StartupSessions.Count > 0)
                        launchShell = config.StartupSessions.SelectMany(w => w.AllPanes()).Any(p => !p.IsBrowser);
                }
                if (!launchShell || startupLua?.IsDisposed == true) return null;
                Models.StartupTrace.Mark("host-start-begin");
                var host = Hosting.SessionHostClient.Start(Models.SessionStore.Folder);
                Models.StartupTrace.Mark("host-start-end");
                return host;
            }) : null;
        if (startingHost is not null)
            _ = startingHost.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        MainWindow mainWindow;
        try
        {
            Models.StartupTrace.Mark("notifications-begin");
            _notifications = new(!smoke || Environment.GetEnvironmentVariable("TOIQTTY_NOTIFICATION_SMOKE") == "1", DispatcherNotification, smoke || hardware || startupProbe);
            _notifications.ReadInitialActivation();
            Models.StartupTrace.Mark("notifications-end");
            Models.StartupTrace.Mark("window-create-begin");
            mainWindow = new MainWindow(_notifications, runningHost, startingHost, saved, startupLua, startupGraphics);
        }
        catch { startupLua?.Dispose(); startupGraphics.Dispose(); throw; }
        Models.StartupTrace.Mark("window-create-end");
        lock (_notificationGate) { _window = mainWindow; }
        _instance.NavigationRequested += DispatcherNotification;
        _instance.Activated += () => _window.DispatcherQueue.TryEnqueue(() =>
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            ShowWindowAsync(handle, 9); SetForegroundWindow(handle); _window.Activate();
        });
        _window.Closed += (_, _) => { _instance.Dispose(); _notifications.Dispose(); };
        _window.Activate();
        Models.StartupTrace.Mark("window-activated");
        lock (_notificationGate)
        {
            if (_pendingNotification is { } pending) _window.NavigateNotification(pending.Workspace, pending.Pane);
            _pendingNotification = null;
        }
    }
    private void DispatcherNotification(string workspace, string pane)
    {
        _notificationActivation.TrySetResult((workspace, pane));
        MainWindow? window;
        lock (_notificationGate)
        {
            window = _window;
            if (window is null) { _pendingNotification = (workspace, pane); return; }
        }
        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.NavigateNotification(workspace, pane);
#if DIAGNOSTICS
            window.RecordHardwareNotification(workspace, pane);
#endif
        });
    }
    [System.Runtime.InteropServices.DllImport("user32")] private static extern bool ShowWindowAsync(nint window, int command);
    [System.Runtime.InteropServices.DllImport("user32")] private static extern bool SetForegroundWindow(nint window);
}
