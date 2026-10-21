using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class Utf8CaptureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(Unsafe.SizeOf<GhosttyTerminal.Native.Cell>() == 64
            && Marshal.OffsetOf<GhosttyTerminal.Native.Cell>(nameof(GhosttyTerminal.Native.Cell.Text)).ToInt32() == 32,
            "UTF-8 host capture uses the validated cache-line-sized 64-byte bridge cell ABI with 32 inline UTF-8 bytes");
        using var managed = new GhosttyTerminal(40, 6);
        using var raw = new GhosttyTerminal(40, 6);
        managed.EnableIncrementalRendering(); raw.EnableIncrementalRendering();
        var cells = new TerminalCell[240]; var bytes = new Utf8TerminalBuffer(240);
        var random = new Random(20261020);
        string longText = "a" + new string('\u0301', 80);
        bool full = true, delta = true, owned = true;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            string text = iteration == 0 ? "\x1b[?2027hASCII café 中🙂 é\r\n" + longText
                : $"\x1b[{random.Next(1, 7)};{random.Next(1, 25)}H\x1b[{(iteration % 2 == 0 ? "1;4:3;58:2::255:0:0" : "0")}m"
                    + new[] { "café", "中🙂", "é", "ASCII", " ", longText }[random.Next(6)];
            byte[] input = Encoding.UTF8.GetBytes(text);
            managed.Write(input, input.Length); raw.Write(input, input.Length);
            var expected = managed.ReadSnapshot(cells); var actual = raw.ReadSnapshot(bytes);
            full &= HostSnapshotCodec.Encode(expected, cells, []).SequenceEqual(HostSnapshotCodec.EncodeUtf8(actual, bytes, []));
            delta &= HostSnapshotCodec.EncodeDelta(expected, cells, []).SequenceEqual(HostSnapshotCodec.EncodeUtf8(actual, bytes, [], true));
            owned &= bytes.Cells.All(cell => cell.ExtendedText == 0);
        }
        check(full, "UTF-8 and managed captures encode byte-identical full frames across Unicode, long graphemes, styles and overwrite operations");
        check(delta, "UTF-8 and managed captures encode byte-identical incremental row deltas");
        check(owned, "Every raw snapshot clears borrowed native overflow pointers before leaving the capture gate");
        var held = raw.ReadSnapshot(bytes); byte[] heldPayload = HostSnapshotCodec.EncodeUtf8(held, bytes, []);
        var another = new Utf8TerminalBuffer(240);
        for (int i = 0; i < 10; i++)
        {
            byte[] input = Encoding.UTF8.GetBytes("\x1b[H\x1b[2J" + "z" + new string('\u0300', 90 + i));
            raw.Write(input, input.Length); _ = raw.ReadSnapshot(another);
        }
        check(heldPayload.SequenceEqual(HostSnapshotCodec.EncodeUtf8(held, bytes, [])),
            "A host-owned UTF-8 snapshot survives later engine captures and native overflow replacement");
        var alternated = raw.ReadSnapshot(bytes);
        check(alternated.ChangedRows.Count == 6, "Changing raw destination identity forces a complete native capture");
        _ = raw.ReadSnapshot(cells);
        check(raw.ReadSnapshot(bytes).ChangedRows.Count == 6, "Interleaved managed readers cannot invalidate the raw destination baseline");
        raw.Resize(24, 10, 8, 16); var resized = raw.ReadSnapshot(bytes);
        check(resized.Frame.Columns == 24 && resized.Frame.Rows == 10 && resized.ChangedRows.Count == 10,
            "Same-cell-count geometry changes fully initialize the UTF-8 destination");
        using var image = new GhosttyTerminal(8, 4); var imageCells = new Utf8TerminalBuffer(32);
        byte[] graphic = Encoding.UTF8.GetBytes("\x1b_Ga=T,f=32,s=1,v=1,i=998,q=2;/wAA/w==\x1b\\");
        image.Write(graphic, graphic.Length); var imageSnapshot = image.ReadSnapshot(imageCells);
        var (Snapshot, Cells) = HostSnapshotCodec.Decode(HostSnapshotCodec.EncodeUtf8(imageSnapshot, imageCells, []), []);
        check(Snapshot.Graphics.Count == 1 && Snapshot.Graphics[0].Image.Pixels.SequenceEqual(new byte[] { 255, 0, 0, 255 }),
            "UTF-8 host snapshots preserve Kitty graphics and image pixels");
        using var arenas = new GhosttyTerminal(40, 4); arenas.EnableIncrementalRendering();
        var arenaCells = new TerminalCell[160]; _ = arenas.ReadSnapshot(arenaCells); ulong emptyBytes = arenas.CaptureTextBytesForCheck;
        // Stay within the pinned engine's grapheme limit while exceeding both
        // inline representations and forcing several row-arena growth steps.
        string large = "a" + new string('\u0301', 64);
        WriteArena("\x1b[?2027h\x1b[?25l" + string.Concat(Enumerable.Repeat(large, 20)) + "\x1b[3;1H");
        _ = arenas.ReadSnapshot(arenaCells); ulong peakBytes = arenas.CaptureTextBytesForCheck;
        check(Enumerable.Range(0, 20).All(index => arenaCells[index].GetText() == large) && peakBytes > emptyBytes + 4000,
            $"A dirty row arena grows without invalidating earlier grapheme offsets (first text length {arenaCells[0].GetText().Length}, expected {large.Length}, native bytes {emptyBytes}->{peakBytes}, matching cells {Enumerable.Range(0, 20).Count(index => arenaCells[index].GetText() == large)})");
        WriteArena("next"); var incremental = arenas.ReadSnapshot(arenaCells);
        check(!incremental.ChangedRows.Contains(0) && arenaCells[0].GetText() == large,
            "An unchanged row retains valid native grapheme storage while another row is recaptured");
        WriteArena("\x1b[2J\x1b[Hplain"); _ = arenas.ReadSnapshot(arenaCells);
        check(arenas.CaptureTextBytesForCheck <= emptyBytes + 256,
            "Replacing a long-grapheme row with ASCII releases its previous large arena peak");
        arenas.Resize(20, 8, 8, 16);
        check(arenas.CaptureTextBytesForCheck == 0, "A geometry change releases all transfer-row arenas and their offset scratch");
        using var overflowTerminal = new GhosttyTerminal(40, 12); overflowTerminal.EnableIncrementalRendering();
        var overflowBuffer = new Utf8TerminalBuffer(480);
        byte[] longViewport = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(large, 400)));
        overflowTerminal.Write(longViewport, longViewport.Length); _ = overflowTerminal.ReadSnapshot(overflowBuffer);
        check(overflowBuffer.OverflowCapacityForCheck >= 400, "Exceptional long-grapheme captures own their indexed overflow bytes");
        byte[] clearViewport = "\u001b[2J\u001b[Hplain"u8.ToArray();
        overflowTerminal.Write(clearViewport, clearViewport.Length); _ = overflowTerminal.ReadSnapshot(overflowBuffer);
        check(overflowBuffer.OverflowCapacityForCheck <= 256, "Returning to ordinary text sheds the managed overflow-index peak");
        void WriteArena(string text) { byte[] input = Encoding.UTF8.GetBytes(text); arenas.Write(input, input.Length); }
    }
    internal static void Benchmark(string report)
    {
        var samples = new List<object>(); const int iterations = 1000;
        foreach (string corpus in new[] { "ascii", "unicode" })
            for (int round = 0; round < 3; round++)
                foreach (bool utf8 in round % 2 == 0 ? new[] { false, true } : [true, false])
                    foreach (bool encode in new[] { false, true })
                    {
                        using var terminal = new GhosttyTerminal(100, 40); terminal.EnableIncrementalRendering();
                        var cells = new TerminalCell[4000]; var raw = new Utf8TerminalBuffer(4000);
                        string row = corpus == "ascii" ? new string('x', 96) : string.Concat(Enumerable.Repeat("中🙂café é ", 8));
                        byte[] input = Encoding.UTF8.GetBytes("\x1b[H" + string.Join("\r\n", Enumerable.Repeat(row, 39)));
                        byte[]? payload = null;
                        void Capture()
                        {
                            terminal.Write(input, input.Length);
                            if (utf8) { var snapshot = terminal.ReadSnapshot(raw); if (encode) payload = HostSnapshotCodec.EncodeUtf8(snapshot, raw, []); }
                            else { var snapshot = terminal.ReadSnapshot(cells); if (encode) payload = HostSnapshotCodec.Encode(snapshot, cells, []); }
                        }
                        for (int i = 0; i < 50; i++) Capture();
                        long allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                        for (int i = 0; i < iterations; i++) Capture();
                        timer.Stop();
                        samples.Add(new
                        {
                            corpus,
                            round,
                            utf8,
                            encode,
                            iterations,
                            milliseconds = timer.Elapsed.TotalMilliseconds,
                            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                            payloadBytes = payload?.Length
                        });
                    }
        File.WriteAllText(report, JsonSerializer.Serialize(new { samples, meaning = "Rotated-order, same-binary parse/capture and parse/capture/encode CPU microbenchmarks. No IPC or display timing." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report);
    }
}
