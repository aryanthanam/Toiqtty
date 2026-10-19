using System.Diagnostics;
using System.Text.Json;

namespace Oroterm.Models;

internal static class StartupTrace
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("OROTERM_STARTUP_TRACE") == "1";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly double BeforeTraceMs = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
    private static readonly Dictionary<string, double> Steps = [];
    internal static void Mark(string name)
    {
        if (Enabled) Steps.TryAdd(name, BeforeTraceMs + Clock.Elapsed.TotalMilliseconds);
    }
    internal static void Finish(bool passed, string? error = null)
    {
        if (!Enabled) return;
        File.WriteAllText(Path.Combine(SessionStore.Folder, "startup-trace.json"), JsonSerializer.Serialize(new {
            version = BuildInfo.Version, passed, error, processId = Environment.ProcessId, steps = Steps
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
