using System.Text;

namespace Oroterm.Hosting;

/// <summary>Bounded FIFO admission; acknowledgements never block the caller.</summary>
internal sealed class OrderedHostInput(Func<HostRequest,Task<HostReply>> send)
{
    internal const int MaxRequests=2048, MaxBytes=8*1024*1024;
    private sealed record Entry(HostRequest Request,int Bytes,TaskCompletionSource<HostReply> Completion);
    private readonly object _gate=new();
    private readonly Queue<Entry> _queue=[];
    private readonly HashSet<long> _pressed=[];
    private int _count,_bytes;
    private bool _running,_completed,_failed;
    private TaskCompletionSource _drained=Ready();
    internal int Pending { get { lock(_gate) return _count; } }
    internal int PendingBytes { get { lock(_gate) return _bytes; } }
    private static TaskCompletionSource Ready() { var result=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
    internal Task Drain { get { lock(_gate) return _drained.Task; } }
    internal Task<HostReply> Enqueue(HostRequest request)
    {
        long cost=128L+Encoding.UTF8.GetByteCount(request.Text)+request.Numbers.LongLength*8+request.Images.LongLength*8;
        if(request.Items is not null) foreach(var item in request.Items) cost+=Encoding.UTF8.GetByteCount(item.Key)+item.Value.LongLength;
        if(request.Settings is not null) cost+=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request.Settings).LongLength;
        if(request.Layout is not null) cost+=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request.Layout).LongLength;
        if(cost>MaxBytes) return Task.FromResult(new HostReply { Error="Input busy · the complete request exceeds the queue limit." });
        int bytes=(int)cost;
        lock(_gate)
        {
            if(_completed || _failed) return Task.FromResult(new HostReply { Error="Input delivery is closed; input was not retried." });
            bool key=request.Operation is "key" or "copy-key" && request.Numbers.Length>=3 && request.Numbers[0]!=0;
            long code=key?request.Numbers[0]:0;
            bool release=key && request.Numbers[2]==0;
            int held=_pressed.Count+(key && !release && !_pressed.Contains(code)?1:0)-(release && _pressed.Contains(code)?1:0);
            int focusReserve=request.Operation=="focus" && request.Numbers.FirstOrDefault()==0?0:1;
            // Each admitted held key reserves a release, plus one final focus
            // loss. A paste is admitted as one complete request or rejected.
            if(_count+1+held+focusReserve>MaxRequests || _bytes+(long)bytes+(held+focusReserve)*256L>MaxBytes)
                return Task.FromResult(new HostReply { Error="Input busy · input was not queued. Retry when the host resumes." });
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
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                reply=new() { Error="Host delivery failed; input may have been sent and was not retried. "+ex.Message };
                lock(_gate)
                {
                    _failed=true;
                    while(_queue.TryDequeue(out var abandoned))
                    { _count--; _bytes-=abandoned.Bytes; abandoned.Completion.TrySetResult(new() { Error="Host delivery failed; pending input was not sent." }); }
                }
            }
            lock(_gate) { _count--; _bytes-=entry.Bytes; }
            entry.Completion.TrySetResult(reply);
        }
    }
}
