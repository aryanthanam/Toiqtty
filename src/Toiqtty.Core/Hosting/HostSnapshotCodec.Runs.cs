using System.Text;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

public static partial class HostSnapshotCodec
{
    private const uint FullRunsMagic = 0x4652544f, DeltaRunsMagic = 0x4452544f; // OTRF / OTRD.

    // Negotiated with style-runs-v1. A row owns its run boundaries; no run may
    // cross a row or make a delta depend on another row's previous attributes.
    internal static byte[] EncodeUtf8Runs(TerminalSnapshot snapshot, Utf8TerminalBuffer cells, HashSet<ulong> known, bool delta = false)
    {
        var frame = snapshot.Frame; int count = checked((int)(frame.Columns * frame.Rows));
        if (cells.Length < count) throw new ArgumentException("The cell buffer does not fit the frame.");
        IReadOnlyList<int>? rows = delta ? snapshot.ChangedRows : null;
        if (rows is not null)
        {
            int previous = -1;
            foreach (int row in rows) { if (row <= previous || row >= frame.Rows) throw new ArgumentException("Invalid changed row."); previous = row; }
        }
        var images = snapshot.Graphics.Count == 0 ? [] : snapshot.Graphics.Select(g => g.Image).DistinctBy(i => i.Generation).ToArray();
        long baseBytes = 76L + images.Length * 21L + snapshot.Graphics.Count * 60L + (delta ? 8L + rows!.Count * 4L : 0);
        foreach (var image in images) if (!known.Contains(image.Generation)) baseBytes += 4L + image.Pixels.Length;
        long legacyBytes = baseBytes, runBytes = baseBytes + (delta ? 0 : 4);
        int rowCount = rows?.Count ?? (int)frame.Rows, columns = (int)frame.Columns;
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            int start = (rows is null ? rowIndex : rows[rowIndex]) * columns, end = start + columns;
            for (int first = start; first < end;)
            {
                int last = RunEnd(cells, first, end);
                runBytes += 24 + PrefixSize(last - first);
                for (int index = first; index < last; index++)
                {
                    int length = checked((int)cells.Cells[index].Length), textBytes = checked(PrefixSize(length) + length);
                    runBytes += textBytes; legacyBytes += 24L + textBytes;
                }
                first = last;
            }
        }
        // Highly varied styles can cost more than ordinary cells. Never expand
        // their payload, and never allocate a limit-sized temporary for fallback.
        if (legacyBytes <= runBytes) return EncodeUtf8(snapshot, cells, known, delta);
        if (runBytes > HostProtocol.MaxSnapshotBytes) throw new IOException("Terminal snapshot exceeds the transport limit.");
        var payload = new byte[checked((int)runBytes)];
        using var stream = new MemoryStream(payload, true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(delta ? DeltaRunsMagic : FullRunsMagic); WriteFrame(writer, frame); writer.Write(count);
        if (delta) writer.Write(rows!.Count);
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            int row = rows is null ? rowIndex : rows[rowIndex]; if (delta) writer.Write(row);
            stream.Position += WriteRunRow(payload.AsSpan(checked((int)stream.Position)), cells, row * columns, columns);
        }
        WriteGraphics(writer, snapshot.Graphics, images, known);
        if (stream.Position != runBytes) throw new IOException("Terminal snapshot changed during encoding.");
        return payload;
    }
    private static int PrefixSize(int length)
    {
        int count = 1; for (uint remaining = (uint)length; remaining >= 128; remaining >>= 7) count++; return count;
    }
    private static int RunEnd(Utf8TerminalBuffer cells, int first, int end)
    {
        ref var a = ref cells.Cells[first]; int last = first + 1;
        while (last < end)
        {
            ref var b = ref cells.Cells[last];
            if (a.Foreground != b.Foreground || a.Background != b.Background || a.Flags != b.Flags || a.Width != b.Width
                || a.UnderlineColor != b.UnderlineColor || a.UnderlineStyle != b.UnderlineStyle) break;
            last++;
        }
        return last;
    }
    private static int WriteRunRow(Span<byte> destination, Utf8TerminalBuffer cells, int start, int columns)
    {
        int written = 0, end = start + columns;
        for (int first = start; first < end;)
        {
            int last = RunEnd(cells, first, end); written += WriteLength(destination[written..], last - first);
            ref var cell = ref cells.Cells[first];
            WriteCellHeader(destination[written..], cell.Foreground, cell.Background, cell.Flags, cell.UnderlineColor, cell.UnderlineStyle, cell.Width); written += 24;
            for (int index = first; index < last; index++)
            {
                var text = cells.Text(index); written += WriteLength(destination[written..], text.Length);
                text.CopyTo(destination[written..]); written += text.Length;
            }
            first = last;
        }
        return written;
    }
    private static void ReadRunRow(BinaryReader reader, byte[] payload, TerminalCell[] cells, int start, int columns)
    {
        int end = start + columns;
        while (start < end)
        {
            int length = reader.Read7BitEncodedInt();
            if (length <= 0 || length > end - start) throw new HostSnapshotException("Invalid terminal style-run length.");
            var style = new TerminalCell
            {
                Foreground = reader.ReadUInt32(),
                Background = reader.ReadUInt32(),
                Flags = reader.ReadUInt32(),
                UnderlineColor = reader.ReadUInt32(),
                UnderlineStyle = reader.ReadUInt32(),
                Width = reader.ReadUInt32()
            };
            int runEnd = start + length;
            for (; start < runEnd; start++) { cells[start] = style; ReadCellText(reader, payload, ref cells[start]); }
        }
    }
}
