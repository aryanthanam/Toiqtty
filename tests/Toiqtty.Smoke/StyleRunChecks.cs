using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class StyleRunChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var terminal = new GhosttyTerminal(80, 8); terminal.EnableIncrementalRendering();
        var raw = new Utf8TerminalBuffer(640);
        Write("\x1b[?2027h" + string.Join("\r\n", Enumerable.Repeat(new string('x', 78), 7)));
        var first = terminal.ReadSnapshot(raw);
        byte[] full = HostSnapshotCodec.EncodeUtf8Runs(first, raw, []), legacy = HostSnapshotCodec.EncodeUtf8(first, raw, []);
        check(full.Length < legacy.Length / 5, "Style runs reduce uniform ASCII full-frame traffic by more than 80 percent");
        var expected = HostSnapshotCodec.Decode(legacy, []); var actual = HostSnapshotCodec.Decode(full, []);
        var expectedSpare = new TerminalCell[640]; var actualSpare = new TerminalCell[640]; IReadOnlyList<int>? repair = null;
        var random = new Random(20261021); bool identical = true, bounded = true;
        for (int i = 0; i < 300; i++)
        {
            if (i % 41 == 0) { terminal.Resize((ushort)(i % 82 == 0 ? 80 : 40), (ushort)(i % 82 == 0 ? 8 : 16), 8, 16); }
            string text = $"\x1b[{random.Next(1, 8)};{random.Next(1, 25)}H\x1b[{(i % 2 == 0 ? "1;4:3;58:2::255:0:0" : "0")}m"
                + new[] { "café", "中🙂", "é", "ASCII", " ", "a" + new string('\u0301', 90) }[random.Next(6)];
            Write(text);
            var snapshot = terminal.ReadSnapshot(raw);
            bool delta = i % 41 != 0 && expected.Snapshot.Frame.Columns == snapshot.Frame.Columns;
            byte[] before = delta ? HostSnapshotCodec.EncodeUtf8(snapshot, raw, [], true) : HostSnapshotCodec.EncodeUtf8(snapshot, raw, []);
            byte[] after = HostSnapshotCodec.EncodeUtf8Runs(snapshot, raw, [], delta);
            bounded &= after.Length <= before.Length;
            var oldExpected = expected.Cells; var oldActual = actual.Cells;
            expected = delta ? HostSnapshotCodec.DecodeDelta(before, [], expected.Snapshot, oldExpected, expectedSpare) : HostSnapshotCodec.Decode(before, [], expectedSpare);
            actual = delta ? HostSnapshotCodec.DecodeDeltaIntoSpare(after, [], actual.Snapshot, oldActual, actualSpare, repair) : HostSnapshotCodec.Decode(after, [], actualSpare);
            expectedSpare = oldExpected; actualSpare = oldActual; repair = delta ? actual.Snapshot.ChangedRows : null;
            identical &= expected.Cells.Select((cell, index) => cell.SameAppearance(in actual.Cells[index])).All(value => value)
                && expected.Snapshot.Frame.Equals(actual.Snapshot.Frame);
        }
        check(identical, "Mixed full/style-run/delta frames preserve Unicode, styles, long graphemes and geometry through spare-buffer repair");
        check(bounded, "Style-run negotiation never expands a payload beyond the legacy cell encoding");
        var varied = new Utf8TerminalBuffer(160);
        for (int i = 0; i < varied.Length; i++)
        {
            varied.Cells[i].Foreground = (uint)i; varied.Cells[i].Width = (byte)(i % 3);
            varied.Cells[i].Length = 1; varied.Cells[i].Text[0] = (byte)'x';
        }
        var variedFrame = new TerminalSnapshot(new TerminalFrame { Columns = 160, Rows = 1 }, [], [0]);
        check(HostSnapshotCodec.EncodeUtf8Runs(variedFrame, varied, []).SequenceEqual(HostSnapshotCodec.EncodeUtf8(variedFrame, varied, [])),
            "A style change on every cell falls back to byte-identical legacy encoding");
        bool malformed = true;
        foreach (byte value in new byte[] { 0, 81, 127 })
        {
            var corrupt = (byte[])full.Clone(); corrupt[72] = value;
            try { _ = HostSnapshotCodec.Decode(corrupt, []); malformed = false; } catch (HostSnapshotException) { }
        }
        check(malformed, "Zero and cross-row style-run lengths are rejected without an unbounded decoder loop");
        bool truncated = true;
        for (int length = 0; length < full.Length; length += Math.Max(1, full.Length / 40))
        {
            try { _ = HostSnapshotCodec.Decode(full[..length], []); truncated = false; } catch (HostSnapshotException) { }
        }
        check(truncated, "Truncated style-run payloads cannot publish incomplete cells or graphics");
        var impossible = full[..72]; BinaryPrimitives.WriteUInt32LittleEndian(impossible.AsSpan(4), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(impossible.AsSpan(8), 500); BinaryPrimitives.WriteInt32LittleEndian(impossible.AsSpan(68), 500000);
        long beforeAllocation = GC.GetAllocatedBytesForCurrentThread(); bool rejected = false;
        try { _ = HostSnapshotCodec.Decode(impossible, []); } catch (HostSnapshotException) { rejected = true; }
        check(rejected && GC.GetAllocatedBytesForCurrentThread() - beforeAllocation < 65536, "Impossible full-run dimensions are rejected before renting a large cell grid");
        Write("\x1b_Ga=T,f=32,s=1,v=1,i=998,q=2;/wAA/w==\x1b\\"); var graphic = terminal.ReadSnapshot(raw);
        var image = HostSnapshotCodec.Decode(HostSnapshotCodec.EncodeUtf8Runs(graphic, raw, []), []);
        check(image.Snapshot.Graphics.Count == 1 && image.Snapshot.Graphics[0].Image.Pixels.SequenceEqual(new byte[] { 255, 0, 0, 255 }),
            "Style-run frames preserve Kitty image generations and pixels");
        void Write(string text) { var input = Encoding.UTF8.GetBytes(text); terminal.Write(input, input.Length); }
    }
    internal static void Benchmark(string report)
    {
        using var terminal = new GhosttyTerminal(160, 50); var raw = new Utf8TerminalBuffer(8000);
        byte[] input = Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Repeat(new string('x', 158), 49)));
        terminal.Write(input, input.Length); var snapshot = terminal.ReadSnapshot(raw);
        byte[] legacy = HostSnapshotCodec.EncodeUtf8(snapshot, raw, []), runs = HostSnapshotCodec.EncodeUtf8Runs(snapshot, raw, []);
        var destination = new TerminalCell[8000]; var samples = new List<object>(); const int iterations = 1000;
        for (int round = 0; round < 3; round++)
            foreach (string operation in round % 2 == 0 ? new[] { "encode-legacy", "encode-runs", "decode-legacy", "decode-runs" } : new[] { "decode-runs", "decode-legacy", "encode-runs", "encode-legacy" })
            {
                void Once()
                {
                    if (operation == "encode-legacy") _ = HostSnapshotCodec.EncodeUtf8(snapshot, raw, []);
                    else if (operation == "encode-runs") _ = HostSnapshotCodec.EncodeUtf8Runs(snapshot, raw, []);
                    else _ = HostSnapshotCodec.Decode(operation == "decode-runs" ? runs : legacy, [], destination);
                }
                for (int i = 0; i < 100; i++) Once();
                long allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++) Once(); timer.Stop();
                samples.Add(new { round, operation, iterations, milliseconds = timer.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
            }
        File.WriteAllText(report, JsonSerializer.Serialize(new { legacyBytes = legacy.Length, runBytes = runs.Length, samples, meaning = "Same-binary full-frame codec microbenchmark, uniform ASCII styles. No IPC or presentation timing." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report);
    }
}
