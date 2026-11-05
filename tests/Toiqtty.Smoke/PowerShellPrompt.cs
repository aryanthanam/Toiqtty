using System.Diagnostics;
using Toiqtty.Terminal;

internal static class PowerShellPrompt
{
    internal static async Task Wait(TerminalSession session)
    {
        // Windows PowerShell's first launch on a fresh runner can be much
        // slower than subsequent launches. Keep startup separate from the
        // command deadlines, and never send test input before a prompt.
        var elapsed = Stopwatch.StartNew();
        var cells = new TerminalCell[3000];
        string screen = "";
        while (elapsed.Elapsed < TimeSpan.FromSeconds(90))
        {
            var frame = session.ReadFrame(cells);
            int columns = checked((int)frame.Columns), rowCount = checked((int)frame.Rows);
            var rows = Enumerable.Range(0, rowCount).Select(y => string.Concat(cells
                .Skip(y * columns).Take(columns).Select(c => c.GetText())).TrimEnd()).ToArray();
            if (rows.Any(row => row.StartsWith("PS ", StringComparison.Ordinal))) return;
            screen = string.Join(" | ", rows.Where(row => row.Length > 0));
            if (session.HasExited) break;
            await Task.Delay(100);
        }
        throw new TimeoutException($"PowerShell did not reach its prompt after {elapsed.Elapsed.TotalSeconds:0.0}s; "
            + $"exited={session.HasExited}, exit code={session.ExitCode}, bytes read={session.Performance.BytesRead}, "
            + $"directory='{session.Directory}', screen: {screen}");
    }
}
