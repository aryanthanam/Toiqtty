using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

internal static class UiaObserveChecks
{
    // Read-only diagnostics for an explicitly identified isolated test window.
    // Never focus a window or obtain text from another process. Repeated UIA
    // queries allocate snapshots in the target, so use brief probes and keep
    // long resource-bound validation free of continuous text observation.
    public static Task Run(int pid, string expectedExecutable, int seconds, string report, int collectAfterSeconds = -1, bool postSoakOnly = false) => Task.Run(async () =>
    {
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (collectAfterSeconds is < -1 or > 3600) throw new ArgumentOutOfRangeException(nameof(collectAfterSeconds));
        expectedExecutable = Path.GetFullPath(expectedExecutable);
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.MainModule!.FileName, expectedExecutable, StringComparison.OrdinalIgnoreCase)
            || !expectedExecutable.Contains(Path.DirectorySeparatorChar + "gpu-run-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Observer requires the exact isolated GPU-test executable.");
        var creation = process.StartTime;
        var timer = Stopwatch.StartNew();
        int samples = 0, clientCollections = 0;
        bool textEnabled = !postSoakOnly;
        DateTime reportWrite = DateTime.MinValue;
        string rendererReport = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(expectedExecutable))!, "data", "renderer-soak.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            process.Refresh();
            if (process.HasExited) break;
            if (process.StartTime != creation) throw new InvalidOperationException("Observed process identity changed.");
            nint hwnd = process.MainWindowHandle;
            string? error = null;
            var documents = new List<string>();
            if (!textEnabled && File.Exists(rendererReport))
            {
                DateTime written = File.GetLastWriteTimeUtc(rendererReport);
                if (written != reportWrite)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(rendererReport));
                        textEnabled = json.RootElement.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True;
                        reportWrite = written;
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
            }
            if (hwnd != 0 && textEnabled)
            {
                try
                {
                    var window = AutomationElement.FromHandle(hwnd);
                    var terminals = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "Toiqtty.Terminal"));
                    foreach (AutomationElement terminal in terminals)
                        if (terminal.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                            documents.Add(((TextPattern)pattern).DocumentRange.GetText(65536));
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            }
            // Optional client-only collection supports controlled lifetime
            // experiments. It neither forces target collection nor establishes
            // that target snapshots are live or retained by the client.
            if (collectAfterSeconds >= 0 && timer.Elapsed.TotalSeconds >= collectAfterSeconds && ++samples % 40 == 0)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); clientCollections++;
            }
            File.AppendAllText(report, JsonSerializer.Serialize(new { observed = DateTimeOffset.Now, pid,
                hwnd = hwnd.ToInt64(), minimized = hwnd != 0 && IsIconic(hwnd), visible = hwnd != 0 && IsWindowVisible(hwnd),
                foreground = hwnd != 0 && GetForegroundWindow() == hwnd, targetPrivateBytes = process.PrivateMemorySize64,
                collectAfterSeconds, clientCollections, postSoakOnly, textEnabled, documents, error }) + Environment.NewLine);
            await Task.Delay(250);
        }
    });

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
