using System.Diagnostics;
using System.Text;

internal static class PerformanceClient
{
    public static void Run()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var output = Console.OpenStandardOutput(); var gate = new object(); bool completed = false;
        string? traceFolder=Environment.GetEnvironmentVariable("OROTERM_PERFORMANCE_COMMAND_TRACE")=="1"
            ?Environment.GetEnvironmentVariable("OROTERM_DATA_DIR"):null;
        string? trace=traceFolder is null?null:Path.Combine(traceFolder,$"performance-commands-{Environment.ProcessId}.jsonl");
        var traceGate=new object();
        void FloodTrace(string kind,string run,long bytes=0,double elapsedMs=0,double longestWriteMs=0,string? error=null)
        {
            if(traceFolder is null) return;
            lock(traceGate) File.AppendAllText(Path.Combine(traceFolder,$"performance-floods-{Environment.ProcessId}.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new { timestamp=DateTimeOffset.Now,kind,run,bytes,elapsedMs,longestWriteMs,error })+Environment.NewLine);
        }
        string completedRun=""; long completedBytes=0;
        void Write(string text) { lock (gate) { output.Write(Encoding.UTF8.GetBytes(text)); output.Flush(); } }
        Write("PERF_READY PID="+Environment.ProcessId+"\r\n");
        while (Console.ReadLine() is { } line)
        {
            long received=Stopwatch.GetTimestamp();
            if(trace is not null) File.AppendAllText(trace,System.Text.Json.JsonSerializer.Serialize(new { received,line })+Environment.NewLine);
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "ping") { lock (gate) { if (!completed) Write("\x1b]2;PING_" + parts[1] + "\x07"); } }
            else if(parts.Length==3 && parts[0]=="ping2" && long.TryParse(parts[2],out long origin))
            {
                lock(gate)
                {
                    long sending=Stopwatch.GetTimestamp();
                    string marker=$"PONG_{parts[1]}_{(long)Stopwatch.GetElapsedTime(origin,received).TotalMicroseconds:X}_{(long)Stopwatch.GetElapsedTime(origin,sending).TotalMicroseconds:X}";
                    Write("\x1b]2;PING_"+parts[1]+"\x07\u001b7\x1b[2;1H\x1b[2K"+marker+"\u001b8");
                }
            }
            else if(parts.Length==2 && parts[0]=="flood-status")
            {
                lock(gate)
                {
                    FloodTrace(completed && completedRun==parts[1]?"status-done":"status-running",parts[1],completedBytes);
                    Write("\x1b]2;"+(completed && completedRun==parts[1]?$"FLOOD_DONE_{completedRun}_{completedBytes}":"FLOOD_RUNNING_"+parts[1])+"\x07");
                }
            }
            else if(parts.Length==1 && parts[0]=="scale-image")
                Write("\u001b7\x1b[3;1HUnicode café 中🙂\x1b[4;2H\x1b_Ga=T,f=32,s=1,v=1,i=1002,q=2,c=2,r=2,C=1;/wAA/w==\x1b\\\u001b8");
            else if(parts.Length==2 && parts[0]=="scene" && int.TryParse(parts[1],out int sequence))
            {
                Write($"\x1b[r\x1b[2J\x1b[HSOAK_{sequence}\r\n\r\nUnicode café 中🙂 العربية\r\n\x1b[1;4;38;2;110;180;240mBold underline != =>\x1b[0m\r\n"
                    +"\x1b[41m red \x1b[44m blue \x1b[0m\r\n\x1b[7;1H\x1b_Ga=d,d=A,q=2\x1b\\"
                    +$"\x1b_Ga=T,f=32,s=1,v=1,i=1001,q=2,c=3,r=2,C=1;/wAA/w==\x1b\\\x1b[10;1H\x1b]2;SCENE_{sequence}\x07");
            }
            else if (parts.Length >= 2 && parts[0] is "flood" or "flood2" && int.TryParse(parts[1], out int milliseconds))
            {
                milliseconds = Math.Clamp(milliseconds, 100, 60000);
                int columns = Math.Clamp(Console.WindowWidth, 20, 1000);
                bool stable=parts[0]=="flood2";
                string run=parts.Length>=4?parts[3]:"";
                long rate=parts.Length>=3 && long.TryParse(parts[2],out long kib)?Math.Clamp(kib,0,1024*1024)*1024:0;
                if(stable) Write($"\x1b[3;{Math.Max(4,Console.WindowHeight)}r\x1b[3;1H\x1b]2;FLOOD_RUNNING_{run}\x07");
                lock (gate) completed = false;
                FloodTrace("queued",run);
                _ = Task.Run(() =>
                {
                    var timer = Stopwatch.StartNew(); long bytes = 0; uint random = 0x12345678;
                    double longestWriteMs=0; FloodTrace("started",run);
                    try
                    {
                    var buffer = new byte[(columns + 2) * 16];
                    while (timer.ElapsedMilliseconds < milliseconds)
                    {
                        for (int row = 0; row < 16; row++)
                        {
                            int offset = row * (columns + 2);
                            for (int x = 0; x < columns; x++)
                            { random ^= random << 13; random ^= random >> 17; random ^= random << 5; buffer[offset + x] = (byte)('a' + random % 26); }
                            buffer[offset + columns] = 13; buffer[offset + columns + 1] = 10;
                        }
                        long writeStarted=traceFolder is not null?Stopwatch.GetTimestamp():0;
                        lock (gate) { output.Write(buffer); bytes += buffer.Length; }
                        if(traceFolder is not null) longestWriteMs=Math.Max(longestWriteMs,Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds);
                        if(rate>0)
                        {
                            double ahead=bytes*1000d/rate-timer.Elapsed.TotalMilliseconds;
                            if(ahead>=1) Thread.Sleep((int)Math.Min(10,ahead));
                        }
                    }
                    lock (gate) { completed = true; completedRun=run; completedBytes=bytes; Write($"\x1b]2;FLOOD_DONE_{(run.Length>0?run+"_":"")}{bytes}\x07"+(stable?$"\x1b[r\x1b[3;1HPERF_DONE {bytes}":$"\r\nPERF_DONE {bytes}\r\n")); }
                    FloodTrace("done",run,bytes,timer.Elapsed.TotalMilliseconds,longestWriteMs);
                    }
                    catch(Exception ex)
                    {
                        FloodTrace("failed",run,bytes,timer.Elapsed.TotalMilliseconds,longestWriteMs,ex.ToString()); throw;
                    }
                });
            }
            else if (line.Trim() == "exit") return;
        }
    }
}
