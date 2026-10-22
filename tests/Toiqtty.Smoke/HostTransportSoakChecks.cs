using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class HostTransportSoakChecks
{
    public static async Task Run(string hostExecutable, int seconds, string reportPath, Action<bool, string> check)
    {
        if (seconds is < 60 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        string folder = Path.GetFullPath("artifacts/performance-host-soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string client = Environment.ProcessPath!;
        var layout = new SavedSession(); var workspace = new Workspace { Name = "Host transport soak", Tabs = [] };
        layout.Workspaces.Add(workspace);
        for (int i = 0; i < 4; i++) workspace.Tabs.Add(new TerminalTab { Root = new PaneNode {
            Command = $"\"{client}\" " + (i < 2 ? "--performance-client" : "--persistent-shell-client"), Directory = Environment.CurrentDirectory } });
        var host = SessionHostClient.ConnectOrStart(folder, hostExecutable);
        var sessions = new List<RemoteTerminalSession>();
        var cells = new TerminalCell[4][]; var samples = new List<object>(); var memory = new List<long>();
        var timer = Stopwatch.StartNew(); long snapshots = 0, reconnects = 0, images = 0; int cycle = 0;
        try
        {
            Attach();
            for (int i = 0; i < 4; i++) await Wait(() => Text(i).Contains(i < 2 ? "PERF_READY" : "PERSIST_READY"));
            var pids = Enumerable.Range(2, 2).Select(i => System.Text.RegularExpressions.Regex.Match(Text(i), @"PERSIST_READY PID=(\d+)").Groups[1].Value).ToArray();
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                for (int i = 0; i < 2; i++) Send(i, "flood 1000");
                for (int i = 2; i < 4; i++) { Send(i, "seed"); Send(i, "animate"); Send(i, "mark"); }
                long until = Environment.TickCount64 + 1500;
                while (Environment.TickCount64 < until)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (sessions[i].TryReadSnapshot(cells[i], out var snapshot)) { snapshots++; images += snapshot!.Graphics.Count; }
                        Healthy(i);
                    }
                    await Task.Delay(16);
                }
                bool large = cycle % 2 == 0;
                ushort columns = large ? (ushort)160 : (ushort)100, rows = large ? (ushort)50 : (ushort)30;
                for (int i = 0; i < 4; i++) { sessions[i].ResizeForTest(columns, rows, 10, 20); cells[i] = new TerminalCell[columns * rows]; }
                for (int i = 2; i < 4; i++)
                {
                    Send(i, "mark"); string pid = pids[i - 2];
                    await Wait(() => Text(i).Contains("PID=" + pid));
                }
                if (cycle % 4 == 3)
                {
                    foreach (var session in sessions) session.Detach(); sessions.Clear();
                    Attach(columns, rows); reconnects++;
                    for (int i = 2; i < 4; i++) { Send(i, "mark"); string pid = pids[i - 2]; await Wait(() => Text(i).Contains("PID=" + pid)); }
                }
                using var self = Process.GetCurrentProcess(); using var owner = Process.GetProcessById(host.ProcessId);
                long privateBytes = self.PrivateMemorySize64 + owner.PrivateMemorySize64;
                if (timer.Elapsed.TotalSeconds > 30) memory.Add(privateBytes);
                samples.Add(new { seconds = timer.Elapsed.TotalSeconds, privateBytes, clientPrivateBytes = self.PrivateMemorySize64,
                    hostPrivateBytes = owner.PrivateMemorySize64, snapshots, reconnects, placements = images,
                    bytesRead = sessions.Sum(s => s.Performance.BytesRead), hostCpuMs = owner.TotalProcessorTime.TotalMilliseconds });
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = false, status = "running", requestedSeconds = seconds,
                    elapsedSeconds = timer.Elapsed.TotalSeconds, host = host.Identity, folder, snapshots, reconnects, samples }));
                Console.WriteLine($"HOST_SOAK {timer.Elapsed.TotalSeconds:0}/{seconds}s: {privateBytes / 1048576.0:0.0} MiB combined; {snapshots} snapshots; {reconnects} reattachments");
                cycle++; await Task.Delay(3500);
            }
            check(snapshots > 100 && images > 0 && reconnects >= 2, "host soak repeatedly transports text and animated images through resize and frontend reattachments");
            check(workspace.AllPanes().Select(p=>p.Id).ToHashSet().SetEquals(SessionHostClient.TryConnect(folder)!.Panes), "host soak preserves every shell pane identity throughout the run");
            double growth = memory.TakeLast(Math.Min(6, memory.Count)).Average() - memory.Skip(memory.Count / 2).Take(Math.Min(6, memory.Count)).Average();
            if (seconds >= 1800) check(growth < 64 * 1048576, "host and remote-client memory stabilize within 64 MiB after midpoint");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = true, durationSeconds = timer.Elapsed.TotalSeconds,
                folder, host = host.Identity, snapshots, reconnects, placements = images, lateMemoryGrowthBytes = growth, samples }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = false, elapsedSeconds = timer.Elapsed.TotalSeconds,
                folder, snapshots, reconnects, samples, captureFailures=sessions.Select(s=>s.CaptureFailure).ToArray(), error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true })); throw;
        }
        finally { foreach (var session in sessions) session.Detach(); host.ShutdownForTest(); }

        void Attach(ushort columns = 100, ushort rows = 30)
        {
            foreach (var tab in workspace.Tabs)
            {
                var pane = tab.Root;
                var remote = host.StartForTest(pane.Id, pane.Command, pane.Directory, layout.Settings);
                remote.AttachView();
                // Rapid view churn must coalesce wakeups without losing the
                // final attachment or replacing its persistent shell.
                for (int burst = 0; burst < 100; burst++) { remote.DetachView(); remote.AttachView(); }
                remote.ResizeForTest(columns, rows, 10, 20); sessions.Add(remote);
            }
            for (int i = 0; i < 4; i++) cells[i] = new TerminalCell[columns * rows];
        }
        void Healthy(int i)
        {
            var session=sessions[i];
            // A recovered frame must not turn a transient five-second capture
            // timeout into a passing long-run result. CaptureFailure is latched.
            if(!string.IsNullOrEmpty(session.CaptureFailure)) throw new IOException($"Soak pane {i} capture failed: {session.CaptureFailure}");
            if(session.HasExited || session.StatusSummary.Contains("disconnected",StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Soak pane {i} disconnected or exited.");
        }
        string Text(int i) { sessions[i].TryReadSnapshot(cells[i], out _); Healthy(i); return string.Concat(cells[i].Select(c => c.GetText())); }
        void Send(int i, string command) { sessions[i].Paste(command); sessions[i].Key(13, 0); }
        static async Task Wait(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!condition()) { if (DateTime.UtcNow >= until) throw new TimeoutException("Host transport soak timed out."); await Task.Delay(20); }
        }
    }
}
