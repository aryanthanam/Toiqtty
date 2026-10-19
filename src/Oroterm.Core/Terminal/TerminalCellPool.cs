using System.Runtime.CompilerServices;

namespace Oroterm.Terminal;

/// <summary>Exact-sized, cleared buffers with a process-wide retention budget.</summary>
internal sealed class TerminalCellPool(long maxBytes=16L*1024*1024,int maxBuffers=128,int maxBufferBytes=2*1024*1024)
{
    internal static readonly TerminalCellPool Shared=new();
    private static readonly int CellBytes=Unsafe.SizeOf<TerminalCell>();
    private readonly object _gate=new();
    private readonly List<TerminalCell[]> _buffers=[];
    private long _retainedBytes,_created,_reused;

    internal TerminalCell[] Rent(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        lock(_gate)
        {
            for(int i=_buffers.Count-1;i>=0;i--)
            {
                var buffer=_buffers[i];
                if(buffer.Length!=count) continue;
                _buffers.RemoveAt(i); _retainedBytes-=(long)count*CellBytes; _reused++;
                return buffer;
            }
            _created++;
        }
        return new TerminalCell[count];
    }

    // Call only after the owning view/decoder can no longer read this array.
    // Native local-terminal consumers retain buffer identity; their arrays are
    // deliberately not returned by a view while the engine may still own them.
    internal void Return(TerminalCell[] buffer)
    {
        long bytes=(long)buffer.Length*CellBytes;
        if(bytes==0 || bytes>maxBufferBytes || bytes>maxBytes || maxBuffers<=0) return;
        Array.Clear(buffer); // Release extended graphemes and previous appearance.
        lock(_gate)
        {
            if(_buffers.Contains(buffer)) throw new InvalidOperationException("A terminal cell buffer was returned twice.");
            while(_buffers.Count>0 && (_buffers.Count>=maxBuffers || _retainedBytes+bytes>maxBytes))
            {
                _retainedBytes-=(long)_buffers[0].Length*CellBytes; _buffers.RemoveAt(0);
            }
            _buffers.Add(buffer); _retainedBytes+=bytes;
        }
    }

    internal (long Bytes,int Buffers,long Created,long Reused) Statistics
    {
        get { lock(_gate) return (_retainedBytes,_buffers.Count,_created,_reused); }
    }
}
