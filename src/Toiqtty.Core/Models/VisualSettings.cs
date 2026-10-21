namespace Toiqtty.Models;

public sealed class PanePadding
{
    public double Left { get; set; } = 8;
    public double Right { get; set; } = 8;
    public double Top { get; set; } = 5;
    public double Bottom { get; set; } = 5;
}
public sealed class VisualSettings
{
    public PanePadding Padding { get; set; } = new();
    public double LineHeight { get; set; } = 1.5;
    public double CellWidth { get; set; } = 1;
    public double InactivePaneOpacity { get; set; } = 1;
    public Dictionary<string, uint> FontFeatures { get; set; } = [];
    public void Validate()
    {
        if (Padding is null || new[] { Padding.Left, Padding.Right, Padding.Top, Padding.Bottom }.Any(v => !double.IsFinite(v) || v is < 0 or > 128)
            || !double.IsFinite(LineHeight) || LineHeight is < 1 or > 3 || !double.IsFinite(CellWidth) || CellWidth is < .5 or > 2
            || !double.IsFinite(InactivePaneOpacity) || InactivePaneOpacity is < .1 or > 1 || FontFeatures is null || FontFeatures.Count > 32
            || FontFeatures.Any(p => p.Key.Length != 4 || p.Key.Any(c => !char.IsAsciiLetterOrDigit(c)) || p.Value > 65535))
            throw new ArgumentException("Invalid appearance settings: padding 0–128, line_height 1–3, cell_width 0.5–2, inactive_pane_opacity 0.1–1, and four-character OpenType tags.");
    }
}
public sealed class WindowSettings
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 820;
    public string State { get; set; } = "normal";
    public bool AlwaysOnTop { get; set; }
    public string Backdrop { get; set; } = "none";
    public double BackgroundOpacity { get; set; } = 1;
    public void Validate()
    {
        if (Width is < 400 or > 16384 || Height is < 300 or > 16384 || State is not ("normal" or "maximized" or "fullscreen")
            || Backdrop is not ("none" or "mica" or "mica_alt" or "acrylic") || !double.IsFinite(BackgroundOpacity) || BackgroundOpacity is < 0 or > 1)
            throw new ArgumentException("Invalid window size, state, backdrop or background opacity.");
    }
}
