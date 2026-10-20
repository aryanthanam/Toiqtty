using System.Text.Json;

namespace Toiqtty.Models;

public static class SessionThemePresets
{
    public static IReadOnlyList<SessionTheme> All { get; } = Load();
    private static IReadOnlyList<SessionTheme> Load()
    {
        using var stream = typeof(SessionThemePresets).Assembly.GetManifestResourceStream("Toiqtty.SessionThemes.json")!;
        return Array.AsReadOnly(JsonSerializer.Deserialize<SessionTheme[]>(stream)!);
    }
    public static SessionTheme? Find(string name) => All.FirstOrDefault(theme => theme.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
