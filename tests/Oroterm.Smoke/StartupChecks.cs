using System.Diagnostics;
using System.Text.Json;
using Oroterm.Hosting;
using Oroterm.Models;

internal static class StartupChecks
{
    public static async Task Run(string executable, int iterations, bool profile = false)
    {
        if (iterations is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(iterations));
        executable = Path.GetFullPath(executable);
        foreach (var running in Process.GetProcessesByName("Oroterm"))
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
            try
            {
                foreach (string phase in new[] { "new-host", "reattach" })
                {
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
                    start.ArgumentList.Add("--startup-probe");
                    start.Environment["OROTERM_DATA_DIR"] = folder;
                    start.Environment["OROTERM_STARTUP_TRACE"] = "1";
                    start.Environment["OROTERM_CONFIG"] = Path.Combine(folder, "oroterm.lua");
                    start.Environment["OROTERM_STARTUP_EXPECT"] = profile ? "OROTERM_STARTUP_PROFILE_READY" : "PS ";
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
            void UserPreferences() => Oroterm.Configuration.UserPreferences.Save(folder, new AppSettings {
                Shell = ShellDefaults.Command + (profile ? " -NoExit -Command \"[Console]::Write('OROTERM_STARTUP_PROFILE_READY')\"" : " -NoProfile"), StartDirectory = Environment.CurrentDirectory
            });
        }
        File.WriteAllText(Path.Combine(evidence, "startup-benchmark.json"), JsonSerializer.Serialize(new { executable, profileEnabled = profile, runs }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Startup evidence: " + evidence);
    }
}
