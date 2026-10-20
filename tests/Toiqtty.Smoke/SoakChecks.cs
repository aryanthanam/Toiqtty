using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class SoakChecks
{
    public static async Task Run(int seconds, Action<bool, string> check, string reportPath = "artifacts/soak.json")
    {
        if (seconds is < 30 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds), "Soak duration must be 30 seconds to 24 hours.");
        string folder = Path.Combine(Path.GetTempPath(), "toiqtty-soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var sessions = new List<TerminalSession>();
        var samples = new List<object>();
        var memory = new List<long>();
        var latencies = new List<double>();
        long snapshots = 0, replacements = 0;
        Directory.CreateDirectory("artifacts");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = false, status = "running", requestedSeconds = seconds }));
        try
        {
            string script = Path.Combine(folder, "output.ps1");
            File.WriteAllText(script, """
                $e = [char]27
                [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                $i = 0
                while ($true) {
                    [Console]::Write("${e}[38;2;80;160;240mSOAK_$i 中🙂 é${e}[0m" + ('x' * 80) + "`r`n")
                    $id = 1 + ($i % 16)
                    [Console]::Write("${e}_Ga=T,i=$id,f=24,s=1,v=1,c=2,r=1,C=1,q=2;/wAA${e}\")
                    if ($i % 97 -eq 0) { [Console]::Write("${e}[?2026hSOAK_HOLD${e}[?2026l") }
                    if ($i % 251 -eq 0) { [Console]::Write("${e}[?1049hSOAK_ALT${e}[?1049l") }
                    $i++; Start-Sleep -Milliseconds 25
                }
                """, new UTF8Encoding(false));
            for (int i = 0; i < 4; i++) sessions.Add(new TerminalSession($"\"{ShellDefaults.FindPowerShell7() ?? "powershell.exe"}\" -NoLogo -NoProfile -File \"{script}\"", folder));
            using var pressure = new GhosttyTerminal(100, 30);
            pressure.EnableIncrementalRendering();
            var pixels = new byte[256 * 256 * 3];
            var pressureCells = new TerminalCell[3000];
            var cells = sessions.Select(_ => new TerminalCell[3000]).ToArray();
            var timer = Stopwatch.StartNew();
            long nextSample = 0, nextResize = 1000;
            int cycle = 0;
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                int id = cycle % 16 + 1;
                Array.Fill(pixels, (byte)cycle);
                var sequence = Encoding.ASCII.GetBytes($"\x1b[H\x1b_Ga=T,i={id},f=24,s=256,v=256,c=4,r=2,C=1,q=2;{Convert.ToBase64String(pixels)}\x1b\\");
                // Fragmented graphics, replacement and deletion exercise both
                // engine ownership and native/managed immutable generations.
                for (int offset = 0; offset < sequence.Length; offset += 4093)
                    pressure.Write(sequence.AsSpan(offset, Math.Min(4093, sequence.Length - offset)).ToArray(), Math.Min(4093, sequence.Length - offset));
                var snapshot = pressure.ReadSnapshot(pressureCells);
                replacements++;
                checkBound(pressure.CachedImageBytes <= 16UL * (ulong)pixels.Length, "native graphics cache exceeded its live image set");
                checkBound(snapshot.Graphics.Select(g => g.Image).Distinct().Count() <= 16, "managed graphics generations accumulated");
                for (int i = 0; i < sessions.Count; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    if (sessions[i].TryReadSnapshot(cells[i], out _))
                    {
                        snapshots++;
                        // Keep a bounded sample set during day-long runs.
                        if (latencies.Count == 20000) latencies.RemoveRange(0, 10000);
                        latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                    checkBound(!sessions[i].HasExited, "a soak shell exited unexpectedly");
                }
                if (timer.ElapsedMilliseconds >= nextResize)
                {
                    bool large = (cycle & 1) == 0;
                    ushort columns = large ? (ushort)120 : (ushort)80, rows = large ? (ushort)40 : (ushort)24;
                    for (int i = 0; i < sessions.Count; i++) { sessions[i].Resize(columns, rows, 10, 20); cells[i] = new TerminalCell[columns * rows]; }
                    pressure.Scroll(-3); pressure.ReadSnapshot(pressureCells); pressure.Scroll(0);
                    nextResize += 1000;
                }
                if (timer.ElapsedMilliseconds >= nextSample)
                {
                    using var process = Process.GetCurrentProcess(); process.Refresh();
                    long privateBytes = process.PrivateMemorySize64;
                    if (timer.Elapsed.TotalSeconds >= 20) memory.Add(privateBytes);
                    samples.Add(new { seconds = timer.Elapsed.TotalSeconds, privateBytes, managedBytes = GC.GetTotalMemory(false), snapshots, replacements, bytesRead = sessions.Sum(s => s.Performance.BytesRead), nativeCacheBytes = pressure.CachedImageBytes });
                    Console.WriteLine($"SOAK {timer.Elapsed.TotalSeconds:0}/{seconds}s: {privateBytes / 1048576.0:0.0} MiB private; {snapshots} snapshots; {replacements} image replacements");
                    nextSample += 5000;
                }
                cycle++; await Task.Delay(25);
            }
            long bytesRead = sessions.Sum(s => s.Performance.BytesRead);
            check(bytesRead > 10000 && snapshots > 100, "soak sustained four real shells with Unicode, graphics, alternate screens and synchronized redraws");
            check(replacements > 100, "soak repeatedly replaces and fragments large graphics without retaining stale native or managed generations");
            double growth = memory.Count >= 4 ? memory.TakeLast(3).Average() - memory.Take(3).Average() : 0;
            long historyAllowance = seconds >= 1800 ? sessions.Count * 50_000_000L : 0;
            check(growth < historyAllowance + 128 * 1048576, "soak private-memory growth stays within its scrollback budgets plus 128 MiB after warmup");
            double lateGrowth = memory.Count >= 12 ? memory.TakeLast(6).Average() - memory.Skip(memory.Count / 2).Take(6).Average() : 0;
            if (seconds >= 1800) check(lateGrowth < 64 * 1048576, "long soak memory stabilizes within 64 MiB after the midpoint");
            latencies.Sort();
            double p95 = latencies[(int)((latencies.Count - 1) * .95)];
            Directory.CreateDirectory("artifacts");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = true, durationSeconds = timer.Elapsed.TotalSeconds, bytesRead, snapshots, replacements, snapshotP95Ms = p95, privateMemoryGrowthBytes = growth, historyAllowanceBytes = historyAllowance, lateMemoryGrowthBytes = lateGrowth, samples }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Soak report: {reportPath}; snapshot p95 {p95:0.000} ms");
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = false, requestedSeconds = seconds, snapshots, replacements, samples, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            foreach (var session in sessions) await session.DisposeAsync();
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            try { Directory.Delete(folder); } catch (IOException) { }
        }
        static void checkBound(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
