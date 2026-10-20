using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class NotificationActivationChecks
{
    // Windows SDK NotificationActivationCallback.idl; this exercises the real
    // OS COM server without UI injection or touching the user's notification IDs.
    [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationActivationCallback
    {
        void Activate([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, nint input, uint count);
    }
    public static async Task Run(string executable, Action<bool, string> check)
    {
        string target = Path.GetFullPath(executable);
        foreach (var running in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (running)
                if (string.Equals(running.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Notification probes require an unused test executable. The running application will not be stopped.");
        }
        foreach (bool cold in new[] { false, true })
        {
            string folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "notification-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory };
            start.ArgumentList.Add("--notification-probe");
            if (cold) start.ArgumentList.Add("----AppNotificationActivated:");
            start.Environment["TOIQTTY_DATA_DIR"] = folder;
            using var process = Process.Start(start)!;
            try
            {
                var until = DateTime.UtcNow.AddSeconds(15);
                string readyPath = Path.Combine(folder, "ready.json"), resultPath = Path.Combine(folder, "result.json");
                while (!File.Exists(readyPath) && !File.Exists(resultPath) && DateTime.UtcNow < until) await Task.Delay(30);
                if (!File.Exists(readyPath)) throw new InvalidOperationException(File.Exists(resultPath) ? File.ReadAllText(resultPath) : "Notification probe registration timed out.");
                using var ready = JsonDocument.Parse(File.ReadAllText(readyPath));
                Guid clsid = Guid.Parse(ready.RootElement.GetProperty("clsid").GetString()!);
                object callback = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true)!)!;
                try { ((INotificationActivationCallback)callback).Activate(ready.RootElement.GetProperty("appId").GetString()!, ready.RootElement.GetProperty("arguments").GetString()!, 0, 0); }
                finally { Marshal.ReleaseComObject(callback); }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
                check(result.RootElement.GetProperty("passed").GetBoolean()
                    && result.RootElement.GetProperty("workspace").GetString() == "hardware-workspace"
                    && result.RootElement.GetProperty("pane").GetString() == "hardware-pane"
                    && result.RootElement.GetProperty("initialPayload").GetBoolean() == cold,
                    cold ? "real COM cold-launch payload is retrieved through Windows App Lifecycle" : "real COM warm activation delivers the workspace and pane callback");
                Console.WriteLine("Notification COM evidence: " + folder);
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
    }
}
