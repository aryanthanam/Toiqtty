using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

public sealed partial class SessionHostServer
{
    // Serializes layout commits, not input/capture or the registry lock.
    private readonly SemaphoreSlim _layoutWrites = new(1, 1);
    private static Task WriteLayoutAsync(SavedSession snapshot) => Task.Run(() => SessionStore.Save(snapshot));

    private void RefreshDirectories(SavedSession layout)
    {
        foreach (var pane in layout.Workspaces.SelectMany(w => w.AllPanes()))
            if (_sessions.TryGetValue(pane.Id, out var entry)) pane.Directory = entry.Session.WorkingDirectory;
    }

    private async Task<HostReply> NavigateAsync(HostRequest request)
    {
        var navigation = request.Navigation ?? throw new ArgumentException("Navigation state is required.");
        if (navigation.Workspaces is null || navigation.Workspaces.Length > 64
            || navigation.Workspaces.Any(w => w is null || w.Id is null || w.FocusedPanes is null || w.FocusedPanes.Count > 64))
            throw new ArgumentException("Invalid navigation state.");
        await _layoutWrites.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            SavedSession snapshot;
            lock (_gate)
            {
                snapshot = SavedSessionSnapshot.Capture(_layout);
                if (snapshot.Workspaces.Any(w => w.Id == navigation.SelectedWorkspace)) snapshot.SelectedWorkspace = navigation.SelectedWorkspace;
                foreach (var update in navigation.Workspaces)
                {
                    var workspace = snapshot.Workspaces.FirstOrDefault(w => w.Id == update.Id); if (workspace is null) continue;
                    if (workspace.Tabs.Any(t => t.Id == update.ActiveTabId)) workspace.ActiveTabId = update.ActiveTabId;
                    foreach (var tab in workspace.Tabs)
                        if (update.FocusedPanes.TryGetValue(tab.Id, out var pane) && tab.Root.Leaves().Any(p => p.Id == pane)) tab.FocusedPaneId = pane;
                }
                RefreshDirectories(snapshot);
            }
            await WriteLayoutAsync(snapshot).ConfigureAwait(false);
            lock (_gate) _layout = snapshot;
            return new();
        }
        finally { _layoutWrites.Release(); }
    }

    private async Task<HostReply> SyncAsync(HostRequest request)
    {
        var layout = request.Layout ?? throw new ArgumentException("Layout is required.");
        ValidateLayout(layout);
        await _layoutWrites.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) RefreshDirectories(layout);
            await WriteLayoutAsync(layout).ConfigureAwait(false);
            string[] removed;
            lock (_gate)
            {
                _layout = layout;
                var retained = layout.Workspaces.SelectMany(w => w.AllPanes()).Where(p => !p.IsBrowser).Select(p => p.Id).ToHashSet();
                removed = _sessions.Where(pair => !retained.Contains(pair.Key) && !CreatedAfter(pair.Value, request)).Select(pair => pair.Key).ToArray();
            }
            foreach (string id in removed) Remove(id);
            return new();
        }
        finally { _layoutWrites.Release(); }
    }

    private static bool CreatedAfter(Entry entry, HostRequest sync)
        => sync.LayoutEpoch is not null && entry.CreatedEpoch == sync.LayoutEpoch && entry.CreatedRevision > sync.LayoutRevision;

    private async Task<HostReply> CreateAsync(HostRequest request)
    {
        if (request.Layout is not null) ValidateLayout(request.Layout);
        await _layoutWrites.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            Entry? entry;
            lock (_gate)
            {
                _sessions.TryGetValue(request.Pane, out entry);
                if (entry is null && (_sessions.Count >= 256 || request.Text.Length is < 1 or > 32768 || request.Directory.Length > 32768))
                    throw new ArgumentException("Shell creation exceeds the session limits.");
                if (entry is null) _creating++;
            }
            if (entry is not null)
            {
                if (request.Settings is not null) entry.Session.ApplySettings(request.Settings);
                return new() { State = State(entry) };
            }
            try
            {
                entry = new(new TerminalSession(request.Text, request.Directory, request.Settings)) { CreatedEpoch = request.LayoutEpoch, CreatedRevision = request.LayoutRevision };
                var observed = entry;
                entry.Session.Attention += () => { Interlocked.Increment(ref observed.Attention); Volatile.Write(ref observed.PendingAttention, 1); };
                entry.Session.OutputChanged += () => OutputChanged(observed);
                lock (_gate) { _sessions.Add(request.Pane, entry); _created = true; _emptySince = Environment.TickCount64; }
            }
            finally { lock (_gate) _creating--; }
            if (request.Layout is not null)
            {
                try
                {
                    await WriteLayoutAsync(request.Layout).ConfigureAwait(false);
                    lock (_gate) _layout = request.Layout;
                }
                catch { Remove(request.Pane); throw; }
            }
            return new() { State = State(entry) };
        }
        finally { _layoutWrites.Release(); }
    }

    private async Task PersistDirectoriesAsync()
    {
        await _layoutWrites.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            SavedSession? snapshot = null;
            lock (_gate)
            {
                bool changed = _layout.Workspaces.SelectMany(w => w.AllPanes()).Any(p => _sessions.TryGetValue(p.Id, out var entry) && p.Directory != entry.Session.WorkingDirectory);
                if (changed) { snapshot = SavedSessionSnapshot.Capture(_layout); RefreshDirectories(snapshot); }
            }
            if (snapshot is not null)
            {
                await WriteLayoutAsync(snapshot).ConfigureAwait(false);
                lock (_gate) _layout = snapshot;
            }
        }
        finally { _layoutWrites.Release(); }
    }
}
