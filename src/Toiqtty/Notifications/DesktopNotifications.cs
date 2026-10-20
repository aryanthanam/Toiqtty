using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Toiqtty.Terminal;

namespace Toiqtty.Notifications;

/// <summary>Windows delivery adapter for notices already parsed by Ghostty.</summary>
internal sealed class DesktopNotifications : IDisposable
{
    private bool _registered;
    private DateTimeOffset _lastShown;
    private readonly Action<string, string>? _invoked;
    private readonly bool _isolatedTest;
    public string? UnavailableReason { get; private set; }
    public bool Available => _registered;

    public DesktopNotifications(bool enabled, Action<string, string>? invoked = null, bool isolatedTest = false)
    {
        _invoked = invoked;
        _isolatedTest = isolatedTest;
        if (!enabled) return;
        try
        {
            if (!AppNotificationManager.IsSupported()) { UnavailableReason = "Windows notifications are unavailable."; return; }
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register("toiqtty", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")));
            _registered = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            UnavailableReason = ex.Message;
        }
    }

    internal static AppNotification Build(string workspace, string pane, string name, TerminalNotice notice) =>
        new AppNotificationBuilder().AddArgument("workspace", workspace).AddArgument("pane", pane)
            .AddText(Limit(notice.Title.Length > 0 ? notice.Title : name, 256)).AddText(Limit(notice.Body, 2048)).BuildNotification();

    private static string Limit(string text, int count) => text.Length <= count ? text : text[..count];

    // Called on the UI thread; bursty terminal output still stays in Activity.
    public bool Show(string workspace, string pane, string name, TerminalNotice notice)
    {
        if (!_registered || DateTimeOffset.UtcNow - _lastShown < TimeSpan.FromSeconds(2)) return false;
        try
        {
            var notification = Build(workspace, pane, name, notice);
            notification.Expiration = DateTimeOffset.Now.AddHours(1);
            AppNotificationManager.Default.Show(notification);
            if (notification.Id == 0) return false;
            _lastShown = DateTimeOffset.UtcNow; return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        { UnavailableReason = ex.Message; return false; }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        args.Arguments.TryGetValue("workspace", out var workspace);
        args.Arguments.TryGetValue("pane", out var pane);
        _invoked?.Invoke(workspace ?? "", pane ?? "");
    }

    internal bool ReadInitialActivation()
    {
        if (!_registered) return false;
        // On a COM cold launch the SDK retains the first payload for lifecycle
        // retrieval instead of raising NotificationInvoked. Register first.
        var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification
            && activation.Data is AppNotificationActivatedEventArgs args)
        { OnInvoked(AppNotificationManager.Default, args); return true; }
        return false;
    }

    public void Dispose()
    {
        if (!_registered) return;
        _registered = false;
        AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
        try
        {
            if (_isolatedTest) AppNotificationManager.Default.UnregisterAll();
            else AppNotificationManager.Default.Unregister();
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
