using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Toiqtty.Terminal;

/// <summary>Windows pseudoconsole and child process. Output is drained until EOF during shutdown.</summary>
public sealed class ConPtyProcess : IDisposable
{
    internal static readonly int OutputBufferBytes = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_READ_KIB") == "64" ? 65536 : 16384;
    private nint _console;
    private readonly Lock _consoleGate = new();
    internal Action? BeforeResizeForCheck { get; set; }
    internal Action? BeforeConsoleCloseForCheck { get; set; }
    private readonly SafeFileHandle? _process;
    private readonly PipeStream? _input, _output;
    private volatile bool _disposed;
    public const int MaxQueuedInputBytes = 8 * 1024 * 1024;
    private readonly Lock _writeGate = new();
    private readonly Channel<byte[]> _writes = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long _queuedInputBytes;
    public long QueuedInputBytes => Interlocked.Read(ref _queuedInputBytes);
    public long PeakQueuedInputBytes { get; private set; }
    private int _closeStarted;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Closed => _closed.Task;
    public Stream Output => _output ?? throw new ObjectDisposedException(nameof(ConPtyProcess));
    public Task<int> ExitTask { get; private set; } = Task.FromResult(-1);

    public ConPtyProcess(string command, string directory, ushort columns, ushort rows, string? directoryToken = null)
    {
        NamedPipeClientStream? inputRead = null, outputWrite = null;
        NamedPipeServerStream? inputWrite = null, outputRead = null;
        nint attributes = 0, environment = 0;
        bool attributesInitialized = false;
        try
        {
            (inputWrite, inputRead) = CreatePipePair(PipeDirection.Out);
            (outputRead, outputWrite) = CreatePipePair(PipeDirection.In);
            // Pin the console host as well as the VT engine. The system ConPTY on
            // older Windows drops APC graphics and other unknown VT sequences.
            // PSEUDOCONSOLE_GLYPH_WIDTH_GRAPHEMES aligns its width model with Ghostty.
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead.SafePipeHandle, outputWrite.SafePipeHandle, 0x08, out _console));
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesInitialized = true;
            // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE takes the HPCON value itself.
            Check(UpdateProcThreadAttribute(attributes, 0, (nuint)0x20016, _console, (nuint)nint.Size, 0, 0));
            // Explicitly clear redirected parent std handles. Otherwise console clients
            // can bypass ConPTY when Toiqtty is launched by a pipe-based runner.
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 }, AttributeList = attributes };
            var inherited = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(entry => new KeyValuePair<string, string>((string)entry.Key, (string)entry.Value!));
            environment = Marshal.StringToHGlobalUni(ShellIntegration.CreateEnvironmentBlock(inherited, directoryToken));
            Check(CreateProcessW(null, new StringBuilder(command), 0, 0, false, 0x00080000 | 0x00000400,
                environment, directory, ref startup, out var info));
            inputRead.Dispose(); outputWrite.Dispose();
            _process = new SafeFileHandle(info.Process, true);
            new SafeFileHandle(info.Thread, true).Dispose();
            _input = inputWrite;
            _output = outputRead;
            inputWrite = null; outputRead = null;
            ExitTask = WaitForExitAsync(_process);
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var bytes in _writes.Reader.ReadAllAsync())
                    {
                        try
                        {
                            if (_disposed) break;
                            for (int offset = 0; offset < bytes.Length; offset += 16384)
                                await _input.WriteAsync(bytes.AsMemory(offset, Math.Min(16384, bytes.Length - offset)));
                        }
                        finally { Interlocked.Add(ref _queuedInputBytes, -bytes.Length); }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                finally
                {
                    _writes.Writer.TryComplete();
                    while (_writes.Reader.TryRead(out var pending)) Interlocked.Add(ref _queuedInputBytes, -pending.Length);
                }
            });
            // ConPTY owns an output handle until it is closed, even after the shell
            // exits. Close it on process exit so the reader can observe EOF.
            _ = ExitTask.ContinueWith(_ => Dispose(), TaskScheduler.Default);
        }
        catch { Dispose(); throw; }
        finally
        {
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }
    private static (NamedPipeServerStream Parent, NamedPipeClientStream Console) CreatePipePair(PipeDirection direction)
    {
        string name = "Toiqtty-ConPTY-" + Guid.NewGuid().ToString("N");
        int capacity = direction == PipeDirection.In ? OutputBufferBytes : 16384;
        var parent = new NamedPipeServerStream(name, direction, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, capacity, capacity);
        NamedPipeClientStream? console = null;
        try
        {
            // ConPTY receives synchronous handles. Only our end is overlapped,
            // so pending reads/writes use I/O completion rather than a worker.
            console = new(".", name, direction == PipeDirection.In ? PipeDirection.Out : PipeDirection.In, PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var connected = parent.WaitForConnectionAsync(timeout.Token);
            console.Connect(1000); connected.GetAwaiter().GetResult();
            return (parent, console);
        }
        catch { console?.Dispose(); parent.Dispose(); throw; }
    }
    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeFileHandle process) => SafeWaitHandle = new(process.DangerousGetHandle(), false);
    }
    private static async Task<int> WaitForExitAsync(SafeFileHandle process)
    {
        // A long-running shell must not occupy a worker just to wait for exit.
        // Keep the original handle alive, including during concurrent disposal;
        // no PID lookup can accidentally attach this wait to a reused PID.
        bool reference = false;
        process.DangerousAddRef(ref reference);
        try
        {
            using var wait = new ProcessWaitHandle(process);
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
                completion.TrySetResult(GetExitCodeProcess(process.DangerousGetHandle(), out uint code) ? unchecked((int)code) : -1),
                null, Timeout.Infinite, true);
            try { return await completion.Task.ConfigureAwait(false); }
            finally { registration.Unregister(null); }
        }
        finally { if (reference) process.DangerousRelease(); }
    }
    public void Write(byte[] bytes)
    {
        if (!TryWrite(bytes)) throw new IOException("Terminal input is busy; the input was not queued. Try again after the application resumes reading.");
    }
    public bool TryWrite(byte[] bytes)
    {
        if (bytes.Length == 0) return true;
        lock (_writeGate)
        {
            if (_disposed || bytes.Length > MaxQueuedInputBytes - QueuedInputBytes) return false;
            Interlocked.Add(ref _queuedInputBytes, bytes.Length);
            if (!_writes.Writer.TryWrite(bytes)) { Interlocked.Add(ref _queuedInputBytes, -bytes.Length); return false; }
            PeakQueuedInputBytes = Math.Max(PeakQueuedInputBytes, QueuedInputBytes);
            return true;
        }
    }
    public void Resize(ushort columns, ushort rows)
    {
        lock (_consoleGate)
        {
            if (_disposed || ExitTask.IsCompleted) return;
            BeforeResizeForCheck?.Invoke();
            if (_disposed || ExitTask.IsCompleted) return;
            int result = ResizePseudoConsole(_console, new Coord((short)columns, (short)rows));
            if (!_disposed && !ExitTask.IsCompleted) Marshal.ThrowExceptionForHR(result);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0) return;
        _disposed = true;
        _writes.Writer.TryComplete();
        // ClosePseudoConsole can wait for the output pipe: never block the UI or its reader.
        _ = Task.Run(() =>
        {
            try
            {
                // A pane can close before the child finishes console initialization.
                // Stop our own child first so startup cannot keep the pseudoconsole alive.
                if (_process is { IsClosed: false, IsInvalid: false } && !ExitTask.IsCompleted)
                    TerminateProcess(_process, 1);
                BeforeConsoleCloseForCheck?.Invoke();
                // Retire the child before waiting for a resize lease: its exit and
                // the output reader can unblock a backed-up ConPTY control call.
                lock (_consoleGate)
                {
                    var console = Interlocked.Exchange(ref _console, 0);
                    if (console != 0) ClosePseudoConsole(console);
                }
                _input?.Dispose();
                _process?.Dispose();
            }
            finally { _closed.TrySetResult(); }
        });
    }
    internal void CloseOutput() => _output?.Dispose();
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public nint ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("conpty.dll", EntryPoint = "ConptyCreatePseudoConsole")] private static extern int CreatePseudoConsole(Coord size, SafePipeHandle input, SafePipeHandle output, uint flags, out nint console);
    [DllImport("conpty.dll", EntryPoint = "ConptyResizePseudoConsole")] private static extern int ResizePseudoConsole(nint console, Coord size);
    [DllImport("conpty.dll", EntryPoint = "ConptyClosePseudoConsole")] private static extern void ClosePseudoConsole(nint console);
    [DllImport("kernel32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string? application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
}
