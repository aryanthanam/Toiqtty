namespace Oroterm.Models;

public sealed class CursorSettings
{
    public string Style { get; set; } = "block";
    public bool Blink { get; set; }
    public int BlinkInterval { get; set; } = 500;
    public void Validate()
    {
        if (Style is not ("block" or "bar" or "underline" or "hollow") || BlinkInterval is < 100 or > 2000)
            throw new ArgumentException("cursor needs style block/bar/underline/hollow and blink_interval 100–2000 milliseconds.");
    }
    [System.Text.Json.Serialization.JsonIgnore] public uint NativeStyle => Style switch { "bar" => 0, "underline" => 2, "hollow" => 3, _ => 1 };
}

public sealed class WallpaperSettings
{
    public bool Enabled { get; set; } = true;
    public string[] Images { get; set; } = [];
    public double Opacity { get; set; } = .04;
    public string Fit { get; set; } = "cover";
    public bool Random { get; set; } = true;
    public void Validate()
    {
        if (Images is null || Images.Length > 64 || Images.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32768 || !Path.IsPathFullyQualified(p))
            || !double.IsFinite(Opacity) || Opacity is < 0 or > 1 || Fit is not ("cover" or "contain" or "stretch"))
            throw new ArgumentException("wallpaper needs up to 64 absolute image paths, opacity 0–1 and fit cover/contain/stretch.");
    }
}
