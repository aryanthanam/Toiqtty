using Toiqtty.Terminal;
using Toiqtty.Models;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class HardeningChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "toiqtty-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var first = InstanceCoordinator.TryAcquire(folder))
            {
                check(first is not null && InstanceCoordinator.TryAcquire(folder) is null, "one process owns persistence for a given data directory");
                var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first!.Activated += () => activated.TrySetResult();
                check(await InstanceCoordinator.ActivateExisting(folder), "a second launch forwards activation through the current-user pipe");
                await activated.Task.WaitAsync(TimeSpan.FromSeconds(4));
                check(activated.Task.IsCompletedSuccessfully, "the existing instance receives forwarded activation");
                var navigated = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.NavigationRequested += (workspace, pane) => navigated.TrySetResult((workspace, pane));
                check(await InstanceCoordinator.ActivateExisting(folder, "workspace", "pane")
                    && await navigated.Task.WaitAsync(TimeSpan.FromSeconds(4)) == ("workspace", "pane"), "notification activation forwards its workspace and pane through the current-user instance pipe");
            }
            using (var reopened = InstanceCoordinator.TryAcquire(folder)) check(reopened is not null, "closing an instance releases its session-directory lease");
            using (var process = new ConPtyProcess("cmd.exe /Q /K", folder, 80, 24))
            {
                var drain = Task.Run(async () => { var buffer = new byte[16384]; try { while (await process.Output.ReadAsync(buffer) > 0) { } } catch (IOException) { } });
                check(!process.TryWrite(new byte[ConPtyProcess.MaxQueuedInputBytes + 1]) && process.QueuedInputBytes == 0,
                    "oversized input is rejected before any partial enqueue or memory reservation");
                process.Dispose(); await drain.WaitAsync(TimeSpan.FromSeconds(10));
                check(process.QueuedInputBytes == 0, "pseudoconsole shutdown releases pending input reservations");
            }
            using (var blocked = new ConPtyProcess("powershell.exe -NoLogo -NoProfile -Command Start-Sleep -Seconds 30", folder, 80, 24))
            {
                var drain = Task.Run(async () => { var buffer = new byte[16384]; try { while (await blocked.Output.ReadAsync(buffer) > 0) { } } catch (IOException) { } });
                check(blocked.TryWrite(new byte[ConPtyProcess.MaxQueuedInputBytes]) && !blocked.TryWrite([1]),
                    "a blocked input writer admits the byte budget and rejects additional input atomically");
                blocked.Dispose();
                await blocked.Closed.WaitAsync(TimeSpan.FromSeconds(10));
                await drain.WaitAsync(TimeSpan.FromSeconds(10));
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (blocked.QueuedInputBytes != 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
                check(blocked.QueuedInputBytes == 0, "closing a blocked input writer completes without leaking its queue reservation");
            }
            await ApplicationMatrix(check, folder);
            string pasteScript = Path.Combine(folder, "paste-limit.ps1");
            File.WriteAllText(pasteScript, "[Console]::Write(([char]27).ToString() + '[?2004hPASTE_READY'); Start-Sleep -Seconds 30\n");
            await using (var paste = new TerminalSession($"powershell.exe -NoLogo -NoProfile -File \"{pasteScript}\"", folder))
            {
                check(await WaitFor(paste, "PASTE_READY"), "real shell enables bracketed-paste mode before input admission testing");
                long startupPeak = paste.Performance.PeakQueuedInputBytes;
                paste.Paste(new string('x', ConPtyProcess.MaxQueuedInputBytes));
                check(paste.Performance.PeakQueuedInputBytes <= startupPeak && paste.Status.Notices.Any(item => item.Title == "Input busy"),
                    "an over-budget bracketed paste rejects its markers and content together");
            }
            await SustainedOutput(check, folder);
        }
        finally
        {
            // Each file is in our unique test directory; running children are disposed above.
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            try { Directory.Delete(folder); } catch (IOException) { Console.WriteLine("Retained test directory while console teardown finishes: " + folder); }
        }
    }
    private static string? Find(string file) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(path => Path.Combine(path, file)).FirstOrDefault(File.Exists);
    private static async Task<bool> WaitFor(TerminalSession session, string needle, int seconds = 15)
    {
        var cells = new TerminalCell[3000]; var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            session.ReadSnapshot(cells);
            if (string.Concat(cells.Select(cell => cell.GetText())).Contains(needle)) return true;
            await Task.Delay(25);
        }
        return false;
    }
    private static async Task ApplicationMatrix(Action<bool, string> check, string folder)
    {
        if (Find("nvim.exe") is { } nvim)
        {
            await using var session = new TerminalSession($"\"{nvim}\" --clean -n -i NONE", folder);
            await Task.Delay(800);
            session.Key(0, 0, ":call setline(1, 'TOIQTTY_NVIM_中🙂')"); session.Key(13, 0);
            check(await WaitFor(session, "TOIQTTY_NVIM_中🙂"), "real Neovim renders Unicode buffer content through ConPTY and alternate-screen state");
            session.Key(27, 0); session.Key(0, 0, ":q!"); session.Key(13, 0);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!session.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
            check(session.HasExited && session.ExitCode == 0, "real Neovim restores terminal modes and exits cleanly");
        }
        else Console.WriteLine("SKIP: Neovim is not installed.");
        if (Find("fzf.exe") is { } fzf)
        {
            string script = Path.Combine(folder, "fzf-check.ps1");
            File.WriteAllText(script, "'alpha','TOIQTTY_FZF_MATCH','omega' | & '" + fzf.Replace("'", "''") + "' --no-mouse --query TOIQTTY_FZF_MATCH --height=8\n");
            await using var session = new TerminalSession($"powershell.exe -NoLogo -NoProfile -File \"{script}\"", folder);
            check(await WaitFor(session, "TOIQTTY_FZF_MATCH"), "real fzf renders and filters interactive input through ConPTY");
            session.Key(13, 0);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!session.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
            check(session.HasExited && session.ExitCode == 0, "real fzf accepts the selected item and exits cleanly");
        }
        else Console.WriteLine("SKIP: fzf is not installed.");
    }
    private static async Task SustainedOutput(Action<bool, string> check, string folder)
    {
        var pwsh = ShellDefaults.FindPowerShell7();
        if (pwsh is null) { Console.WriteLine("SKIP: PowerShell 7 sustained-output benchmark requires pwsh."); return; }
        var script = Path.Combine(folder, "output-load.ps1");
        File.WriteAllText(script, "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n$line = '0123456789' * 12\nfor ($i=0; $i -lt 12000; $i++) { [Console]::WriteLine($line) }\n[Console]::Write('TOIQTTY_LOAD_COMPLETE')\nStart-Sleep -Seconds 2\n");
        var sessions = new List<TerminalSession>(); var timer = Stopwatch.StartNew();
        var buffers = Enumerable.Range(0, 4).Select(_ => new TerminalCell[3000]).ToArray();
        bool[] complete = new bool[4]; long snapshots = 0; var latency = new List<double>();
        try
        {
            for (int i = 0; i < 4; i++) sessions.Add(new($"\"{pwsh}\" -NoLogo -NoProfile -File \"{script}\"", folder));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!complete.All(done => done) && DateTime.UtcNow < deadline)
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    if (sessions[i].TryReadSnapshot(buffers[i], out _))
                    {
                        snapshots++; latency.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        complete[i] |= string.Concat(buffers[i].Select(cell => cell.GetText())).Contains("TOIQTTY_LOAD_COMPLETE");
                    }
                }
                await Task.Delay(4);
            }
            timer.Stop(); var metrics = sessions.Select(session => session.Performance).ToArray();
            check(complete.All(done => done), "four concurrent real ConPTY shells drain sustained output without losing the completion marker");
            check(metrics.Sum(metric => metric.BytesRead) >= 4 * 12000 * 120 && metrics.All(metric => metric.PeakQueuedInputBytes <= ConPtyProcess.MaxQueuedInputBytes),
                "sustained-output counters cover megabytes of data while input memory remains bounded");
            latency.Sort();
            string report = Path.Combine(Environment.CurrentDirectory, "artifacts", "performance.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { panes = 4, milliseconds = timer.Elapsed.TotalMilliseconds,
                bytes = metrics.Sum(metric => metric.BytesRead), snapshots,
                snapshotP95Milliseconds = latency.Count == 0 ? 0 : latency[(int)((latency.Count - 1) * .95)],
                peakQueuedInputBytes = metrics.Max(metric => metric.PeakQueuedInputBytes), metrics }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Performance report: " + report);
        }
        finally { foreach (var session in sessions) await session.DisposeAsync(); }
    }
}
