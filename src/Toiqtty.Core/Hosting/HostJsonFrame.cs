using System.Buffers;

namespace Toiqtty.Hosting;

// Owns the complete length-prefixed JSON message until its asynchronous pipe
// write ends. Common messages borrow bounded pool storage; large messages have
// call lifetime and cannot leave a multi-megabyte buffer in the shared pool.
internal sealed class HostJsonFrame : IBufferWriter<byte>, IDisposable
{
    private byte[] _bytes = ArrayPool<byte>.Shared.Rent(4096);
    private bool _pooled = true;
    private int _written = 4;
    internal int PayloadLength => _written - 4;
    internal ReadOnlyMemory<byte> Message => _bytes.AsMemory(0, _written);
    internal void SetLength() => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_bytes, PayloadLength);
    public void Advance(int count)
    {
        if (count < 0 || count > _bytes.Length - _written) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > HostProtocol.MaxJsonBytes + 4 - _written) throw new HostMessageTooLargeException();
        _written += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _bytes.AsMemory(_written); }
    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _bytes.AsSpan(_written); }
    private void Ensure(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_bytes.Length == 0, this);
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        sizeHint = Math.Max(sizeHint, 1);
        if (sizeHint <= _bytes.Length - _written) return;
        // Utf8JsonWriter may ask for worst-case escaping space before writing
        // fewer bytes. Enforce the wire limit on Advance, not on that estimate.
        const int scratchLimit = HostProtocol.MaxJsonBytes * 6 + 4;
        if (sizeHint > scratchLimit - _written) throw new HostMessageTooLargeException();
        int capacity = Math.Min(scratchLimit, Math.Max(_written + sizeHint, _bytes.Length * 2));
        bool pooled = capacity <= 65536;
        var replacement = pooled ? ArrayPool<byte>.Shared.Rent(capacity) : new byte[capacity];
        _bytes.AsSpan(0, _written).CopyTo(replacement);
        if (_pooled) ArrayPool<byte>.Shared.Return(_bytes, true);
        _bytes = replacement; _pooled = pooled;
    }
    public void Dispose()
    {
        if (_pooled && _bytes.Length > 0) ArrayPool<byte>.Shared.Return(_bytes, true);
        _bytes = []; _written = 0; _pooled = false;
    }
}
