using System.IO.Pipes;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

/// <summary>The only owner of persistent ConPTY handles and libghostty instances.</summary>
public sealed partial class SessionHostServer(string folder) : IAsyncDisposable
{
    private sealed class Entry(TerminalSession session)
    {
        public readonly TerminalSession Session = session;
        public readonly object CaptureGate = new();
        public Utf8TerminalBuffer Cells = new(3000);
        public bool Closed;
        public ushort Columns = 100, Rows = 30;
        public long AnimationDue = long.MaxValue;
        public long Attention;
        public int PendingAttention;
        public int CapturedVersion = -1;
        public long FrameRevision, LastCapture;
        public uint CapturedColumns, CapturedRows;
        public bool RenderHeld;
        public long OutputRevision;
        public TaskCompletionSource? OutputPulse;
        public string? CreatedEpoch;
        public long CreatedRevision;
    }
    // A shell spawning outside every lock. The id is reserved under _gate so the pane
    // cap counts it and a concurrent create of the same pane awaits this one.
    private sealed class PendingCreate(string? epoch, long revision)
    {
        public readonly TaskCompletionSource<Entry> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly string? Epoch = epoch;
        public readonly long Revision = revision;
        // Set under _gate by close, shutdown or a sync that drops the pane; the spawn disposes it.
        public bool Cancelled;
    }
    // Smoke checks stretch the spawn to observe what it no longer blocks; shells never inherit it.
    private const string SpawnDelayVariable = "TOIQTTY_HOST_SPAWN_DELAY_MS";
    private readonly int _spawnDelayForCheck = int.TryParse(Environment.GetEnvironmentVariable(SpawnDelayVariable), out int delay) ? Math.Clamp(delay, 0, 10000) : 0;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _sessions = [];
    private readonly Dictionary<string, PendingCreate> _pendingCreates = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connections = new(32);
    // Leave at least half the accepted connections available to input/control
    // requests, even when several frontends retain idle read connections.
    private readonly SemaphoreSlim _persistentReads = new(16);
    private readonly List<Task> _requests = [];
    private readonly string _folder = folder, _identity = Guid.NewGuid().ToString("N");
    private SavedSession _layout = SessionStore.Load();
    private bool _created;
    // The newest (epoch, revision) applied to _layout; an older create layout is not written over it.
    private string? _layoutEpoch;
    private long _layoutRevision;
    private bool _shutdownPending;
    private int _shutdownReplies;
    private long _emptySince = Environment.TickCount64;
    private long _outputRevision;
    private long _transportAcceptRetries;
    private readonly Lock _outputGate = new();
    private void OutputChanged(Entry entry)
    {
        lock (_outputGate)
        {
            entry.OutputRevision = ++_outputRevision;
            entry.OutputPulse?.TrySetResult(); entry.OutputPulse = null;
        }
    }
    public async Task Run()
    {
        Directory.CreateDirectory(_folder);
        await using var lease = new FileStream(Path.Combine(_folder, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using (var file = File.Create(Path.Combine(_folder, "host.json")))
        using (var json = new System.Text.Json.Utf8JsonWriter(file))
        {
            json.WriteStartObject(); json.WriteNumber("processId", Environment.ProcessId);
            json.WriteString("host", _identity); json.WriteNumber("protocol", HostProtocol.Version); json.WriteEndObject();
        }
        string? readyName = Environment.GetEnvironmentVariable("TOIQTTY_HOST_READY_EVENT");
        Environment.SetEnvironmentVariable("TOIQTTY_HOST_READY_EVENT", null);
        Environment.SetEnvironmentVariable(SpawnDelayVariable, null);
        Task monitor = MonitorLifetime();
        bool first = true;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _connections.WaitAsync(_stop.Token);
                NamedPipeServerStream pipe;
                try
                {
                    // The first instance must own the name: another process holding it
                    // would receive every frontend's input. Later instances join it.
                    pipe = new NamedPipeServerStream(HostProtocol.PipeName(_folder), PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));
                    first = false;
                }
                catch (IOException ex) when (!first)
                {
                    // Transport exhaustion must never end the shells this host owns.
                    _connections.Release(); Interlocked.Increment(ref _transportAcceptRetries); LogFault(ex);
                    await Task.Delay(50, _stop.Token); continue;
                }
                if (readyName is not null)
                {
                    // A frontend may have exited while the child was loading.
                    // Readiness notification must never decide host ownership.
                    try { using var ready = EventWaitHandle.OpenExisting(readyName); ready.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    finally { readyName = null; }
                }
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232 or 233)
                {
                    // A client can close between ConnectNamedPipe creation and
                    // acceptance. That connection owns no session or request.
                    pipe.Dispose(); _connections.Release(); Interlocked.Increment(ref _transportAcceptRetries); continue;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { pipe.Dispose(); _connections.Release(); throw; }
                catch (Exception ex)
                {
                    pipe.Dispose(); _connections.Release(); Interlocked.Increment(ref _transportAcceptRetries); LogFault(ex);
                    await Task.Delay(50, _stop.Token); continue;
                }
                // Async handlers can run synchronously through ready I/O into
                // native resize/capture work. Do not stall new pipe acceptance
                // behind that work; the existing connection limit still bounds it.
                Task request = Task.Run(() => Handle(pipe));
                lock (_requests) { _requests.RemoveAll(t => t.IsCompleted); _requests.Add(request); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _stop.Cancel(); await monitor;
            Task[] pending; lock (_requests) pending = _requests.ToArray();
            await Task.WhenAll(pending);
        }
    }
    private async Task Handle(NamedPipeServerStream pipe)
    {
        // Release the slot only after the pipe instance is closed, so the accept
        // loop never creates an instance beyond the pipe's instance limit.
        try { await HandleConnection(pipe); }
        catch (Exception ex) { LogFault(ex); }
        finally { _connections.Release(); }
    }
    private void LogFault(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(_folder, "host-error.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
    private async Task HandleConnection(NamedPipeServerStream pipe)
    {
        await using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            bool shutdownReply = false, persistentRead = false;
            try
            {
                do
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(25));
                    HostRequest request;
                    try { request = await HostProtocol.ReadJson<HostRequest>(pipe, deadline.Token); }
                    // A complete frame whose payload does not fit the typed envelope is
                    // answered rather than dropped; the frame was read whole, so the stream
                    // stays framed. Payload ranges are checked later, per operation.
                    catch (IOException ex) when (ex.InnerException is System.Text.Json.JsonException)
                    {
                        await HostProtocol.WriteJson(pipe, new HostReply { Host = _identity, ProcessId = Environment.ProcessId, Error = "Invalid session request.", ErrorCode = HostErrorCode.InvalidRequest }, deadline.Token);
                        break;
                    }
                    HostReply reply;
                    try
                    {
                        // Version and identity come before any payload decode, so a request
                        // from another protocol always gets the mismatch reply.
                        if (request.Protocol != HostProtocol.Version) throw new SessionHostException(HostErrorCode.ProtocolMismatch, "Incompatible session-host protocol. Stop existing sessions before upgrading the host.");
                        if (request.Operation is null || request.Pane is null || request.Text is null) throw new ArgumentException("Invalid session request.");
                        if (request.Operation != "ping" && request.Host != _identity) throw new SessionHostException(HostErrorCode.HostChanged, "The session host changed. Reconnect explicitly; input was not sent.");
                        if (request.Operation == "shutdown")
                        {
                            lock (_gate) { _shutdownPending = true; Interlocked.Increment(ref _shutdownReplies); }
                            shutdownReply = true;
                        }
                        if (request.Operation == "watch")
                        {
                            using var watchStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                            var disconnected = ObserveWatchDisconnect(pipe, watchStop);
                            try { reply = await Dispatch(request, watchStop.Token); }
                            finally { watchStop.Cancel(); await disconnected; }
                        }
                        else reply = await Dispatch(request, _stop.Token);
                    }
                    // Every handled failure is answered. Only cancellation of this
                    // connection (host stop, deadline, watch disconnect) closes it unanswered.
                    catch (Exception ex) when (ex is not OperationCanceledException { CancellationToken.IsCancellationRequested: true })
                    {
                        if (ex is not (IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)) LogFault(ex);
                        reply = new() { Error = ex.Message, ErrorCode = ErrorCodeFor(ex) };
                    }
                    if (reply.Binary.Length > HostProtocol.MaxSnapshotBytes) reply = new() { Error = HostProtocol.ReplyTooLarge, ErrorCode = HostErrorCode.ReplyTooLarge };
                    reply.Host = _identity; reply.ProcessId = Environment.ProcessId; reply.BinaryLength = reply.Binary.Length;
                    bool reusable = request.ReuseReadConnection && (request.Operation is "capture" or "watch" or "states") && reply.Error is null;
                    if (reusable && !persistentRead) persistentRead = _persistentReads.Wait(0);
                    reply.KeepAlive = reusable && persistentRead;
                    try { await HostProtocol.WriteJson(pipe, reply, deadline.Token); }
                    catch (HostMessageTooLargeException)
                    {
                        // WriteJson checks its limit before sending any header. A small,
                        // explicit error keeps the client framed; for states it lets a
                        // read-only client split the batch without losing unrelated panes.
                        reply = HostProtocol.OversizedReply(_identity, Environment.ProcessId);
                        await HostProtocol.WriteJson(pipe, reply, deadline.Token);
                    }
                    if (reply.Binary.Length > 0) await pipe.WriteAsync(reply.Binary, deadline.Token);
                    if (!reply.KeepAlive) break;
                } while (!_stop.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException) { System.Diagnostics.Debug.WriteLine(ex); }
            finally
            {
                // Acknowledged shutdowns must finish writing before cancellation
                // tears down their pipe. Failed/disconnected requesters still stop
                // the host after the already-authorized shutdown is processed.
                if (shutdownReply && Interlocked.Decrement(ref _shutdownReplies) == 0) _stop.Cancel();
                if (persistentRead) _persistentReads.Release();
            }
        }
    }
    // Typed faults carry their own code; the rest map by exception kind. Only error replies pay for this.
    private static HostErrorCode ErrorCodeFor(Exception ex) => ex switch
    {
        SessionHostException coded => coded.Code,
        HostMessageTooLargeException => HostErrorCode.ReplyTooLarge,
        ArgumentException => HostErrorCode.InvalidRequest,
        TimeoutException or OperationCanceledException => HostErrorCode.Timeout,
        System.ComponentModel.Win32Exception => HostErrorCode.ConsoleFailed,
        InvalidOperationException => HostErrorCode.TerminalFailed,
        _ => HostErrorCode.Internal
    };
    private static async Task ObserveWatchDisconnect(NamedPipeServerStream pipe, CancellationTokenSource watchStop)
    {
        try { await pipe.ReadAsync(new byte[1], watchStop.Token).ConfigureAwait(false); watchStop.Cancel(); }
        catch (OperationCanceledException) when (watchStop.IsCancellationRequested) { }
        catch (IOException) { watchStop.Cancel(); }
    }
    private ShellState State(Entry entry)
    {
        var s = entry.Session;
        return new(s.Version, s.HasExited, s.ExitCode, s.Title, s.StatusSummary, s.Directory, s.WorkingDirectory, s.LocalDirectory, s.Performance, s.Status, Interlocked.Read(ref entry.Attention), Volatile.Read(ref entry.PendingAttention) != 0) { MouseTracking = s.MouseTracking };
    }
    private static void ValidateLayout(SavedSession layout)
    {
        if (layout.Version != 5 || layout.Settings is null || layout.Workspaces is null || layout.Workspaces.Count > 64) throw new ArgumentException("Invalid session layout.");
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(layout).Length > 1024 * 1024) throw new ArgumentException("Saved sessions exceed the size limit.");
        var identities = new HashSet<string>(); int panes = 0;
        // JSON can carry nulls the model's non-nullable types do not admit; they
        // are invalid input, never a NullReferenceException that drops the reply.
        foreach (var session in layout.Workspaces)
        {
            if (session is null) throw new ArgumentException("Invalid session layout.");
            if (session.Theme is { IsValid: false }) throw new ArgumentException("Invalid session theme.");
            Add(session.Id); if (session.Tabs is null || session.Tabs.Count is < 1 or > 64) throw new ArgumentException("Invalid session tabs.");
            foreach (var tab in session.Tabs) { if (tab is null) throw new ArgumentException("Invalid session tabs."); Add(tab.Id); Walk(tab.Root, 0); }
        }
        void Add(string id) { if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !identities.Add(id)) throw new ArgumentException("Duplicate or invalid session identity."); }
        void Walk(PaneNode node, int depth)
        {
            if (node is null || depth > 12 || string.IsNullOrWhiteSpace(node.Command) || node.Kind is not ("terminal" or "browser") ||
                node.BrowserSourceId is null || node.BrowserSourceId.Length > 128 || (node.IsBrowser && !BrowserAddress.IsAllowed(node.BrowserUrl))) throw new ArgumentException("Invalid pane tree.");
            if (node.First is null && node.Second is null) { Add(node.Id); if (++panes > 256) throw new ArgumentException("Too many panes."); }
            else { if (node.First is null || node.Second is null) throw new ArgumentException("Incomplete pane split."); Walk(node.First, depth + 1); Walk(node.Second, depth + 1); }
        }
    }
    private void Remove(string id)
    {
        Entry? removed;
        lock (_gate)
        {
            if (_pendingCreates.TryGetValue(id, out var pending)) pending.Cancelled = true;
            _sessions.Remove(id, out removed);
            if (_sessions.Count == 0) _emptySince = Environment.TickCount64;
        }
        if (removed is not null) DisposeEntry(removed);
    }
    private static void DisposeEntry(Entry entry)
    {
        lock (entry.CaptureGate)
        {
            if (entry.Closed) return;
            entry.Closed = true; entry.Session.Dispose();
            entry.Cells = new(0);
        }
    }
    private static void Captured(Entry entry, TerminalSnapshot snapshot, int version)
    {
        entry.LastCapture = Environment.TickCount64; entry.CapturedVersion = version; entry.FrameRevision++;
        entry.CapturedColumns = snapshot.Frame.Columns; entry.CapturedRows = snapshot.Frame.Rows;
        entry.RenderHeld = snapshot.Frame.RenderHeld != 0;
        entry.AnimationDue = snapshot.Frame.AnimationDelay == uint.MaxValue ? long.MaxValue : entry.LastCapture + snapshot.Frame.AnimationDelay;
    }
    private async Task MonitorLifetime()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(250, _stop.Token);
                Entry[] entries;
                lock (_gate)
                {
                    entries = _sessions.Values.ToArray();
                    // A shell under construction is not yet registered but keeps the host alive.
                    bool live = _pendingCreates.Count > 0 || _sessions.Values.Any(e => !e.Session.HasExited);
                    if (live) _emptySince = Environment.TickCount64;
                    else if (!_shutdownPending && Environment.TickCount64 - _emptySince > (_created ? 1500 : 30000)) { _stop.Cancel(); return; }
                }
                try { await PersistDirectoriesAsync(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
                // Hidden terminals still advance upstream animation clocks and
                // synchronized-output timeouts. A frame revision prevents a
                // window from missing changes captured by this maintenance pass.
                foreach (var entry in entries)
                {
                    lock (entry.CaptureGate)
                    {
                        if (Environment.TickCount64 - entry.LastCapture < 500) continue;
                        int version = entry.Session.Version;
                        if (version == entry.CapturedVersion && Environment.TickCount64 < entry.AnimationDue && !entry.RenderHeld) continue;
                        if (entry.Session.TryReadSnapshot(entry.Cells, out var snapshot)) Captured(entry, snapshot!, version);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); Entry[] entries; lock (_gate) { entries = _sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var entry in entries) { DisposeEntry(entry); await entry.Session.DisposeAsync(); }
        _stop.Dispose(); _connections.Dispose(); _persistentReads.Dispose(); _layoutWrites.Dispose();
    }
}
