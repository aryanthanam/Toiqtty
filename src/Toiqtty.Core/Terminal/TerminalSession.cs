namespace Toiqtty.Terminal;

public sealed class TerminalSession : ITerminalSession, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Lock _resizeGate = new();
    internal object ParserGateForCheck => _gate;
    private readonly GhosttyTerminal _terminal;
    private readonly ConPtyProcess _process;
    private bool _disposed;
    private int _version;
    private readonly TerminalStatus _status = new();
    private TerminalStatusSnapshot _publishedStatus;
    private bool _mouseTracking;
    private readonly string _startDirectory;
    private readonly string _command;
    private readonly string? _directoryToken;
    private string _lastPwd = "";
    private long _bytesRead, _parseTicks, _snapshotTicks, _snapshots;
    private long _readBatches;
    private int _largestRead;
    private readonly Task _reader;
    private MemoryStream? _pasteOutput;
    private bool _pasteOverflow;
    public TerminalPerformance Performance => new(Interlocked.Read(ref _bytesRead), Interlocked.Read(ref _parseTicks), Interlocked.Read(ref _snapshotTicks), Interlocked.Read(ref _snapshots), _process.QueuedInputBytes, _process.PeakQueuedInputBytes)
    { ReadBatches = Interlocked.Read(ref _readBatches), LargestRead = Volatile.Read(ref _largestRead) };
    public int Version => Volatile.Read(ref _version);
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }
    public string Title { get; private set; } = "Terminal";
    public string StatusSummary { get; private set; } = "";
    public string Directory { get; private set; }
    public string WorkingDirectory { get; private set; }
    public string? LocalDirectory { get; private set; }
    public event Action? Attention;
    public event Action<TerminalNotice>? Notification;
    public event Action? OutputChanged;
    public bool MouseTracking => Volatile.Read(ref _mouseTracking);

    public TerminalSession(string command, string directory, Models.AppSettings? settings = null)
    {
        _publishedStatus = _status.Snapshot();
        Directory = WorkingDirectory = _startDirectory = directory;
        LocalDirectory = directory; _command = command;
        _terminal = new GhosttyTerminal { Clipboard = new WindowsClipboard(), AllowClipboardRead = settings?.AllowClipboardRead ?? false, AllowClipboardWrite = settings?.AllowClipboardWrite ?? true };
        _terminal.EnableIncrementalRendering();
        try
        {
            if (settings is not null) _terminal.SetScrollbackLimits(settings.ScrollbackLimitBytes, settings.ScrollbackLimitLines);
            _terminal.SetTheme(settings?.TerminalTheme);
            _terminal.SetCursorDefaults(settings?.Cursor ?? new());
            _terminal.AllowImageFiles(settings?.AllowImageFiles ?? true);
            string prepared = ShellIntegration.PrepareCommand(command);
            string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            // Integrated shells authenticate their OSC 7; other shells keep unauthenticated tracking.
            _directoryToken = prepared == command ? null : token;
            _process = new ConPtyProcess(prepared, directory, 100, 30, token);
        }
        catch { _terminal.Dispose(); throw; }
        _terminal.Response += WriteInput;
        _terminal.Attention += () => Attention?.Invoke();
        _terminal.HostEvent += item =>
        {
            _status.Apply(item);
            StatusSummary = _status.Summary;
            if (item.Kind == TerminalEventKind.Reset) { Title = "Terminal"; Directory = WorkingDirectory = _startDirectory; LocalDirectory = _startDirectory; _lastPwd = ""; }
            if (item.Kind == TerminalEventKind.Notification && _status.LatestNotice is { } notice) Notification?.Invoke(notice);
            if (item.Kind == TerminalEventKind.Notification || (item.Kind == TerminalEventKind.ProgramStatus && item.State is 2 or 3 or 4)) Attention?.Invoke();
        };
        _reader = Task.Run(ReadLoop);
    }
    private async Task ReadLoop()
    {
        var buffer = new byte[ConPtyProcess.OutputBufferBytes];
        try
        {
            int count;
            while ((count = await _process.Output.ReadAsync(buffer)) > 0)
            {
                Interlocked.Increment(ref _readBatches); if (count > _largestRead) Volatile.Write(ref _largestRead, count);
                lock (_gate)
                {
                    if (_disposed) continue;
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    _terminal.Write(buffer, count);
                    Interlocked.Add(ref _bytesRead, count);
                    Interlocked.Add(ref _parseTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                    var title = _terminal.Metadata(0);
                    if (!string.IsNullOrWhiteSpace(title)) Title = title;
                    var pwd = _terminal.Metadata(1);
                    if (pwd != _lastPwd && TerminalDirectory.Parse(pwd, _command, _directoryToken) is { } location)
                    {
                        _lastPwd = pwd;
                        Directory = location.Display; LocalDirectory = location.LocalPath;
                        WorkingDirectory = location.LocalPath ?? _startDirectory;
                    }
                    // Publish immutable metadata before its version. A capture
                    // that cannot acquire the parser gate can still return the
                    // last completed state without blocking on that same gate.
                    Volatile.Write(ref _mouseTracking, _terminal.MouseTracking);
                    PublishStatus();
                    Interlocked.Increment(ref _version);
                }
                OutputChanged?.Invoke();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally
        {
            lock (_gate) { if (!_disposed) _terminal.CancelDrop(); }
            _process.CloseOutput();
            ExitCode = await _process.ExitTask;
            lock (_gate) { _status.Finish(ExitCode, "Shell exited before the next prompt"); StatusSummary = _status.Summary; PublishStatus(); }
            HasExited = true;
            Interlocked.Increment(ref _version);
            OutputChanged?.Invoke();
        }
    }
    public TerminalFrame ReadFrame(TerminalCell[] cells) { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _terminal.ReadFrame(cells); } }
    public TerminalSnapshot ReadSnapshot(TerminalCell[] cells)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var snapshot = _terminal.ReadSnapshot(cells);
            Interlocked.Add(ref _snapshotTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref _snapshots); return snapshot;
        }
    }
    public bool TryReadSnapshot(TerminalCell[] cells, out TerminalSnapshot? snapshot)
    {
        snapshot = null;
        // Sustained shell output must not stall input/layout while the parser owns the gate.
        if (!Monitor.TryEnter(_gate)) return false;
        try { if (_disposed) return false; snapshot = ReadSnapshot(cells); return true; }
        finally { Monitor.Exit(_gate); }
    }
    internal bool TryReadSnapshot(Utf8TerminalBuffer cells, out TerminalSnapshot? snapshot)
    {
        snapshot = null;
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            if (_disposed) return false;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            snapshot = _terminal.ReadSnapshot(cells);
            Interlocked.Add(ref _snapshotTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref _snapshots);
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }
    private void PublishStatus() => Volatile.Write(ref _publishedStatus, _status.Snapshot());
    public TerminalStatusSnapshot Status => Volatile.Read(ref _publishedStatus);
    public void ApplySettings(Models.AppSettings settings)
    {
        lock (_gate)
        {
            _terminal.SetScrollbackLimits(settings.ScrollbackLimitBytes, settings.ScrollbackLimitLines);
            _terminal.SetTheme(settings.TerminalTheme);
            _terminal.SetCursorDefaults(settings.Cursor);
            _terminal.AllowClipboardRead = settings.AllowClipboardRead;
            _terminal.AllowClipboardWrite = settings.AllowClipboardWrite;
            _terminal.AllowImageFiles(settings.AllowImageFiles);
            Interlocked.Increment(ref _version);
        }
    }
    private void WriteInput(byte[] bytes)
    {
        if (_pasteOutput is { } paste)
        {
            if (paste.Length + bytes.Length > ConPtyProcess.MaxQueuedInputBytes) _pasteOverflow = true;
            else if (!_pasteOverflow) paste.Write(bytes);
            return;
        }
        if (_process.TryWrite(bytes)) return;
        InputBusy();
    }
    private void InputBusy()
    {
        lock (_gate) { if (_disposed || HasExited) return; _status.Apply(new(TerminalEventKind.Notification, Title: "Input busy", Body: "Input was not queued. Retry when the application resumes reading.")); PublishStatus(); Interlocked.Increment(ref _version); }
        Attention?.Invoke();
    }
    public string Hyperlink(ushort column, ushort row) { lock (_gate) { return _disposed ? "" : _terminal.Hyperlink(column, row); } }
    public void Resize(ushort columns, ushort rows, uint width, uint height)
    {
        lock (_resizeGate)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _terminal.Resize(columns, rows, width, height);
                Interlocked.Increment(ref _version);
            }
            // ConPTY resize can wait for its output pipe. Keep the reader free
            // to drain/parse that output while the control call completes.
            if (!HasExited) _process.Resize(columns, rows);
        }
    }
    public void Key(uint key, uint modifiers, string text = "", TerminalKeyAction action = TerminalKeyAction.Press,
        uint? unshiftedCodepoint = null, uint consumedModifiers = 0)
    {
        byte[] bytes;
        lock (_gate)
        {
            if (_disposed || HasExited) return;
            if (action != TerminalKeyAction.Release) _terminal.Scroll(0);
            bytes = _terminal.EncodeKey(key, modifiers, text, action, unshiftedCodepoint, consumedModifiers);
            if (action != TerminalKeyAction.Release) Interlocked.Increment(ref _version);
            WriteInput(bytes);
        }
    }
    public void SendText(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 65536) throw new ArgumentException("Literal input exceeds 64 KiB.");
        lock (_gate)
        {
            if (_disposed || HasExited) throw new Hosting.SessionExitedException("The terminal has exited; literal input was not sent.");
            _terminal.Scroll(0);
            if (!_process.TryWrite(System.Text.Encoding.UTF8.GetBytes(text))) { InputBusy(); throw new Hosting.InputBusyException("Input busy: literal input was not queued."); }
            Interlocked.Increment(ref _version);
        }
    }
    public Task PasteAsync(string text)
    {
        lock (_gate)
        {
            if (_disposed || HasExited) throw new Hosting.SessionExitedException("The terminal has exited; paste was not sent.");
            if (System.Text.Encoding.UTF8.GetByteCount(text) > TerminalClipboardLimits.MaxBytes) throw new ArgumentException("Paste exceeds its size limit.");
            PasteCore(() => _terminal.Paste(text, false), true);
        }
        return Task.CompletedTask;
    }
    public void Paste(string text, bool clipboard = false)
    {
        lock (_gate)
        {
            if (_disposed || HasExited) return;
            if (System.Text.Encoding.UTF8.GetByteCount(text) > TerminalClipboardLimits.MaxBytes) { InputBusy(); return; }
            PasteCore(() => _terminal.Paste(text, clipboard));
        }
    }
    public void PasteClipboard()
    {
        if (Volatile.Read(ref _disposed) || HasExited) return;
        // The OS clipboard owner can render lazily and slowly. Read it before
        // the parser gate; only the mode-dependent encoding runs under it.
        GhosttyTerminal.ClipboardPasteSource? source; IOException? failure = null;
        try { source = _terminal.ReadClipboardPaste(); } catch (IOException ex) { source = null; failure = ex; }
        lock (_gate)
        {
            if (_disposed || HasExited) return;
            if (failure is null) try { PasteCore(() => _terminal.PasteClipboard(source)); } catch (IOException ex) { failure = ex; }
            if (failure is not null) { _status.Apply(new(TerminalEventKind.Notification, Title: "Clipboard unavailable", Body: failure.Message)); Interlocked.Increment(ref _version); Attention?.Invoke(); }
        }
    }
    private void PasteCore(Action paste, bool requireAdmission = false)
    {
        _terminal.Scroll(0);
        using var output = new MemoryStream();
        _pasteOutput = output; _pasteOverflow = false;
        try { paste(); }
        finally { _pasteOutput = null; }
        // Bracketed-paste markers and content must enter the writer together.
        if (requireAdmission)
        {
            if (_pasteOverflow || !_process.TryWrite(output.ToArray())) { InputBusy(); throw new Hosting.InputBusyException("Input busy: the complete paste was not queued."); }
        }
        else if (_pasteOverflow) InputBusy(); else WriteInput(output.ToArray());
        Interlocked.Increment(ref _version);
    }
    public void Scroll(int delta) { lock (_gate) { if (_disposed) return; _terminal.Scroll(delta); Interlocked.Increment(ref _version); } }
    public void ScrollTo(ulong row) { lock (_gate) { if (_disposed) return; _terminal.ScrollTo(row); Interlocked.Increment(ref _version); } }
    public void Focus(bool focused) { lock (_gate) { if (!_disposed) _terminal.Focus(focused); } }
    public void Select(int start, int end) { lock (_gate) { if (_disposed) return; _terminal.Select(start, end); Interlocked.Increment(ref _version); } }
    public int SelectionGesture(int action, float x, float y, bool rectangle = false)
    {
        lock (_gate) { if (_disposed) return 0; int result = _terminal.SelectionGesture(action, x, y, rectangle); Interlocked.Increment(ref _version); return result; }
    }
    public int Drop(int action, int x = 0, int y = 0, IReadOnlyDictionary<string, byte[]>? items = null, uint operations = 1)
    {
        lock (_gate) { return _disposed || HasExited ? 0 : _terminal.Drop(action, x, y, items, operations); }
    }
    public async Task<int> DropAsync(int x, int y, IReadOnlyDictionary<string, byte[]> items, uint operations = 1, TimeSpan? timeout = null)
    {
        Task<int> completion;
        lock (_gate)
        {
            if (_disposed || HasExited) return 0;
            completion = _terminal.BeginDrop(x, y, items, operations);
        }
        try { return await completion.WaitAsync(timeout ?? TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            lock (_gate) { if (!_disposed) _terminal.CancelDrop(completion); }
            return 0;
        }
    }
    public string CopySelection() { lock (_gate) { return _disposed ? "" : _terminal.CopySelection(); } }
    public (int Count, int Index) Find(string text, int direction)
    {
        lock (_gate) { if (_disposed) return (0, 0); var result = _terminal.Find(text, direction); Interlocked.Increment(ref _version); return result; }
    }
    public bool Mouse(int action, int button, uint modifiers, float x, float y)
    {
        byte[]? bytes;
        lock (_gate)
        {
            if (_disposed || HasExited) return false; bytes = _terminal.EncodeMouse(action, button, modifiers, x, y);
            if (bytes is null) return false;
            WriteInput(bytes); return true;
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _mouseTracking, false);
            _terminal.Dispose();
        }
        _process.Dispose();
    }
    public async ValueTask DisposeAsync() { Dispose(); await _reader; await _process.Closed; }
    public ValueTask CloseAsync() => DisposeAsync();
}
public sealed record TerminalPerformance(long BytesRead, long ParseTicks, long SnapshotTicks, long Snapshots, long QueuedInputBytes, long PeakQueuedInputBytes)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public long ReadBatches { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public int LargestRead { get; init; }
}
