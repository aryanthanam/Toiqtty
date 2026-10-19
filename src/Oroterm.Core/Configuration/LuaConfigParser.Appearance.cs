using Lua;
using Oroterm.Models;

namespace Oroterm.Configuration;

public static partial class LuaConfigParser
{
    public static SessionTheme? FindTheme(AppSettings settings, string name) =>
        settings.ColorSchemes.Values.FirstOrDefault(theme => theme.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? SessionThemePresets.Find(name);

    private static void ParseAppearance(LuaTable table, AppSettings settings, List<string> owned, string folder)
    {
        if (Present(table, "font_weight"))
        {
            var value = table["font_weight"];
            settings.FontWeight = (ushort)(value.Type == LuaValueType.Number ? Number(value, "font_weight", 100, 900) : Text(value, "font_weight").ToLowerInvariant() switch
            {
                "thin" => 100, "extralight" => 200, "light" => 300, "regular" or "normal" => 400,
                "medium" => 500, "semibold" => 600, "bold" => 700, "extrabold" => 800, "black" => 900,
                _ => throw new ArgumentException("Unknown font_weight.")
            });
            if (value.Type == LuaValueType.Number && value.Read<double>() != settings.FontWeight) throw new ArgumentException("font_weight must be an integer.");
            owned.Add("FontWeight");
        }
        if (Present(table, "color_schemes"))
        {
            var schemes = Table(table["color_schemes"], "color_schemes");
            if (schemes.Count() > 32) throw new ArgumentException("At most 32 custom color schemes are supported.");
            settings.ColorSchemes = new(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in schemes)
            {
                string name = Text(pair.Key, "color scheme name", 64);
                if (name.Equals("default", StringComparison.OrdinalIgnoreCase) || SessionThemePresets.Find(name) is not null || settings.ColorSchemes.ContainsKey(name))
                    throw new ArgumentException("Color scheme names must be unique and cannot replace bundled presets: " + name);
                var colors = Table(pair.Value, "color scheme");
                Keys(colors, "color scheme", "foreground", "background", "cursor", "cursor_foreground", "accent", "selection_background", "selection_foreground", "ansi", "brights", "indexed");
                string Color(string key, string fallback = "") => Present(colors, key) ? Text(colors[key], "color scheme." + key, 7) : fallback;
                string[] ansi = Present(colors, "ansi") ? Array(colors["ansi"], "ansi", 8).Select(v => Text(v, "ansi color", 7)).ToArray() : [];
                string[] bright = Present(colors, "brights") ? Array(colors["brights"], "brights", 8).Select(v => Text(v, "bright color", 7)).ToArray() : [];
                if (ansi.Length != bright.Length || ansi.Length is not (0 or 8)) throw new ArgumentException("ansi and brights must both contain exactly eight colors, or both be omitted.");
                var indexed = new SortedDictionary<int, string>();
                if (Present(colors, "indexed")) foreach (var entry in Table(colors["indexed"], "indexed"))
                {
                    double index = Number(entry.Key, "indexed color index", 16, 255);
                    if (index != Math.Truncate(index)) throw new ArgumentException("Indexed color indices must be integers.");
                    indexed.Add((int)index, Text(entry.Value, "indexed color", 7));
                }
                var theme = new SessionTheme { Name = name, Foreground = Color("foreground", "#D7DBE2"), Background = Color("background", "#131519"),
                    Accent = Color("accent", "#91A7C7"), Cursor = Color("cursor"), CursorForeground = Color("cursor_foreground"),
                    SelectionBackground = Color("selection_background"), SelectionForeground = Color("selection_foreground"),
                    Palette = string.Join(' ', ansi.Concat(bright)), Indexed = string.Join(' ', indexed.Select(p => p.Key + "=" + p.Value)) };
                if (!theme.IsValid) throw new ArgumentException("Invalid #RRGGBB color in scheme: " + name);
                settings.ColorSchemes.Add(name, theme);
            }
            owned.Add("ColorSchemes");
        }
        if (Present(table, "color_scheme"))
        {
            string name = Text(table["color_scheme"], "color_scheme", 64);
            settings.TerminalTheme = name.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : FindTheme(settings, name) ?? throw new ArgumentException("Unknown color_scheme: " + name);
            owned.Add("TerminalTheme");
        }
        if (Present(table, "cursor"))
        {
            var cursor = Table(table["cursor"], "cursor"); Keys(cursor, "cursor", "style", "blink", "blink_interval");
            if (Present(cursor, "style")) settings.Cursor.Style = Text(cursor["style"], "cursor.style", 16);
            if (Present(cursor, "blink")) settings.Cursor.Blink = Boolean(cursor["blink"], "cursor.blink");
            if (Present(cursor, "blink_interval")) { double interval = Number(cursor["blink_interval"], "cursor.blink_interval", 100, 2000); if (interval != Math.Truncate(interval)) throw new ArgumentException("cursor.blink_interval must be an integer."); settings.Cursor.BlinkInterval = (int)interval; }
            owned.Add("Cursor");
        }
        if (Present(table, "wallpaper"))
        {
            var wallpaper = Table(table["wallpaper"], "wallpaper"); Keys(wallpaper, "wallpaper", "enabled", "images", "directory", "opacity", "fit", "random");
            string PathFor(LuaValue value) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(Text(value, "wallpaper path")), folder);
            var images = new List<string>();
            if (Present(wallpaper, "images")) images.AddRange(Array(wallpaper["images"], "wallpaper.images", 64).Select(PathFor));
            if (Present(wallpaper, "directory"))
            {
                string directory = PathFor(wallpaper["directory"]);
                if (!Directory.Exists(directory)) throw new ArgumentException("Wallpaper directory does not exist: " + directory);
                images.AddRange(Directory.EnumerateFiles(directory).Where(p => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(p).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).Take(65));
            }
            if (Present(wallpaper, "images") || Present(wallpaper, "directory")) settings.Wallpaper.Images = images.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (settings.Wallpaper.Images.Any(p => !File.Exists(p))) throw new ArgumentException("A wallpaper image does not exist.");
            if (Present(wallpaper, "enabled")) settings.Wallpaper.Enabled = Boolean(wallpaper["enabled"], "wallpaper.enabled");
            if (Present(wallpaper, "opacity")) settings.Wallpaper.Opacity = Number(wallpaper["opacity"], "wallpaper.opacity", 0, 1);
            if (Present(wallpaper, "fit")) settings.Wallpaper.Fit = Text(wallpaper["fit"], "wallpaper.fit", 16);
            if (Present(wallpaper, "random")) settings.Wallpaper.Random = Boolean(wallpaper["random"], "wallpaper.random");
            owned.Add("Wallpaper");
        }
    }
    private static void ValidateAppearance(AppSettings settings)
    {
        if (settings.FontWeight is < 100 or > 900 || settings.Cursor is null || settings.Wallpaper is null || settings.ColorSchemes is null
            || settings.ColorSchemes.Count > 32 || settings.ColorSchemes.Values.Any(theme => theme is not { IsValid: true })) throw new ArgumentException("Invalid appearance settings.");
        settings.Cursor.Validate(); settings.Wallpaper.Validate();
    }
}
