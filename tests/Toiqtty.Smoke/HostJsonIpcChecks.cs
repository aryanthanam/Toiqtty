using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class HostJsonIpcChecks
{
    internal static async Task Run(string executable, string output)
    {
        string folder = Path.GetFullPath("artifacts/json-ipc-" + Guid.NewGuid().ToString("N"));
        var client = SessionHostClient.ConnectOrStart(folder, executable); string pane = Guid.NewGuid().ToString("N");
        await client.CallAsync(new() { Operation = "create", Pane = pane, Text = $"\"{Environment.ProcessPath}\" --persistent-shell-client", Directory = Environment.CurrentDirectory });
        using var host = Process.GetProcessById(client.ProcessId); using var current = Process.GetCurrentProcess();
        using var lease = client.AcquireCaptureReads();
        try
        {
            HostReply baseline; var deadline = DateTime.UtcNow.AddSeconds(8);
            do
            {
                baseline = await client.CaptureAsync(new() { Operation = "capture", Pane = pane, Numbers = [-1], AllowStyleRuns = true }, [], CancellationToken.None, lease);
                if (baseline.BinaryLength > 0)
                {
                    var (Snapshot, Cells) = HostSnapshotCodec.Decode(baseline.Binary, [], payloadLength: baseline.BinaryLength);
                    bool ready = string.Concat(Cells.Select(cell => cell.GetText())).Contains("PERSIST_READY"); TerminalCellPool.Shared.Return(Cells);
                    if (ready) break;
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException("JSON IPC fixture did not start."); await Task.Delay(10);
            } while (true);
            var samples = new List<object>(); const int workers = 8, iterations = 1000;
            async Task Worker(int count)
            {
                var request = new HostRequest { Operation = "capture", Pane = pane, Numbers = [baseline.CapturedVersion, baseline.FrameRevision] };
                for (int i = 0; i < count; i++)
                {
                    var reply = await client.CaptureAsync(request, [], CancellationToken.None, lease);
                    if (reply.BinaryLength != 0 || reply.State?.Version != baseline.CapturedVersion) throw new Exception("The metadata-only IPC workload changed its snapshot baseline.");
                }
            }
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker(100)));
            for (int round = 0; round < 3; round++)
            {
                long allocated = GC.GetTotalAllocatedBytes(true); var clientCpu = current.TotalProcessorTime; var hostCpu = host.TotalProcessorTime;
                var timer = Stopwatch.StartNew(); await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker(iterations))); timer.Stop();
                samples.Add(new
                {
                    round,
                    requests = workers * iterations,
                    milliseconds = timer.Elapsed.TotalMilliseconds,
                    clientCpuMs = (current.TotalProcessorTime - clientCpu).TotalMilliseconds,
                    hostCpuMs = (host.TotalProcessorTime - hostCpu).TotalMilliseconds,
                    clientAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated
                });
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                legacyJson = HostProtocol.LegacyJsonForCheck,
                folder,
                samples,
                meaning = "Real named-pipe metadata-only captures over eight reusable authenticated channels. Idle real ConPTY shell; no rendering or output flood."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(output);
        }
        finally { await client.CallAsync(new() { Operation = "close", Pane = pane }); client.Shutdown(); }
    }
}
