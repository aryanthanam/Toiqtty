using System.Text;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class FrameDeltaChecks
{
    internal static async Task RunHost(string executable, Action<bool, string> check)
    {
        string folder = Path.GetFullPath("artifacts/row-handoff-" + Guid.NewGuid().ToString("N"));
        var client = SessionHostClient.ConnectOrStart(folder, executable);
        string pane = Guid.NewGuid().ToString("N");
        var session = new RemoteTerminalSession(client, pane, $"\"{Environment.ProcessPath}\" --delta-client", Environment.CurrentDirectory, new());
        var fast = new TerminalCell[3000]; var slow = new TerminalCell[3000];
        long fastRevision = -1, slowRevision = -1; TerminalSnapshot? latest = null;
        bool ReadFast() => session.TryReadSnapshot(fast, ref fastRevision, out latest);
        string Row(TerminalCell[] cells, int row, int columns = 100) => string.Concat(cells.Skip(row * columns).Take(columns).Select(c => c.GetText()));
        async Task Wait(Func<bool> condition)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!condition()) { if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Row handoff did not settle."); await Task.Delay(10); }
        }
        async Task Set(int row, string token)
        {
            await session.PasteAsync("set " + row + " " + token); session.Key(13, 0);
            await Wait(() => ReadFast() && Row(fast, row - 1).Contains("DELTA_" + token, StringComparison.Ordinal));
        }
        try
        {
            session.AttachView(); session.QueueResize(100, 30, 8, 21);
            await Wait(() => ReadFast() && Row(fast, 0).Contains("DELTA_READY") && latest!.Graphics.Count == 1 && !session.ResizePending);
            // TryRead is deliberately nonblocking. A successful fast read wakes
            // capture, which may own the cache gate before this second reader.
            TerminalSnapshot? first = null;
            await Wait(() => session.TryReadSnapshot(slow, ref slowRevision, out first));
            check(first!.ChangedRows.Count == 30,
                "A new tracked consumer receives all rows of its own destination buffer");
            await Set(3, "FIRST"); await Set(4, "SECOND");
            TerminalSnapshot? skipped = null;
            await Wait(() => session.TryReadSnapshot(slow, ref slowRevision, out skipped));
            check(skipped!.ChangedRows.Contains(2) && skipped.ChangedRows.Contains(3)
                && Row(slow, 2).Contains("DELTA_FIRST") && Row(slow, 3).Contains("DELTA_SECOND"),
                "A slower consumer receives the union of rows changed across skipped frames");
            await Wait(() => ReadFast() && latest!.ChangedRows.Count == 0);
            check(latest!.ChangedRows.Count == 0, "Reading an unchanged tracked frame copies no rows");
            long fullBefore = session.FullFramesForCheck;
            await client.CallAsync(new() { Operation = "capture", Pane = pane, Numbers = [-1] });
            session.Focus(true);
            await Wait(() => ReadFast() && session.FullFramesForCheck > fullBefore);
            check(latest!.ChangedRows.Count == 0, "An unchanged full-frame replacement updates metadata without repainting all text rows");
            Array.Clear(slow); slowRevision = -1;
            TerminalSnapshot? reused = null;
            await Wait(() => session.TryReadSnapshot(slow, ref slowRevision, out reused));
            check(reused!.ChangedRows.Count == 30 && Row(slow, 2).Contains("DELTA_FIRST"),
                "Resetting a consumer revision fully initializes a recycled destination array");
            slow[200].Text = "damaged";
            await Wait(() => session.TryReadSnapshot(slow, out _));
            check(Row(slow, 2).Contains("DELTA_FIRST"),
                "Untracked readers still compare their destination and repair caller modifications");
            int corrupted = 0; long recoveries = session.FullFrameRecoveriesForCheck;
            void Corrupt(HostReply reply)
            {
                if (reply.DeltaBaseRevision is not null && Interlocked.CompareExchange(ref corrupted, 1, 0) == 0) reply.BinaryLength--;
            }
            session.CaptureReplyForCheck += Corrupt;
            try { await Set(5, "RECOVERED"); }
            finally { session.CaptureReplyForCheck -= Corrupt; }
            check(corrupted == 1 && session.FullFrameRecoveriesForCheck == recoveries + 1
                && Row(fast, 2).Contains("DELTA_FIRST") && Row(fast, 3).Contains("DELTA_SECOND") && latest!.Graphics.Count == 1,
                "A truncated delta repairs its spare through full-frame recovery without losing earlier rows or image ownership");
            await session.InputDrained;
            session.DetachView(); await session.DetachAsync();
            check((await client.CallAsync(new())).Panes.Contains(pane), "Retiring row caches detaches the frontend while preserving its host-owned shell");
            session = new RemoteTerminalSession(client, pane, "ignored", Environment.CurrentDirectory, new());
            fastRevision = -1; session.AttachView(); session.QueueResize(100, 30, 8, 21);
            await Wait(() => ReadFast() && Row(fast, 4).Contains("DELTA_RECOVERED"));
            check(Row(fast, 2).Contains("DELTA_FIRST") && Row(fast, 3).Contains("DELTA_SECOND"),
                "A replacement frontend cache reattaches all current rows to the same live shell");
            session.QueueResize(120, 25, 8, 21); fast = new TerminalCell[3000]; fastRevision = -1;
            await Wait(() => ReadFast() && latest!.Frame.Columns == 120 && latest.Frame.Rows == 25);
            check(latest!.ChangedRows.Count == 25, "A geometry change with the same cell count fully initializes the new row layout");
            Console.WriteLine("Row handoff evidence: " + folder);
        }
        finally { await session.CloseAsync(); try { client.Shutdown(); } catch (IOException) { } }
    }
    public static void Run(Action<bool, string> check)
    {
        using var terminal = new GhosttyTerminal(32, 8); terminal.EnableIncrementalRendering();
        var nativeCells = new TerminalCell[256];
        Write("\x1b[?2027h" + string.Join("\r\n", Enumerable.Range(0, 7).Select(i => "Row " + i + " ASCII café 中🙂")));
        var first = terminal.ReadSnapshot(nativeCells); byte[] full = HostSnapshotCodec.Encode(first, nativeCells, []);
        var images = new Dictionary<ulong, TerminalImage>(); var baseline = HostSnapshotCodec.Decode(full, images);
        Write("\x1b[2;3H\x1b[38;2;19;80;130m中é🙂\x1b[0m");
        var changed = terminal.ReadSnapshot(nativeCells); byte[] delta = HostSnapshotCodec.EncodeDelta(changed, nativeCells, []);
        var decoded = HostSnapshotCodec.DecodeDelta(delta, images, baseline.Snapshot, baseline.Cells);
        check(delta.Length < HostSnapshotCodec.Encode(changed, nativeCells, []).Length / 3, "A sparse row delta is substantially smaller than the equivalent full frame");
        check(Equal(nativeCells, decoded.Cells) && decoded.Snapshot.Frame.Equals(changed.Frame), "A row delta preserves Unicode clusters, styles, widths and frame metadata");
        check(Equal(baseline.Cells, HostSnapshotCodec.Decode(full, []).Cells), "Decoding a delta leaves its previous baseline untouched");
        var (Snapshot, Cells) = HostSnapshotCodec.DecodeDelta(delta, images, baseline.Snapshot, baseline.Cells, baseline.Cells);
        check(!ReferenceEquals(Cells, baseline.Cells) && Equal(Cells, nativeCells), "An aliased reusable buffer cannot overwrite a delta baseline");
        byte[] badMagic = (byte[])delta.Clone(); badMagic[0] ^= 255;
        Reject(badMagic, "A delta with an unknown format is rejected");
        byte[] badCount = (byte[])delta.Clone(); BitConverter.GetBytes(9).CopyTo(badCount, 72);
        Reject(badCount, "A delta cannot declare more changed rows than the terminal contains");
        foreach (int row in new[] { -1, 8 })
        {
            byte[] badRow = (byte[])delta.Clone(); BitConverter.GetBytes(row).CopyTo(badRow, 76);
            Reject(badRow, "A delta rejects an out-of-bounds row index " + row);
        }
        Reject(delta[..^1], "A truncated delta is rejected before publishing");
        byte[] trailing = delta.Concat(new byte[] { 1 }).ToArray(); Reject(trailing, "Unexpected trailing delta data is rejected");
        bool duplicate = false;
        try { HostSnapshotCodec.EncodeDelta(changed with { ChangedRows = [1, 1] }, nativeCells, []); } catch (ArgumentException) { duplicate = true; }
        check(duplicate, "The delta encoder rejects duplicate or unordered rows");
        byte[] duplicateRows = HostSnapshotCodec.EncodeDelta(changed with { ChangedRows = [1, 2] }, nativeCells, []);
        using (var stream = new MemoryStream(duplicateRows))
        using (var reader = new BinaryReader(stream, Encoding.UTF8))
        {
            stream.Position = 80;
            for (int i = 0; i < 32; i++) { stream.Position += 24; _ = reader.ReadString(); }
            BitConverter.GetBytes(1).CopyTo(duplicateRows, (int)stream.Position);
        }
        Reject(duplicateRows, "The delta decoder rejects duplicate rows");
        bool dimension = false;
        try { HostSnapshotCodec.DecodeDelta(delta, images, baseline.Snapshot with { Frame = new TerminalFrame { Columns = 16, Rows = 16 } }, baseline.Cells); }
        catch (IOException) { dimension = true; }
        check(dimension, "Equal cell counts cannot disguise a different delta baseline geometry");

        baseline = decoded;
        Write("\x1b[1;1H\x1b_Ga=T,f=32,s=1,v=1,i=998,q=2;/wAA/w==\x1b\\");
        changed = terminal.ReadSnapshot(nativeCells); delta = HostSnapshotCodec.EncodeDelta(changed, nativeCells, images.Keys.ToHashSet());
        decoded = HostSnapshotCodec.DecodeDelta(delta, images, baseline.Snapshot, baseline.Cells);
        check(decoded.Snapshot.Graphics.Count == 1 && images.Count == 1, "Row deltas transport new Kitty image generations and placements");
        var retainedPixels = decoded.Snapshot.Graphics[0].Image.Pixels;
        baseline = decoded; Write("\x1b[6;4H"); changed = terminal.ReadSnapshot(nativeCells);
        delta = HostSnapshotCodec.EncodeDelta(changed, nativeCells, images.Keys.ToHashSet());
        decoded = HostSnapshotCodec.DecodeDelta(delta, images, baseline.Snapshot, baseline.Cells);
        check(ReferenceEquals(retainedPixels, decoded.Snapshot.Graphics[0].Image.Pixels), "Metadata-only deltas reuse previously owned image pixels");
        var retainedImage = images.Single();
        byte[] withPixels = HostSnapshotCodec.EncodeDelta(changed, nativeCells, []);
        bool transactional = false;
        try { HostSnapshotCodec.DecodeDelta(withPixels[..^1], images, baseline.Snapshot, baseline.Cells); }
        catch (IOException) { transactional = images.Count == 1 && ReferenceEquals(images[retainedImage.Key], retainedImage.Value); }
        check(transactional, "Rejected image-bearing deltas preserve the last good image cache");

        baseline = decoded; var scratch = new TerminalCell[256]; var random = new Random(20261015); bool equivalent = true, rejectedBaselineIntact = true;
        IReadOnlyList<int>? repairRows = null;
        int rejectedSpares = 0;
        for (int i = 0; i < 256; i++)
        {
            switch (i % 12)
            {
                case 0: terminal.Select(random.Next(0, 100), random.Next(100, 250)); break;
                case 1: terminal.Select(-1, -1); break;
                case 2: Write("\x1b[3;7r\x1b[7;1H\n\n\x1b[r"); break;
                case 3: Write("\x1b[2S"); break;
                case 4: Write("\x1b[1T"); break;
                case 5: Write("\x1b]11;rgb:12/34/56\x07"); break;
                case 6: Write("\x1b[4:3;58:2::255:0:0m"); break;
                case 7: Write("\x1b[0m"); break;
                case 8: Write("\x1b[?25l"); break;
                case 9: Write("\x1b[?25h"); break;
                case 10: Write("\x1b_Ga=d,d=A;\x1b\\"); break;
                default: Write($"\x1b[{random.Next(1, 9)};{random.Next(1, 30)}H" + (i % 2 == 0 ? "café中🙂" : "x" + new string('\u0301', 70))); break;
            }
            changed = terminal.ReadSnapshot(nativeCells);
            delta = HostSnapshotCodec.EncodeDelta(changed, nativeCells, images.Keys.ToHashSet());
            if (i % 37 == 0)
            {
                byte[] before = HostSnapshotCodec.Encode(baseline.Snapshot, baseline.Cells, []);
                try { HostSnapshotCodec.DecodeDeltaIntoSpare(delta[..^1], images, baseline.Snapshot, baseline.Cells, scratch, repairRows); }
                catch (IOException) { rejectedSpares++; repairRows = null; }
                rejectedBaselineIntact &= before.SequenceEqual(HostSnapshotCodec.Encode(baseline.Snapshot, baseline.Cells, []));
            }
            decoded = HostSnapshotCodec.DecodeDeltaIntoSpare(delta, images, baseline.Snapshot, baseline.Cells, scratch, repairRows);
            equivalent &= Equal(nativeCells, decoded.Cells) && HostSnapshotCodec.Encode(changed, nativeCells, []).SequenceEqual(HostSnapshotCodec.Encode(decoded.Snapshot, decoded.Cells, []));
            repairRows = decoded.Snapshot.ChangedRows; scratch = baseline.Cells; baseline = decoded;
        }
        check(equivalent, "256 incremental updates match full-frame bytes across selections, scrolling, colors, long graphemes, cursor changes and image deletion");
        check(images.Count == 0, "Image deletion deltas prune obsolete generations");
        check(rejectedSpares == 7 && rejectedBaselineIntact, "Seven partially written rejected spares preserve the published baseline and recover with a complete repair");

        void Write(string value) { byte[] data = Encoding.UTF8.GetBytes(value); terminal.Write(data, data.Length); }
        void Reject(byte[] payload, string message)
        {
            bool rejected = false; try { HostSnapshotCodec.DecodeDelta(payload, images, baseline.Snapshot, baseline.Cells); } catch (IOException) { rejected = true; }
            check(rejected, message);
        }
    }
    private static bool Equal(TerminalCell[] left, TerminalCell[] right) => left.Length == right.Length && left.Select((cell, i) => cell.SameAppearance(in right[i])).All(value => value);
}
