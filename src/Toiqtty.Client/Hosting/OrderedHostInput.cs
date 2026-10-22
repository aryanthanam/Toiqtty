using System.Text;

namespace Toiqtty.Hosting;

/// <summary>Bounded FIFO admission; acknowledgements never block the caller. An optional
/// open gate is entry 0: input admitted while it runs is delivered after it succeeds, in
/// order, and is answered without sending if it fails or declines.</summary>
internal sealed class OrderedHostInput
{
    internal const int MaxRequests=2048, MaxBytes=8*1024*1024;
    // Slot 0 is ordinary input. Withdrawn entries stay in the queue, already answered and
    // uncounted, and are skipped when dequeued. Both flags change only under _gate.
    private sealed class Entry(HostRequest request,int bytes,TaskCompletionSource<HostReply> completion,int slot)
    { public readonly HostRequest Request=request; public readonly int Bytes=bytes,Slot=slot; public readonly TaskCompletionSource<HostReply> Completion=completion; public bool Withdrawn,Taken; }
    private readonly Func<HostRequest,Task<HostReply>> _send;
    private readonly Lock _gate =new();
    private readonly Queue<Entry> _queue=[];
    private readonly HashSet<uint> _pressed=[];
    private int _count,_bytes,_slotted;
    private bool _running,_completed;
    // None while deliverable; otherwise the transport code that closed delivery.
    private HostErrorCode _failure;
    private string _failureText="Host delivery failed; input was not retried.";
    // Runs once before any request; false means the target was closed before it opened.
    private Func<Task<bool>>? _open;
    private TaskCompletionSource _drained=Ready();
    internal OrderedHostInput(Func<HostRequest,Task<HostReply>> send,Func<Task<bool>>? open=null)
    {
        _send=send;
        if(open is null) return;
        _open=open; _running=true; _drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Task.Run(Run);
    }
    internal int Pending { get { lock(_gate) return _count; } }
    internal int PendingBytes { get { lock(_gate) return _bytes; } }
    private static TaskCompletionSource Ready() { var result=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
    internal Task Drain { get { lock(_gate) return _drained.Task; } }
    /// <summary>payloadBytes is the serialized size of an object payload (Settings) that the
    /// caller already measured; nothing is serialized here, on the caller's thread.</summary>
    internal Task<HostReply> Enqueue(HostRequest request,long payloadBytes=0) => Enqueue(request,payloadBytes,0,false,default);
    /// <summary>Queues a slotted request (find). A superseding one first withdraws every unsent
    /// request of its slot, which completes as cancelled. Cancelling the token withdraws this one
    /// while it is unsent; a request already sent is never undone.</summary>
    internal Task<HostReply> EnqueueSuperseding(HostRequest request,int slot,bool supersede,CancellationToken cancellation)
    {
        System.Diagnostics.Debug.Assert(slot>0 && request.Key is null && request.Focus is null,"Slotted requests carry no held-key or focus reserve.");
        return cancellation.IsCancellationRequested ? Task.FromCanceled<HostReply>(cancellation) : Enqueue(request,0,slot,supersede,cancellation);
    }
    private Task<HostReply> Enqueue(HostRequest request,long payloadBytes,int slot,bool supersede,CancellationToken cancellation)
    {
        System.Diagnostics.Debug.Assert(request.Layout is null && (request.Settings is null || payloadBytes>0),"Queued object payloads arrive with their measured size.");
        long cost=128L+Encoding.UTF8.GetByteCount(request.Text)+payloadBytes;
        if((request.DropQuery?.Items ?? request.DropCommit?.Items) is { } items) foreach(var item in items) cost+=Encoding.UTF8.GetByteCount(item.Key)+item.Value.LongLength;
        if(cost>MaxBytes) return Task.FromResult(new HostReply { Error="Input busy · the complete request exceeds the queue limit.",ErrorCode=HostErrorCode.InputBusy });
        int bytes=(int)cost;
        Entry entry;
        lock(_gate)
        {
            // Synthetic replies are coded like host replies: a closing session is
            // ShuttingDown, a failed channel repeats the transport code that closed it.
            if(_failure!=HostErrorCode.None) return Task.FromResult(new HostReply { Error=_failureText,ErrorCode=_failure });
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
            if(supersede && _slotted>0) foreach(var queued in _queue) if(queued.Slot==slot && !queued.Withdrawn && !queued.Taken) Withdraw(queued);
            var completion=new TaskCompletionSource<HostReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(entry=new(request,bytes,completion,slot)); _count++; _bytes+=bytes;
            if(slot!=0) _slotted++;
            if(!_running)
            {
                _running=true; _drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
                _=Task.Run(Run);
            }
        }
        // Rare (find only): the registration lives until the request is answered.
        if(cancellation.CanBeCanceled)
        {
            var registration=cancellation.Register(static state => { var (input,queued)=((OrderedHostInput,Entry))state!; lock(input._gate) if(!queued.Withdrawn && !queued.Taken) input.Withdraw(queued); },(this,entry));
            _=entry.Completion.Task.ContinueWith(static (_,state) => ((CancellationTokenRegistration)state!).Dispose(),registration,TaskScheduler.Default);
        }
        return entry.Completion.Task;
    }
    // Under _gate, for an entry still queued: answered as cancelled and no longer counted.
    private void Withdraw(Entry entry)
    { entry.Withdrawn=true; _count--; _bytes-=entry.Bytes; _slotted--; entry.Completion.TrySetCanceled(); }
    // Under _gate: the next entry still owed an answer. It stays counted until answered.
    private bool TryTake(out Entry entry)
    {
        while(_queue.TryDequeue(out entry!))
        {
            if(entry.Withdrawn) continue;
            entry.Taken=true; if(entry.Slot!=0) _slotted--;
            return true;
        }
        return false;
    }
    internal Task Complete() { lock(_gate) { _completed=true; return _drained.Task; } }
    private async Task Run()
    {
        if(_open is { } open)
        {
            _open=null;
            HostErrorCode code=HostErrorCode.ShuttingDown; string text="Input delivery is closed; input was not sent.";
            try { if(await open().ConfigureAwait(false)) code=HostErrorCode.None; }
            catch(Exception ex)
            {
                code=ex switch { SessionHostException { Code: not HostErrorCode.None } coded => coded.Code, OperationCanceledException => HostErrorCode.Timeout,
                    IOException => HostErrorCode.Disconnected, _ => HostErrorCode.ShellStartFailed };
                text=ex.Message;
            }
            if(code!=HostErrorCode.None) Abandon(code,text,code!=HostErrorCode.ShuttingDown);
        }
        while(true)
        {
            Entry entry;
            lock(_gate)
            {
                if(!TryTake(out entry)) { _running=false; _drained.TrySetResult(); return; }
            }
            HostReply reply;
            try { reply=await _send(entry.Request).ConfigureAwait(false); }
            // The host answered: this request was rejected, the transport is intact.
            catch(HostRejectedException ex) { reply=new() { Error=ex.Message,ErrorCode=ex.Code }; }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                var code=ex is SessionHostException { Code: not HostErrorCode.None } coded ? coded.Code : ex is OperationCanceledException ? HostErrorCode.Timeout : HostErrorCode.Disconnected;
                reply=new() { Error="Host delivery failed; input may have been sent and was not retried. "+ex.Message,ErrorCode=code };
                lock(_gate)
                {
                    _failure=code;
                    while(TryTake(out var abandoned))
                    { _count--; _bytes-=abandoned.Bytes; abandoned.Completion.TrySetResult(new() { Error="Host delivery failed; pending input was not sent.",ErrorCode=code }); }
                }
            }
            lock(_gate) { _count--; _bytes-=entry.Bytes; }
            entry.Completion.TrySetResult(reply);
        }
    }
    // The gate failed or declined: nothing queued behind it is sent. A failure also
    // answers later input with the same code and text, as a failed channel does.
    private void Abandon(HostErrorCode code,string text,bool failed)
    {
        lock(_gate)
        {
            if(failed) { _failure=code; _failureText=text; }
            else _completed=true;
            while(TryTake(out var abandoned))
            { _count--; _bytes-=abandoned.Bytes; abandoned.Completion.TrySetResult(new() { Error=text,ErrorCode=code }); }
        }
    }
}
