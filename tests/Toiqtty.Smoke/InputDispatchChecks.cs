using System.Diagnostics;
using Toiqtty.Hosting;

internal static class InputDispatchChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent=new List<HostRequest>();
        var queue=new OrderedHostInput(async request=> { await gate.Task; lock(sent) sent.Add(request); return new(); });
        var watch=Stopwatch.StartNew();
        var pending=Enumerable.Range(0,500).Select(i=>queue.Enqueue(new() { Operation="paste",Text=i.ToString() })).ToArray();
        watch.Stop();
        check(watch.ElapsedMilliseconds<500 && pending.All(t=>!t.IsCompleted),"500 input admissions return while the transport is deliberately stalled");
        gate.SetResult(); await Task.WhenAll(pending); await queue.Drain;
        check(sent.Select(r=>r.Text).SequenceEqual(Enumerable.Range(0,500).Select(i=>i.ToString())),"Acknowledged input reaches the host in exact FIFO order");
        check(queue.Pending==0 && queue.PendingBytes==0,"Input accounting returns to zero after acknowledgements");

        var paused=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bounded=new OrderedHostInput(async _=> { await paused.Task; return new(); });
        var admitted=new List<Task<HostReply>> { bounded.Enqueue(new() { Operation="key",Numbers=[65,0,1] }) };
        for(int i=0;i<OrderedHostInput.MaxRequests+5;i++) admitted.Add(bounded.Enqueue(new() { Operation="select",Numbers=[-1,-1] }));
        check(admitted.Any(t=>t.IsCompleted && t.Result.Error is not null) && bounded.Pending<OrderedHostInput.MaxRequests,"An overloaded queue rejects admission and stays bounded");
        var release=bounded.Enqueue(new() { Operation="key",Numbers=[65,0,0] });
        var focus=bounded.Enqueue(new() { Operation="focus",Numbers=[0] });
        check(!release.IsCompleted && !focus.IsCompleted && bounded.Pending<=OrderedHostInput.MaxRequests,"Held-key releases and focus loss retain reserved admission under backpressure");
        paused.SetResult(); await Task.WhenAll(admitted.Append(release).Append(focus)); await bounded.Drain;
        check((await bounded.Enqueue(new() { Operation="paste",Text="retry" })).Error is null,"Admission resumes after recoverable backpressure has drained");

        int pasteCalls=0;
        var atomic=new OrderedHostInput(_=> { Interlocked.Increment(ref pasteCalls); return Task.FromResult(new HostReply()); });
        var oversized=await atomic.Enqueue(new() { Operation="paste",Text=new string('x',OrderedHostInput.MaxBytes) });
        check(oversized.Error is not null && pasteCalls==0,"An oversized paste is rejected whole before any transport write");
        var largeItems=await atomic.Enqueue(new() { Operation="drop",Items=new() { ["text/plain"]=new byte[OrderedHostInput.MaxBytes] } });
        check(largeItems.Error is not null && pasteCalls==0,"Binary input representations also obey the queue's byte budget");
        var accepted=await atomic.Enqueue(new() { Operation="paste",Text="bracketed paste café 中🙂" });
        check(accepted.Error is null && pasteCalls==1,"An admitted paste remains one host request");

        int attempts=0;
        var uncertain=new OrderedHostInput(_=> { Interlocked.Increment(ref attempts); throw new IOException("Lost acknowledgement"); });
        var failed=await uncertain.Enqueue(new() { Operation="key",Numbers=[13,0,1] });
        var later=await uncertain.Enqueue(new() { Operation="paste",Text="must-not-replay" });
        check(attempts==1 && failed.Error is not null && later.Error is not null,"An uncertain transport failure stops admission and never replays input");
        await atomic.Complete();
        check((await atomic.Enqueue(new() { Operation="paste",Text="late" })).Error is not null,"A completed dispatcher rejects new input");
        var legacy=System.Text.Json.JsonSerializer.Deserialize<HostReply>("{\"Protocol\":2}")!;
        check(legacy.Capabilities.Length==0,"Older hosts remain distinguishable without changing the version-2 protocol");
    }
}
