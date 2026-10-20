namespace Toiqtty.Models;

public static class TerminalFont
{
    public const string BundledFamily = "JetBrainsMono Nerd Font Mono";

    public static string BundledFile(uint style, ushort weight = 400) => "Assets/Fonts/JetBrainsMonoNerdFontMono-" + ((style & 3) switch
    {
        // Each URI names the actual face so bold/italic work without a system install.
        1 => "Bold.ttf",
        2 => weight >= 600 ? "BoldItalic.ttf" : weight >= 450 ? "MediumItalic.ttf" : "Italic.ttf",
        3 => "BoldItalic.ttf",
        _ => weight >= 600 ? "Bold.ttf" : weight >= 450 ? "Medium.ttf" : "Regular.ttf"
    });

    // Unpackaged WinUI apps cannot resolve font files through ms-appx.
    public static Uri BundledUri(uint style, ushort weight = 400) => new(Path.Combine(AppContext.BaseDirectory, BundledFile(style, weight)));

    public static string Resolve(string family, uint style, ushort weight = 400) =>
        string.Equals(family, BundledFamily, StringComparison.OrdinalIgnoreCase)
            ? BundledUri(style, weight).AbsoluteUri + "#" + BundledFamily
            : family;
}
