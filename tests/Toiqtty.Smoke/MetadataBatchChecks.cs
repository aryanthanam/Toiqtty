using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class MetadataBatchChecks
{
    internal static async Task Protocol(Action<bool,string> check)
    {
        using var stream=new MemoryStream(); var header=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,1);
        stream.Write(header); stream.WriteByte((byte)'{'); stream.Position=0;
        bool rejected=false;
        try { await HostProtocol.ReadJson<HostRequest>(stream,CancellationToken.None); }
        catch(IOException ex) when(ex.InnerException is JsonException) { rejected=true; }
        check(rejected,"Malformed session JSON is reported as a bounded transport failure");
    }

    internal static async Task Run(string hostExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/metadata-batch-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var client=SessionHostClient.ConnectOrStart(folder,hostExecutable); var checks=new List<string>();
        void Pass(bool value,string text) { check(value,text); checks.Add(text); }
        string command=$"\"{Environment.ProcessPath}\" --persistent-shell-client";
        var ids=Enumerable.Range(0,16).Select(_=>Guid.NewGuid().ToString("N")).ToArray();
        var sessions=ids.Select(id=>new RemoteTerminalSession(client,id,command,Environment.CurrentDirectory,new AppSettings())).ToArray();
        RemoteTerminalSession? reattached=null;
        async Task Wait(Func<bool> condition)
        {
            var time=Stopwatch.StartNew();
            while(!condition()) { if(time.Elapsed.TotalSeconds>10) throw new TimeoutException("Metadata fixture did not become ready."); await Task.Delay(10); }
        }
        try
        {
            Pass(client.SupportsMetadataBatch,"The updated host negotiates optional batched hidden-pane metadata");
            await Wait(()=>sessions.All(session=>session.Performance.BytesRead>0));
            sessions[0].Select(0,5); bool hadSelection=sessions[0].CopySelection().Length>0;
            sessions[0].Resize(99,30,9,18); bool cleared=sessions[0].CopySelection().Length==0;
            sessions[0].Resize(100,30,9,18);
            Pass(client.SupportsResizeSelectionClear && hadSelection && cleared && sessions[0].ResizeSelectionRequestsSentForCheck==0,
                "Updated-host resizing clears selection without an additional transport request");
            bool demand=true; string subscriber=Guid.NewGuid().ToString("N"); void listener(string _) { }
            client.AddOutputListener(subscriber, listener,()=>demand); var interests=client.OutputInterestsForCheck();
            long allocationStart=GC.GetAllocatedBytesForCurrentThread(); bool same=true;
            for(int i=0;i<1000;i++) same &= ReferenceEquals(interests,client.OutputInterestsForCheck());
            long interestAllocations=GC.GetAllocatedBytesForCurrentThread()-allocationStart;
            Pass(same && interestAllocations==0,"One thousand unchanged output-interest reads reuse immutable subscriptions without managed allocation");
            demand=false; Pass(!client.OutputInterestsForCheck().Contains(subscriber),"Output interest snapshots immediately reflect changed consumer demand");
            client.AddOutputListener(subscriber, listener,()=>true); client.RemoveOutputListener(subscriber, listener);
            Pass(!client.OutputInterestsForCheck().Contains(subscriber),"Removing a duplicate subscription removes its last registration and preserves earlier demand");
            client.RemoveOutputListener(subscriber, listener);
            // Frontend caches and ConPTY resize output can still advance after
            // the RPC completes. Establish the host's own version baseline.
            var versions=ids.ToDictionary(id=>id,_=>-1);
            var baseline=await client.CallAsync(new() { Operation="states",StateVersions=versions });
            Pass(baseline.States is { Count:16 } && baseline.Panes.Length==0,"The initial metadata batch reports every requested pane");
            foreach(var pair in baseline.States!) versions[pair.Key]=pair.Value.Version;
            // Force a real version change so draining changed replies is
            // exercised even when startup output has already settled locally.
            await client.CallAsync(new() { Operation="select",Pane=ids[1],Numbers=[-1,-1] });
            bool observedChange=false; var settling=Stopwatch.StartNew(); HostReply unchanged;
            while(true)
            {
                unchanged=await client.CallAsync(new() { Operation="states",StateVersions=versions });
                if(unchanged.States is null || unchanged.Panes.Length!=0) throw new IOException("The metadata baseline lost a pane.");
                foreach(var pair in unchanged.States)
                {
                    // Retrying must never excuse the regression being tested:
                    // every emitted state must be newer than the known one.
                    if(pair.Value.Version<=versions[pair.Key]) throw new IOException("Metadata repeated an unchanged shell-state payload.");
                    versions[pair.Key]=pair.Value.Version;
                }
                observedChange|=unchanged.States.ContainsKey(ids[1]);
                if(unchanged.States.Count==0) break;
                if(settling.Elapsed.TotalSeconds>10) throw new TimeoutException("Metadata fixture output did not settle.");
                await Task.Delay(10);
            }
            Pass(observedChange,"A changed pane advances its version in batched metadata");
            Pass(unchanged.States is { Count:0 } && unchanged.Panes.Length==0,"An unchanged metadata batch sends no repeated shell-state payloads");
            string missing=Guid.NewGuid().ToString("N"); versions.Add(missing,-1);
            var absent=await client.CallAsync(new() { Operation="states",StateVersions=versions }); versions.Remove(missing);
            Pass(absent.Panes.SequenceEqual([missing]),"Batched metadata distinguishes a missing pane from an unchanged pane");
            long batches=client.MetadataBatchesForCheck,panes=client.MetadataPanesForCheck;
            await Task.Delay(1300);
            long batchCount=client.MetadataBatchesForCheck-batches,paneCount=client.MetadataPanesForCheck-panes;
            Pass(batchCount is >=2 and <=5 && paneCount>=32,"Sixteen hidden panes share a bounded number of metadata requests instead of one request per pane");

            // Leave the next metadata batch far in the future. Activating a
            // pane must cancel its queued state read rather than waiting for it.
            client.MetadataIntervalForCheck=2000; await Task.Delay(500);
            long beforeAttach=client.MetadataBatchesForCheck;
            var cells=new TerminalCell[3000]; sessions[0].AttachView();
            sessions[0].Paste("mark"); sessions[0].Key(13,0);
            await Wait(()=>sessions[0].TryReadSnapshot(cells,out _) && string.Concat(cells.Select(cell=>cell.GetText())).Contains("PERSIST_MARK=1"));
            Pass(client.MetadataBatchesForCheck==beforeAttach && sessions[0].CaptureFailure.Length==0,
                "Showing a hidden pane captures its response before the pending metadata batch, without a cancellation failure");
            sessions[0].DetachView(); client.MetadataIntervalForCheck=400;
            sessions[0].Detach(); await sessions[0].ClosedForCheck;
            bool closedCapture=false;
            try { sessions[0].ReadSnapshot(cells); } catch(ObjectDisposedException) { closedCapture=true; }
            Pass(closedCapture && !sessions[0].TryReadSnapshot(cells,out _),"Detached remote sessions reject new captures after releasing their cell buffers");
            Pass((await client.CallAsync(new())).Panes.Contains(ids[0]),"Releasing frontend buffers leaves the original hidden shell owned by the host");
            reattached=new(client,ids[0],command,Environment.CurrentDirectory,new AppSettings()); reattached.AttachView();
            await Wait(()=>reattached.TryReadSnapshot(cells,out _) && string.Concat(cells.Select(cell=>cell.GetText())).Contains("PERSIST_MARK=1"));
            Pass(true,"Reattaching after pooled-buffer cleanup restores the original shell state");
            reattached.DetachView(); reattached.Detach(); await reattached.ClosedForCheck;
            foreach(var session in sessions.Skip(1)) session.Detach();
            await Task.WhenAll(sessions.Select(session=>session.ClosedForCheck));
            await Wait(()=>!client.MetadataRunningForCheck);
            Pass(true,"Canceling the final hidden readers stops the metadata coordinator without a permanent timer");
            using(var cancel=new CancellationTokenSource())
            {
                long requestsBefore=client.MetadataBatchesForCheck;
                var canceled=Enumerable.Range(0,80).Select(_=>client.ReadHiddenState(ids[0],-1,cancel.Token)).ToArray(); cancel.Cancel();
                try { await Task.WhenAll(canceled); } catch(OperationCanceledException) { }
                await Wait(()=>!client.MetadataRunningForCheck);
                Pass(canceled.All(task=>task.IsCanceled) && client.MetadataBatchesForCheck==requestsBefore,
                    "Canceling eighty queued metadata readers releases them without sending an unnecessary host batch");
            }
            bool invalid=false;
            try { await client.CallAsync(new() { Operation="states",StateVersions=Enumerable.Range(0,257).ToDictionary(i=>"pane"+i,_=>-1) }); }
            catch(IOException) { invalid=true; }
            Pass(invalid && (await client.CallAsync(new())).Panes.Length==16,"Oversized metadata batches are rejected without changing any shell ownership");
            var churn=new RemoteTerminalSession(client,ids[0],command,Environment.CurrentDirectory,new AppSettings());
            int transitions=0;
            try
            {
                churn.HiddenMetadataQueuedForCheck=()=> {
                    churn.HiddenMetadataQueuedForCheck=null;
                    churn.AttachView(); churn.DetachView(); Interlocked.Increment(ref transitions);
                };
                await Wait(()=>Volatile.Read(ref transitions)>0);
                await Task.Delay(100);
                Pass(churn.ViewersForCheck==0 && churn.CaptureFailure.Length==0,
                    "Showing and hiding a pane before its canceled metadata continuation resumes remains an expected transition");
            }
            finally { churn.Detach(); await churn.ClosedForCheck; }
            long boundaryBefore=client.MetadataBatchesForCheck;
            var boundaryIds=ids.Concat(Enumerable.Range(0,260).Select(i=>"missing-batch-"+i)).ToArray();
            var boundaryTasks=boundaryIds.Select(id=>client.ReadHiddenState(id,-1,CancellationToken.None)).ToArray();
            try { await Task.WhenAll(boundaryTasks); } catch(IOException) { }
            Pass(boundaryTasks.Take(ids.Length).All(task=>task.IsCompletedSuccessfully && task.Result is not null) &&
                boundaryTasks.Skip(ids.Length).All(task=>task.IsFaulted && task.Exception!.InnerException is IOException) &&
                client.MetadataBatchesForCheck-boundaryBefore==2,
                "More than 256 queued pane reads split into bounded batches and isolate missing panes from valid metadata");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,checks,batchCount,paneCount },new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("Metadata batch evidence: "+folder);
        }
        finally
        {
            foreach(var session in sessions) session.Detach(); reattached?.Detach();
            try { client.Shutdown(); } catch(IOException) { }
        }
    }
}
