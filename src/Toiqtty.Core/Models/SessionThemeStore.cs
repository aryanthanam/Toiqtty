using System.Text.Json;

namespace Toiqtty.Models;

// Presentation preferences belong to the frontend. Keeping an ID-scoped copy
// also preserves themes when reconnecting to an older host that drops new fields.
internal sealed class SessionThemeStore(string folder)
{
    private readonly string _path = Path.Combine(folder, "session-themes.json");
    private string? _serialized;
    private Task _pending = Task.CompletedTask;

    internal void LoadInto(IEnumerable<Workspace> workspaces)
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 512 * 1024) return;
            var themes = JsonSerializer.Deserialize<Dictionary<string, SessionTheme?>>(File.ReadAllText(_path));
            if (themes is null || themes.Count > 64) return;
            foreach (var workspace in workspaces)
                if (themes.TryGetValue(workspace.Id, out var theme) && (theme is null || theme.IsValid))
                    workspace.Theme = theme is null ? null : SessionThemePresets.Find(theme.Name) ?? theme;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { System.Diagnostics.Debug.WriteLine(ex); }
    }

    internal Task SaveAsync(IEnumerable<Workspace> workspaces)
    {
        string json = JsonSerializer.Serialize(workspaces.ToDictionary(w => w.Id, w => w.Theme));
        if (_serialized == json) return _pending;
        _serialized = json;
        return _pending = Persist(_pending, json);
    }
    internal Task FlushAsync() => _pending;
    private async Task Persist(Task previous, string json)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
        await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, json); File.Move(temp, _path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }).ConfigureAwait(false);
    }
}
