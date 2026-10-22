using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

public sealed partial class SessionHostServer
{
    // Serializes layout commits, not input/capture or the registry lock.
    private readonly SemaphoreSlim _layoutWrites = new(1, 1);
    private static Task WriteLayoutAsync(SavedSession snapshot) => Task.Run(() =>
    {
        try { SessionStore.Save(snapshot); }
        catch (Exception ex) when (ex is (IOException and not SessionHostException) or UnauthorizedAccessException)
        { throw new SessionHostException(HostErrorCode.StorageFailed, ex.Message, ex); }
    });

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
                _layout = layout; Applied(request);
                var retained = layout.Workspaces.SelectMany(w => w.AllPanes()).Where(p => !p.IsBrowser).Select(p => p.Id).ToHashSet();
                removed = _sessions.Where(pair => !retained.Contains(pair.Key) && !CreatedAfter(pair.Value.CreatedEpoch, pair.Value.CreatedRevision, request)).Select(pair => pair.Key).ToArray();
                // A shell still spawning is judged as if it had registered: dropped unless created after this sync.
                foreach (var (id, pending) in _pendingCreates)
                    if (!retained.Contains(id) && !CreatedAfter(pending.Epoch, pending.Revision, request)) pending.Cancelled = true;
            }
            foreach (string id in removed) Remove(id);
            return new();
        }
        finally { _layoutWrites.Release(); }
    }

    private static bool CreatedAfter(string? epoch, long revision, HostRequest sync)
        => sync.LayoutEpoch is not null && epoch == sync.LayoutEpoch && revision > sync.LayoutRevision;

    // Caller holds _gate. Records the newest stamped layout for the stale-create guard.
    private void Applied(HostRequest request)
    {
        if (request.LayoutEpoch is null) return;
        if (request.LayoutEpoch != _layoutEpoch || request.LayoutRevision > _layoutRevision) { _layoutEpoch = request.LayoutEpoch; _layoutRevision = request.LayoutRevision; }
    }

    // Caller holds _gate. A layout stamped older than one already applied from the same epoch is stale.
    private bool Superseded(HostRequest request)
        => request.LayoutEpoch is not null && request.LayoutEpoch == _layoutEpoch && request.LayoutRevision < _layoutRevision;

    private async Task<HostReply> CreateAsync(HostRequest request, CreateArgs create)
    {
        if (request.Layout is not null) ValidateLayout(request.Layout);
        // A reattach writes no layout and a new pane's spawn takes no layout lock, so
        // neither queues behind syncs, navigation saves, closes or another pane's spawn.
        Entry? existing; Task<Entry>? spawning = null; PendingCreate? pending = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(request.Pane, out existing)) { }
            else if (_pendingCreates.TryGetValue(request.Pane, out var other)) spawning = other.Completion.Task;
            else
            {
                if (_sessions.Count + _pendingCreates.Count >= 256 || create.Command.Length is < 1 or > 32768 || create.Directory.Length > 32768)
                    throw new SessionHostException(HostErrorCode.LimitExceeded, "Shell creation exceeds the session limits.");
                _pendingCreates.Add(request.Pane, pending = new(request.LayoutEpoch, request.LayoutRevision));
            }
        }
        if (existing is not null) return Reattach(existing, request);
        // A concurrent create of the same pane shares the first one's shell.
        if (spawning is not null) return Reattach(await spawning.WaitAsync(_stop.Token).ConfigureAwait(false), request);
        Entry entry;
        try { entry = Spawn(request, create); }
        catch (Exception ex)
        {
            lock (_gate) _pendingCreates.Remove(request.Pane);
            pending!.Completion.TrySetException(ex); throw;
        }
        bool cancelled;
        lock (_gate)
        {
            _pendingCreates.Remove(request.Pane);
            cancelled = pending!.Cancelled;
            if (!cancelled) { _sessions.Add(request.Pane, entry); _created = true; _emptySince = Environment.TickCount64; }
        }
        if (cancelled)
        {
            // Closed, shut down or synced away while spawning: answer as if the close
            // had come after the create, as it did when creates held the layout lock.
            var state = State(entry); DisposeEntry(entry);
            pending.Completion.TrySetResult(entry);
            return new() { State = state, Created = true };
        }
        if (request.Layout is not null)
        {
            try
            {
                await _layoutWrites.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    bool stale; lock (_gate) stale = Superseded(request);
                    if (!stale)
                    {
                        await WriteLayoutAsync(request.Layout).ConfigureAwait(false);
                        lock (_gate) { _layout = request.Layout; Applied(request); }
                    }
                }
                finally { _layoutWrites.Release(); }
            }
            catch (Exception ex) { Remove(request.Pane); pending.Completion.TrySetException(ex); throw; }
        }
        pending.Completion.TrySetResult(entry);
        return new() { State = State(entry), Created = true };
    }

    private Entry Spawn(HostRequest request, CreateArgs create)
    {
        // 0 keeps the default size; the first resize supplies cell pixel metrics.
        ushort columns = create.Columns == 0 ? (ushort)100 : (ushort)create.Columns, rows = create.Rows == 0 ? (ushort)30 : (ushort)create.Rows;
        if (_spawnDelayForCheck > 0) Thread.Sleep(_spawnDelayForCheck);
        TerminalSession session;
        try { session = new(create.Command, create.Directory, request.Settings, columns, rows); }
        // Only the expected spawn failures; anything else stays Internal and is logged.
        catch (Exception ex) when (ex is (IOException and not SessionHostException) or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
        { throw new SessionHostException(HostErrorCode.ShellStartFailed, ex.Message, ex); }
        // CreatePseudoConsole failures surface as HRESULTs.
        catch (System.Runtime.InteropServices.COMException ex)
        { throw new SessionHostException(HostErrorCode.ConsoleFailed, ex.Message, ex); }
        var entry = new Entry(session) { CreatedEpoch = request.LayoutEpoch, CreatedRevision = request.LayoutRevision, Columns = columns, Rows = rows };
        // Capture reads cols*rows cells; the default buffer only fits 100x30.
        if (columns * rows != entry.Cells.Length) entry.Cells = new(columns * rows);
        entry.Session.Attention += () => { Interlocked.Increment(ref entry.Attention); Volatile.Write(ref entry.PendingAttention, 1); };
        entry.Session.OutputChanged += () => OutputChanged(entry);
        return entry;
    }

    private HostReply Reattach(Entry entry, HostRequest request)
    {
        // The shared spawn of a concurrent create was closed before it registered.
        if (entry.Closed) throw new SessionExitedException("This shell session has closed.");
        if (request.Settings is not null) entry.Session.ApplySettings(request.Settings);
        return new() { State = State(entry), Created = false };
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
