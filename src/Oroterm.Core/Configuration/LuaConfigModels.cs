using System.Text.Json;
using Oroterm.Models;

namespace Oroterm.Configuration;

public sealed class LuaConfigRequest
{
    public string Path { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public AppSettings Baseline { get; set; } = new();
}

public sealed class LuaConfigResult
{
    public AppSettings Settings { get; set; } = new();
    public string[] Overrides { get; set; } = [];
    public List<Workspace> StartupSessions { get; set; } = [];
    public bool ReplaceSavedStartup { get; set; }
    public string? Error { get; set; }
    public string[] Dependencies { get; set; } = [];
    public string[] Hooks { get; set; } = [];
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}

public static class UserPreferences
{
    public static string PathFor(string folder) => Path.Combine(folder, "preferences.json");
    public static AppSettings Load(string folder, AppSettings legacy)
    {
        string path = PathFor(folder);
        if (!File.Exists(path)) return LuaConfigResult.Clone(legacy);
        try
        {
            if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("Preferences file is too large.");
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? LuaConfigResult.Clone(legacy);
            settings.Keybindings ??= new(); settings.Browser ??= new(); settings.StatusBar ??= new(); LuaConfigParser.Validate(settings); return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException or FormatException) { return LuaConfigResult.Clone(legacy); }
    }
    public static void Save(string folder, AppSettings settings)
    {
        Directory.CreateDirectory(folder);
        string path = PathFor(folder), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
