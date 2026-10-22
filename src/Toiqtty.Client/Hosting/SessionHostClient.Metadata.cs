namespace Toiqtty.Hosting;

public sealed partial class SessionHostClient
{
    private sealed record MetadataRequest(int Version,CancellationToken Cancellation,TaskCompletionSource<ShellState?> Completion);
    private readonly Lock _metadataGate =new();
    private Dictionary<string,List<MetadataRequest>> _metadataPending=[];
    private bool _metadataRunning;
    internal int MetadataIntervalForCheck { get; set; }=400;
    internal long MetadataBatchesForCheck,MetadataPanesForCheck;
    internal Action? MetadataOverflowForCheck { get; set; }
    internal bool MetadataRunningForCheck { get { lock(_metadataGate) return _metadataRunning; } }

    internal Task<ShellState?> ReadHiddenState(string pane,int version,CancellationToken cancellation)
    {
        if(cancellation.IsCancellationRequested) return Task.FromCanceled<ShellState?>(cancellation);
        var completion=new TaskCompletionSource<ShellState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_metadataGate)
        {
            if(!_metadataPending.TryGetValue(pane,out var requests)) _metadataPending.Add(pane,requests=[]);
            requests.Add(new(version,cancellation,completion));
            if(!_metadataRunning) { _metadataRunning=true; _=ReadPendingMetadata(); }
        }
        // Cancel this caller without canceling the shared read for other panes.
        return completion.Task.WaitAsync(cancellation);
    }

    private async Task ReadPendingMetadata()
    {
        while(true)
        {
            await Task.Delay(MetadataIntervalForCheck).ConfigureAwait(false);
            Dictionary<string,List<MetadataRequest>> pending;
            lock(_metadataGate)
            {
                if(_metadataPending.Count==0) { _metadataRunning=false; return; }
                pending=_metadataPending; _metadataPending=[];
            }
            var versions=new Dictionary<string,int>();
            foreach(var pair in pending)
            {
                foreach(var canceled in pair.Value.Where(request=>request.Cancellation.IsCancellationRequested))
                    canceled.Completion.TrySetCanceled(canceled.Cancellation);
                var active=pair.Value.Where(request=>!request.Cancellation.IsCancellationRequested).ToArray();
                if(active.Length>0) versions.Add(pair.Key,active.Min(request=>request.Version));
            }
            if(versions.Count==0) continue;
            if(versions.Count<=HostProtocol.MaxMetadataPanes)
                await ReadMetadataBatch(versions,pending).ConfigureAwait(false);
            else
            {
                // Closing panes can leave queued reads while replacements are
                // admitted. Bound each message even across that transient set.
                foreach(var chunk in versions.Chunk(HostProtocol.MaxMetadataPanes))
                {
                    var batch=chunk.ToDictionary(pair=>pair.Key,pair=>pair.Value);
                    await ReadMetadataBatch(batch,pending).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ReadMetadataBatch(Dictionary<string,int> versions,Dictionary<string,List<MetadataRequest>> pending)
    {
        try
        {
            Interlocked.Increment(ref MetadataBatchesForCheck); Interlocked.Add(ref MetadataPanesForCheck,versions.Count);
            var reply=await CallAsync(HostRequests.States(versions)).ConfigureAwait(false);
            if(reply.States is null) throw new IOException("The host omitted its batched metadata response.");
            foreach(var pane in versions.Keys)
            foreach(var request in pending[pane])
            {
                if(request.Cancellation.IsCancellationRequested) request.Completion.TrySetCanceled(request.Cancellation);
                else if(reply.Panes.Contains(pane)) request.Completion.TrySetException(new HostRejectedException(HostErrorCode.SessionMissing,"This shell session no longer exists."));
                else request.Completion.TrySetResult(reply.States.GetValueOrDefault(pane));
            }
        }
        catch(HostRejectedException ex) when(ex.Code==HostErrorCode.ReplyTooLarge && versions.Count>1)
        {
            MetadataOverflowForCheck?.Invoke();
            // Reads are safe to subdivide. A byte-limit failure never replays
            // shell input, and each subdivision strictly reduces the pane set.
            foreach(var pane in versions.Keys)
            foreach(var request in pending[pane])
                if(request.Cancellation.IsCancellationRequested) request.Completion.TrySetCanceled(request.Cancellation);
            var active=versions.Where(pair=>pending[pair.Key].Any(request=>!request.Cancellation.IsCancellationRequested));
            foreach(var chunk in active.Chunk((versions.Count+1)/2))
                await ReadMetadataBatch(chunk.ToDictionary(pair=>pair.Key,pair=>pair.Value),pending).ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            // Complete only this bounded batch; a transport failure must not
            // strand readers or prevent subsequent batches from completing.
            foreach(var pane in versions.Keys)
            foreach(var request in pending[pane])
                if(request.Cancellation.IsCancellationRequested) request.Completion.TrySetCanceled(request.Cancellation);
                else request.Completion.TrySetException(ex);
        }
    }
}
