using System.Text;
using System.Buffers.Binary;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

internal sealed class HostSnapshotException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Lossless full frames and negotiated row deltas; pixels cross once per generation.</summary>
public static partial class HostSnapshotCodec
{
    private const uint DeltaMagic = 0x3144544f; // OTD1, only used for row deltas.
    public static byte[] Encode(TerminalSnapshot snapshot, TerminalCell[] cells, HashSet<ulong> known)
        => EncodeCore(snapshot, cells, known, null);
    public static byte[] EncodeDelta(TerminalSnapshot snapshot, TerminalCell[] cells, HashSet<ulong> known)
        => EncodeCore(snapshot, cells, known, snapshot.ChangedRows);
    internal static byte[] EncodeUtf8(TerminalSnapshot snapshot, Utf8TerminalBuffer cells, HashSet<ulong> known, bool delta = false)
        => EncodeCore(snapshot, null, known, delta ? snapshot.ChangedRows : null, cells);
    private static byte[] EncodeCore(TerminalSnapshot snapshot, TerminalCell[]? cells, HashSet<ulong> known, IReadOnlyList<int>? rows, Utf8TerminalBuffer? utf8 = null)
    {
        var frame = snapshot.Frame; int count = checked((int)(frame.Columns * frame.Rows));
        if ((utf8?.Length ?? cells!.Length) < count) throw new ArgumentException("The cell buffer does not fit the frame.");
        if (rows is not null)
        {
            int previous = -1;
            foreach (int row in rows) { if (row <= previous || row >= frame.Rows) throw new ArgumentException("Changed rows must be unique and ordered within the frame."); previous = row; }
        }
        var images = snapshot.Graphics.Count == 0 ? [] : snapshot.Graphics.Select(g => g.Image).DistinctBy(i => i.Generation).ToArray();
        int encodedSize = EncodedSize(frame, cells, count, snapshot.Graphics, images, known, rows, utf8);
        // This exact-sized array belongs to the reply until its pipe write has
        // completed. A fixed stream writes directly into it without ToArray.
        var payload = new byte[encodedSize];
        using var stream = new MemoryStream(payload, true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        if (rows is not null) writer.Write(DeltaMagic);
        WriteFrame(writer, frame); writer.Write(count);
        if (rows is null)
            WriteCells(0, count);
        else
        {
            writer.Write(rows.Count);
            foreach (int row in rows)
            {
                writer.Write(row); int start = checked(row * (int)frame.Columns);
                WriteCells(start, (int)frame.Columns);
            }
        }
        WriteGraphics(writer, snapshot.Graphics, images, known);
        if (stream.Length > HostProtocol.MaxSnapshotBytes) throw new IOException("Terminal snapshot exceeds the transport limit.");
        if (stream.Position != encodedSize) throw new IOException("Terminal snapshot changed during encoding.");
        return payload;
        void WriteCells(int start, int length)
        {
            var destination = payload.AsSpan(checked((int)stream.Position)); int written = 0;
            if (utf8 is null)
                for (int index = start; index < start + length; index++) written += WriteCell(destination[written..], in cells![index]);
            else
                for (int index = start; index < start + length; index++) written += WriteCell(destination[written..], utf8, index);
            stream.Position += written;
        }
    }
    private static int EncodedSize(TerminalFrame frame, TerminalCell[]? cells, int count, IReadOnlyList<TerminalGraphic> graphics,
        TerminalImage[] images, HashSet<ulong> known, IReadOnlyList<int>? rows, Utf8TerminalBuffer? utf8)
    {
        // Frame/count plus both graphics counts, then image and placement fields.
        // The caller owns these cells under its capture lease through encoding.
        // Count actual UTF-8 and 7-bit string prefixes before obtaining a payload
        // buffer; a heuristic capacity still allowed hundreds of MiB of growth
        // before the old trailing size check rejected an oversized frame.
        long bytes = 76L + images.Length * 21L + graphics.Count * 60L;
        if (rows is not null) bytes += 8L + rows.Count * 4L; // Delta magic, count, row indices.
        foreach (var image in images) if (!known.Contains(image.Generation)) bytes += 4L + image.Pixels.Length;
        Limit(bytes);
        if (rows is null)
            for (int i = 0; i < count; i++) AddCell(i);
        else
            foreach (int row in rows)
            {
                int start = checked(row * (int)frame.Columns);
                for (int i = start; i < start + frame.Columns; i++) AddCell(i);
            }
        return (int)bytes;
        void AddCell(int index)
        {
            int length;
            if (utf8 is not null) length = checked((int)utf8.Cells[index].Length);
            else
            {
                var text = cells![index].GetText();
                length = text.Length == 0 ? 0 : text.Length == 1 && text[0] < 128 ? 1 : Encoding.UTF8.GetByteCount(text);
            }
            int prefix = 1;
            for (uint remaining = (uint)length; remaining >= 128; remaining >>= 7) prefix++;
            bytes += 24L + prefix + length;
            Limit(bytes);
        }
        static void Limit(long length)
        {
            if (length > HostProtocol.MaxSnapshotBytes) throw new IOException("Terminal snapshot exceeds the transport limit.");
        }
    }
    public static (TerminalSnapshot Snapshot, TerminalCell[] Cells) Decode(byte[] bytes, Dictionary<ulong, TerminalImage> cache, TerminalCell[]? reusableCells = null, int payloadLength = -1)
        => DecodeChecked(bytes, cache, reusableCells, payloadLength, null);
    public static (TerminalSnapshot Snapshot, TerminalCell[] Cells) DecodeDelta(byte[] bytes, Dictionary<ulong, TerminalImage> cache,
        TerminalSnapshot baseline, TerminalCell[] baselineCells, TerminalCell[]? reusableCells = null, int payloadLength = -1)
        => DecodeChecked(bytes, cache, reusableCells, payloadLength, (baseline.Frame, baselineCells));
    // The serialized pane decoder knows which rows differ between its current
    // and spare buffers. Repair only those rows before applying the next delta.
    // Null means the spare is unknown (first use, full frame or rejected decode).
    internal static (TerminalSnapshot Snapshot, TerminalCell[] Cells) DecodeDeltaIntoSpare(byte[] bytes, Dictionary<ulong, TerminalImage> cache,
        TerminalSnapshot baseline, TerminalCell[] baselineCells, TerminalCell[] spare, IReadOnlyList<int>? repairRows, int payloadLength = -1)
        => DecodeChecked(bytes, cache, spare, payloadLength, (baseline.Frame, baselineCells), repairRows);
    private static (TerminalSnapshot Snapshot, TerminalCell[] Cells) DecodeChecked(byte[] bytes, Dictionary<ulong, TerminalImage> cache,
        TerminalCell[]? reusable, int payloadLength, (TerminalFrame Frame, TerminalCell[] Cells)? baseline, IReadOnlyList<int>? repairRows = null)
    {
        try { return DecodeCore(bytes, cache, reusable, payloadLength, baseline, repairRows); }
        catch (HostSnapshotException) { throw; }
        catch (Exception ex) when (ex is IOException or FormatException or OverflowException or ArgumentException)
        { throw new HostSnapshotException("Invalid terminal snapshot: " + ex.Message, ex); }
    }
    private static (TerminalSnapshot Snapshot, TerminalCell[] Cells) DecodeCore(byte[] bytes, Dictionary<ulong, TerminalImage> cache,
        TerminalCell[]? reusable, int payloadLength, (TerminalFrame Frame, TerminalCell[] Cells)? baseline, IReadOnlyList<int>? repairRows)
    {
        int length = payloadLength == -1 ? bytes.Length : payloadLength;
        if (length < 0 || length > bytes.Length || length > HostProtocol.MaxSnapshotBytes) throw new HostSnapshotException("Invalid snapshot payload length.");
        using var stream = new MemoryStream(bytes, 0, length, false); using var reader = new BinaryReader(stream, Encoding.UTF8);
        bool delta = baseline.HasValue, runs = false;
        uint magic = reader.ReadUInt32();
        if (delta)
        {
            runs = magic == DeltaRunsMagic;
            if (!runs && magic != DeltaMagic) throw new HostSnapshotException("Invalid row-delta format.");
        }
        else if (magic == FullRunsMagic) runs = true;
        else stream.Position = 0;
        var frame = ReadFrame(reader); int count = reader.ReadInt32();
        if (frame.Columns is < 2 or > 1000 || frame.Rows is < 1 or > 500 || count != checked((int)(frame.Columns * frame.Rows)))
            throw new HostSnapshotException("Invalid terminal dimensions.");
        if (frame.CursorVisible != 0 && (frame.CursorX >= frame.Columns || frame.CursorY >= frame.Rows)) throw new HostSnapshotException("Invalid terminal cursor.");
        if (baseline is { } previous && (previous.Frame.Columns != frame.Columns || previous.Frame.Rows != frame.Rows || previous.Cells.Length != count))
            throw new HostSnapshotException("The row-delta baseline has different dimensions.");
        // Six UInt32 fields plus at least one UTF-8 length byte per full-frame
        // cell. Reject impossible payloads before renting a large cell array.
        long minimumCells = runs ? count + frame.Rows * 25L + 8 : (long)count * 25;
        if (!delta && minimumCells > stream.Length - stream.Position)
            throw new HostSnapshotException("The full-frame payload is too short for its declared cells.");
        var cells = reusable?.Length == count && (!delta || !ReferenceEquals(reusable, baseline!.Value.Cells)) ? reusable : TerminalCellPool.Shared.Rent(count);
        try
        {
            int[] rows;
            if (baseline is { } old)
            {
                if (repairRows is null || !ReferenceEquals(cells, reusable)) Array.Copy(old.Cells, cells, count);
                else
                {
                    int lastRepair = -1, columns = (int)frame.Columns;
                    for (int index = 0; index < repairRows.Count; index++)
                    {
                        int row = repairRows[index];
                        if (row <= lastRepair || row >= frame.Rows) throw new HostSnapshotException("Invalid spare-buffer repair row.");
                        old.Cells.AsSpan(row * columns, columns).CopyTo(cells.AsSpan(row * columns, columns)); lastRepair = row;
                    }
                }
                int changed = reader.ReadInt32();
                if (changed < 0 || changed > frame.Rows) throw new HostSnapshotException("Invalid changed-row count.");
                rows = new int[changed]; int last = -1;
                for (int index = 0; index < changed; index++)
                {
                    int row = reader.ReadInt32(); if (row <= last || row >= frame.Rows) throw new HostSnapshotException("Invalid changed-row index.");
                    rows[index] = row; last = row; int start = checked(row * (int)frame.Columns);
                    if (runs) ReadRunRow(reader, bytes, cells, start, (int)frame.Columns);
                    else for (int i = start; i < start + frame.Columns; i++) ReadCell(reader, bytes, ref cells[i]);
                }
            }
            else
            {
                if (runs)
                    for (int row = 0; row < frame.Rows; row++) ReadRunRow(reader, bytes, cells, row * (int)frame.Columns, (int)frame.Columns);
                else for (int i = 0; i < count; i++) ReadCell(reader, bytes, ref cells[i]);
                rows = Enumerable.Range(0, (int)frame.Rows).ToArray();
            }
            var (graphics, incoming, used) = ReadGraphics(reader, cache);
            if (stream.Position != stream.Length) throw new HostSnapshotException("Unexpected snapshot data.");
            // A rejected delta must not poison the cache used by the last good frame.
            foreach (var oldKey in cache.Keys.Where(key => !used.Contains(key)).ToArray()) cache.Remove(oldKey);
            if (incoming is not null) foreach (var pair in incoming) cache[pair.Key] = pair.Value;
            return (new(frame, graphics, rows), cells);
        }
        catch
        {
            // A caller-owned spare remains its caller's responsibility. Newly
            // rented storage has no published owner when validation fails.
            if (!ReferenceEquals(cells, reusable)) TerminalCellPool.Shared.Return(cells);
            throw;
        }
    }
    private static void WriteFrame(BinaryWriter w, TerminalFrame f)
    {
        w.Write(f.Columns); w.Write(f.Rows); w.Write(f.CursorX); w.Write(f.CursorY); w.Write(f.CursorVisible); w.Write(f.CursorStyle);
        w.Write(f.Foreground); w.Write(f.Background); w.Write(f.CursorColor); w.Write(f.CursorBlinking); w.Write(f.RenderHeld);
        w.Write(f.CellWidth); w.Write(f.CellHeight); w.Write(f.AnimationDelay); w.Write(f.CursorWideTail); w.Write(f.ChangedRows);
    }
    private static TerminalFrame ReadFrame(BinaryReader r) => new()
    {
        Columns = r.ReadUInt32(),
        Rows = r.ReadUInt32(),
        CursorX = r.ReadUInt32(),
        CursorY = r.ReadUInt32(),
        CursorVisible = r.ReadUInt32(),
        CursorStyle = r.ReadUInt32(),
        Foreground = r.ReadUInt32(),
        Background = r.ReadUInt32(),
        CursorColor = r.ReadUInt32(),
        CursorBlinking = r.ReadUInt32(),
        RenderHeld = r.ReadUInt32(),
        CellWidth = r.ReadUInt32(),
        CellHeight = r.ReadUInt32(),
        AnimationDelay = r.ReadUInt32(),
        CursorWideTail = r.ReadUInt32(),
        ChangedRows = r.ReadUInt32()
    };
    private static void WriteCellHeader(Span<byte> destination, uint foreground, uint background, uint flags, uint underlineColor, uint underlineStyle, uint width)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, foreground);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], background);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], underlineColor);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], underlineStyle);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], width);
    }
    private static int WriteLength(Span<byte> destination, int length)
    {
        int count = 0; uint remaining = (uint)length;
        while (remaining >= 128) { destination[count++] = (byte)(remaining | 0x80); remaining >>= 7; }
        destination[count++] = (byte)remaining; return count;
    }
    private static int WriteCell(Span<byte> destination, in TerminalCell cell)
    {
        WriteCellHeader(destination, cell.Foreground, cell.Background, cell.Flags, cell.UnderlineColor, cell.UnderlineStyle, cell.Width);
        string text = cell.GetText();
        if (text.Length == 0) { destination[24] = 0; return 25; }
        if (text.Length == 1 && text[0] < 128) { destination[24] = 1; destination[25] = (byte)text[0]; return 26; }
        int length = Encoding.UTF8.GetByteCount(text), prefix = WriteLength(destination[24..], length);
        int written = Encoding.UTF8.GetBytes(text, destination[(24 + prefix)..]);
        return 24 + prefix + written;
    }
    private static int WriteCell(Span<byte> destination, Utf8TerminalBuffer buffer, int index)
    {
        ref var cell = ref buffer.Cells[index];
        WriteCellHeader(destination, cell.Foreground, cell.Background, cell.Flags, cell.UnderlineColor, cell.UnderlineStyle, cell.Width);
        int length = checked((int)cell.Length), prefix = WriteLength(destination[24..], length);
        buffer.Text(index).CopyTo(destination[(24 + prefix)..]);
        return 24 + prefix + length;
    }
    private static void ReadCell(BinaryReader r, byte[] bytes, ref TerminalCell cell)
    {
        cell.Foreground = r.ReadUInt32(); cell.Background = r.ReadUInt32(); cell.Flags = r.ReadUInt32(); cell.UnderlineColor = r.ReadUInt32();
        cell.UnderlineStyle = r.ReadUInt32(); cell.Width = r.ReadUInt32();
        ReadCellText(r, bytes, ref cell);
    }
    private static void ReadCellText(BinaryReader r, byte[] bytes, ref TerminalCell cell)
    {
        int length = r.Read7BitEncodedInt();
        if (length < 0 || length > 65536 || length > r.BaseStream.Length - r.BaseStream.Position) throw new HostSnapshotException("Invalid terminal cell text.");
        int offset = checked((int)r.BaseStream.Position);
        // Transport cells already use shared ASCII strings. Decoding the few
        // non-ASCII strings directly is cheaper than comparing stack-decoded
        // UTF-16 against the spare for every reused cell buffer.
        string text = length == 0 ? "" : length == 1 && bytes[offset] < 128 ? TerminalCell.Ascii(bytes[offset]) : Encoding.UTF8.GetString(bytes, offset, length);
        r.BaseStream.Position += length;
        if (text.Length > 16384) throw new HostSnapshotException("Invalid terminal cell text.");
        cell.Text = text; cell.Length = length == 0 || length == 1 && bytes[offset] < 128 ? (uint)length : (uint)Encoding.UTF8.GetByteCount(text);
    }
    private static void WriteGraphics(BinaryWriter w, IReadOnlyList<TerminalGraphic> graphics, TerminalImage[] images, HashSet<ulong> known)
    {
        w.Write(images.Length);
        foreach (var image in images)
        {
            w.Write(image.Generation); w.Write(image.Width); w.Write(image.Height); w.Write(image.Channels);
            bool include = !known.Contains(image.Generation); w.Write(include);
            if (include) { w.Write(image.Pixels.Length); w.Write(image.Pixels); }
        }
        w.Write(graphics.Count);
        foreach (var g in graphics)
        {
            w.Write(g.Image.Generation); w.Write(g.ImageId); w.Write(g.PlacementId); w.Write(g.Z); w.Write(g.Column); w.Write(g.Row);
            w.Write(g.OffsetX); w.Write(g.OffsetY); w.Write(g.PixelWidth); w.Write(g.PixelHeight); w.Write(g.SourceX); w.Write(g.SourceY); w.Write(g.SourceWidth); w.Write(g.SourceHeight);
        }
    }
    private static (IReadOnlyList<TerminalGraphic> Graphics, Dictionary<ulong, TerminalImage>? Incoming, HashSet<ulong> Used) ReadGraphics(BinaryReader r, Dictionary<ulong, TerminalImage> cache)
    {
        int count = r.ReadInt32(); if (count is < 0 or > 65536) throw new HostSnapshotException("Invalid image count.");
        var used = new HashSet<ulong>(); Dictionary<ulong, TerminalImage>? incoming = null; long total = 0;
        for (int i = 0; i < count; i++)
        {
            ulong generation = r.ReadUInt64(); uint width = r.ReadUInt32(), height = r.ReadUInt32(), channels = r.ReadUInt32();
            if (!used.Add(generation)) throw new HostSnapshotException("Duplicate image generation.");
            if (r.ReadBoolean())
            {
                int length = r.ReadInt32(); total += length;
                if (length < 0 || width == 0 || height == 0 || channels is < 1 or > 4 || length != checked((long)width * height * channels)
                    || total > 64 * 1024 * 1024 || length > r.BaseStream.Length - r.BaseStream.Position) throw new HostSnapshotException("Invalid terminal image.");
                byte[] pixels = r.ReadBytes(length);
                if (pixels.Length != length) throw new HostSnapshotException("Truncated terminal image.");
                (incoming ??= [])[generation] = new(generation, width, height, channels, pixels);
            }
            if (incoming?.ContainsKey(generation) != true && !cache.ContainsKey(generation)) throw new HostSnapshotException("Missing image generation.");
        }
        int placements = r.ReadInt32();
        if (placements is < 0 or > 1000000 || placements * 60L > r.BaseStream.Length - r.BaseStream.Position) throw new HostSnapshotException("Invalid placement count.");
        if (placements == 0) return (Array.Empty<TerminalGraphic>(), incoming, used);
        var graphics = new List<TerminalGraphic>(placements);
        for (int i = 0; i < placements; i++)
        {
            ulong generation = r.ReadUInt64();
            if (!used.Contains(generation)) throw new HostSnapshotException("Unknown placement image generation.");
            var image = incoming is not null && incoming.TryGetValue(generation, out var added) ? added : cache[generation];
            graphics.Add(new(image, r.ReadUInt32(), r.ReadUInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadUInt32(), r.ReadUInt32(),
                r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));
        }
        return (graphics, incoming, used);
    }
}
