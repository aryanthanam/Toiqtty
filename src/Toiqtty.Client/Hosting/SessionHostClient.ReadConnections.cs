using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Toiqtty.Hosting;

public sealed partial class SessionHostClient
{
    private readonly Lock _capturePoolGate = new();
    private CaptureReadPool? _capturePool;
    private int _capturePoolReferences;
    internal bool HasCapturePoolForCheck { get { lock (_capturePoolGate) return _capturePool is not null; } }
    internal CaptureReadLease? AcquireCaptureReads()
    {
        if (DisableReusableReadsForCheck) return null;
        lock (_capturePoolGate)
        {
            _capturePool ??= new(this); _capturePoolReferences++;
            return new(this, _capturePool);
        }
    }
    private void ReleaseCaptureReads(CaptureReadPool pool)
    {
        bool retire = false;
        lock (_capturePoolGate)
        {
            if (!ReferenceEquals(pool, _capturePool) || _capturePoolReferences <= 0) throw new InvalidOperationException("Invalid capture-pool lifetime.");
            if (--_capturePoolReferences == 0) { _capturePool = null; retire = true; }
        }
        if (retire) pool.Dispose();
    }
    // One lease per remote-session lifetime. Its owner drains its capture gate
    // and polling task before release; the last lease retires every idle pipe.
    internal sealed class CaptureReadLease(SessionHostClient owner, CaptureReadPool pool) : IDisposable
    {
        private SessionHostClient? _owner = owner;
        internal Task<HostReply> SendAsync(HostRequest request, byte[]? reuse, CancellationToken cancellation)
        {
            ObjectDisposedException.ThrowIf(_owner is null, this); return pool.SendAsync(request, reuse, cancellation);
        }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseCaptureReads(pool);
    }
    internal sealed class CaptureReadPool(SessionHostClient client) : IDisposable
    {
        private readonly SemaphoreSlim _slots = new(8);
        private readonly ConcurrentQueue<HostReadConnection> _idle = new();
        internal async Task<HostReply> SendAsync(HostRequest request, byte[]? reuse, CancellationToken cancellation)
        {
            await _slots.WaitAsync(cancellation).ConfigureAwait(false);
            HostReadConnection? connection = null;
            try
            {
                if (!_idle.TryDequeue(out connection)) connection = new(client);
                return await connection.SendAsync(request, reuse, cancellation).ConfigureAwait(false);
            }
            finally { if (connection is not null) _idle.Enqueue(connection); _slots.Release(); }
        }
        public void Dispose()
        {
            System.Diagnostics.Debug.Assert(_slots.CurrentCount == 8, "Remote sessions must drain captures before releasing their last pool lease.");
            while (_idle.TryDequeue(out var connection)) connection.Dispose(); _slots.Dispose();
        }
    }
    // Exclusive ownership is supplied by the pool or the sole output watcher.
    // Reconnect only a stale read-only channel, never an input/control request.
    internal sealed class HostReadConnection(SessionHostClient client) : IDisposable
    {
        private NamedPipeClientStream? _pipe;
        internal async Task<HostReply> SendAsync(HostRequest request, byte[]? reuse, CancellationToken cancellation)
        {
            if (request.Operation is not ("capture" or "watch" or "states")) throw new ArgumentException("Reusable channels accept only read-only requests.");
            request.ReuseReadConnection = true;
            for (int attempt = 0; ; attempt++)
            {
                bool reused = _pipe is not null;
                try
                {
                    if (_pipe is null)
                    {
                        _pipe = client.CreatePipe(); await _pipe.ConnectAsync(cancellation).ConfigureAwait(false); PipeServerTrust.Verify(_pipe.SafePipeHandle);
                        Interlocked.Increment(ref client.ReadConnectionsOpenedForCheck);
                    }
                    var reply = await client.ExchangeAsync(_pipe, request, reuse, cancellation).ConfigureAwait(false);
                    if (reused) Interlocked.Increment(ref client.ReadConnectionsReusedForCheck);
                    if (!reply.KeepAlive) Dispose(); return reply;
                }
                catch (Exception ex)
                {
                    Dispose();
                    bool stale = ex is EndOfStreamException || ex is IOException && ((ex.HResult & 0xffff) is 109 or 232 or 233);
                    if (!reused || attempt != 0 || !stale || cancellation.IsCancellationRequested) throw;
                }
            }
        }
        public void Dispose() { _pipe?.Dispose(); _pipe = null; }
    }
}
