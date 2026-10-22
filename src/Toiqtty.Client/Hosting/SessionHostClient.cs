using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Toiqtty.Models;

namespace Toiqtty.Hosting;

public sealed class SessionHostVersionException() : SessionHostException(HostErrorCode.ProtocolMismatch,
    "A different toiqtty session-host version is still running. In the installed version, choose ‘Quit toiqtty and stop all sessions’, then open this version again. Your running shells have been left intact.");

/// <summary>The host received and answered the request with an error. Unlike a
/// transport failure this is unambiguous, so later requests remain deliverable.</summary>
public sealed class HostRejectedException(HostErrorCode code, string message) : SessionHostException(code, message);

/// <summary>No usable reply: connect failure, EOF, broken pipe or a different host.
/// Whether the request ran is unknown, so an input channel must not continue.</summary>
public sealed class HostTransportException(HostErrorCode code, string message, Exception? inner = null) : SessionHostException(code, message, inner);

public sealed partial class SessionHostClient
{
    public string Folder { get; }
    private readonly string _pipeName;
    public string Identity { get; private set; } = "";
    public int ProcessId { get; private set; }
    public SavedSession? Layout { get; private set; }
    public string[] Panes { get; private set; } = [];
    // Set after repeated watch failures; visible panes then poll on short timers.
    private volatile bool _outputWatchDegraded;
    internal bool OutputWatchDegraded => _outputWatchDegraded;
    internal bool DisableReusableReadsForCheck { get; set; }
    internal long ReadConnectionsOpenedForCheck, ReadConnectionsReusedForCheck;
    private readonly SemaphoreSlim _layoutSaveGate = new(1, 1);
    private readonly Lock _navigationGate = new();
    private HostRequest? _pendingNavigation;
    private List<TaskCompletionSource> _navigationCompletions = [];
    private bool _savingNavigation;
    internal long WatchRequestsForCheck, WatchNotificationsForCheck;
    internal bool CancelRetiredWatchForCheck { get; set; }
    private readonly Lock _watchGate = new();
    private sealed record OutputSubscription(Action<string> Listener, Func<bool> Demand, Func<bool>? Visible);
    private readonly Dictionary<string, OutputSubscription[]> _outputListeners = [];
    private string[] _lastInterests = [];
    private string[] _lastVisibleInterests = [];
    private readonly SemaphoreSlim _watchWake = new(0, 1);
    private CancellationTokenSource? _watchStop;
    internal void AddOutputListener(string pane, Action<string> listener, Func<bool>? demand = null, Func<bool>? visible = null)
    {
        lock (_watchGate)
        {
            var subscription = new OutputSubscription(listener, demand ?? AlwaysDemand, visible);
            _outputListeners[pane] = _outputListeners.TryGetValue(pane, out var existing) ? [.. existing, subscription] : [subscription];
            if (!_outputWatchDegraded && _watchStop is null)
            {
                _watchStop = new(); var token = _watchStop.Token;
                _ = Task.Run(() => WatchOutput(token));
            }
        }
        OutputDemandChanged();
    }
    private static bool AlwaysDemand() => true;
    internal void OutputDemandChanged() { try { _watchWake.Release(); } catch (SemaphoreFullException) { } }
    internal void RemoveOutputListener(string pane, Action<string> listener)
    {
        lock (_watchGate)
        {
            if (_outputListeners.TryGetValue(pane, out var existing))
            {
                int index = Array.FindLastIndex(existing, item => item.Listener == listener);
                if (index >= 0)
                {
                    if (existing.Length == 1) _outputListeners.Remove(pane);
                    else _outputListeners[pane] = [.. existing[..index], .. existing[(index + 1)..]];
                }
            }
            if (_outputListeners.Count == 0) { _lastInterests = []; _lastVisibleInterests = []; _watchStop?.Cancel(); _watchStop?.Dispose(); _watchStop = null; }
        }
        OutputDemandChanged();
    }
    private async Task WatchOutput(CancellationToken stop)
    {
        var seen = new Dictionary<string, long>();
        // A watch reply does not invalidate the independent demand signal.
        // Reuse its pending wait across replies instead of canceling a task
        // and allocating a linked cancellation source on every output batch.
        using var demandStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var readConnection = new HostReadConnection(this);
        Task? demand = null;
        Task DemandWake() => demand ??= _watchWake.WaitAsync(demandStop.Token);
        int failures = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        lock (_watchGate) if (seen.Count > _outputListeners.Count)
                            foreach (var pane in seen.Keys.Where(pane => !_outputListeners.ContainsKey(pane)).ToArray()) seen.Remove(pane);
                        if (OutputInterests().Length == 0) { await DemandWake().ConfigureAwait(false); demand = null; continue; }
                        // One bounded read covers visible panes through temporary
                        // per-frame capture/backpressure pauses. Request admission
                        // still sleeps whenever none of them currently need output.
                        string[] interests = OutputInterests(visible: !CancelRetiredWatchForCheck);
                        Interlocked.Increment(ref WatchRequestsForCheck);
                        // Every interest carries its own seen revision, so the host
                        // never needs the global revision fallback.
                        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        var pending = CallCoreAsync(HostRequests.Watch(interests.ToDictionary(id => id, id => seen.GetValueOrDefault(id, -1))),
                            TimeSpan.FromSeconds(3), requestStop.Token, null,
                            readConnection: DisableReusableReadsForCheck ? null : readConnection);
                        bool changed = false;
                        while (!pending.IsCompleted)
                        {
                            var wake = DemandWake();
                            if (await Task.WhenAny(pending, wake).ConfigureAwait(false) == pending)
                                break;
                            await wake.ConfigureAwait(false);
                            demand = null;
                            var current = OutputInterests(visible: !CancelRetiredWatchForCheck);
                            // Existing interests may retire while a capture covers
                            // them. Only a newly interested pane needs an early wake.
                            if ((CancelRetiredWatchForCheck && current.Length == 0) || (!ReferenceEquals(current, interests) && current.Except(interests).Any()))
                            { changed = true; requestStop.Cancel(); break; }
                        }
                        if (changed) { try { await pending.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or IOException) { } continue; }
                        var reply = await pending.ConfigureAwait(false);
                        // A temporary capture/backpressure pause need not destroy
                        // its read channel. Finish the bounded outstanding watch,
                        // but acknowledge only currently demanded panes so a later
                        // resume still receives output published while it slept.
                        var active = OutputInterests();
                        if (reply.OutputRevisions is { } revisions) foreach (var pair in revisions)
                        {
                            bool visible = false;
                            lock (_watchGate) if (_outputListeners.TryGetValue(pair.Key, out var subscriptions))
                                foreach (var subscription in subscriptions) if (subscription.Visible?.Invoke() == true) { visible = true; break; }
                            // Remote pane owners explicitly wake captures when a
                            // frame is consumed or a view resumes. Generic demand
                            // subscribers retain their unacknowledged catch-up.
                            if (CancelRetiredWatchForCheck || visible || Array.IndexOf(active, pair.Key) >= 0) seen[pair.Key] = pair.Value;
                        }
                        failures = 0;
                        if (reply.OutputRevisions is { } output) foreach (var pane in output.Keys)
                        {
                            Interlocked.Increment(ref WatchNotificationsForCheck); OutputSubscription[]? listeners;
                            lock (_watchGate) _outputListeners.TryGetValue(pane, out listeners);
                            // Visible pane owners coalesce their own unread-frame
                            // backpressure. Keep their wake even during a capture: a
                            // just-acknowledged output revision may be newer than the
                            // capture reply, including an unchanged/empty reply.
                            if (listeners is not null) foreach (var listener in listeners)
                                if (CancelRetiredWatchForCheck || listener.Visible?.Invoke() == true || listener.Demand()) listener.Listener(pane);
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (IOException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // Poll while degraded. A transient stall must not cost the
                    // whole session its output wakeups, so retry with backoff.
                    Action<string>[] listeners;
                    bool retire = ++failures >= 5;
                    lock (_watchGate) { if (retire) _outputWatchDegraded = true; listeners = _outputListeners.Values.SelectMany(items => items.Select(item => item.Listener)).ToArray(); }
                    foreach (var listener in listeners) listener(""); // Fall back to bounded snapshot polling.
                    if (retire) break;
                    try { await Task.Delay(100 << failures, stop).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            // Retire the sole waiter when this watcher ends so a replacement
            // watcher cannot lose its next notification to an abandoned wait.
            demandStop.Cancel();
            if (demand is not null) try { await demand.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
    private string[] OutputInterests(bool visible = false)
    {
        lock (_watchGate)
        {
            var previous = visible ? _lastVisibleInterests : _lastInterests;
            List<string>? changed = null; int index = 0;
            foreach (var pair in _outputListeners)
            {
                bool needed = false;
                foreach (var subscription in pair.Value) if (subscription.Demand() || visible && subscription.Visible?.Invoke() == true) { needed = true; break; }
                if (!needed) continue;
                if (changed is null && (index >= previous.Length || previous[index] != pair.Key))
                {
                    changed = new(_outputListeners.Count);
                    for (int i = 0; i < index; i++) changed.Add(previous[i]);
                }
                changed?.Add(pair.Key); index++;
            }
            if (changed is not null) previous = changed.ToArray();
            else if (index != previous.Length) previous = previous[..index];
            if (visible) _lastVisibleInterests = previous; else _lastInterests = previous;
            return previous;
        }
    }
    // The host process handle is opened once in Accept. Liveness is then a
    // wait-state read on that handle, never a process-table lookup by id.
    private Process? _hostProcess;
    private volatile bool _hostExited;
    private int _exitRaised;
    /// <summary>Raised once, on the thread pool, when the connected host process exits.</summary>
    public event Action? Exited;
    public bool HostExited => _hostExited;
    internal bool IsRunning => !_hostExited && _hostProcess?.HasExited != true;
    private void WatchHostProcess()
    {
        try
        {
            var owner = Process.GetProcessById(ProcessId);
            owner.EnableRaisingEvents = true; owner.Exited += (_, _) => OnHostExited();
            _hostProcess = owner;
            if (owner.HasExited) OnHostExited();
        }
        catch (ArgumentException) { OnHostExited(); }
        // Access denied or a scripted fixture: liveness is unknown, so assume
        // running and let request failures decide, as before this handle existed.
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private void OnHostExited() { _hostExited = true; if (Interlocked.Exchange(ref _exitRaised, 1) == 0) Exited?.Invoke(); }
    private SessionHostClient(string folder) { Folder = folder; _pipeName = HostProtocol.PipeName(folder); }
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);
    /// <summary>Probe for a running host on the thread pool. Null when none answers.</summary>
    public static Task<SessionHostClient?> TryConnectAsync(string folder, CancellationToken cancellation = default)
        => Task.Run(() => TryConnectCoreAsync(folder, ProbeTimeout, cancellation), cancellation);
    private static async Task<SessionHostClient?> TryConnectCoreAsync(string folder, TimeSpan timeout, CancellationToken cancellation)
    {
        // Only fast-fail when Windows says the pipe does not exist. A busy or
        // inaccessible existing pipe still follows the bounded connection path.
        if (!WaitNamedPipeW(@"\\.\pipe\" + HostProtocol.PipeName(folder), 1) && Marshal.GetLastWin32Error() == 2) return null;
        var client = new SessionHostClient(folder);
        try { client.Accept(await client.CallAsync(HostRequests.Ping(), timeout, cancellation).ConfigureAwait(false)); return client; }
        catch (SessionHostVersionException) { throw; }
        catch (Exception ex) when (ex is IOException or TimeoutException || ex is OperationCanceledException && !cancellation.IsCancellationRequested) { return null; }
    }
    /// <summary>Connect to the running host or start one, entirely on the thread pool.</summary>
    public static Task<SessionHostClient> ConnectOrStartAsync(string folder, string? executable = null, CancellationToken cancellation = default)
        => Task.Run(() => ConnectOrStartCoreAsync(folder, executable, cancellation), cancellation);
    private static async Task<SessionHostClient> ConnectOrStartCoreAsync(string folder, string? executable, CancellationToken cancellation)
    {
        if (await TryConnectCoreAsync(folder, ProbeTimeout, cancellation).ConfigureAwait(false) is { } existing) return existing;
        // A live but busy host misses the short probe; wait for it rather than
        // launching replacements that can only lose its lease.
        if (LiveHostProcess(folder) && await TryConnectCoreAsync(folder, TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false) is { } busy) return busy;
        return await StartCoreAsync(folder, executable, cancellation).ConfigureAwait(false);
    }
    /// <summary>Whether the lease owner recorded in host.json is still running.</summary>
    private static bool LiveHostProcess(string folder)
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(folder, "host.json"));
            if (file.Length > 4096) return false;
            using var json = System.Text.Json.JsonDocument.Parse(file);
            using var owner = Process.GetProcessById(json.RootElement.GetProperty("processId").GetInt32());
            return !owner.HasExited && owner.ProcessName.Equals("Toiqtty.SessionHost", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException) { return false; }
    }
    // The application already probed once before constructing its window.
    // A concurrent launcher is safe: the host's exclusive lease still decides ownership.
    internal static Task<SessionHostClient> StartAsync(string folder, string? executable = null, CancellationToken cancellation = default)
        => Task.Run(() => StartCoreAsync(folder, executable, cancellation), cancellation);
    private static async Task<SessionHostClient> StartCoreAsync(string folder, string? executable, CancellationToken cancellation)
    {
        executable ??= Path.Combine(AppContext.BaseDirectory, "Toiqtty.SessionHost.exe");
        if (!File.Exists(executable)) throw new IOException("The toiqtty session host is missing. Build or reinstall the complete application.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--data-directory"); start.ArgumentList.Add(Path.GetFullPath(folder));
        string readyName = @"Local\Toiqtty-HostReady-" + Guid.NewGuid().ToString("N");
        using var readyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, readyName);
        start.Environment["TOIQTTY_HOST_READY_EVENT"] = readyName;
        var deadline = Stopwatch.StartNew();
        Process? child = null;
        try
        {
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                // A previous host can be releasing its lease after its last
                // shell exits. Retry startup if our racing launcher exits.
                // A child that exits while another live host holds the lease
                // must not be respawned; connect to that host instead.
                bool owned = child is not null && child.HasExited && LiveHostProcess(folder);
                if (!owned && (child is null || child.HasExited)) { child?.Dispose(); child = Process.Start(start); }
                if (await TryConnectCoreAsync(folder, owned ? TimeSpan.FromSeconds(1) : ProbeTimeout, cancellation).ConfigureAwait(false) is { } ready) return ready;
                // Hosts signal after creating their first pipe. The bounded
                // timeout also covers lease races. No thread blocks on the event.
                await WaitAsync(readyEvent, 50).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
            }
        }
        finally { child?.Dispose(); }
        throw new IOException("The session host did not start. See host-error.log in the toiqtty data directory.");
    }
    // Completes when the handle is signalled or the timeout passes.
    private static async Task WaitAsync(WaitHandle handle, int milliseconds)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), done, milliseconds, executeOnlyOnce: true);
        try { await done.Task.ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }
    // Any other protocol version already failed in ExchangeAsync; the version window
    // explains how to stop that host instead of this client carrying fallbacks.
    private void Accept(HostReply reply) { Identity = reply.Host; ProcessId = reply.ProcessId; Layout = reply.Layout; Panes = reply.Panes; WatchHostProcess(); }
    internal string[] OutputInterestsForCheck() => OutputInterests();
    public Task<HostReply> CallAsync(HostRequest request, TimeSpan? timeout = null, CancellationToken cancellation = default)
        => CallCoreAsync(request, timeout, cancellation, null);
    internal Task<HostReply> CaptureAsync(HostRequest request, byte[] reuse, CancellationToken cancellation, CaptureReadLease? lease = null)
        => CallCoreAsync(request, null, cancellation, reuse, lease);
    private async Task<HostReply> CallCoreAsync(HostRequest request, TimeSpan? timeout, CancellationToken cancellation, byte[]? reuse,
        CaptureReadLease? captureLease = null, HostReadConnection? readConnection = null)
    {
        request.Host = Identity;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            if (captureLease is not null) return await captureLease.SendAsync(request, reuse, deadline.Token).ConfigureAwait(false);
            if (readConnection is not null) return await readConnection.SendAsync(request, reuse, deadline.Token).ConfigureAwait(false);
            using var pipe = CreatePipe(); await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false); PipeServerTrust.Verify(pipe.SafePipeHandle);
            return await ExchangeAsync(pipe, request, reuse, deadline.Token).ConfigureAwait(false);
        }
        // Classified once here, after the read channel's stale-pipe retry has seen
        // the raw exception. Cancellation stays an OperationCanceledException.
        catch (IOException ex) when (ex is not SessionHostException) { throw new HostTransportException(HostErrorCode.Disconnected, ex.Message, ex); }
    }
    private NamedPipeClientStream CreatePipe() => new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private async Task<HostReply> ExchangeAsync(NamedPipeClientStream pipe, HostRequest request, byte[]? reuse, CancellationToken cancellation)
    {
        await HostProtocol.WriteJson(pipe, request, cancellation).ConfigureAwait(false);
        var reply = await HostProtocol.ReadJson<HostReply>(pipe, cancellation).ConfigureAwait(false);
        if (reply.Protocol != HostProtocol.Version) throw new SessionHostVersionException();
        if (Identity.Length > 0 && reply.Host != Identity) throw new HostTransportException(HostErrorCode.HostChanged, "The session host changed; input was not replayed.");
        // Protocol-3 hosts code every error; an uncoded one is a host fault.
        if (reply.Error is not null) throw new HostRejectedException(reply.ErrorCode == HostErrorCode.None ? HostErrorCode.Internal : reply.ErrorCode, reply.Error);
        if (reply.BinaryLength is < 0 or > HostProtocol.MaxSnapshotBytes) throw new IOException("Invalid snapshot length.");
        if (reply.BinaryLength > 0)
        {
            int capacity = reply.BinaryLength;
            if (reuse is not null && capacity <= 1024 * 1024)
                capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 16384));
            reply.Binary = reuse is not null && reuse.Length >= reply.BinaryLength ? reuse : new byte[capacity];
            await pipe.ReadExactlyAsync(reply.Binary.AsMemory(0, reply.BinaryLength), cancellation).ConfigureAwait(false);
        }
        return reply;
    }
    private readonly string _layoutEpoch = Guid.NewGuid().ToString("N");
    private long _layoutRevision;
    /// <summary>Creates in flight from this client. Spawns run concurrently on the host, so
    /// a burst (a restore or a scripted layout) must not exhaust pipe instances or the
    /// 5 s connect deadline; later creates wait here, before their own deadline starts.</summary>
    internal readonly SemaphoreSlim CreateAdmission = new(4, 4);
    /// <summary>Stamp a layout snapshot or shell creation at the moment it is taken.</summary>
    internal long NextLayoutRevision() => Interlocked.Increment(ref _layoutRevision);
    internal string LayoutEpoch => _layoutEpoch;
    public Task SaveAsync(SavedSession layout) => SaveAsync(layout, NextLayoutRevision());
    internal async Task SaveAsync(SavedSession layout, long revision)
    {
        await _layoutSaveGate.WaitAsync().ConfigureAwait(false);
        List<TaskCompletionSource> pending;
        lock (_navigationGate) { _pendingNavigation = null; pending = _navigationCompletions; _navigationCompletions = []; }
        try { await CallAsync(HostRequests.Sync(layout, _layoutEpoch, revision)).ConfigureAwait(false); foreach (var completion in pending) completion.TrySetResult(); }
        catch (Exception ex) { foreach (var completion in pending) completion.TrySetException(ex); throw; }
        finally { _layoutSaveGate.Release(); }
    }
    public Task SaveNavigationAsync(SavedSession layout)
    {
        var request = HostRequests.Navigate(new(layout.SelectedWorkspace, layout.Workspaces.Select(w =>
            new HostWorkspaceNavigation(w.Id, w.ActiveTabId, w.Tabs.ToDictionary(t => t.Id, t => t.FocusedPaneId))).ToArray()));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_navigationGate)
        {
            _pendingNavigation = request; _navigationCompletions.Add(completion);
            if (!_savingNavigation) { _savingNavigation = true; _ = SavePendingNavigation(); }
        }
        return completion.Task;
    }
    private async Task SavePendingNavigation()
    {
        while (true)
        {
            await Task.Delay(20).ConfigureAwait(false);
            await _layoutSaveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                HostRequest? request; List<TaskCompletionSource> completions;
                lock (_navigationGate)
                {
                    request = _pendingNavigation; _pendingNavigation = null; completions = _navigationCompletions; _navigationCompletions = [];
                    if (request is null) { _savingNavigation = false; return; }
                }
                try { await CallAsync(request).ConfigureAwait(false); foreach (var done in completions) done.TrySetResult(); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                { foreach (var done in completions) done.TrySetException(ex); }
            }
            finally { _layoutSaveGate.Release(); }
        }
    }
    internal long AttachRequestsForCheck;
    /// <summary>Reattach many panes in one round trip instead of one create each. Settings are
    /// sent once per distinct instance. The result maps every live pane to its state; a pane the
    /// host no longer has is absent. An oversized reply splits the batch, as metadata reads do.</summary>
    public async Task<Dictionary<string, ShellState>> AttachAsync(IReadOnlyList<(string Pane, AppSettings Settings)> panes)
    {
        var states = new Dictionary<string, ShellState>(panes.Count);
        foreach (var chunk in panes.Chunk(HostProtocol.MaxMetadataPanes)) await AttachBatchAsync(chunk, states).ConfigureAwait(false);
        return states;
    }
    private async Task AttachBatchAsync((string Pane, AppSettings Settings)[] panes, Dictionary<string, ShellState> states)
    {
        var settings = new List<AppSettings>(); var attach = new AttachPane[panes.Length];
        for (int i = 0; i < panes.Length; i++)
        {
            int index = settings.FindIndex(s => ReferenceEquals(s, panes[i].Settings));
            if (index < 0) { index = settings.Count; settings.Add(panes[i].Settings); }
            attach[i] = new(panes[i].Pane, index);
        }
        HostReply reply;
        try
        {
            Interlocked.Increment(ref AttachRequestsForCheck);
            // Applying settings to many shells can take longer than one create.
            reply = await CallAsync(HostRequests.Attach(settings.ToArray(), attach), TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (HostRejectedException ex) when (ex.Code == HostErrorCode.ReplyTooLarge && panes.Length > 1)
        {
            // Reattaching is idempotent; each half strictly reduces the reply.
            foreach (var half in panes.Chunk((panes.Length + 1) / 2)) await AttachBatchAsync(half, states).ConfigureAwait(false);
            return;
        }
        if (reply.States is null) throw new IOException("The host omitted its attach states.");
        foreach (var pair in reply.States) states[pair.Key] = pair.Value;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, uint timeout);
}
