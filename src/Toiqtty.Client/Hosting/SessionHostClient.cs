using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Toiqtty.Models;

namespace Toiqtty.Hosting;

public sealed class SessionHostVersionException : IOException
{
    public SessionHostVersionException() : base("A different toiqtty session-host version is still running. In the installed version, choose ‘Quit toiqtty and stop all sessions’, then open this version again. Your running shells have been left intact.") { }
}

public sealed partial class SessionHostClient
{
    public string Folder { get; }
    private readonly string _pipeName;
    public string Identity { get; private set; } = "";
    public int ProcessId { get; private set; }
    public SavedSession? Layout { get; private set; }
    public string[] Panes { get; private set; } = [];
    public bool SupportsOutputWatch { get; private set; }
    public bool SupportsFrameDeltas { get; private set; }
    public bool SupportsStyleRuns { get; private set; }
    public bool SupportsReusableReads { get; private set; }
    public bool SupportsStatusRevisions { get; private set; }
    internal bool DisableStatusRevisionsForCheck { get; set; }
    internal bool DisableReusableReadsForCheck { get; set; }
    internal long ReadConnectionsOpenedForCheck, ReadConnectionsReusedForCheck;
    internal bool DisableStyleRunsForCheck { get; set; }
    public bool SupportsPaneOutputWatch { get; private set; }
    public bool SupportsNavigationSave { get; private set; }
    public bool SupportsMetadataBatch { get; private set; }
    public bool SupportsResizeSelectionClear { get; private set; }
    public bool SupportsPaneThemes { get; private set; }
    public bool SupportsAdvancedAppearance { get; private set; }
    public bool SupportsLiteralInput { get; private set; }
    private readonly SemaphoreSlim _layoutSaveGate = new(1, 1);
    private readonly object _navigationGate = new();
    private HostRequest? _pendingNavigation;
    private List<TaskCompletionSource> _navigationCompletions = [];
    private bool _savingNavigation;
    internal long WatchRequestsForCheck, WatchNotificationsForCheck;
    internal bool DisablePaneOutputWatchForCheck { get; set; }
    internal bool CancelRetiredWatchForCheck { get; set; }
    private readonly object _watchGate = new();
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
            if (SupportsOutputWatch && _watchStop is null)
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
        long revision = -1;
        var seen = new Dictionary<string, long>();
        // A watch reply does not invalidate the independent demand signal.
        // Reuse its pending wait across replies instead of canceling a task
        // and allocating a linked cancellation source on every output batch.
        using var demandStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var readConnection = SupportsReusableReads ? new HostReadConnection(this) : null;
        Task? demand = null;
        Task DemandWake() => demand ??= _watchWake.WaitAsync(demandStop.Token);
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
                bool filtered = SupportsPaneOutputWatch && !DisablePaneOutputWatchForCheck;
                HostReply reply;
                if (filtered)
                {
                    using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    var pending = CallCoreAsync(new()
                    {
                        Operation = "watch",
                        Numbers = [revision],
                        WatchPanes = interests,
                        WatchRevisions = interests.ToDictionary(id => id, id => seen.GetValueOrDefault(id, -1))
                    }, TimeSpan.FromSeconds(3), requestStop.Token, null,
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
                    reply = await pending.ConfigureAwait(false);
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
                }
                else reply = await CallCoreAsync(new() { Operation = "watch", Numbers = [revision] }, TimeSpan.FromSeconds(3), stop, null,
                    readConnection: DisableReusableReadsForCheck ? null : readConnection).ConfigureAwait(false);
                revision = reply.Numbers.FirstOrDefault(revision);
                foreach (var pane in reply.Panes)
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
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Action<string>[] listeners;
            lock (_watchGate) { SupportsOutputWatch = false; listeners = _outputListeners.Values.SelectMany(items => items.Select(item => item.Listener)).ToArray(); }
            foreach (var listener in listeners) listener(""); // Fall back to bounded snapshot polling.
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
    internal bool IsRunning
    {
        get
        {
            try { using var owner = Process.GetProcessById(ProcessId); return !owner.HasExited; }
            catch (ArgumentException) { return false; }
        }
    }
    private SessionHostClient(string folder) { Folder = folder; _pipeName = HostProtocol.PipeName(folder); }
    public static SessionHostClient? TryConnect(string folder)
    {
        // Only fast-fail when Windows says the pipe does not exist. A busy or
        // inaccessible existing pipe still follows the bounded connection path.
        if (!WaitNamedPipeW(@"\\.\pipe\" + HostProtocol.PipeName(folder), 1) && Marshal.GetLastWin32Error() == 2) return null;
        var client = new SessionHostClient(folder);
        try { client.Accept(client.Call(new(), TimeSpan.FromMilliseconds(250))); return client; }
        catch (SessionHostVersionException) { throw; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { return null; }
    }
    public static SessionHostClient ConnectOrStart(string folder, string? executable = null)
    {
        if (TryConnect(folder) is { } existing) return existing;
        return Start(folder, executable);
    }
    // The application already probed once before constructing its window.
    // A concurrent launcher is safe: the host's exclusive lease still decides ownership.
    internal static SessionHostClient Start(string folder, string? executable = null)
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
                if (child is null || child.HasExited) { child?.Dispose(); child = Process.Start(start); }
                if (TryConnect(folder) is { } ready) return ready;
                // Current hosts signal after creating their first pipe. The
                // bounded timeout also supports older hosts and lease races.
                readyEvent.WaitOne(50);
            }
        }
        finally { child?.Dispose(); }
        throw new IOException("The session host did not start. See host-error.log in the toiqtty data directory.");
    }
    private void Accept(HostReply reply)
    {
        Identity = reply.Host; ProcessId = reply.ProcessId; Layout = reply.Layout; Panes = reply.Panes;
        SupportsOutputWatch = reply.Capabilities.Contains("output-watch");
        SupportsFrameDeltas = reply.Capabilities.Contains("row-deltas");
        SupportsStyleRuns = reply.Capabilities.Contains("style-runs-v1");
        SupportsReusableReads = reply.Capabilities.Contains(HostProtocol.ReusableReadCapability);
        SupportsStatusRevisions = reply.Capabilities.Contains(HostProtocol.StatusRevisionCapability);
        SupportsPaneThemes = reply.Capabilities.Contains("pane-themes");
        SupportsAdvancedAppearance = reply.Capabilities.Contains("appearance-v2");
        SupportsLiteralInput = reply.Capabilities.Contains("literal-input");
        SupportsPaneOutputWatch = reply.Capabilities.Contains("pane-output-watch");
        SupportsNavigationSave = reply.Capabilities.Contains("navigation-save");
        // Version 1 cannot report response overflow. Keep its safe individual
        // state path rather than disconnecting every pane in a large batch.
        SupportsMetadataBatch = reply.Capabilities.Contains(HostProtocol.MetadataBatchCapability);
        SupportsResizeSelectionClear = reply.Capabilities.Contains("resize-clears-selection");
    }
    internal string[] OutputInterestsForCheck() => OutputInterests();
    public HostReply Call(HostRequest request, TimeSpan? timeout = null)
    {
        try { return CallAsync(request, timeout).GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { throw new IOException("Session host request timed out. Input was not retried.", ex); }
    }
    public Task<HostReply> CallAsync(HostRequest request, TimeSpan? timeout = null, CancellationToken cancellation = default)
        => CallCoreAsync(request, timeout, cancellation, null);
    internal Task<HostReply> CaptureAsync(HostRequest request, byte[] reuse, CancellationToken cancellation, CaptureReadLease? lease = null)
        => CallCoreAsync(request, null, cancellation, reuse, lease);
    private async Task<HostReply> CallCoreAsync(HostRequest request, TimeSpan? timeout, CancellationToken cancellation, byte[]? reuse,
        CaptureReadLease? captureLease = null, HostReadConnection? readConnection = null)
    {
        request.Host = Identity;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        if (captureLease is not null) return await captureLease.SendAsync(request, reuse, deadline.Token).ConfigureAwait(false);
        if (readConnection is not null) return await readConnection.SendAsync(request, reuse, deadline.Token).ConfigureAwait(false);
        using var pipe = CreatePipe(); await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        return await ExchangeAsync(pipe, request, reuse, deadline.Token).ConfigureAwait(false);
    }
    private NamedPipeClientStream CreatePipe() => new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private async Task<HostReply> ExchangeAsync(NamedPipeClientStream pipe, HostRequest request, byte[]? reuse, CancellationToken cancellation)
    {
        await HostProtocol.WriteJson(pipe, request, cancellation).ConfigureAwait(false);
        var reply = await HostProtocol.ReadJson<HostReply>(pipe, cancellation).ConfigureAwait(false);
        if (reply.Protocol != HostProtocol.Version) throw new SessionHostVersionException();
        if (Identity.Length > 0 && reply.Host != Identity) throw new IOException("The session host changed; input was not replayed.");
        if (reply.Error is not null) throw new IOException(reply.Error);
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
    public void Save(SavedSession layout)
        => SaveAsync(layout).GetAwaiter().GetResult();
    public async Task SaveAsync(SavedSession layout)
    {
        await _layoutSaveGate.WaitAsync().ConfigureAwait(false);
        List<TaskCompletionSource> pending;
        lock (_navigationGate) { _pendingNavigation = null; pending = _navigationCompletions; _navigationCompletions = []; }
        try { await CallAsync(new() { Operation = "sync", Layout = layout }).ConfigureAwait(false); foreach (var completion in pending) completion.TrySetResult(); }
        catch (Exception ex) { foreach (var completion in pending) completion.TrySetException(ex); throw; }
        finally { _layoutSaveGate.Release(); }
    }
    public Task SaveNavigationAsync(SavedSession layout)
    {
        if (!SupportsNavigationSave) return SaveAsync(layout);
        var request = new HostRequest
        {
            Operation = "navigate",
            Navigation = new(layout.SelectedWorkspace, layout.Workspaces.Select(w =>
            new HostWorkspaceNavigation(w.Id, w.ActiveTabId, w.Tabs.ToDictionary(t => t.Id, t => t.FocusedPaneId))).ToArray())
        };
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
    public void Detach() => Call(new() { Operation = "detach" });
    public void Shutdown() => Call(new() { Operation = "shutdown" });
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, uint timeout);
}
