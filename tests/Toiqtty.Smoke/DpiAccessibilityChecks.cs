using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

internal static class DpiAccessibilityChecks
{
    public static Task Run(int pid, string path, int cellWidth, int cellHeight) => Task.Run(() =>
    {
        string? error = null;
        object? geometry = null;
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            if (previousDpi == 0) throw new InvalidOperationException("Could not enable per-monitor DPI awareness for the UIA reader.");
            using var process = Process.GetProcessById(pid);
            var window = AutomationElement.FromHandle(process.MainWindowHandle);
            var terminals = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "toiqtty.Terminal"));
            if (terminals.Count != 1) throw new InvalidOperationException("Expected one isolated DPI terminal.");
            var terminal = terminals[0];
            var text = (TextPattern)terminal.GetCurrentPattern(TextPattern.Pattern);
            var match = text.DocumentRange.FindText("DPI_CHECK", false, false) ?? throw new InvalidOperationException("Fixture marker is not accessible.");
            var rectangles = match.GetBoundingRectangles();
            var bounds = window.Current.BoundingRectangle;
            geometry = new { window = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                terminal = terminal.Current.BoundingRectangle.ToString(),
                ranges = rectangles.Select(r => new { r.X, r.Y, r.Width, r.Height }).ToArray() };
            if (rectangles.Length != 1 || !bounds.Contains(rectangles[0]))
                throw new InvalidOperationException("External UIA text range lies outside the physical window.");
            var rect = rectangles[0];
            if (Math.Abs(rect.Width - 9 * cellWidth) > 1 || Math.Abs(rect.Height - cellHeight) > 1)
                throw new InvalidOperationException("External text bounds do not match the actual physical cell dimensions.");
            var point = new System.Windows.Point(rect.X + rect.Width / 18, rect.Y + rect.Height / 2);
            var character = text.RangeFromPoint(point);
            character.ExpandToEnclosingUnit(TextUnit.Character);
            if (character.GetText(-1) != "D") throw new InvalidOperationException("External screen-point hit testing missed the first character.");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed = error is null, geometry, error }, new JsonSerializerOptions { WriteIndented = true }));
    });
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
