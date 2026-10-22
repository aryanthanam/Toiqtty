using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

public sealed partial class SessionHostServer
{
    /// <summary>Protocol-3 payload decode. Runs only after the version and identity checks;
    /// a missing payload, out-of-range number or undefined enum is an ArgumentException,
    /// answered as InvalidRequest. Nothing here allocates on the valid path.</summary>
    private static class HostArgs
    {
        private static T Required<T>(T? args, string operation) where T : class
            => args ?? throw new ArgumentException("The " + operation + " request has no arguments.");
        internal static KeyArgs Key(HostRequest r)
        {
            var key = Required(r.Key, r.Operation);
            if (key.Action is not (TerminalKeyAction.Release or TerminalKeyAction.Press or TerminalKeyAction.Repeat)) throw new ArgumentException("Invalid key action.");
            return key;
        }
        internal static ResizeArgs Resize(HostRequest r)
        {
            var size = Required(r.Resize, "resize");
            if (size.Columns is < 2 or > 1000 || size.Rows is < 1 or > 500 || size.CellWidth is < 1 or > 1024 || size.CellHeight is < 1 or > 1024) throw new ArgumentException("Invalid terminal size.");
            return size;
        }
        internal static CaptureArgs Capture(HostRequest r)
        {
            var capture = Required(r.Capture, "capture");
            if (capture.KnownVersion < -1 || capture.KnownStatusRevision < -1 || capture.Images is { Length: > 65536 }) throw new ArgumentException("Invalid capture baseline.");
            return capture;
        }
        internal static Dictionary<string, long> Watch(HostRequest r)
        {
            var panes = Required(r.Watch, "watch").Panes;
            if (panes is null || panes.Count is 0 or > 256) throw new ArgumentException("Invalid output watch interests.");
            foreach (var pair in panes) if (string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 128 || pair.Value < -1) throw new ArgumentException("Invalid output watch interests.");
            return panes;
        }
        internal static CreateArgs Create(HostRequest r)
        {
            var create = Required(r.Create, "create");
            if (create.Command is null || create.Directory is null) throw new ArgumentException("Invalid shell creation.");
            if (create.Columns != 0 || create.Rows != 0)
                if (create.Columns is < 2 or > 1000 || create.Rows is < 1 or > 500) throw new ArgumentException("Invalid terminal size.");
            return create;
        }
        internal static AttachArgs Attach(HostRequest r)
        {
            var attach = Required(r.Attach, "attach");
            if (attach.Panes is null || attach.Settings is null || attach.Panes.Length is 0 or > HostProtocol.MaxMetadataPanes || attach.Settings.Length > attach.Panes.Length)
                throw new ArgumentException("Invalid attach request.");
            foreach (var settings in attach.Settings) if (settings is null) throw new ArgumentException("Invalid attach settings.");
            foreach (var pane in attach.Panes)
                if (pane is null || string.IsNullOrEmpty(pane.Id) || pane.Id.Length > 128 || pane.SettingsIndex < -1 || pane.SettingsIndex >= attach.Settings.Length)
                    throw new ArgumentException("Invalid attach pane.");
            return attach;
        }
        internal static DropCommitArgs DropCommit(HostRequest r)
        {
            var drop = Required(r.DropCommit, "drop-async");
            if (drop.Items is null) throw new ArgumentException("Drop items are required.");
            return drop;
        }
        internal static T Of<T>(T? args, string operation) where T : class => Required(args, operation);
    }

    private async Task<HostReply> Dispatch(HostRequest r, CancellationToken watchCancellation)
    {
        switch (r.Operation)
        {
            case "ping":
                lock (_gate) return new() { Layout = SavedSessionSnapshot.Capture(_layout), Panes = _sessions.Keys.ToArray(), TransportAcceptRetries = Interlocked.Read(ref _transportAcceptRetries) };
            case "states": return States(r);
            case "watch": return await WatchAsync(HostArgs.Watch(r), watchCancellation).ConfigureAwait(false);
            case "attach": return Attach(HostArgs.Attach(r));
            case "navigate": return await NavigateAsync(r);
            case "sync": return await SyncAsync(r);
            case "shutdown":
                await _layoutWrites.WaitAsync(_stop.Token);
                try
                {
                    string[] ids;
                    lock (_gate) { ids = _sessions.Keys.ToArray(); _created = true; _emptySince = Environment.TickCount64 - 2000; }
                    foreach (string id in ids) Remove(id);
                }
                finally { _layoutWrites.Release(); }
                return new();
            case "detach":
                Entry[] entries; lock (_gate) entries = _sessions.Values.ToArray();
                foreach (var entry in entries) { entry.Session.Focus(false); entry.Session.Drop(2); }
                return new();
        }
        if (r.Pane.Length is < 1 or > 128) throw new ArgumentException("Invalid pane identity.");
        if (r.Operation == "create") return await CreateAsync(r, HostArgs.Create(r));
        if (r.Operation == "close")
        {
            await _layoutWrites.WaitAsync(_stop.Token);
            try { Remove(r.Pane); return new(); }
            finally { _layoutWrites.Release(); }
        }
        Entry e; lock (_gate) e = _sessions.GetValueOrDefault(r.Pane) ?? throw new PaneNotFoundException();
        var s = e.Session;
        switch (r.Operation)
        {
            case "capture": return Capture(e, HostArgs.Capture(r));
            case "resize":
                var size = HostArgs.Resize(r);
                lock (e.CaptureGate)
                {
                    if (e.Closed) throw new SessionExitedException("This shell session has closed.");
                    ushort columns = (ushort)size.Columns, rows = (ushort)size.Rows;
                    var replacement = e.Cells.Length == columns * rows ? e.Cells : new Utf8TerminalBuffer(columns * rows);
                    s.Resize(columns, rows, (uint)size.CellWidth, (uint)size.CellHeight); s.Select(-1, -1);
                    e.Columns = columns; e.Rows = rows; e.Cells = replacement;
                }
                break;
            case "key": { var key = HostArgs.Key(r); s.Key(key.Key, key.Modifiers, r.Text, key.Action, key.Unshifted, key.Consumed); break; }
            case "copy-or-key":
            {
                var key = HostArgs.Key(r);
                string copied = s.CopyOrKey(key.Key, key.Modifiers, r.Text, key.Action, key.Unshifted, key.Consumed);
                return new() { Text = copied, Copied = copied.Length > 0, State = State(e) };
            }
            case "paste": s.Paste(r.Text, HostArgs.Of(r.Paste, "paste").Clipboard); break;
            case "send-text": s.SendText(r.Text); break;
            case "paste-checked": await s.PasteAsync(r.Text); break;
            case "clipboard": s.PasteClipboard(); break;
            case "scroll": s.Scroll(HostArgs.Of(r.Scroll, "scroll").Delta); break;
            case "scroll-to": s.ScrollTo(HostArgs.Of(r.ScrollTo, "scroll-to").Row); break;
            case "focus":
                bool focused = HostArgs.Of(r.Focus, "focus").Focused;
                s.Focus(focused); if (focused) Volatile.Write(ref e.PendingAttention, 0); break;
            case "select": { var select = HostArgs.Of(r.Select, "select"); s.Select(select.Start, select.End); break; }
            case "gesture":
            {
                var gesture = HostArgs.Of(r.Gesture, "gesture");
                return new() { Value = s.SelectionGesture(gesture.Action, gesture.XMilli / 1000f, gesture.YMilli / 1000f, gesture.Rectangle) };
            }
            case "mouse":
            {
                var mouse = HostArgs.Of(r.Mouse, "mouse");
                return new() { Value = s.Mouse(mouse.Action, mouse.Button, mouse.Modifiers, mouse.XMilli / 1000f, mouse.YMilli / 1000f) ? 1 : 0 };
            }
            case "drop":
            {
                var drop = HostArgs.Of(r.DropQuery, "drop");
                return new() { Value = s.Drop(drop.Action, drop.X, drop.Y, drop.Items, drop.Operations) };
            }
            case "drop-async":
            {
                var drop = HostArgs.DropCommit(r);
                return new() { Value = await s.DropAsync(drop.X, drop.Y, drop.Items, drop.Operations, TimeSpan.FromMilliseconds(Math.Clamp(drop.TimeoutMilliseconds, 1, 20000))) };
            }
            case "copy": return new() { Text = s.CopySelection() };
            case "hyperlink": { var cell = HostArgs.Of(r.Cell, "hyperlink"); return new() { Text = s.Hyperlink(cell.Column, cell.Row) }; }
            case "find": { var found = s.Find(r.Text, HostArgs.Of(r.Find, "find").Direction); return new() { Find = new(found.Count, found.Index) }; }
            case "settings": s.ApplySettings(r.Settings ?? throw new ArgumentException("Settings are required.")); break;
            default: throw new SessionHostException(HostErrorCode.UnknownOperation, "Unknown session operation.");
        }
        return new() { State = State(e) };
    }

    private HostReply States(HostRequest r)
    {
        var versions = r.StateVersions ?? throw new ArgumentException("State versions are required.");
        if (versions.Count is 0 or > HostProtocol.MaxMetadataPanes || versions.Keys.Any(id => string.IsNullOrEmpty(id) || id.Length > 128))
            throw new ArgumentException("Invalid metadata interests.");
        (string Id, int Seen, Entry? Entry)[] entries;
        lock (_gate) entries = versions.Select(pair => (pair.Key, pair.Value, _sessions.GetValueOrDefault(pair.Key))).ToArray();
        var states = new Dictionary<string, ShellState>(); var missing = new List<string>();
        foreach (var item in entries)
        {
            if (item.Entry is null) { missing.Add(item.Id); continue; }
            if (item.Entry.Session.Version != item.Seen) states.Add(item.Id, State(item.Entry));
        }
        return new() { States = states, Panes = missing.ToArray() };
    }

    // One reply for many reattaching panes: settings are applied by shared index,
    // and an absent pane is reported in Missing rather than failing the batch.
    private HostReply Attach(AttachArgs attach)
    {
        var entries = new Entry?[attach.Panes.Length];
        lock (_gate) for (int i = 0; i < entries.Length; i++) entries[i] = _sessions.GetValueOrDefault(attach.Panes[i].Id);
        var states = new Dictionary<string, ShellState>(entries.Length); List<string>? missing = null;
        for (int i = 0; i < entries.Length; i++)
        {
            var pane = attach.Panes[i];
            if (entries[i] is not { } entry) { (missing ??= []).Add(pane.Id); continue; }
            if (pane.SettingsIndex >= 0) entry.Session.ApplySettings(attach.Settings[pane.SettingsIndex]);
            states[pane.Id] = State(entry);
        }
        return new() { States = states, Missing = missing?.ToArray() ?? [] };
    }

    private async Task<HostReply> WatchAsync(Dictionary<string, long> interests, CancellationToken watchCancellation)
    {
        Task pulseForPanes;
        lock (_gate) lock (_outputGate)
        {
            List<Task>? pulses = null; bool ready = false;
            foreach (var pair in interests)
            {
                if (!_sessions.TryGetValue(pair.Key, out var entry)) continue;
                if (entry.OutputRevision > pair.Value) { ready = true; break; }
                (pulses ??= []).Add((entry.OutputPulse ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
            }
            pulseForPanes = ready ? Task.CompletedTask : pulses is null ? Task.Delay(250, watchCancellation)
                : Task.WhenAny(pulses.Append(Task.Delay(250, watchCancellation)));
        }
        try { await pulseForPanes.WaitAsync(TimeSpan.FromMilliseconds(250), watchCancellation).ConfigureAwait(false); } catch (TimeoutException) { }
        watchCancellation.ThrowIfCancellationRequested();
        lock (_gate) lock (_outputGate)
        {
            // The changed panes are the keys of OutputRevisions; an idle watch sends neither.
            Dictionary<string, long>? revisions = null;
            foreach (var pair in interests)
                if (_sessions.TryGetValue(pair.Key, out var entry) && entry.OutputRevision > pair.Value) (revisions ??= []).Add(pair.Key, entry.OutputRevision);
            return new() { OutputRevisions = revisions, OutputRevision = _outputRevision };
        }
    }

    private HostReply Capture(Entry e, CaptureArgs capture)
    {
        var s = e.Session;
        lock (e.CaptureGate)
        {
            if (e.Closed) throw new SessionExitedException("This shell session has closed.");
            int version = s.Version;
            if (capture.KnownVersion == version && capture.KnownFrameRevision == e.FrameRevision && Environment.TickCount64 < e.AnimationDue) return CaptureStatus(capture, new() { State = State(e) });
            // A row delta needs the client's frame to be this host's last capture at
            // the current geometry; anything else gets a full frame.
            bool delta = capture.KnownVersion >= 0 && capture.KnownFrameRevision == e.FrameRevision && e.CapturedVersion >= 0
                && e.CapturedColumns == e.Columns && e.CapturedRows == e.Rows;
            long baseline = e.FrameRevision;
            if (!s.TryReadSnapshot(e.Cells, out var snapshot)) return CaptureStatus(capture, new() { State = State(e) });
            delta = delta && snapshot!.Frame.Columns == e.CapturedColumns && snapshot.Frame.Rows == e.CapturedRows && snapshot.ChangedRows.Count < snapshot.Frame.Rows;
            Captured(e, snapshot!, version);
            var known = capture.Images is { Length: > 0 } images ? new HashSet<ulong>(images) : [];
            return CaptureStatus(capture, new()
            {
                State = State(e),
                CapturedVersion = version,
                FrameRevision = e.FrameRevision,
                DeltaBaseRevision = delta ? baseline : null,
                Scrollbar = snapshot!.Scrollbar,
                // Style runs fall back to per-cell encoding internally when smaller.
                Binary = HostSnapshotCodec.EncodeUtf8Runs(snapshot, e.Cells, known, delta)
            });
        }
    }

    private static HostReply CaptureStatus(CaptureArgs capture, HostReply reply)
    {
        if (reply.State is not { } state) return reply;
        var status = state.Status ?? throw new InvalidOperationException("The owned shell status is incomplete.");
        reply.StatusRevision = status.Revision;
        // A matching baseline omits the immutable lists the client already holds.
        if (capture.KnownStatusRevision == status.Revision) reply.State = state with { Status = null };
        return reply;
    }
}
