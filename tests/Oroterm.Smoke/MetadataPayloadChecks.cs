using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Oroterm.Hosting;

internal static class MetadataPayloadChecks
{
    internal static async Task Run(string hostExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/metadata-payload-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string history=Path.Combine(folder,"history.txt");
        var output=new StringBuilder();
        for(int i=0;i<100;i++)
            output.Append("\x1b]133;C;cmdline_url=PAYLOAD_").Append(i.ToString("D3")).Append('_').Append('x',1800)
                .Append("\x07\x1b]133;D;0\x07");
        File.WriteAllText(history,output.ToString(),new UTF8Encoding(false));
        var client=SessionHostClient.ConnectOrStart(folder,hostExecutable);
        using var owner=Process.GetProcessById(client.ProcessId);
        var states=new Dictionary<string,ShellState>();
        int successful=0; long combinedBytes=0,maxIndividualBytes=0; bool passed=false; string? error=null;
        var timer=Stopwatch.StartNew();
        try
        {
            check(client.SupportsMetadataBatch,"The host negotiates byte-aware metadata batching");
            for(int i=0;i<96;i++)
            {
                string pane=Guid.NewGuid().ToString("N");
                await client.CallAsync(new() { Operation="create",Pane=pane,Text=$"cmd.exe /Q /D /K type \"{history}\"",Directory=folder });
                var ready=Stopwatch.StartNew(); ShellState? state;
                do
                {
                    state=(await client.CallAsync(new() { Operation="state",Pane=pane })).State;
                    if(state?.Status.Commands.Count==100) break;
                    if(ready.Elapsed.TotalSeconds>10) throw new TimeoutException("The real terminal did not capture its bounded command history.");
                    await Task.Delay(20);
                }while(true);
                states.Add(pane,state!);
                maxIndividualBytes=Math.Max(maxIndividualBytes,JsonSerializer.SerializeToUtf8Bytes(new HostReply { State=state }).LongLength);
                if(i%16==15) Console.WriteLine($"METADATA_PAYLOAD {i+1}/96 real terminal histories ready");
            }
            check(maxIndividualBytes<HostProtocol.MaxJsonBytes,"Each real terminal's bounded command history fits an individual state reply");
            combinedBytes=JsonSerializer.SerializeToUtf8Bytes(new HostReply { States=states }).LongLength;
            check(combinedBytes>HostProtocol.MaxJsonBytes,"The valid pane-count batch exercises the IPC response byte limit");
            var reads=states.Keys.Select(pane=>client.ReadHiddenState(pane,-1,CancellationToken.None)).ToArray();
            try { await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch(IOException) { }
            successful=reads.Count(task=>task.IsCompletedSuccessfully && task.Result?.Status.Commands.Count==100);
            check(successful==states.Count,"Batched hidden metadata delivers every valid large history without disconnecting unrelated panes");
            var unchanged=await client.CallAsync(new() { Operation="states",StateVersions=states.Keys.Select((pane,i)=>(pane,i))
                .ToDictionary(pair=>pair.pane,pair=>reads[pair.i].Result!.Version) });
            check(unchanged.States is { Count:0 },"Unchanged large histories omit their payload after a successful split");
            using(var cancel=new CancellationTokenSource())
            {
                long before=client.MetadataBatchesForCheck;
                client.MetadataOverflowForCheck=()=>cancel.Cancel();
                try
                {
                    var canceled=states.Keys.Select(pane=>client.ReadHiddenState(pane,-1,cancel.Token)).ToArray();
                    try { await Task.WhenAll(canceled); } catch(OperationCanceledException) { }
                    var stopped=Stopwatch.StartNew();
                    while(client.MetadataRunningForCheck)
                    { if(stopped.Elapsed.TotalSeconds>5) throw new TimeoutException("Canceled large metadata reads did not retire."); await Task.Delay(10); }
                    check(canceled.All(task=>task.IsCanceled) && client.MetadataBatchesForCheck==before+1,
                        "Canceling every reader after overflow avoids further subdivisions and stops the coordinator");
                }
                finally { client.MetadataOverflowForCheck=null; }
            }
            passed=true;
        }
        catch(Exception ex) { error=ex.ToString(); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed,error,hostExecutable,
                panes=states.Count,successfulReads=successful,maxIndividualBytes,combinedBytes,limit=HostProtocol.MaxJsonBytes,
                seconds=timer.Elapsed.TotalSeconds,meaning="Real ConPTY CMD shells emit supported OSC133 command history. No user sessions, desktop input or global configuration changes." },new JsonSerializerOptions { WriteIndented=true }));
            try { client.Shutdown(); } catch(IOException) { }
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Console.WriteLine("Metadata payload evidence: "+folder);
        }
    }
}
