using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class SnapshotFuzzChecks
{
    internal static async Task Run(int seconds, int seed, Action<bool, string> check)
    {
        if (seconds is < 10 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        string folder = Path.GetFullPath("artifacts/snapshot-fuzz-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var optimized = new GhosttyTerminal(80, 24); optimized.EnableIncrementalRendering();
        using var reference = new GhosttyTerminal(80, 24); reference.EnableIncrementalRendering(false);
        using var utf8 = new GhosttyTerminal(80, 24); utf8.EnableIncrementalRendering();
        var raw = new Utf8TerminalBuffer(80 * 24);
        optimized.SetScrollbackLimits(1024 * 1024, 1000); reference.SetScrollbackLimits(1024 * 1024, 1000);
        utf8.SetScrollbackLimits(1024 * 1024, 1000);
        var outputA = new List<byte[]>(); var outputB = new List<byte[]>(); optimized.Response += outputA.Add; reference.Response += outputB.Add;
        var pool = new TerminalCellPool(); var cells = pool.Rent(80 * 24); var expected = new TerminalCell[80 * 24];
        var cache = new Dictionary<ulong, TerminalImage>(); TerminalSnapshot? decoded = null; TerminalCell[] decodedCells = [], spare = [];
        var recent = new Queue<string>(); var random = new Random(seed); var timer = Stopwatch.StartNew();
        int steps = 0, comparisons = 0, full = 0, delta = 0, skipped = 0, invalid = 0, graphics = 0; long cellsChecked = 0; double nextSave = 0;
        int managedPayloads = 0, utf8Payloads = 0, runPayloads = 0;
        bool baselineValid = false, passed = false; string? error = null;
        IReadOnlyList<int>? repairRows = null;
        void Save()
        {
            var (Bytes, Buffers, Created, Reused) = pool.Statistics;
            string path = Path.Combine(folder, "result.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
            {
                passed,
                error,
                seed,
                requestedSeconds = seconds,
                elapsedSeconds = timer.Elapsed.TotalSeconds,
                steps,
                comparisons,
                cellsChecked,
                fullFrames = full,
                deltaFrames = delta,
                skippedFrames = skipped,
                rejectedPayloads = invalid,
                graphicsChecked = graphics,
                managedPayloads,
                utf8Payloads,
                runPayloads,
                nativeBufferAllocations = optimized.NativeCellBufferAllocationsForCheck,
                nativeCapacity = optimized.NativeCellBufferCapacityForCheck,
                pool = new { bytes = Bytes, buffers = Buffers, created = Created, reused = Reused },
                recent,
                meaning = "Seeded direct-engine differential test: incremental/pool/transport output versus full reference captures. No real shell, desktop input, clipboard or physical GPU events."
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
        try
        {
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                steps++; int operation = random.Next(20); string description; byte[]? bytes = null;
                switch (operation)
                {
                    case 0:
                    case 1:
                    case 2:
                    case 3:
                        description = "text " + steps;
                        bytes = Encoding.UTF8.GetBytes($"ROW_{steps} café 中🙂 العربية e\u0301 != =>\r\n"); break;
                    case 4:
                        description = "cursor"; bytes = Encoding.UTF8.GetBytes($"\x1b[{random.Next(1, 40)};{random.Next(1, 120)}HX"); break;
                    case 5:
                        description = "erase"; bytes = Encoding.UTF8.GetBytes(random.Next(2) == 0 ? "\x1b[2K" : "\x1b[J"); break;
                    case 6:
                        description = "style"; bytes = Encoding.UTF8.GetBytes($"\x1b[1;3;4:3;38;2;{random.Next(256)};{random.Next(256)};{random.Next(256)}mStyle 中\x1b[0m"); break;
                    case 7:
                        description = "reverse-index"; bytes = Encoding.UTF8.GetBytes("\x1bM\r\n\r\n"); break;
                    case 8:
                        description = "alternate-screen"; bytes = Encoding.UTF8.GetBytes(random.Next(2) == 0 ? "\x1b[?1049h" : "\x1b[?1049l"); break;
                    case 9:
                        int scroll = -random.Next(16); description = "scroll " + scroll; optimized.Scroll(scroll); reference.Scroll(scroll); utf8.Scroll(scroll); break;
                    case 10:
                        int id = 500 + random.Next(2); description = "image " + id;
                        string rgba = Convert.ToBase64String(new byte[] { (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255 });
                        bytes = Encoding.UTF8.GetBytes($"\x1b_Ga=T,f=32,s=1,v=1,i={id},p=1,c=3,r=2,q=2,C=1;{rgba}\x1b\\"); break;
                    case 11:
                        description = "delete-image"; bytes = Encoding.UTF8.GetBytes($"\x1b_Ga=d,d=I,i={500 + random.Next(2)},q=2\x1b\\"); break;
                    case 12:
                        description = "reset"; bytes = Encoding.UTF8.GetBytes("\u001bc"); break;
                    case 13:
                        description = "progress"; bytes = Encoding.UTF8.GetBytes($"\x1b]9;4;1;{random.Next(101)}\x07"); break;
                    case 14:
                        description = "synchronized-frame"; bytes = Encoding.UTF8.GetBytes($"\x1b[?2026hSYNC_{steps}\r\n\x1b[?2026l"); break;
                    case 15:
                        description = "fragmented-utf8"; bytes = [0xf0, 0x9f, 0x99, 0x82, 0xff, 0xc0, 0x80, 0x61, 0xcc, 0x81]; break;
                    case 16:
                        ushort columns = (ushort)random.Next(2, 121), rows = (ushort)random.Next(1, 41); description = $"resize {columns}x{rows}";
                        optimized.Resize(columns, rows, 9, 18); reference.Resize(columns, rows, 9, 18);
                        utf8.Resize(columns, rows, 9, 18); raw = new Utf8TerminalBuffer(columns * rows);
                        pool.Return(cells); cells = pool.Rent(columns * rows); expected = new TerminalCell[columns * rows]; baselineValid = false; break;
                    case 17:
                        int start = random.Next(cells.Length), end = random.Next(cells.Length); description = $"selection {start}:{end}";
                        optimized.Select(start, end); reference.Select(start, end); utf8.Select(start, end); break;
                    case 18:
                        ulong offset = (ulong)random.Next(200); description = "absolute-scroll " + offset;
                        optimized.ScrollTo(offset); reference.ScrollTo(offset); utf8.ScrollTo(offset); break;
                    default:
                        description = "cursor-query"; bytes = Encoding.UTF8.GetBytes("\x1b[6n"); break;
                }
                recent.Enqueue(steps + " " + description); while (recent.Count > 32) recent.Dequeue();
                if (bytes is not null)
                {
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int count = Math.Min(bytes.Length - offset, random.Next(1, 8)); var fragment = bytes.AsSpan(offset, count).ToArray();
                        optimized.Write(fragment, fragment.Length); reference.Write(fragment, fragment.Length); utf8.Write(fragment, fragment.Length); offset += count;
                    }
                }
                if (outputA.Count != outputB.Count || outputA.Where((data, i) => !data.AsSpan().SequenceEqual(outputB[i])).Any())
                    throw new InvalidOperationException("Engine replies differ at step " + steps);
                outputA.Clear(); outputB.Clear();
                var snapshot = optimized.ReadSnapshot(cells); var target = reference.ReadSnapshot(expected);
                var rawSnapshot = utf8.ReadSnapshot(raw);
                AssertFrame(snapshot, cells, target, expected, steps); cellsChecked += cells.Length; graphics += snapshot.Graphics.Count; comparisons++;
                if (random.Next(8) == 0) { baselineValid = false; skipped++; }
                else
                {
                    bool useDelta = baselineValid && decoded is not null && random.Next(4) != 0;
                    var known = cache.Keys.ToHashSet();
                    int codec = random.Next(3);
                    byte[] payload;
                    if (codec == 0) { payload = useDelta ? HostSnapshotCodec.EncodeDelta(snapshot, cells, known) : HostSnapshotCodec.Encode(snapshot, cells, known); managedPayloads++; }
                    else if (codec == 1) { payload = HostSnapshotCodec.EncodeUtf8(rawSnapshot, raw, known, useDelta); utf8Payloads++; }
                    else { payload = HostSnapshotCodec.EncodeUtf8Runs(rawSnapshot, raw, known, useDelta); runPayloads++; }
                    if (steps % 97 == 0)
                    {
                        var baselineCopy = (TerminalCell[])decodedCells.Clone(); var imageCopy = cache.ToDictionary(); bool rejected = false;
                        try
                        {
                            if (useDelta) HostSnapshotCodec.DecodeDeltaIntoSpare(payload[..^1], cache, decoded!, decodedCells, spare, repairRows);
                            else HostSnapshotCodec.Decode(payload[..^1], cache, spare);
                        }
                        catch (HostSnapshotException) { rejected = true; repairRows = null; }
                        if (!rejected || !SameCells(baselineCopy, decodedCells) || imageCopy.Count != cache.Count
                            || imageCopy.Any(pair => !cache.TryGetValue(pair.Key, out var image) || !ReferenceEquals(image, pair.Value)))
                            throw new InvalidOperationException("Rejected transport data changed a published baseline or image cache at step " + steps);
                        invalid++;
                    }
                    var old = decodedCells; var unused = spare;
                    (decoded, decodedCells) = useDelta ? HostSnapshotCodec.DecodeDeltaIntoSpare(payload, cache, decoded!, decodedCells, spare, repairRows) : HostSnapshotCodec.Decode(payload, cache, spare);
                    repairRows = useDelta ? decoded.ChangedRows : null;
                    if (!ReferenceEquals(unused, decodedCells) && !ReferenceEquals(unused, old)) TerminalCellPool.Shared.Return(unused);
                    spare = old; decoded = decoded with { Scrollbar = snapshot.Scrollbar };
                    AssertFrame(decoded, decodedCells, snapshot, cells, steps); baselineValid = true;
                    if (useDelta) delta++; else full++;
                }
                if (timer.Elapsed.TotalSeconds >= nextSave) { Save(); nextSave = timer.Elapsed.TotalSeconds + 5; }
                if (steps % 4 == 0) await Task.Delay(10);
            }
            check(comparisons > 0 && full > 0 && delta > 0 && invalid > 0, "Differential snapshots preserve cells, styles, replies and static graphics across seeded resizes and protocol transitions");
            check(pool.Statistics.Bytes <= 16L * 1024 * 1024, "The randomized resize corpus retains cell buffers within its process budget");
            check(managedPayloads > 0 && utf8Payloads > 0 && runPayloads > 0, "Managed, UTF-8 and negotiated style-run payloads interoperate within one repaired snapshot baseline");
            passed = true; Save(); Console.WriteLine("Snapshot fuzz evidence: " + folder);
        }
        catch (Exception ex) { error = ex.ToString(); Save(); throw; }
        finally
        {
            // The engine retains cells until it is disposed; release it first.
            optimized.Dispose(); reference.Dispose(); utf8.Dispose(); pool.Return(cells);
            TerminalCellPool.Shared.Return(decodedCells); if (!ReferenceEquals(decodedCells, spare)) TerminalCellPool.Shared.Return(spare);
        }
    }

    private static bool SameCells(TerminalCell[] left, TerminalCell[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (!left[i].SameAppearance(in right[i])) return false;
        return true;
    }
    private static void AssertFrame(TerminalSnapshot actual, TerminalCell[] cells, TerminalSnapshot expected, TerminalCell[] expectedCells, int step)
    {
        var left = actual.Frame; var right = expected.Frame; left.ChangedRows = right.ChangedRows = 0; left.AnimationDelay = right.AnimationDelay = 0;
        if (!MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref left, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref right, 1)))
            || actual.Scrollbar != expected.Scrollbar || !SameCells(cells, expectedCells))
            throw new InvalidOperationException("Terminal frame differs from the full reference at step " + step);
        if (actual.Graphics.Count != expected.Graphics.Count) throw new InvalidOperationException("Graphic placement count differs at step " + step);
        for (int i = 0; i < actual.Graphics.Count; i++)
        {
            var a = actual.Graphics[i]; var b = expected.Graphics[i];
            if ((a with { Image = b.Image }) != b || a.Image.Width != b.Image.Width || a.Image.Height != b.Image.Height || a.Image.Channels != b.Image.Channels
                || !a.Image.Pixels.AsSpan().SequenceEqual(b.Image.Pixels)) throw new InvalidOperationException("Graphic pixels or geometry differ at step " + step);
        }
    }
}
