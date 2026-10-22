using System.Diagnostics;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class InputDispatchChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent=new List<HostRequest>();
        var queue=new OrderedHostInput(async request=> { await gate.Task; lock(sent) sent.Add(request); return new(); });
        var watch=Stopwatch.StartNew();
        var pending=Enumerable.Range(0,500).Select(i=>queue.Enqueue(HostRequests.Paste("p",i.ToString()))).ToArray();
        watch.Stop();
        check(watch.ElapsedMilliseconds<500 && pending.All(t=>!t.IsCompleted),"500 input admissions return while the transport is deliberately stalled");
        gate.SetResult(); await Task.WhenAll(pending); await queue.Drain;
        check(sent.Select(r=>r.Text).SequenceEqual(Enumerable.Range(0,500).Select(i=>i.ToString())),"Acknowledged input reaches the host in exact FIFO order");
        check(queue.Pending==0 && queue.PendingBytes==0,"Input accounting returns to zero after acknowledgements");

        var paused=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bounded=new OrderedHostInput(async _=> { await paused.Task; return new(); });
        var admitted=new List<Task<HostReply>> { bounded.Enqueue(HostRequests.Key("p",65,0)) };
        for(int i=0;i<OrderedHostInput.MaxRequests+5;i++) admitted.Add(bounded.Enqueue(HostRequests.Select("p",-1,-1)));
        check(admitted.Any(t=>t.IsCompleted && t.Result.Error is not null) && bounded.Pending<OrderedHostInput.MaxRequests,"An overloaded queue rejects admission and stays bounded");
        var release=bounded.Enqueue(HostRequests.Key("p",65,0,action:TerminalKeyAction.Release));
        var focus=bounded.Enqueue(HostRequests.Focus("p",false));
        check(!release.IsCompleted && !focus.IsCompleted && bounded.Pending<=OrderedHostInput.MaxRequests,"Held-key releases and focus loss retain reserved admission under backpressure");
        paused.SetResult(); await Task.WhenAll(admitted.Append(release).Append(focus)); await bounded.Drain;
        check((await bounded.Enqueue(HostRequests.Paste("p","retry"))).Error is null,"Admission resumes after recoverable backpressure has drained");

        int pasteCalls=0;
        var atomic=new OrderedHostInput(_=> { Interlocked.Increment(ref pasteCalls); return Task.FromResult(new HostReply()); });
        var oversized=await atomic.Enqueue(HostRequests.Paste("p",new string('x',OrderedHostInput.MaxBytes)));
        check(oversized.Error is not null && pasteCalls==0,"An oversized paste is rejected whole before any transport write");
        var largeItems=await atomic.Enqueue(HostRequests.DropQuery("p",1,0,0,1,new Dictionary<string,byte[]> { ["text/plain"]=new byte[OrderedHostInput.MaxBytes] }));
        check(largeItems.Error is not null && pasteCalls==0,"Binary input representations also obey the queue's byte budget");
        var accepted=await atomic.Enqueue(HostRequests.Paste("p","bracketed paste café 中🙂"));
        check(accepted.Error is null && pasteCalls==1,"An admitted paste remains one host request");

        int attempts=0;
        var uncertain=new OrderedHostInput(_=> { Interlocked.Increment(ref attempts); throw new IOException("Lost acknowledgement"); });
        var failed=await uncertain.Enqueue(HostRequests.Key("p",13,0));
        var later=await uncertain.Enqueue(HostRequests.Paste("p","must-not-replay"));
        check(attempts==1 && failed.Error is not null && later.Error is not null,"An uncertain transport failure stops admission and never replays input");
        check(failed.ErrorCode==HostErrorCode.Disconnected && later.ErrorCode==HostErrorCode.Disconnected,"A raw transport failure and the admissions it closes are coded Disconnected");
        var changed=new OrderedHostInput(_=>Task.FromException<HostReply>(new HostTransportException(HostErrorCode.HostChanged,"replaced")));
        var timedOut=new OrderedHostInput(_=>Task.FromException<HostReply>(new OperationCanceledException()));
        check((await changed.Enqueue(HostRequests.Key("p",13,0))).ErrorCode==HostErrorCode.HostChanged && (await changed.Enqueue(HostRequests.Paste("p","x"))).ErrorCode==HostErrorCode.HostChanged
            && (await timedOut.Enqueue(HostRequests.Key("p",13,0))).ErrorCode==HostErrorCode.Timeout && (await timedOut.Enqueue(HostRequests.Paste("p","x"))).ErrorCode==HostErrorCode.Timeout,
            "A failed channel keeps answering with the transport code that closed it");
        await atomic.Complete();
        var late=await atomic.Enqueue(HostRequests.Paste("p","late"));
        check(late.Error is not null && late.ErrorCode==HostErrorCode.ShuttingDown,"A completed dispatcher rejects new input as ShuttingDown");
        check(oversized.ErrorCode==HostErrorCode.InputBusy && admitted.First(t=>t.IsCompleted && t.Result.Error is not null).Result.ErrorCode==HostErrorCode.InputBusy,
            "Admission rejections are coded InputBusy, so the session's busy notice clears on the code");
        // Held-key and focus reserves come from the typed payloads: a copy-or-key press
        // is a held key like any other, and only a focus loss skips its own reserve.
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var typed=new OrderedHostInput(async _=> { await held.Task; return new(); });
        var pressed=typed.Enqueue(HostRequests.CopyOrKey("p",67,2,"c",TerminalKeyAction.Press,null,0));
        var filler=new List<Task<HostReply>>();
        for(int i=0;i<OrderedHostInput.MaxRequests;i++) filler.Add(typed.Enqueue(HostRequests.Scroll("p",1)));
        int admittedFiller=filler.Count(t=>!t.IsCompleted);
        var copyRelease=typed.Enqueue(HostRequests.Key("p",67,2,"c",TerminalKeyAction.Release));
        var focusLoss=typed.Enqueue(HostRequests.Focus("p",false));
        check(admittedFiller==OrderedHostInput.MaxRequests-3 && !copyRelease.IsCompleted && !focusLoss.IsCompleted,
            "A copy-or-key press reserves its release and the final focus loss from typed key and focus arguments");
        held.SetResult(); await Task.WhenAll(filler.Append(pressed).Append(copyRelease).Append(focusLoss)); await typed.Drain;

        // Find: a new search withdraws every unsent find, steps keep their order, other input is untouched,
        // and cancellation withdraws only what is still queued.
        var stalled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finds=new List<HostRequest>();
        var sending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finding=new OrderedHostInput(async request=> { sending.TrySetResult(); await stalled.Task; lock(finds) finds.Add(request); return new(); });
        var inFlight=finding.EnqueueSuperseding(HostRequests.Find("p","a",0),1,true,default);
        await sending.Task;
        var key=finding.Enqueue(HostRequests.Key("p",65,0));
        var olderSearch=finding.EnqueueSuperseding(HostRequests.Find("p","ab",0),1,true,default);
        var olderStep=finding.EnqueueSuperseding(HostRequests.Find("p","ab",1),1,false,default);
        using var closing=new CancellationTokenSource();
        var newest=finding.EnqueueSuperseding(HostRequests.Find("p","abc",0),1,true,default);
        var step1=finding.EnqueueSuperseding(HostRequests.Find("p","abc",1),1,false,default);
        var step2=finding.EnqueueSuperseding(HostRequests.Find("p","abc",1),1,false,closing.Token);
        var cancelled=finding.EnqueueSuperseding(HostRequests.Find("p","abc",-1),1,false,closing.Token);
        closing.Cancel();
        check(olderSearch.IsCanceled && olderStep.IsCanceled && cancelled.IsCanceled && step2.IsCanceled && !inFlight.IsCompleted && !newest.IsCompleted && !step1.IsCompleted && finding.Pending==4,
            "A new search supersedes unsent finds and cancellation withdraws queued ones without touching the sent find or other input");
        var afterClose=finding.EnqueueSuperseding(HostRequests.Find("p","",0),1,true,closing.Token);
        check(afterClose.IsCanceled,"A find admitted with an already cancelled token is never queued");
        stalled.SetResult(); await Task.WhenAll(inFlight,key,newest,step1); await finding.Drain;
        check(finds.Select(r=>r.Operation+":"+r.Text+":"+(r.Find?.Direction.ToString() ?? "-")).SequenceEqual(["find:a:0","key::-","find:abc:0","find:abc:1"]) && finding.Pending==0 && finding.PendingBytes==0,
            "Only the sent find, other input and the surviving search and step reach the host, in order, and accounting returns to zero");
    }
}
