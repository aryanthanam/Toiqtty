using Oroterm.Models;

namespace Oroterm.Configuration;

public static class ConfigurationSettings
{
    public static void Apply(AppSettings source, AppSettings target)
    {
        var copy = LuaConfigResult.Clone(source);
        foreach (var property in typeof(AppSettings).GetProperties()) property.SetValue(target, property.GetValue(copy));
    }
    public static void CapturePreferences(AppSettings effective, AppSettings baseline, IReadOnlyCollection<string> owned)
    {
        var copy = LuaConfigResult.Clone(effective);
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (property.Name == nameof(AppSettings.Browser)) continue;
            if (!owned.Contains(property.Name)) property.SetValue(baseline, property.GetValue(copy));
        }
        foreach (var property in typeof(BrowserSettings).GetProperties())
            if (!owned.Contains("Browser." + property.Name)) property.SetValue(baseline.Browser, property.GetValue(effective.Browser));
    }
    public static void ResolveProfiles(LuaConfigResult config, IReadOnlyDictionary<string, string> profiles)
    {
        string Resolve(string value)
        {
            if (value.Length == 0 || Guid.TryParseExact(value, "N", out _)) return value;
            return profiles.TryGetValue(value, out var id) ? id : throw new ArgumentException("No connected browser profile named ‘" + value + "’. Connect it first or use 'independent'.");
        }
        config.Settings.Browser.DefaultSourceId = Resolve(config.Settings.Browser.DefaultSourceId);
        foreach (var pane in config.StartupSessions.SelectMany(w => w.AllPanes()).Where(p => p.IsBrowser)) pane.BrowserSourceId = Resolve(pane.BrowserSourceId);
    }
}
