namespace Toiqtty;

public partial class App
{
    private static async Task RunNotificationProbe()
    {
        string folder = Models.SessionStore.Folder;
        if (!System.IO.Path.GetFileName(folder).StartsWith("notification-probe-", StringComparison.Ordinal))
            throw new InvalidOperationException("Notification probe requires its own data directory.");
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            var received = new TaskCompletionSource<(string Workspace, string Pane)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var notifications = new Notifications.DesktopNotifications(true, (workspace, pane) => received.TrySetResult((workspace, pane)), true);
            if (!notifications.Available) throw new InvalidOperationException(notifications.UnavailableReason);
            string key = Environment.ProcessPath!.Replace('\\', '.');
            using var pathKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + key);
            string appId = pathKey?.GetValue("NotificationGUID") as string ?? throw new InvalidOperationException("Notification app registration not found.");
            using var appKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + appId);
            string clsid = appKey?.GetValue("CustomActivator") as string ?? throw new InvalidOperationException("Notification COM registration not found.");
            var probe = Notifications.DesktopNotifications.Build("hardware-workspace", "hardware-pane", "Probe", new(DateTimeOffset.Now, "Probe", ""));
            string arguments = System.Xml.Linq.XDocument.Parse(probe.Payload).Root!.Attribute("launch")!.Value;
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "ready.json"), System.Text.Json.JsonSerializer.Serialize(new { appId, clsid, arguments, processId = Environment.ProcessId }));
            bool initialPayload = notifications.ReadInitialActivation();
            var (Workspace, Pane) = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = true, initialPayload, workspace = Workspace, pane = Pane }));
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
        }
    }
}
