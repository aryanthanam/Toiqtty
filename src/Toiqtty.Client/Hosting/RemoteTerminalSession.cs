using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

/// <summary>Window-side terminal cache. Disposing closes a pane; detaching leaves its shell alive.</summary>
public sealed class RemoteTerminalSession : ITerminalSession
{
    private readonly SessionHostClient _client;
    private readonly Task _pollTask;
    private Task _finishInput = Task.CompletedTask;
    internal Task ClosedForCheck => _finishInput;
    internal int StateVersionForCheck => _state.Version;
    internal long ResizeSelectionRequestsSentForCheck;
    private readonly string _pane;
    private readonly object _gate = new();
    private readonly Lock _closeGate = new();
    private readonly Lock _metadataGate = new();
    private CancellationTokenSource? _metadataRequest;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ulong, TerminalImage> _images = [];
    private readonly SemaphoreSlim _captureRequests = new(1);
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private readonly OrderedHostInput _input;
    private readonly SessionHostClient.CaptureReadLease? _captureReads;
    private readonly HashSet<long> _copiedKeys = [];
    private long _interactiveUntil;
    private long _urgentPresentationUntil;
    internal bool IsInteractiveForPresentation => System.Diagnostics.Stopwatch.GetTimestamp() < Interlocked.Read(ref _urgentPresentationUntil);
    private void MarkInteractive()
    {
        Interlocked.Exchange(ref _interactiveUntil, Environment.TickCount64 + 250);
        Interlocked.Exchange(ref _urgentPresentationUntil, System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 20);
        WakeCapture();
    }
    private int _frameAwaitingRead;
    private int _capturing;
    private bool NeedsOutputNotification() => Volatile.Read(ref _viewers) > 0 && Volatile.Read(ref _frameAwaitingRead) == 0 && Volatile.Read(ref _capturing) == 0;
    private bool HasVisibleView() => Volatile.Read(ref _viewers) > 0;
    internal bool FrameAwaitingReadForCheck => Volatile.Read(ref _frameAwaitingRead) != 0;
    internal object DebugCacheForCheck
    {
        get
        {
            lock (_gate) return new
            {
                columns = _columns,
                rows = _rows,
                cachedColumns = _snapshot?.Frame.Columns,
                cachedRows = _snapshot?.Frame.Rows,
                cells = _cells.Length,
                version = _version,
                captured = _captured,
                frameRevision = _frameRevision,
                awaitingRead = _frameAwaitingRead,
                viewers = _viewers,
                stateVersion = _state.Version,
                performance = _state.Performance
            };
        }
    }
    internal long CapturesForCheck;
    internal Action? HiddenMetadataQueuedForCheck { get; set; }
    internal Task<HostReply> CaptureDiagnosticsForCheck() => _client.CallAsync(Request("capture", [-1]));
    internal long DeltaFramesForCheck, FullFramesForCheck, FrameBytesForCheck;
    internal long FrameMetadataBytesForCheck, FullStatusesForCheck, ReusedStatusesForCheck;
    public event Action? FrameAvailable;
    internal event Action<string, long>? LatencyStageForCheck;
    internal event Action<ShellState, long>? StateObservedForCheck;
    internal event Action<HostReply>? CaptureReplyForCheck;
    internal long FullFrameRecoveriesForCheck;
    private void TraceLatency(string stage) => LatencyStageForCheck?.Invoke(stage, System.Diagnostics.Stopwatch.GetTimestamp());
    internal int PendingInput => _input.Pending;
    internal Task InputDrained => _input.Drain;
    internal Task CompleteInput() => _input.Complete();
    internal string CaptureFailure { get; private set; } = "";
    private static readonly Lock CaptureTraceGate = new();
    private TerminalSnapshot? _snapshot;
    private TerminalCell[] _cells = [];
    private TerminalCell[] _decodeCells = [];
    private IReadOnlyList<int>? _decodeRepairRows;
    private long[] _rowRevisions = [];
    private long _cacheRevision;
    // Owned by this pane's serialized capture path. Decoding copies all image
    // bytes and text before the next read can overwrite this bounded buffer.
    private byte[] _captureBuffer = [];
    private ShellState _state;
    private int _version, _captured = -1, _viewers, _closed;
    private volatile int _columns = 100, _rows = 30;
    private volatile uint _cellPixelWidth, _cellPixelHeight;
    private bool MatchesViewport(TerminalFrame frame) => frame.Columns == _columns && frame.Rows == _rows
        && (_cellPixelWidth == 0 || frame.CellWidth == _cellPixelWidth)
        && (_cellPixelHeight == 0 || frame.CellHeight == _cellPixelHeight);
    private readonly Lock _resizeGate = new();
    private (ushort Columns, ushort Rows, uint Width, uint Height)? _pendingResize;
    private bool _resizing;
    internal long ResizeRequestsSent;
    internal bool ResizePending { get { lock (_resizeGate) return _resizing; } }
    private long _attention;
    private long _frameRevision = -1;
    private long _captureStatusRevision = -1;
    private TerminalStatusSnapshot? _captureStatus;
    private DateTimeOffset _notice;
    private string _failure = "";
    public RemoteTerminalSession(SessionHostClient client, string pane, string command, string directory, AppSettings settings, SavedSession? layout = null)
    {
        _client = client; _pane = pane;
        _input = new(SendQueuedRequest);
        _state = client.Call(new() { Operation = "create", Pane = pane, Text = command, Directory = directory, Settings = settings, Layout = layout }).State
            ?? throw new IOException("The session host did not return shell state.");
        var initialStatus = _state.Status ?? throw new IOException("The session host returned incomplete shell status.");
        _attention = _state.Attention; _notice = initialStatus.Notices.LastOrDefault()?.Time ?? default;
        _captureReads = client.AcquireCaptureReads();
        _client.AddOutputListener(_pane, HostOutputChanged, NeedsOutputNotification, HasVisibleView);
        _pollTask = Poll();
    }
    public int Version => Volatile.Read(ref _version);
    public bool HasExited => _state.HasExited;
    public int ExitCode => _state.ExitCode;
    public string Title => _state.Title;
    public string StatusSummary => _failure.Length > 0 ? _failure : _state.StatusSummary;
    public string Directory => _state.Directory;
    public string WorkingDirectory => _state.WorkingDirectory;
    public string? LocalDirectory => _state.LocalDirectory;
    public TerminalPerformance Performance => _state.Performance;
    public TerminalStatusSnapshot Status => _state.Status ?? throw new InvalidOperationException("The accepted shell status is incomplete.");
    public bool PendingAttention => _state.PendingAttention;
    public event Action? Attention;
    public event Action<TerminalNotice>? Notification;
    public void AttachView()
    {
        bool first = Interlocked.Increment(ref _viewers) == 1;
        Interlocked.Exchange(ref _captured, -1);
        Volatile.Write(ref _frameAwaitingRead, 0);
        // The initial state request can start before the view exists. Wake its
        // hidden-pane wait when attaching, including rapid tab/zoom transitions.
        if (first)
        {
            lock (_metadataGate) _metadataRequest?.Cancel();
            WakeCapture();
        }
    }
    public void DetachView() { if (Interlocked.Decrement(ref _viewers) == 0) WakeCapture(); }
    internal int ViewersForCheck => Volatile.Read(ref _viewers);
    internal int FrameListenersForCheck => FrameAvailable?.GetInvocationList().Length ?? 0;
    private void WakeCapture() { _client.OutputDemandChanged(); try { _pollWake.Release(); } catch (SemaphoreFullException) { } }
    internal void WakeCaptureForCheck() => WakeCapture();
    private void HostOutputChanged(string pane) { if (Volatile.Read(ref _viewers) > 0 && Volatile.Read(ref _frameAwaitingRead) == 0 && (pane == _pane || pane.Length == 0)) WakeCapture(); }
    private HostRequest Request(string operation, long[]? numbers = null, string text = "")
    {
        var request = new HostRequest
        {
            Operation = operation,
            Pane = _pane,
            Numbers = numbers ?? [],
            Text = text,
            AllowStyleRuns = operation == "capture" && _client.SupportsStyleRuns && !_client.DisableStyleRunsForCheck
        };
        if (operation == "capture" && _client.SupportsStatusRevisions && !_client.DisableStatusRevisionsForCheck)
            lock (_gate) request.KnownStatusRevision = _captureStatusRevision;
        return request;
    }
    private async Task<HostReply> SendQueuedRequest(HostRequest request)
    {
        if (LatencyStageForCheck is not null) TraceLatency("input-send-" + request.Operation);
        HostReply reply;
        if (request.Operation == "copy-key")
        {
            reply = await _client.CallAsync(Request("copy")).ConfigureAwait(false);
            if (reply.Text.Length == 0)
            {
                _copiedKeys.Remove(request.Numbers[0]);
                reply = await _client.CallAsync(Request("key", request.Numbers, request.Text)).ConfigureAwait(false);
            }
            else _copiedKeys.Add(request.Numbers[0]);
            var cleared = await _client.CallAsync(Request("select", [-1, -1])).ConfigureAwait(false);
            reply.State = cleared.State;
        }
        else if (request.Operation == "key" && request.Numbers[2] == 0 && _copiedKeys.Remove(request.Numbers[0])) reply = new();
        else
        {
            if (request.Operation == "key" && request.Numbers[2] != 0) _copiedKeys.Remove(request.Numbers[0]);
            reply = await _client.CallAsync(request).ConfigureAwait(false);
        }
        if (request.Operation == "resize" && !_client.SupportsResizeSelectionClear)
        {
            Interlocked.Increment(ref ResizeSelectionRequestsSentForCheck);
            await _client.CallAsync(Request("select", [-1, -1])).ConfigureAwait(false);
        }
        if (LatencyStageForCheck is not null) TraceLatency("input-ack-" + request.Operation);
        Accept(reply);
        // A new interaction can supersede a waiting frame, especially a resize
        // whose old snapshot no longer fits the view's requested cell buffer.
        Volatile.Write(ref _frameAwaitingRead, 0); WakeCapture(); return reply;
    }
    internal async Task<string> CopyOrKeyAsync(uint key, uint modifiers, string text, TerminalKeyAction action, uint unshifted, uint consumed)
    {
        MarkInteractive();
        var reply = await _input.Enqueue(Request("copy-key", [key, modifiers, (long)action, unshifted, consumed], text)).ConfigureAwait(false);
        ObserveInputReply(reply); return reply.Text;
    }
    public async Task<string> CopySelectionAsync()
    { var reply = await _input.Enqueue(Request("copy")).ConfigureAwait(false); ObserveInputReply(reply); return reply.Text; }
    private void Accept(HostReply reply)
    {
        if (reply.State is not { } state) return;
        var status = state.Status ?? throw new HostSnapshotException("The terminal status baseline is unavailable.");
        TerminalNotice[] notices; bool attention;
        lock (_gate)
        {
            var previous = _state; if (state.Version < previous.Version) return; _state = state;
            if (previous.Version != state.Version) Interlocked.Increment(ref _version);
            attention = state.Attention > _attention; _attention = state.Attention;
            notices = status.Notices.Where(n => n.Time > _notice).ToArray();
            if (notices.Length > 0) _notice = notices[^1].Time;
        }
        StateObservedForCheck?.Invoke(state, System.Diagnostics.Stopwatch.GetTimestamp());
        if (attention) Attention?.Invoke();
        foreach (var notice in notices) Notification?.Invoke(notice);
    }
    private void Cache(HostReply reply)
    {
        Interlocked.Add(ref FrameMetadataBytesForCheck, reply.JsonPayloadLength);
        lock (_gate)
        {
            if (reply.StatusRevision is { } revision)
            {
                if (revision < 0 || reply.State is not { } state) throw new HostSnapshotException("Invalid terminal status revision.");
                if (state.Status is null)
                {
                    if (_captureStatus is null || revision != _captureStatusRevision) throw new HostSnapshotException("The terminal status baseline is unavailable.");
                    reply.State = state with { Status = _captureStatus };
                    Interlocked.Increment(ref ReusedStatusesForCheck);
                }
                else { _captureStatus = state.Status; _captureStatusRevision = revision; Interlocked.Increment(ref FullStatusesForCheck); }
            }
            else if (reply.State is { Status: null }) throw new HostSnapshotException("The terminal status baseline is unavailable.");
        }
        Accept(reply);
        if (reply.Binary.Length == 0) return;
        TraceLatency("frame-decode-begin");
        lock (_gate)
        {
            var previous = _cells;
            var previousFrame = _snapshot?.Frame;
            var spare = _decodeCells;
            var repairRows = _decodeRepairRows;
            // A failed decode can partially write the spare. Its next use must
            // repair the entire baseline, never just the last good delta rows.
            _decodeRepairRows = null;
            if (reply.DeltaBaseRevision is { } baseline)
            {
                if (_snapshot is null || baseline != _frameRevision) throw new HostSnapshotException("The row-delta baseline is unavailable.");
                (_snapshot, _cells) = HostSnapshotCodec.DecodeDeltaIntoSpare(reply.Binary, _images, _snapshot, _cells, _decodeCells, repairRows, reply.BinaryLength);
                _decodeRepairRows = _snapshot.ChangedRows;
                Interlocked.Increment(ref DeltaFramesForCheck);
            }
            else
            {
                (_snapshot, _cells) = HostSnapshotCodec.Decode(reply.Binary, _images, _decodeCells, reply.BinaryLength);
                Interlocked.Increment(ref FullFramesForCheck);
            }
            Interlocked.Add(ref FrameBytesForCheck, reply.BinaryLength);
            _snapshot = _snapshot with { Scrollbar = reply.Scrollbar };
            long revision = ++_cacheRevision;
            if (_rowRevisions.Length != _snapshot.Frame.Rows) _rowRevisions = new long[_snapshot.Frame.Rows];
            if (reply.DeltaBaseRevision is not null)
            {
                for (int index = 0; index < _snapshot.ChangedRows.Count; index++) _rowRevisions[_snapshot.ChangedRows[index]] = revision;
            }
            else if (previousFrame is { } oldFrame && oldFrame.Columns == _snapshot.Frame.Columns && oldFrame.Rows == _snapshot.Frame.Rows)
            {
                // Older hosts and recovery captures send complete grids. Compare
                // those here, off the UI thread, so a cursor-only full reply does
                // not force every retained text row to be repainted.
                int columns = (int)_snapshot.Frame.Columns;
                for (int row = 0; row < _rowRevisions.Length; row++)
                    for (int cell = row * columns; cell < (row + 1) * columns; cell++)
                        if (!previous[cell].SameAppearance(in _cells[cell])) { _rowRevisions[row] = revision; break; }
            }
            else Array.Fill(_rowRevisions, revision);
            _decodeCells = previous;
            if (!ReferenceEquals(spare, _cells) && !ReferenceEquals(spare, previous)) TerminalCellPool.Shared.Return(spare);
            _captured = reply.CapturedVersion; _frameRevision = reply.FrameRevision; Interlocked.Increment(ref _version);
            Volatile.Write(ref _frameAwaitingRead, Volatile.Read(ref _viewers) > 0
                && MatchesViewport(_snapshot.Frame) ? 1 : 0);
        }
        TraceLatency("frame-cached"); FrameAvailable?.Invoke();
    }
    private async Task Poll()
    {
        int failures = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                bool visible = Volatile.Read(ref _viewers) > 0;
                // Keep one unread frame rather than sleeping between captures.
                // The view wakes us after copying it; output keeps accumulating
                // in Ghostty while a slow/occluded view catches up.
                if (visible && Volatile.Read(ref _frameAwaitingRead) != 0)
                { await _pollWake.WaitAsync(1000, _stop.Token).ConfigureAwait(false); continue; }
                var request = Request(visible ? "capture" : "state", [_captured, Interlocked.Read(ref _frameRevision)]);
                request.AllowFrameDelta = visible && _client.SupportsFrameDeltas && _captured >= 0;
                lock (_gate) request.Images = _images.Keys.ToArray();
                try
                {
                    Volatile.Write(ref _capturing, 1);
                    if (!visible && _client.SupportsMetadataBatch)
                    {
                        using var metadataStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        lock (_metadataGate) _metadataRequest = metadataStop;
                        try
                        {
                            if (Volatile.Read(ref _viewers) > 0) continue;
                            var pending = _client.ReadHiddenState(_pane, _state.Version, metadataStop.Token);
                            HiddenMetadataQueuedForCheck?.Invoke();
                            var state = await pending.ConfigureAwait(false);
                            if (state is not null) Accept(new() { State = state });
                        }
                        // Attach can be followed by another detach before this
                        // continuation runs. Classify by this request's canceled
                        // token rather than the pane's later visibility.
                        catch (OperationCanceledException) when (metadataStop.IsCancellationRequested && !_stop.IsCancellationRequested) { continue; }
                        finally { lock (_metadataGate) _metadataRequest = null; }
                    }
                    else
                    {
                        await _captureRequests.WaitAsync(_stop.Token).ConfigureAwait(false);
                        try
                        {
                            if (visible) Interlocked.Increment(ref CapturesForCheck); TraceLatency("capture-request");
                            var reply = await _client.CaptureAsync(request, _captureBuffer, _stop.Token, _captureReads).ConfigureAwait(false);
                            TraceLatency("capture-reply"); CaptureReplyForCheck?.Invoke(reply); Cache(reply);
                            if (reply.BinaryLength > 0 && reply.Binary.Length <= 1024 * 1024) _captureBuffer = reply.Binary;
                        }
                        finally { _captureRequests.Release(); }
                    }
                    failures = 0;
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested && ex is IOException or OperationCanceledException)
                {
                    if (ex is HostSnapshotException)
                    {
                        Interlocked.Increment(ref FullFrameRecoveriesForCheck);
                        lock (_gate) { _captured = -1; _frameRevision = -1; _captureStatusRevision = -1; _captureStatus = null; _images.Clear(); Volatile.Write(ref _frameAwaitingRead, 0); }
                    }
                    CaptureFailure = ex.ToString(); failures++;
                    if (Environment.GetEnvironmentVariable("TOIQTTY_SESSION_UI_PROBE") is not null)
                        lock (CaptureTraceGate) try { File.AppendAllText(Path.Combine(_client.Folder, "capture-errors.jsonl"), System.Text.Json.JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, pane = _pane, attempt = failures, error = CaptureFailure }) + Environment.NewLine); } catch (IOException) { }
                    if (failures > 3 || !_client.IsRunning) throw;
                    await Task.Delay(50 * failures, _stop.Token).ConfigureAwait(false);
                    continue;
                }
                finally { Volatile.Write(ref _capturing, 0); if (visible) _client.OutputDemandChanged(); }
                if (!visible && _client.SupportsMetadataBatch) continue;
                int delay = visible ? (_client.SupportsOutputWatch ? 1000 : Environment.TickCount64 < Interlocked.Read(ref _interactiveUntil) ? 8 : 16) : 400;
                if (visible && _snapshot is { } frame)
                    delay = (int)Math.Min(delay, frame.Frame.RenderHeld != 0 ? 16 : frame.Frame.AnimationDelay == uint.MaxValue ? delay : Math.Max(1, frame.Frame.AnimationDelay));
                await _pollWake.WaitAsync(delay, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        { CaptureFailure = ex.ToString(); if (!_state.HasExited) _failure = "Session host disconnected · reopen toiqtty to reconnect"; Interlocked.Increment(ref _version); }
    }
    private HostReply Send(HostRequest request)
    {
        if (Volatile.Read(ref _closed) != 0) return new();
        var reply = _input.Enqueue(request).GetAwaiter().GetResult();
        ObserveInputReply(reply); return reply;
    }
    private void ObserveInputReply(HostReply reply)
    { if (reply.Error is { } error) { _failure = error; Interlocked.Increment(ref _version); FrameAvailable?.Invoke(); } }
    private void QueueInput(HostRequest request)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        MarkInteractive();
        var admitted = _input.Enqueue(request);
        if (_failure.StartsWith("Input busy", StringComparison.Ordinal) && (!admitted.IsCompleted || admitted.Result.Error is null))
        { _failure = ""; Interlocked.Increment(ref _version); FrameAvailable?.Invoke(); }
        _ = ObserveQueuedInput(admitted);
    }
    private async Task ObserveQueuedInput(Task<HostReply> request)
    { ObserveInputReply(await request.ConfigureAwait(false)); }
    public async Task<string> HyperlinkAsync(ushort column, ushort row)
    { try { return (await _client.CallAsync(Request("hyperlink", [column, row]), cancellation: _stop.Token).ConfigureAwait(false)).Text; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { return ""; } }
    public TerminalFrame ReadFrame(TerminalCell[] cells) => ReadSnapshot(cells).Frame;
    public TerminalSnapshot ReadSnapshot(TerminalCell[] cells)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _input.Drain.GetAwaiter().GetResult();
        _captureRequests.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            var request = Request("capture", [-1]); lock (_gate) request.Images = _images.Keys.ToArray();
            Cache(_client.Call(request));
            if (!TryReadSnapshot(cells, out var snapshot)) throw new IOException("Terminal snapshot is not ready.");
            return snapshot!;
        }
        finally { _captureRequests.Release(); }
    }
    public bool TryReadSnapshot(TerminalCell[] cells, out TerminalSnapshot? snapshot)
    {
        long unused = 0;
        return TryReadSnapshot(cells, ref unused, false, out snapshot);
    }
    // The view owns this revision alongside its destination buffer and resets
    // it to -1 when replacing that buffer. Row stamps accumulate skipped frames
    // without comparing every cell or retaining references to disposed views.
    internal bool TryReadSnapshot(TerminalCell[] cells, ref long revision, out TerminalSnapshot? snapshot)
        => TryReadSnapshot(cells, ref revision, true, out snapshot);
    private bool TryReadSnapshot(TerminalCell[] cells, ref long revision, bool tracked, out TerminalSnapshot? snapshot)
    {
        snapshot = null;
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            if (Volatile.Read(ref _closed) != 0 || _snapshot is null || cells.Length != _cells.Length || !MatchesViewport(_snapshot.Frame)) return false;
            int columns = (int)_snapshot.Frame.Columns; List<int>? changed = null;
            for (int row = 0; row < _snapshot.Frame.Rows; row++)
            {
                bool differs = tracked && (revision < 0 || _rowRevisions[row] > revision);
                if (!tracked) for (int i = row * columns; i < (row + 1) * columns; i++)
                {
                    if (!cells[i].SameAppearance(in _cells[i])) { differs = true; break; }
                }
                if (differs)
                { _cells.AsSpan(row * columns, columns).CopyTo(cells.AsSpan(row * columns, columns)); (changed ??= []).Add(row); }
            }
            snapshot = _snapshot with { ChangedRows = changed ?? (IReadOnlyList<int>)[] };
            if (tracked) revision = _cacheRevision;
            if (Interlocked.Exchange(ref _frameAwaitingRead, 0) != 0) WakeCapture();
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }
    public void ApplySettings(AppSettings settings)
    { var request = Request("settings"); request.Settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings)); QueueInput(request); }
    public void Resize(ushort columns, ushort rows, uint width, uint height)
    { _columns = columns; _rows = rows; _cellPixelWidth = width; _cellPixelHeight = height; Send(Request("resize", [columns, rows, width, height])); }
    internal void QueueResize(ushort columns, ushort rows, uint width, uint height)
    {
        lock (_resizeGate)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            _columns = columns; _rows = rows;
            _cellPixelWidth = width; _cellPixelHeight = height;
            _pendingResize = (columns, rows, width, height);
            if (_resizing) return;
            _resizing = true;
        }
        _ = SendResizes();
    }
    private async Task SendResizes()
    {
        try
        {
            while (true)
            {
                (ushort Columns, ushort Rows, uint Width, uint Height) size;
                lock (_resizeGate)
                {
                    if (_pendingResize is not { } pending || Volatile.Read(ref _closed) != 0)
                    { _resizing = false; return; }
                    size = pending; _pendingResize = null;
                }
                // Never synchronously wait on an STA layout callback: the CLR's
                // COM wait pumps pointer messages back into WinUI layout.
                Interlocked.Increment(ref ResizeRequestsSent);
                var reply = await _input.Enqueue(Request("resize", [size.Columns, size.Rows, size.Width, size.Height])).ConfigureAwait(false);
                ObserveInputReply(reply); WakeCapture();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            lock (_resizeGate) { _resizing = false; _pendingResize = null; }
            if (Volatile.Read(ref _closed) == 0)
            { _failure = "Session host unavailable · resize was not retried"; Interlocked.Increment(ref _version); }
        }
    }
    public void Key(uint key, uint modifiers, string text = "", TerminalKeyAction action = TerminalKeyAction.Press, uint? unshiftedCodepoint = null, uint consumedModifiers = 0)
        => QueueInput(Request("key", [key, modifiers, (long)action, unshiftedCodepoint is { } code ? code : -1, consumedModifiers], text));
    public void Paste(string text, bool clipboard = false) => QueueInput(Request("paste", [clipboard ? 1 : 0], text));
    public void SendText(string text)
    {
        if (!_client.SupportsLiteralInput) throw new NotSupportedException("Literal text sending needs a 0.7.19+ session host. Existing shells have been left running.");
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 65536) throw new ArgumentException("Literal input exceeds 64 KiB.");
        QueueInput(Request("send-text", text: text));
    }
    public Task SendTextAsync(string text)
    {
        if (!_client.SupportsLiteralInput) throw new NotSupportedException("Literal text sending needs a 0.7.19+ session host. Existing shells have been left running.");
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 65536) throw new ArgumentException("Literal input exceeds 64 KiB.");
        return AdmitText(Request("send-text", text: text));
    }
    public Task PasteAsync(string text) => AdmitText(Request(_client.SupportsLiteralInput ? "paste-checked" : "paste", [0], text));
    private async Task AdmitText(HostRequest request)
    {
        var reply = await _input.Enqueue(request).ConfigureAwait(false); ObserveInputReply(reply);
        if (reply.Error is not null) throw new IOException(reply.Error);
    }
    public void PasteClipboard() => QueueInput(Request("clipboard"));
    public void Scroll(int delta) => QueueInput(Request("scroll", [delta]));
    public void ScrollTo(ulong row) => QueueInput(Request("scroll-to", [(long)Math.Min(row, (ulong)long.MaxValue)]));
    public void Focus(bool focused) => QueueInput(Request("focus", [focused ? 1 : 0]));
    public void Select(int start, int end) => QueueInput(Request("select", [start, end]));
    public int SelectionGesture(int action, float x, float y, bool rectangle = false)
    => (int)Send(Request("gesture", [action, (long)(x * 1000), (long)(y * 1000), rectangle ? 1 : 0])).Numbers.FirstOrDefault();
    public void QueueSelectionGesture(int action, float x, float y, bool rectangle = false)
    => QueueInput(Request("gesture", [action, (long)(x * 1000), (long)(y * 1000), rectangle ? 1 : 0]));
    public int Drop(int action, int x = 0, int y = 0, IReadOnlyDictionary<string, byte[]>? items = null, uint operations = 1)
    {
        var request = Request("drop", [action, x, y, operations]); request.Items = items?.ToDictionary();
        return (int)Send(request).Numbers.FirstOrDefault();
    }
    public async Task<int> DropAsync(int x, int y, IReadOnlyDictionary<string, byte[]> items, uint operations = 1, TimeSpan? timeout = null)
    {
        await _input.Drain.ConfigureAwait(false);
        var request = Request("drop-async", [x, y, operations, (long)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds]); request.Items = items.ToDictionary();
        try { return (int)(await _client.CallAsync(request, TimeSpan.FromSeconds(23), _stop.Token)).Numbers.FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return 0; }
    }
    public string CopySelection() => Send(Request("copy")).Text;
    public async Task<int> QueryDropAsync(int action, int x = 0, int y = 0, IReadOnlyDictionary<string, byte[]>? items = null, uint operations = 1)
    {
        var reply = await _input.Enqueue(new HostRequest { Operation = "drop", Pane = _pane, Numbers = [action, x, y, operations], Items = items?.ToDictionary() }).ConfigureAwait(false);
        ObserveInputReply(reply); return (int)reply.Numbers.FirstOrDefault();
    }
    public async Task<(int Count, int Index)> FindAsync(string text, int direction)
    {
        var reply = await _input.Enqueue(Request("find", [direction], text)).ConfigureAwait(false);
        ObserveInputReply(reply); var values = reply.Numbers;
        return values.Length == 2 ? ((int)values[0], (int)values[1]) : (0, 0);
    }
    public string Hyperlink(ushort column, ushort row) => Send(Request("hyperlink", [column, row])).Text;
    public (int Count, int Index) Find(string text, int direction)
    { var values = Send(Request("find", [direction], text)).Numbers; return values.Length == 2 ? ((int)values[0], (int)values[1]) : (0, 0); }
    public bool Mouse(int action, int button, uint modifiers, float x, float y)
    {
        var request = Request("mouse", [action, button, modifiers, (long)(x * 1000), (long)(y * 1000)]);
        if (_state.MouseTracking is not { } tracking) return Send(request).Numbers.FirstOrDefault() != 0;
        if (tracking) QueueInput(request);
        return tracking;
    }
    public void Detach() => BeginClose(false);
    public void Dispose() => BeginClose(true);
    public ValueTask CloseAsync() => new(BeginClose(true));
    public ValueTask DetachAsync() => new(BeginClose(false));
    private Task BeginClose(bool close)
    {
        lock (_closeGate)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return _finishInput;
            return _finishInput = FinishInput(close);
        }
    }
    private async Task FinishInput(bool close)
    {
        _client.RemoveOutputListener(_pane, HostOutputChanged);
        await _input.Complete().ConfigureAwait(false); _stop.Cancel();
        try { await _pollTask.ConfigureAwait(false); }
        finally
        {
            // Public synchronous captures can overlap detachment. Wait for
            // their lease before releasing buffers and the transport owner.
            await _captureRequests.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    TerminalCellPool.Shared.Return(_cells);
                    if (!ReferenceEquals(_decodeCells, _cells)) TerminalCellPool.Shared.Return(_decodeCells);
                    _cells = []; _decodeCells = []; _decodeRepairRows = null; _rowRevisions = []; _captureBuffer = []; _snapshot = null; _captureStatus = null; _captureStatusRevision = -1; _images.Clear();
                }
            }
            finally { _captureRequests.Release(); _captureReads?.Dispose(); }
        }
        if (close) try { await _client.CallAsync(Request("close")).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }
}
