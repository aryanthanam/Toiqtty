using System.Diagnostics;
using System.Text.Json;
using Oroterm.Hosting;

internal static class ResizeChecks
{
    public static async Task RunWindows(string executable,Action<bool,string> check,string phase="resize",string? hostExecutable=null)
    {
        string folder=Path.GetFullPath("artifacts/"+phase+"-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            if(hostExecutable is not null) _=SessionHostClient.ConnectOrStart(folder,hostExecutable);
            var start=new ProcessStartInfo(executable) { UseShellExecute=false,WorkingDirectory=Environment.CurrentDirectory };
            start.ArgumentList.Add("--ui-smoke");
            start.Environment["OROTERM_DATA_DIR"]=folder;
            start.Environment["OROTERM_SESSION_UI_PROBE"]=phase;
            start.Environment["OROTERM_NOTIFICATION_SMOKE"]="0";
            start.Environment["OROTERM_SMOKE_EXE"]=Environment.ProcessPath!;
            start.Environment.Remove("NO_COLOR");
            using var window=Process.Start(start)!;
            if(phase=="input-close-native")
            {
                var ready=Stopwatch.StartNew();
                while(!File.Exists(Path.Combine(folder,"session-ui-"+phase+".json")) && !window.HasExited && ready.Elapsed<TimeSpan.FromSeconds(20)) await Task.Delay(5);
                if(!window.CloseMainWindow()) throw new IOException("The isolated native-close fixture has no window.");
            }
            try { await window.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(phase=="scale"?180:60)); }
            catch(TimeoutException) { if(!window.HasExited) { window.Kill(); await window.WaitForExitAsync(); } throw; }
            if(window.ExitCode!=0) throw new IOException($"Resize window exited abnormally ({window.ExitCode}).");
            using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"session-ui-"+phase+".json")));
            if(!report.RootElement.GetProperty("passed").GetBoolean()) throw new IOException(report.RootElement.GetProperty("error").GetString());
            foreach(var item in report.RootElement.GetProperty("checks").EnumerateArray()) check(true,item.GetString()!);
            if(phase is "input-close" or "input-close-native")
            {
                using var identity=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"input-close.json")));
                string pane=identity.RootElement.GetProperty("pane").GetString()!;
                string expected="ACK_"+identity.RootElement.GetProperty("token").GetString()+"_249";
                var host=SessionHostClient.TryConnect(folder) ?? throw new IOException("Closing stopped the session host.");
                bool delivered=false; string lastText=""; var deadline=Stopwatch.StartNew();
                while(deadline.Elapsed<TimeSpan.FromSeconds(10))
                {
                    var reply=await host.CallAsync(new() { Operation="capture",Pane=pane,Numbers=[-1] });
                    if(reply.Binary.Length>0)
                    {
                        var (_,cells)=HostSnapshotCodec.Decode(reply.Binary,[]);
                        lastText=string.Concat(cells.Select(c=>c.GetText()));
                        if(lastText.Contains(expected)) { delivered=true; break; }
                    }
                    await Task.Delay(10);
                }
                File.WriteAllText(Path.Combine(folder,"input-close-last-frame.txt"),lastText);
                check(delivered,"All 250 admitted commands reach the surviving shell after immediate frontend closure");
            }
            Console.WriteLine(phase+" WinUI evidence: "+folder);
        }
        finally { try { SessionHostClient.TryConnect(folder)?.Shutdown(); } catch(IOException) { } }
    }
}
