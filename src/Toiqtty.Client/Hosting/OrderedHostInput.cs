using System.Text;

namespace Toiqtty.Hosting;

/// <summary>Bounded FIFO admission; acknowledgements never block the caller.</summary>
internal sealed class OrderedHostInput(Func<HostRequest,Task<HostReply>> send)
{
    internal const int MaxRequests=2048, MaxBytes=8*1024*1024;
    private sealed record Entry(HostRequest Request,int Bytes,TaskCompletionSource<HostReply> Completion);
    private readonly Lock _gate =new();
    private readonly Queue<Entry> _queue=[];
    private readonly HashSet<uint> _pressed=[];
    private int _count,_bytes;
    private bool _running,_completed;
    // None while deliverable; otherwise the transport code that closed delivery.
    private HostErrorCode _failure;
    private TaskCompletionSource _drained=Ready();
    internal int Pending { get { lock(_gate) return _count; } }
    internal int PendingBytes { get { lock(_gate) return _bytes; } }
    private static TaskCompletionSource Ready() { var result=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
    internal Task Drain { get { lock(_gate) return _drained.Task; } }
    internal Task<HostReply> Enqueue(HostRequest request)
    {
        long cost=128L+Encoding.UTF8.GetByteCount(request.Text);
        if((request.DropQuery?.Items ?? request.DropCommit?.Items) is { } items) foreach(var item in items) cost+=Encoding.UTF8.GetByteCount(item.Key)+item.Value.LongLength;
        if(request.Settings is not null) cost+=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request.Settings,HostJsonContext.Default.AppSettings).LongLength;
        if(request.Layout is not null) cost+=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request.Layout,HostJsonContext.Default.SavedSession).LongLength;
        if(cost>MaxBytes) return Task.FromResult(new HostReply { Error="Input busy · the complete request exceeds the queue limit.",ErrorCode=HostErrorCode.InputBusy });
        int bytes=(int)cost;
        lock(_gate)
        {
            // Synthetic replies are coded like host replies: a closing session is
            // ShuttingDown, a failed channel repeats the transport code that closed it.
            if(_failure!=HostErrorCode.None) return Task.FromResult(new HostReply { Error="Host delivery failed; input was not retried.",ErrorCode=_failure });
            if(_completed) return Task.FromResult(new HostReply { Error="Input delivery is closed; input was not sent.",ErrorCode=HostErrorCode.ShuttingDown });
            // Only key and copy-or-key carry KeyArgs.
            bool key=request.Key is { Key: not 0 };
            uint code=key?request.Key!.Key:0;
            bool release=key && request.Key!.Action==Terminal.TerminalKeyAction.Release;
            int held=_pressed.Count+(key && !release && !_pressed.Contains(code)?1:0)-(release && _pressed.Contains(code)?1:0);
            int focusReserve=request.Focus is { Focused: false }?0:1;
            // Each admitted held key reserves a release, plus one final focus
            // loss. A paste is admitted as one complete request or rejected.
            if(_count+1+held+focusReserve>MaxRequests || _bytes+(long)bytes+(held+focusReserve)*256L>MaxBytes)
                return Task.FromResult(new HostReply { Error="Input busy · input was not queued. Retry when the host resumes.",ErrorCode=HostErrorCode.InputBusy });
            if(key) { if(release) _pressed.Remove(code); else _pressed.Add(code); }
            var completion=new TaskCompletionSource<HostReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(new(request,bytes,completion)); _count++; _bytes+=bytes;
            if(!_running)
            {
                _running=true; _drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
                _=Task.Run(Run);
            }
            return completion.Task;
        }
    }
    internal Task Complete() { lock(_gate) { _completed=true; return _drained.Task; } }
    private async Task Run()
    {
        while(true)
        {
            Entry entry;
            lock(_gate)
            {
                if(_queue.Count==0) { _running=false; _drained.TrySetResult(); return; }
                entry=_queue.Dequeue();
            }
            HostReply reply;
            try { reply=await send(entry.Request).ConfigureAwait(false); }
            // The host answered: this request was rejected, the transport is intact.
            catch(HostRejectedException ex) { reply=new() { Error=ex.Message,ErrorCode=ex.Code }; }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                var code=ex is SessionHostException { Code: not HostErrorCode.None } coded ? coded.Code : ex is OperationCanceledException ? HostErrorCode.Timeout : HostErrorCode.Disconnected;
                reply=new() { Error="Host delivery failed; input may have been sent and was not retried. "+ex.Message,ErrorCode=code };
                lock(_gate)
                {
                    _failure=code;
                    while(_queue.TryDequeue(out var abandoned))
                    { _count--; _bytes-=abandoned.Bytes; abandoned.Completion.TrySetResult(new() { Error="Host delivery failed; pending input was not sent.",ErrorCode=code }); }
                }
            }
            lock(_gate) { _count--; _bytes-=entry.Bytes; }
            entry.Completion.TrySetResult(reply);
        }
    }
}
