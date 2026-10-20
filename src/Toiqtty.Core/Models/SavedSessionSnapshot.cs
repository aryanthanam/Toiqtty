using System.Text.Json;

namespace Toiqtty.Models;

internal static class SavedSessionSnapshot
{
    internal static SavedSession Capture(SavedSession layout)
    {
        var snapshot = JsonSerializer.Deserialize<SavedSession>(JsonSerializer.SerializeToUtf8Bytes(layout))!;
        var directories = layout.Workspaces.SelectMany(w => w.AllPanes()).ToDictionary(p => p.Id, p => p.Session?.WorkingDirectory ?? p.Directory);
        foreach (var pane in snapshot.Workspaces.SelectMany(w => w.AllPanes())) pane.Directory = directories[pane.Id];
        return snapshot;
    }
}
