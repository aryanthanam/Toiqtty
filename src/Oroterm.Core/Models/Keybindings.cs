namespace Oroterm.Models;

public static class Keybindings
{
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        ["newWorkspace"] = "Ctrl+Shift+N", ["splitRight"] = "Ctrl+Shift+D", ["splitDown"] = "Ctrl+Shift+E",
        ["closePane"] = "Ctrl+Shift+W", ["movePane"] = "Ctrl+Shift+M", ["palette"] = "Ctrl+Shift+P", ["sidebar"] = "Ctrl+Shift+B",
        ["find"] = "Ctrl+Shift+F", ["zoom"] = "Ctrl+Shift+Enter", ["nextWorkspace"] = "Ctrl+Shift+PageDown",
        ["previousWorkspace"] = "Ctrl+Shift+PageUp", ["focusLeft"] = "Ctrl+Alt+Left", ["focusRight"] = "Ctrl+Alt+Right",
        ["focusUp"] = "Ctrl+Alt+Up", ["focusDown"] = "Ctrl+Alt+Down", ["copy"] = "Ctrl+Shift+C", ["paste"] = "Ctrl+Shift+V",
        ["scrollUp"] = "Shift+PageUp", ["scrollDown"] = "Shift+PageDown", ["notifications"] = "Ctrl+Shift+H",
        ["workspace1"] = "Ctrl+1", ["workspace2"] = "Ctrl+2", ["workspace3"] = "Ctrl+3", ["workspace4"] = "Ctrl+4",
        ["workspace5"] = "Ctrl+5", ["workspace6"] = "Ctrl+6", ["workspace7"] = "Ctrl+7", ["workspace8"] = "Ctrl+8", ["workspace9"] = "Ctrl+9",
        ["newTab"] = "Ctrl+Shift+T", ["nextTab"] = "Ctrl+Tab", ["previousTab"] = "Ctrl+Shift+Tab",
        ["newBrowser"] = "Ctrl+Shift+U", ["openDirectory"] = "Ctrl+Shift+O",
        ["nextWallpaper"]="",["previousWallpaper"]="",["randomWallpaper"]="",["toggleWallpaper"]="",["chooseWallpaper"]="",
        ["switchSession"]="Ctrl+Shift+K",["toggleFullscreen"]="F11",["toggleMaximized"]="",["toggleAlwaysOnTop"]="",
        ["increaseFontSize"]="",["decreaseFontSize"]="",["resetFontSize"]="",["resizeLeft"]="",["resizeRight"]="",["resizeUp"]="",["resizeDown"]=""
    };
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    { ["Tab"] = 9, ["Enter"] = 13, ["Escape"] = 27, ["Space"] = 32, ["PageUp"] = 33, ["PageDown"] = 34, ["End"] = 35, ["Home"] = 36,
        ["Left"] = 37, ["Up"] = 38, ["Right"] = 39, ["Down"] = 40, ["Insert"] = 45, ["Delete"] = 46, ["Backspace"] = 8,
        [";"]=186,["Semicolon"]=186,["="]=187,["Equals"]=187,["Plus"]=187,[","]=188,["Comma"]=188,
        ["-"]=189,["Minus"]=189,["."]=190,["Period"]=190,["/"]=191,["Slash"]=191,["`"]=192,["Backtick"]=192,
        ["["]=219,["LeftBracket"]=219,["\\"]=220,["Backslash"]=220,["]"]=221,["RightBracket"]=221,["'"]=222,["Quote"]=222 };
    public static (uint Key, uint Modifiers) Parse(string chord)
    {
        if (chord.EndsWith("++", StringComparison.Ordinal)) chord = chord[..^1] + "Shift+Equals";
        uint modifiers = 0, key = 0;
        foreach (var part in chord.Split('+', StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= 2;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= 1;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= 4;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= 8;
            else
            {
                if (key != 0) throw new FormatException("A shortcut must contain exactly one key.");
                if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]);
                else if (part.Length == 1 && "!@#$%^&*()_+{}|:\"<>?~".IndexOf(part[0]) is var shifted && shifted >= 0)
                {
                    const string bases = "1234567890-=[]\\;',./`";
                    char basis = bases[shifted]; modifiers |= 1;
                    key = char.IsAsciiDigit(basis) ? basis : NamedKeys[basis.ToString()];
                }
                else if (NamedKeys.TryGetValue(part, out var named)) key = named;
                else if (part.StartsWith('F') && uint.TryParse(part.AsSpan(1), out var number) && number is >= 1 and <= 24) key = 111 + number;
                else throw new FormatException($"Unknown shortcut key: {part}");
            }
        }
        if (key == 0) throw new FormatException("A shortcut needs a key.");
        return (key, modifiers);
    }
    public static string? Match(AppSettings settings, uint key, uint modifiers)
    {
        foreach (var (action, defaultChord) in Defaults)
        {
            var chord = settings.Keybindings.GetValueOrDefault(action, defaultChord);
            if (string.IsNullOrWhiteSpace(chord)) continue;
            try { if (Parse(chord) == (key, modifiers & 15)) return action; } catch (FormatException) { }
        }
        return null;
    }
    public static Dictionary<string, string> ParseOverrides(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator < 0) throw new FormatException("Use action = shortcut, one per line.");
            string action = line[..separator].Trim(), chord = line[(separator + 1)..].Trim();
            if (!Defaults.ContainsKey(action)) throw new FormatException($"Unknown action: {action}");
            if (chord.Length > 0) Parse(chord);
            if (!result.TryAdd(action, chord)) throw new FormatException($"Duplicate action: {action}");
        }
        var used = new HashSet<(uint, uint)>();
        foreach (var (action, chord) in Defaults)
        {
            var value = result.GetValueOrDefault(action, chord);
            if (value.Length > 0 && !used.Add(Parse(value))) throw new FormatException($"Shortcut is already assigned: {value}");
        }
        return result;
    }
}
