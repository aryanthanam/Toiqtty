using System.Text;
using Oroterm.Models;
using Oroterm.Terminal;

internal static class ScrollbackChecks
{
    public static void Run(Action<bool, string> check)
    {
        var defaults = new AppSettings();
        check(defaults.ScrollbackLimitBytes == 50_000_000 && defaults.ScrollbackLimitLines is null,
            "scrollback defaults match Ghostty: 50 MB per shell and no line limit");
        using var terminal = new GhosttyTerminal(80, 3);
        void Write(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); terminal.Write(bytes, bytes.Length); }
        string flood = string.Concat(Enumerable.Repeat(new string('x', 78) + "\r\n", 20_000));
        Write("SCROLLBACK_FIRST\r\n" + flood + "SCROLLBACK_LAST");
        check(terminal.Find("SCROLLBACK_FIRST", 0).Count == 1,
            "the default native budget retains 20,000 full lines instead of the tiny VT default");
        terminal.Find("", 0);
        terminal.SetScrollbackLimits(null, 100);
        check(terminal.Find("SCROLLBACK_FIRST", 0).Count == 0 && terminal.Find("SCROLLBACK_LAST", 0).Count == 1,
            "a line limit prunes old history while preserving the live screen");
        terminal.Find("", 0);
        terminal.SetScrollbackLimits(null, null);
        Write("\r\nUNLIMITED_FIRST\r\n" + flood + "UNLIMITED_LAST");
        check(terminal.Find("UNLIMITED_FIRST", 0).Count == 1,
            "removing both limits allows history to grow again");
        terminal.Find("", 0);
        terminal.SetScrollbackLimits(262_144, null);
        check(terminal.Find("UNLIMITED_FIRST", 0).Count == 0 && terminal.Find("UNLIMITED_LAST", 0).Count == 1,
            "lowering the byte limit immediately prunes eligible old pages");
        terminal.Find("", 0);
        terminal.SetScrollbackLimits(50_000_000, null);
        Write("\r\nCLEAR_MARKER\r\n" + flood + "LIVE_SCREEN");
        check(terminal.Find("CLEAR_MARKER", 0).Count == 1, "raising the byte limit retains subsequent output");
        terminal.Find("", 0);
        terminal.SetScrollbackLimits(0, null);
        check(terminal.Find("CLEAR_MARKER", 0).Count == 0 && terminal.Find("LIVE_SCREEN", 0).Count == 1,
            "zero bytes clears scrollback without erasing the live screen");
        terminal.Find("", 0);
        bool rejected = false;
        try { terminal.SetScrollbackLimits(null, -1); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected, "negative managed limits are rejected instead of becoming unlimited");
        using var resized = new GhosttyTerminal(80, 24);
        resized.SetScrollbackLimits(262_144, null);
        string batch = string.Concat(Enumerable.Repeat("café 中🙂 é " + new string('x', 68) + "\r\n", 1000));
        for (int i = 0; i < 20; i++)
        {
            byte[] output = Encoding.UTF8.GetBytes((i == 0 ? "RESIZE_FIRST\r\n" : "") + batch + (i == 19 ? "RESIZE_LAST" : ""));
            resized.Write(output, output.Length);
            ushort columns = i % 2 == 0 ? (ushort)120 : (ushort)80, rows = i % 2 == 0 ? (ushort)40 : (ushort)24;
            resized.Resize(columns, rows, 8, 24); resized.ReadSnapshot(new TerminalCell[columns * rows]);
        }
        check(resized.Find("RESIZE_FIRST", 0).Count == 0 && resized.Find("RESIZE_LAST", 0).Count == 1,
            "byte-limited Unicode scrollback remains bounded through repeated reflow and consumer-buffer replacements");
        resized.Find("", 0); resized.SetScrollbackLimits(0, null); resized.Resize(100, 30, 8, 24);
        byte[] cleared = Encoding.UTF8.GetBytes("ZERO_RESIZE_FIRST\r\n" + batch + "ZERO_RESIZE_LAST"); resized.Write(cleared, cleared.Length);
        check(resized.Find("ZERO_RESIZE_FIRST", 0).Count == 0 && resized.Find("ZERO_RESIZE_LAST", 0).Count == 1,
            "disabled scrollback stays disabled after a resize while the active screen remains usable");
    }
}
