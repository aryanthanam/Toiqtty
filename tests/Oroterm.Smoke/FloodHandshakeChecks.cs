using System.Diagnostics;
using Oroterm.Terminal;

internal static class FloodHandshakeChecks
{
    internal static async Task Run(Action<bool,string> check)
    {
        await using var shell=new TerminalSession($"\"{Environment.ProcessPath}\" --performance-client",Environment.CurrentDirectory);
        async Task Wait(Func<bool> condition)
        {
            var timer=Stopwatch.StartNew();
            while(!condition()) { if(timer.Elapsed.TotalSeconds>5) throw new TimeoutException("Flood handshake timed out: "+shell.Title); await Task.Delay(10); }
        }
        void Command(string text) { shell.Paste(text); shell.Key(13,0); }
        await Wait(()=>shell.Performance.BytesRead>0);
        string run=Guid.NewGuid().ToString("N");
        Command("flood2 200 1024 "+run);
        await Wait(()=>shell.Title.StartsWith("FLOOD_DONE_"+run+"_",StringComparison.Ordinal));
        check(true,"A timed output client records its own flood completion");
        Command("ping2 late "+Stopwatch.GetTimestamp());
        await Wait(()=>shell.Title=="PING_late");
        check(true,"A late input response reproduces replacement of the earlier flood-completion title");
        Command("flood-status "+run);
        await Wait(()=>shell.Title.StartsWith("FLOOD_DONE_"+run+"_",StringComparison.Ordinal));
        check(true,"An explicit status query recovers sticky completion after late input without depending on an intermediate title");
    }
}
