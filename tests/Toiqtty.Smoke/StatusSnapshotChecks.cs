using System.Text.Json;
using System.Diagnostics;
using Toiqtty.Terminal;

internal static class StatusSnapshotChecks
{
    internal static async Task RunSession(Action<bool,string> check)
    {
        await using var session=new TerminalSession($"\"{Environment.ProcessPath}\" --status-snapshot-client",Environment.CurrentDirectory);
        async Task Wait(Func<bool> condition)
        {
            var timer=Stopwatch.StartNew();
            while(!condition()){if(timer.Elapsed.TotalSeconds>10)throw new TimeoutException("Published session status did not become ready.");await Task.Delay(10);}
        }
        await Wait(()=>session.Status.Progress==75 && session.MouseTracking);
        var previous=session.Status;
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var owner=Task.Run(()=>{lock(session.ParserGateForCheck){entered.Set();release.Wait(TimeSpan.FromSeconds(5));}});
        if(!entered.Wait(TimeSpan.FromSeconds(2)))throw new TimeoutException("The parser contention fixture did not start.");
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read=Task.Run(()=>{started.SetResult();return (Status:session.Status,Tracking:session.MouseTracking);});
        bool nonBlocking=false;
        try {await started.Task.WaitAsync(TimeSpan.FromSeconds(2));await read.WaitAsync(TimeSpan.FromMilliseconds(250));nonBlocking=true;}
        catch(TimeoutException){}
        finally{release.Set();await owner;await read;}
        check(nonBlocking && ReferenceEquals(previous,read.Result.Status) && read.Result.Tracking,
            "Status and mouse metadata read the last published state without waiting for a busy parser gate");
        session.Paste("next");session.Key(13,0);
        await Wait(()=>session.Status.Progress==25 && !session.MouseTracking);
        check(previous.Progress==75 && !ReferenceEquals(previous,session.Status),
            "Real ConPTY output publishes fresh status and mouse modes while preserving the previous immutable snapshot");
    }

    internal static void Run(Action<bool,string> check)
    {
        var status=new TerminalStatus();
        status.Apply(new(TerminalEventKind.ProgramStatus,State:1,Id:"work",Title:"Building"));
        status.Apply(new(TerminalEventKind.Notification,Title:"Notice",Body:"Done"));
        status.Apply(new(TerminalEventKind.Progress,State:1,Progress:10));
        status.Apply(new(TerminalEventKind.SemanticPrompt,State:3,Title:"build"));
        var first=status.Snapshot(); long before=GC.GetAllocatedBytesForCurrentThread(); bool reused=true;
        for(int i=0;i<1000;i++) reused &= ReferenceEquals(first,status.Snapshot());
        check(reused && GC.GetAllocatedBytesForCurrentThread()==before,"One thousand unchanged status reads reuse an immutable snapshot without managed allocation");
        status.Apply(new(TerminalEventKind.Progress,State:2,Progress:30)); var updated=status.Snapshot();
        check(!ReferenceEquals(first,updated) && first.Progress==10 && updated.Progress==30,"Status changes publish a fresh snapshot while previous observations remain unchanged");
        bool readOnly=false;
        try { ((IList<TerminalEvent>)updated.Programs)[0]=new(TerminalEventKind.ProgramStatus,Title:"Changed by caller"); }
        catch(NotSupportedException) { readOnly=true; }
        check(readOnly && status.Snapshot().Programs[0].Title=="Building","Consumers cannot mutate a cached program list through its collection interface");
        var arrayReference=new TerminalStatusSnapshot(updated.ProgressState,updated.Progress,updated.CommandRunning,updated.LastExitCode,
            updated.Programs.ToArray(),updated.Notices.ToArray(),updated.Commands.ToArray());
        check(JsonSerializer.Serialize(updated)==JsonSerializer.Serialize(arrayReference),"Read-only status collections preserve the existing session JSON representation");
        status.Finish(9,"Shell ended"); var finished=status.Snapshot();
        check(!finished.CommandRunning && finished.LastExitCode==9 && finished.Commands[0].Finished is not null && first.CommandRunning,
            "Command completion invalidates the status cache without mutating an earlier running snapshot");
        status.Apply(new(TerminalEventKind.Reset)); var reset=status.Snapshot();
        check(reset.Programs.Count==0 && reset.Notices.Count==0 && reset.Commands.Count==0 && finished.Commands.Count==1,
            "Terminal reset clears new status observations and preserves previously published history");
    }
}
