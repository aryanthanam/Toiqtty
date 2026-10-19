using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Oroterm.Hosting;
using Oroterm.Models;
using Oroterm.Terminal;

internal static class MetadataBatchChecks
{
    internal static async Task LegacyMetadata(string hostExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/metadata-compat-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var client=SessionHostClient.ConnectOrStart(folder,hostExecutable);
        string host=client.Identity;
        var session=new RemoteTerminalSession(client,Guid.NewGuid().ToString("N"),$"\"{Environment.ProcessPath}\" --persistent-shell-client",Environment.CurrentDirectory,new AppSettings());
        bool attached=false;
        async Task Wait(Func<bool> condition)
        {
            var timer=Stopwatch.StartNew();
            while(!condition()) { if(timer.Elapsed.TotalSeconds>10) throw new TimeoutException("Legacy metadata did not become ready."); await Task.Delay(10); }
        }
        try
        {
            check(!client.SupportsMetadataBatch,"An older host without byte-aware batching retains the individual-state fallback");
            await Wait(()=>session.Performance.BytesRead>0);
            long bytes=session.Performance.BytesRead;
            session.Paste("mark"); session.Key(13,0); await session.InputDrained;
            await Wait(()=>session.Performance.BytesRead>bytes);
            check(client.MetadataBatchesForCheck==0 && session.CaptureFailure.Length==0,"Hidden metadata still advances on the older host without sending batch requests");
            session.AttachView(); attached=true; var cells=new TerminalCell[3000];
            await Wait(()=>session.TryReadSnapshot(cells,out _) && string.Concat(cells.Select(cell=>cell.GetText())).Contains("PERSIST_MARK=1"));
            check(client.Identity==host,"Showing the older-host pane restores its original shell and text without restarting the host");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,checks=3,hostExecutable,host }));
            Console.WriteLine("Metadata compatibility evidence: "+folder);
        }
        finally { if(attached)session.DetachView();session.Detach();await session.ClosedForCheck;try{client.Shutdown();}catch(IOException){} }
    }

    internal static async Task ResizeCompatibility(string hostExecutable,bool legacy,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/resize-selection-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var client=SessionHostClient.ConnectOrStart(folder,hostExecutable);
        string pane=Guid.NewGuid().ToString("N"),command=$"\"{Environment.ProcessPath}\" --persistent-shell-client";
        var session=new RemoteTerminalSession(client,pane,command,Environment.CurrentDirectory,new AppSettings());
        try
        {
            session.AttachView(); var initial=new TerminalCell[3000];
            async Task Wait(Func<bool> ready)
            {
                var timer=Stopwatch.StartNew();
                while(!ready()) { if(timer.Elapsed.TotalSeconds>10) throw new TimeoutException("Resize compatibility did not become ready."); await Task.Delay(10); }
            }
            await Wait(()=>session.TryReadSnapshot(initial,out _) && string.Concat(initial.Select(cell=>cell.GetText())).Contains("PERSIST_READY"));
            session.Select(0,5); bool selected=session.CopySelection().Length>0;
            session.Resize(99,30,9,18);
            check(selected && session.CopySelection().Length==0,"Resize preserves selection-clearing behavior on the selected host version");
            check(client.SupportsResizeSelectionClear!=legacy && session.ResizeSelectionRequestsSentForCheck==(legacy?1:0),
                legacy?"An older protocol-2 host retains the explicit selection-clear fallback":"An updated host avoids the extra selection-clear transport request");
            session.Paste("mark"); session.Key(13,0); var resized=new TerminalCell[99*30];
            await Wait(()=>session.TryReadSnapshot(resized,out _) && string.Concat(resized.Select(cell=>cell.GetText())).Contains("PERSIST_MARK=1"));
            check(true,"Shell input and resized snapshots remain usable after the selection-clear path");
            File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new { passed=true,legacy,checks=3,selectionRequests=session.ResizeSelectionRequestsSentForCheck }));
            Console.WriteLine("Resize selection evidence: "+folder);
        }
        finally { session.DetachView(); session.Dispose(); await session.ClosedForCheck; try { client.Shutdown(); } catch(IOException) { } }
    }

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
            bool demand=true; string subscriber=Guid.NewGuid().ToString("N"); Action<string> listener=_=> { };
            client.AddOutputListener(subscriber,listener,()=>demand); var interests=client.OutputInterestsForCheck();
            long allocationStart=GC.GetAllocatedBytesForCurrentThread(); bool same=true;
            for(int i=0;i<1000;i++) same &= ReferenceEquals(interests,client.OutputInterestsForCheck());
            long interestAllocations=GC.GetAllocatedBytesForCurrentThread()-allocationStart;
            Pass(same && interestAllocations==0,"One thousand unchanged output-interest reads reuse immutable subscriptions without managed allocation");
            demand=false; Pass(!client.OutputInterestsForCheck().Contains(subscriber),"Output interest snapshots immediately reflect changed consumer demand");
            client.AddOutputListener(subscriber,listener,()=>true); client.RemoveOutputListener(subscriber,listener);
            Pass(!client.OutputInterestsForCheck().Contains(subscriber),"Removing a duplicate subscription removes its last registration and preserves earlier demand");
            client.RemoveOutputListener(subscriber,listener);
            var versions=ids.Select((id,i)=>(id,i)).ToDictionary(pair=>pair.id,pair=>sessions[pair.i].StateVersionForCheck);
            var unchanged=await client.CallAsync(new() { Operation="states",StateVersions=versions });
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
