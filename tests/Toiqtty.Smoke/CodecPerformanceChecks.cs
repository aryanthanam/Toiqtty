using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class CodecPerformanceChecks
{
    internal static void RunRows(string reportPath)
    {
        var cells=new TerminalCell[160*50]; Array.Fill(cells,new TerminalCell { Text="x",Length=1,Width=1 });
        var frame=new TerminalFrame { Columns=160,Rows=50,AnimationDelay=uint.MaxValue };
        var snapshot=new TerminalSnapshot(frame,[],[20]);
        byte[] full=HostSnapshotCodec.Encode(snapshot,cells,[]);
        cells[20*160].Text="a"; byte[] first=HostSnapshotCodec.EncodeDelta(snapshot,cells,[]);
        cells[20*160].Text="b"; byte[] second=HostSnapshotCodec.EncodeDelta(snapshot,cells,[]);
        var samples=new List<object>(); const int iterations=5000;
        for(int round=0;round<3;round++)
        foreach(bool sparse in round%2==0?new[]{false,true}:[true,false])
        {
            var images=new Dictionary<ulong,TerminalImage>();
            var current=HostSnapshotCodec.Decode(full,images);
            var spare=(TerminalCell[])current.Cells.Clone();
            IReadOnlyList<int>? repair=[]; int index=0;
            void Decode()
            {
                var payload=index++%2==0?first:second; var previous=current.Cells;
                current=sparse?HostSnapshotCodec.DecodeDeltaIntoSpare(payload,images,current.Snapshot,current.Cells,spare,repair)
                    :HostSnapshotCodec.DecodeDelta(payload,images,current.Snapshot,current.Cells,spare);
                spare=previous; repair=current.Snapshot.ChangedRows;
            }
            for(int i=0;i<100;i++) Decode();
            long before=GC.GetAllocatedBytesForCurrentThread(); var timer=Stopwatch.StartNew();
            for(int i=0;i<iterations;i++) Decode();
            timer.Stop();
            if(current.Cells[20*160].GetText()!="b" || current.Cells[0].GetText()!="x") throw new Exception("Sparse decode benchmark lost cell content.");
            samples.Add(new { round,operation=sparse?"repair-dirty-rows":"copy-full-baseline",iterations,
                milliseconds=timer.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-before,
                cellBytes=System.Runtime.CompilerServices.Unsafe.SizeOf<TerminalCell>(),columns=160,rows=50,changedRows=1 });
        }
        File.WriteAllText(reportPath,JsonSerializer.Serialize(new { samples,meaning="Isolated CPU microbenchmark of identical sparse deltas; not end-to-end presentation latency." },new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine(File.ReadAllText(reportPath));
    }
    public static void RunMetadata(string reportPath)
    {
        using var terminal = new GhosttyTerminal();
        byte[] value = Encoding.UTF8.GetBytes("\x1b]2;metadata benchmark café\x07\x1b]7;file:///D:/repo/toiqtty\x07");
        terminal.Write(value, value.Length); _ = terminal.Metadata(0); _ = terminal.Metadata(1);
        long allocated = GC.GetTotalAllocatedBytes(); var timer = Stopwatch.StartNew(); const int iterations = 100000;
        for (int i = 0; i < iterations; i++) { _ = terminal.Metadata(0); _ = terminal.Metadata(1); }
        var report = new { iterations, milliseconds = timer.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(reportPath));
    }
    public static void Run(string reportPath)
    {
        using var terminal = new GhosttyTerminal(160, 50);
        string row = new string('x', 150) + " café 中🙂";
        byte[] text = Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Repeat(row, 50)));
        terminal.Write(text, text.Length);
        var cells = new TerminalCell[160 * 50]; var snapshot = terminal.ReadSnapshot(cells);
        byte[] payload = HostSnapshotCodec.Encode(snapshot, cells, []);
        var samples = new List<object>();
        var reusable = new TerminalCell[cells.Length];
        foreach (string operation in new[] { "encode", "decode", "decode-reuse" })
        {
            for (int i = 0; i < 20; i++) RunOnce();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetTotalAllocatedBytes(); var timer = Stopwatch.StartNew();
            const int iterations = 1000;
            for (int i = 0; i < iterations; i++) RunOnce();
            samples.Add(new { operation, iterations, milliseconds = timer.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, payloadBytes = payload.Length });
            void RunOnce()
            {
                if (operation == "encode") _ = HostSnapshotCodec.Encode(snapshot, cells, []);
                else _ = HostSnapshotCodec.Decode(payload, [], operation == "decode-reuse" ? reusable : null);
            }
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, samples }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(reportPath));
    }
}
