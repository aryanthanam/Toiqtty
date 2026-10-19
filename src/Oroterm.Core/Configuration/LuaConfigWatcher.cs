namespace Oroterm.Configuration;

public sealed class LuaConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    public LuaConfigWatcher(string path, Action changed)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(folder);
        _timer = new(_ => changed(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new(folder, "*.lua") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Deleted += Changed; _watcher.Renamed += Changed;
        _watcher.Error += (_, _) => changed(); _watcher.EnableRaisingEvents = true;
    }
    private void Changed(object sender, FileSystemEventArgs args) { try { _timer.Change(350, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    public void Dispose() { _watcher.Dispose(); _timer.Dispose(); }
}
