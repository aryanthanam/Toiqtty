using Microsoft.UI.Xaml.Media;

namespace Toiqtty.Controls;

internal static class Chrome
{
    internal readonly record struct Palette(uint Accent, uint Background, uint Selected, uint Text, uint Secondary, uint Border);
    internal static Palette For(Models.SessionTheme? theme)
    {
        if (theme is not { IsValid: true }) return new(Accent, Background, Selected, Text, Secondary, Border);
        Models.SessionTheme.TryColor(theme.Accent, out uint accent);
        Models.SessionTheme.TryColor(theme.Background, out uint background);
        Models.SessionTheme.TryColor(theme.Foreground, out uint text);
        uint selected = Models.SessionTheme.TryColor(theme.SelectionBackground, out uint selection) ? selection : Mix(background, accent, .25);
        return new(accent, background, selected, text, Mix(text, background, .25), Mix(background, text, .25));
    }
    internal static uint Mix(uint first, uint second, double fraction)
    {
        uint channel(int shift) => (uint)Math.Round(((first >> shift) & 255) * (1 - fraction) + ((second >> shift) & 255) * fraction);
        return 0xff000000 | (channel(16) << 16) | (channel(8) << 8) | channel(0);
    }
    public const uint Background = 0xff131519, Sidebar = 0xff16181d, Toolbar = 0xff131519;
    public const uint Border = 0xff292d34, Selected = 0xff20232a, Accent = 0xff91a7c7;
    public const uint Text = 0xffd7dbe2, Secondary = 0xff9aa2af, Muted = 0xff78818f;
    public static SolidColorBrush Brush(uint color) => new(Windows.UI.Color.FromArgb(
        (byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    public static string ShellLabel(string command)
    {
        string value = command.Trim();
        int end = value.StartsWith('"') ? value.IndexOf('"', 1) : value.IndexOfAny([' ', '\t']);
        string executable = value.StartsWith('"') && end > 0 ? value[1..end] : end > 0 ? value[..end] : value;
        string name = Path.GetFileNameWithoutExtension(executable);
        return name.ToLowerInvariant() switch { "pwsh" => "PowerShell 7", "powershell" => "Windows PowerShell", "cmd" => "Command Prompt", "bash" => "Bash", "zsh" => "Zsh", "wsl" => "WSL", "" => "Terminal", _ => name };
    }
}
