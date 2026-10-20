using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;

internal static class StartupChecks
{
    public static async Task Run(string executable, int iterations, bool profile = false, bool immediate = false)
    {
        if (iterations is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(iterations));
        executable = Path.GetFullPath(executable);
        foreach (var running in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (running)
                if (string.Equals(running.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Startup probes require an unused executable copy to preserve the running application's notification registration. Use scripts/test-startup.ps1.");
        }
        string evidence = Path.GetFullPath("artifacts/startup-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var runs = new List<object>();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            string folder = Path.Combine(evidence, "startup-check-" + iteration);
            Directory.CreateDirectory(folder);
            UserPreferences();
            string? configSource = Environment.GetEnvironmentVariable("TOIQTTY_STARTUP_CONFIG_SOURCE");
            if (!string.IsNullOrEmpty(configSource))
            {
                // Copy only config modules, never the user's sessions or browser data.
                foreach (string file in Directory.EnumerateFiles(configSource, "*.lua", SearchOption.TopDirectoryOnly))
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            }
            try
            {
                foreach (string phase in new[] { "new-host", "reattach" })
                {
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
                    start.ArgumentList.Add("--startup-probe");
                    start.Environment["TOIQTTY_DATA_DIR"] = folder;
                    start.Environment["TOIQTTY_STARTUP_TRACE"] = "1";
                    start.Environment["TOIQTTY_CONFIG"] = Path.Combine(folder, "toiqtty.lua");
                    start.Environment["TOIQTTY_STARTUP_EXPECT"] = immediate ? "TOIQTTY_STARTUP_READY" : profile ? "TOIQTTY_STARTUP_PROFILE_READY" : "PS ";
                    start.Environment.Remove("NO_COLOR");
                    using var process = Process.Start(start)!;
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
                    catch (TimeoutException) { if (!process.HasExited) process.Kill(); throw; }
                    string trace = File.ReadAllText(Path.Combine(folder, "startup-trace.json"));
                    using var report = JsonDocument.Parse(trace);
                    if (!report.RootElement.GetProperty("passed").GetBoolean()) throw new InvalidOperationException(trace);
                    var steps = report.RootElement.GetProperty("steps").Deserialize<Dictionary<string, double>>()!;
                    runs.Add(new { iteration, phase, steps });
                    File.WriteAllText(Path.Combine(folder, phase + ".json"), trace);
                    Console.WriteLine($"{phase}: window {steps["window-activated"]:0} ms; first frame {steps["first-frame"]:0} ms; prompt {steps["prompt-ready"]:0} ms");
                }
            }
            finally { try { SessionHostClient.TryConnect(folder)?.Shutdown(); } catch (IOException) { } }
            void UserPreferences() => Toiqtty.Configuration.UserPreferences.Save(folder, new AppSettings {
                Shell = immediate ? "cmd.exe /D /Q /K echo TOIQTTY_STARTUP_READY" : ShellDefaults.Command + (profile ? " -NoExit -Command \"[Console]::Write('TOIQTTY_STARTUP_PROFILE_READY')\"" : " -NoProfile"), StartDirectory = Environment.CurrentDirectory
            });
        }
        File.WriteAllText(Path.Combine(evidence, "startup-benchmark.json"), JsonSerializer.Serialize(new { executable, profileEnabled = profile, immediateShell = immediate, runs }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Startup evidence: " + evidence);
    }
}
