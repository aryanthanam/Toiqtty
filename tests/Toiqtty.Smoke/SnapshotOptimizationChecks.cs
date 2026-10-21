using System.Text;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class SnapshotOptimizationChecks
{
    public static void Run(Action<bool, string> check)
    {
        using var terminal = new GhosttyTerminal(40, 6);
        byte[] input = Encoding.UTF8.GetBytes("\x1b[?2027hASCII 123 café 中🙂 é\r\n\x1b[4:3;58:2::255:0:0munderline\x1b[0m\r\n" + "a" + new string('\u0301', 70));
        terminal.Write(input, input.Length);
        var cells = new TerminalCell[240]; var snapshot = terminal.ReadSnapshot(cells);
        byte[] encoded = HostSnapshotCodec.Encode(snapshot, cells, []);
        using var stream = new MemoryStream(encoded); using var reader = new BinaryReader(stream, Encoding.UTF8);
        stream.Position = 16 * 4;
        check(reader.ReadInt32() == cells.Length, "optimized snapshot keeps the original protocol-2 cell count");
        bool identical = true;
        foreach (var cell in cells)
        {
            identical &= reader.ReadUInt32() == cell.Foreground && reader.ReadUInt32() == cell.Background && reader.ReadUInt32() == cell.Flags
                && reader.ReadUInt32() == cell.UnderlineColor && reader.ReadUInt32() == cell.UnderlineStyle && reader.ReadUInt32() == cell.Width;
            identical &= reader.ReadString() == cell.GetText();
        }
        check(identical, "a legacy BinaryReader decodes optimized UTF-8 cell fields byte for byte, including long graphemes");
        var largeCells=new TerminalCell[8000]; Array.Fill(largeCells,new TerminalCell { Text="x",Length=1,Width=1 });
        var largeSnapshot=new TerminalSnapshot(new TerminalFrame { Columns=160,Rows=50 },[],[]);
        for(int i=0;i<20;i++) _=HostSnapshotCodec.Encode(largeSnapshot,largeCells,[]);
        long encodeBefore=GC.GetAllocatedBytesForCurrentThread();
        byte[] singleBuffer=HostSnapshotCodec.Encode(largeSnapshot,largeCells,[]);
        check(GC.GetAllocatedBytesForCurrentThread()-encodeBefore<singleBuffer.Length+4096,
            "Full-frame encoding allocates one payload buffer without a second payload-sized copy");
        byte[] retainedPayload=(byte[])singleBuffer.Clone(); largeCells[0].Text="changed";
        _=HostSnapshotCodec.Encode(largeSnapshot,largeCells,[]);
        check(singleBuffer.SequenceEqual(retainedPayload),"Each encoded reply retains its own bytes while later replies are encoded");
        var reusable = new TerminalCell[cells.Length];
        var (Snapshot, Cells) = HostSnapshotCodec.Decode(encoded, [], reusable);
        check(ReferenceEquals(Cells, reusable) && cells.Select((cell, i) => cell.SameAppearance(in reusable[i])).All(value => value),
            "reusing a decoder buffer preserves every glyph and style field");
        var arena=new byte[encoded.Length+4096]; Array.Fill(arena,(byte)0xcc); encoded.CopyTo(arena,0);
        var bounded=HostSnapshotCodec.Decode(arena,[],payloadLength:encoded.Length);
        Array.Fill(arena,(byte)0);
        check(cells.Select((cell,i)=>cell.SameAppearance(in bounded.Cells[i])).All(value=>value),
            "bounded decoding ignores unused buffer capacity and owns its decoded text before reuse");
        using(var imageTerminal=new GhosttyTerminal(8,4))
        {
            byte[] imageInput=Encoding.UTF8.GetBytes("\x1b_Ga=T,f=32,s=1,v=1,i=998,q=2;/wAA/w==\x1b\\");
            imageTerminal.Write(imageInput,imageInput.Length);
            var imageCells=new TerminalCell[32]; var imageSnapshot=imageTerminal.ReadSnapshot(imageCells);
            byte[] imagePayload=HostSnapshotCodec.Encode(imageSnapshot,imageCells,[]);
            var imageArena=new byte[imagePayload.Length+1024]; imagePayload.CopyTo(imageArena,0);
            var imageDecoded=HostSnapshotCodec.Decode(imageArena,[],payloadLength:imagePayload.Length);
            Array.Fill(imageArena,(byte)0);
            check(imageDecoded.Snapshot.Graphics.Count==1 && imageDecoded.Snapshot.Graphics[0].Image.Pixels.SequenceEqual(new byte[] { 255,0,0,255 }),
                "reusing transport memory cannot overwrite retained Kitty image pixels");
        }
        var wrongSize = HostSnapshotCodec.Decode(encoded, [], new TerminalCell[1]);
        check(wrongSize.Cells.Length == cells.Length, "a decoder resize obtains a correctly sized cell buffer");
        var impossible=encoded[..68];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(impossible.AsSpan(0,4),1000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(impossible.AsSpan(4,4),500);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(impossible.AsSpan(64,4),500000);
        long allocationBefore=GC.GetAllocatedBytesForCurrentThread(); bool rejectedBeforeRent=false;
        try { HostSnapshotCodec.Decode(impossible,[]); }
        catch(HostSnapshotException ex) { rejectedBeforeRent=ex.Message.Contains("too short"); }
        check(rejectedBeforeRent && GC.GetAllocatedBytesForCurrentThread()-allocationBefore<1024*1024,
            "An impossible large full-frame declaration is rejected before allocating its cell storage");
        var changed = reusable[0]; changed.Background ^= 1;
        check(!changed.SameAppearance(in reusable[0]), "row comparison detects background-only changes");
        changed = reusable[0]; changed.UnderlineStyle ^= 1;
        check(!changed.SameAppearance(in reusable[0]), "row comparison detects decoration-only changes");
        changed = reusable[0]; changed.Text = "Z";
        check(!changed.SameAppearance(in reusable[0]), "row comparison detects text changes independently of style");
        check(ReferenceEquals(cells[0].GetText(), TerminalCell.Ascii((byte)'A')) && ReferenceEquals(reusable[0].GetText(), TerminalCell.Ascii((byte)'A')),
            "native and transported ASCII cells share cached glyph strings");
        var unicodeCell=cells.First(cell=>cell.GetText()=="中");
        check(ReferenceEquals(TerminalCell.DecodeText(Encoding.UTF8.GetBytes("中"),unicodeCell.Text),unicodeCell.Text),
            "Unchanged short Unicode text reuses its owned string without an allocation");
        check(TerminalCell.DecodeText(Encoding.UTF8.GetBytes("界"),unicodeCell.Text)=="界",
            "Changed Unicode text replaces the previous owned string");
        bool truncated = false;
        try { HostSnapshotCodec.Decode(encoded[..(16 * 4 + 4 + 24)], []); } catch (IOException) { truncated = true; }
        check(truncated, "a truncated cell payload is rejected before publishing a snapshot");
        byte[] malformed = (byte[])encoded.Clone();
        // First text prefix begins immediately after frame, count and six style fields.
        Array.Fill(malformed, (byte)0xff, 16 * 4 + 4 + 24, 5);
        bool overflow = false;
        try { HostSnapshotCodec.Decode(malformed, []); } catch (Exception ex) when (ex is IOException or FormatException) { overflow = true; }
        check(overflow, "an overflowing text prefix cannot trigger an unbounded cell allocation");
        byte[] metadata = Encoding.UTF8.GetBytes("\x1b]2;cached café title\x07\x1b]7;file:///D:/repo/toiqtty\x07");
        terminal.Write(metadata, metadata.Length);
        string title = terminal.Metadata(0), directory = terminal.Metadata(1);
        check(title == "cached café title" && directory == "file:///D:/repo/toiqtty", "metadata caching preserves Unicode title and directory values");
        check(ReferenceEquals(title, terminal.Metadata(0)) && ReferenceEquals(directory, terminal.Metadata(1)), "unchanged metadata returns the original string without allocating another buffer or value");
        metadata = Encoding.UTF8.GetBytes("\x1b]2;new title\x07\x1b]7;file:///C:/Windows\x07"); terminal.Write(metadata, metadata.Length);
        check(terminal.Metadata(0) == "new title" && terminal.Metadata(1) == "file:///C:/Windows", "metadata changes invalidate only the current cached values");
        metadata = Encoding.UTF8.GetBytes("\x1b" + "c"); terminal.Write(metadata, metadata.Length);
        check(terminal.Metadata(0) != "new title" && terminal.Metadata(1) != "file:///C:/Windows", "terminal reset cannot expose cached metadata from before the reset");
        check(terminal.Metadata(42) == terminal.Metadata(1), "metadata caching preserves the native bridge's nonzero-kind directory semantics");
    }
}
