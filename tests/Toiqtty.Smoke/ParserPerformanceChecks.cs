using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Toiqtty.Terminal;

internal static class ParserPerformanceChecks
{
    internal static void Run(string output)
    {
        string ascii = new string('a', 158) + "\r\n";
        string unicode = string.Concat(Enumerable.Repeat("café 中🙂 é العربية ", 6)) + "\r\n";
        string colors = "\x1b[1;38;2;120;180;240m" + new string('W', 78) + "\x1b[0m" + new string('x', 78) + "\r\n";
        var corpora = new[] { ("ascii", ascii), ("unicode", unicode), ("truecolor", colors) };
        var samples = new List<object>();
        for (int round = 0; round < 3; round++) for (int turn = 0; turn < corpora.Length; turn++)
        {
            var (name, line) = corpora[(round + turn) % corpora.Length];
            byte[] block = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, Math.Max(1, 65536 / Encoding.UTF8.GetByteCount(line)))));
            using var terminal = new GhosttyTerminal(160, 50); terminal.SetScrollbackLimits(1024 * 1024, 1000);
            for (int warm = 0; warm < 16; warm++) terminal.Write(block, block.Length);
            const int iterations = 512;
            long allocated = GC.GetAllocatedBytesForCurrentThread(); using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew(); for (int i = 0; i < iterations; i++) terminal.Write(block, block.Length); timer.Stop();
            double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var cells = new TerminalCell[8000]; var snapshot = terminal.ReadSnapshot(cells);
            // The checksum lies outside timing and catches semantic differences
            // between scalar/SIMD builds, including style and UTF-8 conversion.
            using var fingerprint = new MemoryStream(); using (var writer = new BinaryWriter(fingerprint, Encoding.UTF8, true))
            {
                writer.Write(snapshot.Frame.Columns); writer.Write(snapshot.Frame.Rows); writer.Write(snapshot.Frame.CursorX); writer.Write(snapshot.Frame.CursorY);
                foreach (var cell in cells) { writer.Write(cell.GetText()); writer.Write(cell.Flags); writer.Write(cell.Width); writer.Write(cell.Foreground); writer.Write(cell.Background); }
            }
            samples.Add(new
            {
                round,
                corpus = name,
                bytes = (long)block.Length * iterations,
                milliseconds = timer.Elapsed.TotalMilliseconds,
                cpuMs,
                allocatedBytes = allocated,
                sha256 = Convert.ToHexString(SHA256.HashData(fingerprint.ToArray()))
            });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { samples, meaning = "Direct libghostty parser throughput, bounded one MiB/1000-line history, fixed 160x50 grid. No snapshots during timing, no IPC or rendering. Compare output fingerprints before timings." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
    }
}
