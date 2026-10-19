using System.Text.RegularExpressions;

namespace Oroterm.Models;

public sealed class StatusBarSettings
{
    public bool Enabled { get; set; }
    public string[] Left { get; set; } = ["{status}", "{directory}", "{branch}"];
    public string[] Right { get; set; } = ["{shells} shells", "{viewport}"];
    public string Separator { get; set; } = "  ·  ";
    public double Height { get; set; } = 20;
    public double FontSize { get; set; } = 10;
    public string Background { get; set; } = "";
    public string Foreground { get; set; } = "";

    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "session", "tab", "directory", "branch", "status", "exit_code", "shells",
        "panes", "tabs", "sessions", "font_size", "viewport", "url", "version", "key_table", "leader"
    };

    public void Validate()
    {
        if (!double.IsFinite(Height) || Height is < 18 or > 64 ||
            !double.IsFinite(FontSize) || FontSize is < 9 or > 24 || Height < FontSize + 6)
            throw new ArgumentException("status_bar needs height 18–64, font_size 9–24, and height at least font_size + 6.");
        if (!SingleLine(Separator, 32) || !Color(Background) || !Color(Foreground))
            throw new ArgumentException("status_bar needs a single-line separator and #RRGGBB colors (or empty strings to follow the session theme).");
        foreach (var segments in new[] { Left, Right })
        {
            if (segments is null || segments.Length > 16) throw new ArgumentException("status_bar left/right must contain at most 16 segments.");
            foreach (string segment in segments)
            {
                if (!SingleLine(segment, 256)) throw new ArgumentException("status_bar segments must be single-line strings of at most 256 characters.");
                foreach (Match match in Placeholder.Matches(segment))
                    if (!Fields.Contains(match.Groups[1].Value)) throw new ArgumentException("Unknown status_bar field: " + match.Groups[1].Value);
                string remainder = Placeholder.Replace(segment, "");
                if (remainder.Contains('{') || remainder.Contains('}')) throw new ArgumentException("Unmatched braces in status_bar segment.");
            }
        }
    }

    // Metadata is inserted once as literal text: braces in titles/directories
    // cannot become additional placeholders. Missing values omit the segment.
    public string Format(IEnumerable<string> segments, IReadOnlyDictionary<string, string> values)
    {
        var output = new List<string>();
        foreach (string segment in segments)
        {
            bool missing = false;
            string text = Placeholder.Replace(segment, match =>
            {
                if (!values.TryGetValue(match.Groups[1].Value, out string? value) || value.Length == 0) { missing = true; return ""; }
                return new string(value.Take(1024).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            });
            if (!missing && text.Length > 0) output.Add(text);
        }
        return string.Join(Separator, output);
    }

    private static bool SingleLine(string? value, int maximum) => value is not null && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Color(string? value) => value is not null && (value.Length == 0 || SessionTheme.TryColor(value, out _));
}
