using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Oroterm.Hosting;
using Oroterm.Models;

internal static class OutputWatchChecks
{
    internal static async Task Run(string executable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/output-watch-"+Guid.NewGuid().ToString("N"));
        var host=SessionHostClient.ConnectOrStart(folder,executable);
        string pane=Guid.NewGuid().ToString("N"),other=Guid.NewGuid().ToString("N");
        int demand=0,notifications=0,otherNotifications=0,cancellations=0;
        Action<string> listener=_=>Interlocked.Increment(ref notifications);
        Action<string> otherListener=_=>Interlocked.Increment(ref otherNotifications);
        void Thrown(object? _,FirstChanceExceptionEventArgs args)
        { if(args.Exception is OperationCanceledException)Interlocked.Increment(ref cancellations); }
        async Task Wait(Func<bool> condition)
        {
            var elapsed=Stopwatch.StartNew();
            while(!condition()) { if(elapsed.Elapsed.TotalSeconds>5)throw new TimeoutException("Output-watch notification did not arrive.");await Task.Delay(5); }
        }
        async Task Ping(string token)
        {
            await host.CallAsync(new(){Operation="paste",Pane=pane,Text="ping "+token});
            await host.CallAsync(new(){Operation="key",Pane=pane,Numbers=[13,0]});
        }
        try
        {
            string command=$"\"{Environment.ProcessPath}\" --performance-client";
            await host.CallAsync(new(){Operation="create",Pane=pane,Text=command,Directory=Environment.CurrentDirectory,Settings=new AppSettings()});
            host.AddOutputListener(pane,listener,()=>Volatile.Read(ref demand)!=0);
            await Task.Delay(100);
            check(notifications==0,"An output subscriber without demand stays asleep");
            Volatile.Write(ref demand,1);host.OutputDemandChanged();await Wait(()=>Volatile.Read(ref notifications)>0);
            await Task.Delay(100);
            AppDomain.CurrentDomain.FirstChanceException+=Thrown;
            try
            {
                for(int i=0;i<50;i++)
                {
                    int before=Volatile.Read(ref notifications);
                    await Ping("steady_"+i);await Wait(()=>Volatile.Read(ref notifications)>before);
                }
                check(cancellations==0,"Fifty ordinary output replies deliver notifications without canceling independent demand waits");
            }
            finally { AppDomain.CurrentDomain.FirstChanceException-=Thrown; }
            Volatile.Write(ref demand,0);host.OutputDemandChanged();await Task.Delay(100);
            int retired=Volatile.Read(ref notifications);await Ping("hidden");await Task.Delay(100);
            check(notifications==retired,"Retiring demand stops notifications while the shell keeps running");
            Volatile.Write(ref demand,1);host.OutputDemandChanged();await Wait(()=>Volatile.Read(ref notifications)>retired);
            check(true,"Restoring demand wakes the same subscription after an idle interval");
            host.RemoveOutputListener(pane,listener);
            host.AddOutputListener(pane,listener,()=>true);
            int restarted=Volatile.Read(ref notifications);await Ping("restart");await Wait(()=>Volatile.Read(ref notifications)>restarted);
            check(true,"Replacing the final subscriber leaves no abandoned waiter that can steal its notification");
            await host.CallAsync(new(){Operation="create",Pane=other,Text=command,Directory=Environment.CurrentDirectory,Settings=new AppSettings()});
            host.AddOutputListener(other,otherListener,()=>true);await Wait(()=>Volatile.Read(ref otherNotifications)>0);
            check(true,"A newly interested pane interrupts an older watch and receives its first output");
        }
        finally
        {
            host.RemoveOutputListener(pane,listener);host.RemoveOutputListener(other,otherListener);
            host.Shutdown();
        }
        Console.WriteLine("Output-watch evidence: "+folder);
    }
}
