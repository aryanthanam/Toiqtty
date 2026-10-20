using Toiqtty.Terminal;

internal static class ConsoleLifetimeChecks
{
    internal static async Task Run(Action<bool,string> check)
    {
        using var process=new ConPtyProcess("cmd.exe /Q /D /K",Environment.CurrentDirectory,80,24);
        var drain=process.Output.CopyToAsync(Stream.Null);
        using var resizing=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var closing=new ManualResetEventSlim();
        process.BeforeResizeForCheck=()=> {
            resizing.Set();release.Wait(TimeSpan.FromSeconds(5));
            // The failing baseline must never actually call a freed HPCON.
            if(process.Closed.IsCompleted)throw new OperationCanceledException("Fixture prevented stale console-handle use.");
        };
        process.BeforeConsoleCloseForCheck=()=>closing.Set();
        Exception? resizeError=null;
        var resize=Task.Run(()=>{try{process.Resize(100,30);}catch(Exception ex){resizeError=ex;}});
        bool closedEarly=false;
        try
        {
            if(!resizing.Wait(TimeSpan.FromSeconds(2)))throw new TimeoutException("Console resize fixture did not start.");
            process.Dispose();
            if(!closing.Wait(TimeSpan.FromSeconds(2)))throw new TimeoutException("Console close fixture did not start.");
            try{await process.Closed.WaitAsync(TimeSpan.FromMilliseconds(250));closedEarly=true;}catch(TimeoutException){}
        }
        finally
        {
            release.Set();await resize.WaitAsync(TimeSpan.FromSeconds(5));
            process.Dispose();await process.Closed.WaitAsync(TimeSpan.FromSeconds(10));
            try{await drain.WaitAsync(TimeSpan.FromSeconds(5));}catch(IOException){}
            process.CloseOutput();
        }
        check(!closedEarly,"Console destruction waits for an in-flight resize to release its handle lease");
        check(resizeError is null && process.ExitTask.IsCompleted,"Concurrent resize and close retire the owned child and output reader without using a stale handle");
    }
}
