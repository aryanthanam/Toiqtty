using System.Diagnostics;
using Oroterm.Hosting;
using Oroterm.Models;

internal static class ShutdownReplyChecks
{
    public static async Task Run(string executable, Action<bool, string> check)
    {
        for (int iteration = 0; iteration < 5; iteration++)
        {
            string folder = Path.GetFullPath("artifacts/performance-shutdown-" + Guid.NewGuid().ToString("N"));
            var host = SessionHostClient.ConnectOrStart(folder, executable);
            using var owner = Process.GetProcessById(host.ProcessId);
            var shells = new List<RemoteTerminalSession>();
            try
            {
                for (int i = 0; i < 4; i++) shells.Add(new RemoteTerminalSession(host, Guid.NewGuid().ToString("N"), "cmd.exe /Q /K", Environment.CurrentDirectory, new AppSettings()));
                // Captures contend with output/state cleanup during shutdown.
                var capture = host.CallAsync(new() { Operation = "capture", Pane = host.Call(new()).Panes[0], Numbers = [-1] });
                host.Shutdown();
                check(true, "shutdown round " + (iteration + 1) + " receives a complete acknowledgement before the host cancels connections");
                try { await capture; } catch (IOException) { }
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
                check(SessionHostClient.TryConnect(folder) is null, "shutdown round " + (iteration + 1) + " closes all four shells and releases its pipe");
            }
            finally
            {
                foreach (var shell in shells) shell.Detach();
                try { SessionHostClient.TryConnect(folder)?.Shutdown(); } catch (IOException) { }
            }
        }
    }
}
