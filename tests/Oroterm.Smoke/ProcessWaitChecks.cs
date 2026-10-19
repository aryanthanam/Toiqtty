using System.Diagnostics;
using Oroterm.Terminal;

internal static class ProcessWaitChecks
{
    internal static void Run(Action<bool,string> check)
    {
        ThreadPool.GetMinThreads(out int oldMin,out int oldIoMin);
        ThreadPool.GetMaxThreads(out int oldMax,out int oldIoMax);
        var processes=new List<ConPtyProcess>(); var drains=new List<Task>();
        bool admitted=false; double admissionMs=0;
        try
        {
            check(ThreadPool.SetMinThreads(1,oldIoMin) && ThreadPool.SetMaxThreads(4,oldIoMax),"The isolated process-wait fixture constrains its own worker pool");
            for(int i=0;i<8;i++)
            {
                var process=new ConPtyProcess("cmd.exe /Q /C \"ping -n 8 127.0.0.1 >nul & exit /b 37\"",Environment.CurrentDirectory,80,25);
                processes.Add(process); drains.Add(process.Output.CopyToAsync(Stream.Null));
            }
            var watch=Stopwatch.StartNew();
            var queued=Task.Run(()=>Stopwatch.GetTimestamp());
            admitted=queued.Wait(TimeSpan.FromSeconds(1)); admissionMs=watch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            ThreadPool.SetMaxThreads(oldMax,oldIoMax); ThreadPool.SetMinThreads(oldMin,oldIoMin);
        }
        try
        {
            check(admitted,"Eight live shells with pending output reads leave workers available for newly queued input work");
            Console.WriteLine($"Worker admission with eight live shells and output readers: {admissionMs:F2}ms");
            check(Task.WhenAll(processes.Select(p=>p.ExitTask)).Wait(TimeSpan.FromSeconds(15)) && processes.All(p=>p.ExitTask.Result==37),
                "Registered process waits preserve each naturally exiting shell's exit code");
            check(Task.WhenAll(drains).Wait(TimeSpan.FromSeconds(10)),"Natural exit closes ConPTY and drains all output without a blocked worker wait");
        }
        finally { foreach(var process in processes) process.Dispose(); }
        check(Task.WhenAll(processes.Select(p=>p.Closed)).Wait(TimeSpan.FromSeconds(10)),"All process and pseudoconsole handles finish teardown after exit");
        var forced=new List<ConPtyProcess>(); var forcedDrains=new List<Task>();
        try
        {
            for(int i=0;i<8;i++)
            {
                var process=new ConPtyProcess("cmd.exe /Q /K",Environment.CurrentDirectory,80,25);
                forced.Add(process); forcedDrains.Add(process.Output.CopyToAsync(Stream.Null));
            }
            Parallel.ForEach(forced,process=> { process.Dispose(); process.Dispose(); });
            check(Task.WhenAll(forced.Select(p=>p.ExitTask)).Wait(TimeSpan.FromSeconds(15)),"Explicit concurrent closure completes all registered shell-exit waits");
            check(Task.WhenAll(forced.Select(p=>p.Closed).Concat(forcedDrains)).Wait(TimeSpan.FromSeconds(15)),
                "Concurrent disposal closes pseudoconsoles and drains output without losing or double-closing process handles");
        }
        finally { foreach(var process in forced) process.Dispose(); }
    }
}
