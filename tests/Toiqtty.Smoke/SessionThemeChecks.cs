using System.Text.Json;
using System.Text;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class SessionThemeChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var presets = SessionThemePresets.All;
        check(presets.Count == 10 && presets.All(p => p.IsValid), "Exactly ten valid bundled session theme presets load from the shared production assembly");
        check(presets.All(p => p.PaletteColors.Length == 16 && SessionTheme.TryColor(p.Cursor, out _)), "Every preset includes its upstream cursor color and complete sixteen-color ANSI palette");
        check(presets.Select(p => p.Name).Distinct().Count() == 10 && presets.Select(p => p.Background).Distinct().Count() == 10,
            "The session theme gallery has ten different names and background palettes");
        check(SessionThemePresets.Find("cobalt2") == presets.Single(p => p.Name == "Cobalt2") && SessionThemePresets.Find("unknown") is null,
            "Named theme lookup accepts case differences and rejects missing presets");
        string folder = Path.GetFullPath("artifacts/theme-check-" + Guid.NewGuid().ToString("N"));
        var workspace = new Workspace { Theme = SessionThemePresets.Find("Dracula") };
        var store = new SessionThemeStore(folder);
        await store.SaveAsync([workspace]);
        var legacy = new Workspace { Id = workspace.Id };
        new SessionThemeStore(folder).LoadInto([legacy]);
        check(legacy.Theme == workspace.Theme, "Frontend theme preferences restore a session identity even when the host layout omits its theme");
        File.WriteAllText(Path.Combine(folder, "session-themes.json"), JsonSerializer.Serialize(new Dictionary<string,SessionTheme> {
            [workspace.Id] = new() { Name = "Dracula", Accent = "#FF79C6", Background = "#282A36", Foreground = "#F8F8F2" } }));
        new SessionThemeStore(folder).LoadInto([legacy]);
        check(legacy.Theme == workspace.Theme && legacy.Theme!.PaletteColors.Length == 16,
            "Themes saved by the previous interface-only version gain the bundled pane palette on reconnect");
        var cold = new Workspace(); new SessionThemeStore(folder).LoadInto([cold]);
        check(cold.Theme is null, "Theme preferences cannot resurrect old sessions or attach an old theme to a fresh session identity");
        workspace.Theme = null; await store.SaveAsync([workspace]);
        legacy.Theme = presets[0]; new SessionThemeStore(folder).LoadInto([legacy]);
        check(legacy.Theme is null, "Resetting to the default theme overrides a stale custom theme on reconnect");
        workspace.Theme = presets[0]; _ = store.SaveAsync([workspace]); workspace.Theme = presets[1]; await store.SaveAsync([workspace]);
        new SessionThemeStore(folder).LoadInto([legacy]);
        check(legacy.Theme == presets[1], "Ordered asynchronous theme saves preserve the last selection rather than a stale write");
        var saved = SavedSessionSnapshot.Capture(new SavedSession { Workspaces = [workspace] });
        check(saved.Workspaces[0].Theme == workspace.Theme && !ReferenceEquals(saved.Workspaces[0].Theme, workspace.Theme),
            "Session layout snapshots retain theme values without sharing mutable layout objects");
        await store.SaveAsync([cold]);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "session-themes.json")));
        check(json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.TryGetProperty(cold.Id, out _),
            "Theme preferences prune sessions that have closed instead of accumulating stale IDs");
        File.WriteAllText(Path.Combine(folder, "session-themes.json"), "{\"" + cold.Id + "\":{\"Accent\":\"invalid\"}}");
        new SessionThemeStore(folder).LoadInto([cold]);
        check(cold.Theme is null, "Malformed theme preferences do not replace a working default or break startup");
        using var terminal = new GhosttyTerminal(64, 5);
        terminal.EnableIncrementalRendering();
        var cells = new TerminalCell[320]; var original = terminal.ReadFrame(cells);
        void Write(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); terminal.Write(bytes,bytes.Length); }
        uint Color(string value) { SessionTheme.TryColor(value,out uint color); return color; }
        Write(string.Concat(Enumerable.Range(0,16).Select(i=>$"\x1b[38;5;{i}m{(char)('A'+i)}"))+"\x1b[0mD\x1b[38;2;1;2;3mT\x1b[38;5;196mI");
        foreach (var theme in presets)
        {
            terminal.SetTheme(theme); var frame = terminal.ReadFrame(cells);
            check(frame.Background == Color(theme.Background) && frame.Foreground == Color(theme.Foreground) && frame.CursorColor == Color(theme.Cursor)
                && Enumerable.Range(0,16).All(i=>cells[i].Foreground==Color(theme.PaletteColors[i])) && cells[16].Foreground==Color(theme.Foreground),
                theme.Name+" updates existing default and ANSI-colored cells through Ghostty's configuration API");
            check(cells[17].Foreground==0xff010203 && cells[18].Foreground==0xffff0000 && frame.CursorX==19 && frame.CursorY==0,
                theme.Name+" preserves explicit truecolor, extended indexed colors and cursor position");
        }
        Write("\x1b]10;#010203\x07\x1b]11;#040506\x07\x1b]12;#070809\x07\x1b]4;1;#0A0B0C\x07");
        terminal.SetTheme(presets[0]); var overridden = terminal.ReadFrame(cells);
        check(overridden.Foreground==0xff010203 && overridden.Background==0xff040506 && overridden.CursorColor==0xff070809 && cells[1].Foreground==0xff0a0b0c,
            "Theme changes preserve application OSC foreground, background, cursor and palette overrides");
        terminal.SetTheme(null); overridden=terminal.ReadFrame(cells);
        check(overridden.Background==0xff040506 && cells[1].Foreground==0xff0a0b0c,"Resetting the configured theme preserves active application color overrides");
        Write("\x1b]110\x07\x1b]111\x07\x1b]112\x07\x1b]104\x07"); var restored=terminal.ReadFrame(cells);
        check(restored.Foreground==original.Foreground && restored.Background==original.Background && restored.CursorColor==original.CursorColor,
            $"Clearing application overrides after resetting a theme restores the original terminal defaults ({original.Foreground:X8}/{original.Background:X8}/{original.CursorColor:X8} → {restored.Foreground:X8}/{restored.Background:X8}/{restored.CursorColor:X8})");
        var replies = new List<string>(); terminal.Response += bytes=>replies.Add(Encoding.UTF8.GetString(bytes));
        terminal.SetTheme(SessionThemePresets.Find("Hot Dog Stand (Mustard)")); Write("\x1b[?996n");
        check(replies.Any(r=>r.Contains("997;2n")),"Light pane themes report the light color scheme through Ghostty");
        replies.Clear(); terminal.SetTheme(presets[0]); Write("\x1b[?996n");
        check(replies.Any(r=>r.Contains("997;1n")),"Dark pane themes report the dark color scheme through Ghostty");
    }
}
