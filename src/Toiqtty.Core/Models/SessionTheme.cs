using System.Globalization;
using System.Text.Json.Serialization;

namespace Toiqtty.Models;

public sealed record SessionTheme
{
    public string Name { get; init; } = "";
    public string Accent { get; init; } = "#91A7C7";
    public string Background { get; init; } = "#131519";
    public string Foreground { get; init; } = "#D7DBE2";
    public string Cursor { get; init; } = "";
    public string Palette { get; init; } = "";
    public string Indexed { get; init; } = "";
    public string SelectionBackground { get; init; } = "";
    public string SelectionForeground { get; init; } = "";
    public string CursorForeground { get; init; } = "";
    [JsonIgnore] public bool IsValid => TryColor(Accent, out _) && TryColor(Background, out _) && TryColor(Foreground, out _)
        && OptionalColor(Cursor) && OptionalColor(SelectionBackground) && OptionalColor(SelectionForeground) && OptionalColor(CursorForeground)
        && (Palette is { Length: 0 } || Palette is not null && PaletteColors is { Length: 16 } colors && colors.All(c => TryColor(c, out _))) && ValidIndexed();
    [JsonIgnore] public string[] PaletteColors => (Palette ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    [JsonIgnore] public string Key => Name + Accent + Background + Foreground + Cursor + Palette + Indexed + SelectionBackground + SelectionForeground + CursorForeground;
    private static bool OptionalColor(string? value) => value is { Length: 0 } || TryColor(value, out _);
    private bool ValidIndexed()
    {
        if (Indexed is null || Indexed.Length > 4096) return false;
        var seen = new HashSet<int>();
        foreach (string entry in Indexed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('=');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index is < 16 or > 255 || !seen.Add(index) || !TryColor(parts[1], out _)) return false;
        }
        return true;
    }
    public static bool TryColor(string? text, out uint color)
    {
        color = 0;
        if (text is not { Length: 7 } || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out color)) return false;
        color |= 0xff000000; return true;
    }
}
