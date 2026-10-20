using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class IdleMetadataBenchmark
{
    internal static async Task Run(string hostExecutable,int seconds,string reportPath,Action<bool,string> check)
    {
        if(seconds is < 15 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        string folder=Path.GetFullPath("artifacts/performance-idle-metadata-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var client=SessionHostClient.ConnectOrStart(folder,hostExecutable);
        var sessions=new List<RemoteTerminalSession>();
        using var self=Process.GetCurrentProcess();
        using var host=Process.GetProcessById(client.ProcessId);
        object? measurement=null;
        try
        {
            for(int i=0;i<64;i++) sessions.Add(new(client,Guid.NewGuid().ToString("N"),
                $"\"{Environment.ProcessPath}\" --persistent-shell-client",Environment.CurrentDirectory,new AppSettings()));
            await Wait(()=>sessions.All(s=>s.Performance.BytesRead>0));
            await Task.Delay(10000); // Exclude shell creation, first state, and initial JIT work.
            Healthy();
            self.Refresh();host.Refresh();
            double uiCpu=self.TotalProcessorTime.TotalMilliseconds,hostCpu=host.TotalProcessorTime.TotalMilliseconds;
            long allocated=GC.GetTotalAllocatedBytes(true),batches=client.MetadataBatchesForCheck,panes=client.MetadataPanesForCheck;
            var timer=Stopwatch.StartNew();var samples=new List<object>();
            while(timer.Elapsed.TotalSeconds<seconds)
            {
                await Task.Delay(1000);Healthy();self.Refresh();host.Refresh();
                samples.Add(new { seconds=timer.Elapsed.TotalSeconds,clientCpuMs=self.TotalProcessorTime.TotalMilliseconds-uiCpu,
                    hostCpuMs=host.TotalProcessorTime.TotalMilliseconds-hostCpu,allocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated,
                    metadataBatches=client.MetadataBatchesForCheck-batches,metadataPanes=client.MetadataPanesForCheck-panes,
                    clientPrivateBytes=self.PrivateMemorySize64,hostPrivateBytes=host.PrivateMemorySize64,
                    hostThreads=host.Threads.Count,hostHandles=host.HandleCount });
            }
            double elapsed=timer.Elapsed.TotalSeconds;
            measurement=new {seconds=elapsed,clientCpuCores=(self.TotalProcessorTime.TotalMilliseconds-uiCpu)/elapsed/1000,
                hostCpuCores=(host.TotalProcessorTime.TotalMilliseconds-hostCpu)/elapsed/1000,
                allocatedMiBPerSecond=(GC.GetTotalAllocatedBytes(true)-allocated)/elapsed/1048576,
                metadataBatches=client.MetadataBatchesForCheck-batches,metadataPanes=client.MetadataPanesForCheck-panes,samples};
            check(sessions.All(s=>s.CapturesForCheck==0),"Sixty-four hidden shells remain healthy without visible frame captures during idle measurement");
            check(client.SupportsMetadataBatch?client.MetadataBatchesForCheck>batches:client.MetadataBatchesForCheck==0,
                "Idle measurement uses negotiated batched metadata or the older-host individual fallback");
            var active=sessions[0];var cells=new TerminalCell[3000];var wake=Stopwatch.StartNew();
            active.AttachView();active.Paste("mark");active.Key(13,0);
            await Wait(()=>active.TryReadSnapshot(cells,out _) && string.Concat(cells.Select(c=>c.GetText())).Contains("PERSIST_MARK=1"));
            wake.Stop();
            active.DetachView();Healthy();
            check(client.Identity==SessionHostClient.TryConnect(folder)?.Identity,"Activation after sustained hidden polling preserves the original host and shell response");
            File.WriteAllText(reportPath,JsonSerializer.Serialize(new {passed=true,folder,hostExecutable,host=client.Identity,
                panes=sessions.Count,requestedSeconds=seconds,warmupSeconds=10,supportsMetadataBatch=client.SupportsMetadataBatch,
                measurement,wakeToSnapshotMs=wake.Elapsed.TotalMilliseconds,
                meaning="Same final client against each exact published host,64 real hidden persistent-shell clients,10s warmup then sustained idle polling. Measures client managed allocation and client/host CPU; no WinUI renderer, no physical display latency. Final activation verifies shell response."},new JsonSerializerOptions {WriteIndented=true}));
            Console.WriteLine("Idle metadata evidence: "+reportPath);
        }
        catch(Exception ex)
        {
            File.WriteAllText(reportPath,JsonSerializer.Serialize(new {passed=false,folder,hostExecutable,measurement,
                captureFailures=sessions.Select(s=>s.CaptureFailure).ToArray(),error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));throw;
        }
        finally
        {
            foreach(var session in sessions) session.Detach();
            try {await Task.WhenAll(sessions.Select(s=>s.ClosedForCheck)).WaitAsync(TimeSpan.FromSeconds(15));}
            finally {try{client.Shutdown();}catch(IOException){}}
        }
        void Healthy()
        {
            foreach(var session in sessions)
                if(session.HasExited || session.CaptureFailure.Length>0)
                    throw new IOException("Hidden idle shell exited or capture failed: "+session.CaptureFailure);
        }
        async Task Wait(Func<bool> ready)
        {
            var timer=Stopwatch.StartNew();
            while(!ready()){Healthy();if(timer.Elapsed.TotalSeconds>15)throw new TimeoutException("Idle metadata shell did not become ready.");await Task.Delay(10);}
        }
    }
}
