namespace Toiqtty.Terminal;

/// <summary>Host-owned capture storage. Inline text stays in the native ABI;
/// overflow bytes are copied before leaving the terminal's parser gate.</summary>
internal sealed class Utf8TerminalBuffer(int count)
{
    internal readonly GhosttyTerminal.Native.Cell[] Cells = new GhosttyTerminal.Native.Cell[count];
    private readonly Dictionary<int, byte[]> _overflow = [];
    internal int OverflowCapacityForCheck => _overflow.EnsureCapacity(0);
    internal int Length => Cells.Length;
    internal ReadOnlySpan<byte> Text(int index)
    {
        ref var cell = ref Cells[index];
        if (cell.Length > GhosttyTerminal.Native.CellTextBytes) return _overflow[index];
        ReadOnlySpan<byte> inline = cell.Text;
        return inline[..checked((int)cell.Length)];
    }
    internal unsafe void OwnOverflow(IReadOnlyList<int> changedRows, int columns)
    {
        for (int rowIndex = 0; rowIndex < changedRows.Count; rowIndex++)
        {
            int start = changedRows[rowIndex] * columns;
            for (int index = start; index < start + columns; index++)
            {
                ref var cell = ref Cells[index];
                if (cell.Length > GhosttyTerminal.Native.CellTextBytes)
                {
                    var source = new ReadOnlySpan<byte>((void*)cell.ExtendedText, checked((int)cell.Length));
                    if (!_overflow.TryGetValue(index, out var previous) || !source.SequenceEqual(previous))
                        _overflow[index] = source.ToArray();
                }
                else if (_overflow.Count != 0) _overflow.Remove(index);
                // No borrowed engine pointer may escape the capture gate.
                cell.ExtendedText = 0;
            }
        }
        // A viewport full of long graphemes is exceptional. Once all of them
        // disappear, retain no large index table for the remaining pane life.
        if (_overflow.Count == 0 && _overflow.EnsureCapacity(0) > 256) _overflow.TrimExcess();
    }
}
