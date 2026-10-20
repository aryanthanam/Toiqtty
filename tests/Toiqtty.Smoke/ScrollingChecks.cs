using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class ScrollingChecks
{
    public static void Run(Action<bool,string> check)
    {
        var wheel = new WheelScroll();
        check(wheel.Consume(30) == 0 && wheel.Consume(30) == 0 && wheel.Consume(60) == 1,
            "partial wheel deltas accumulate into one notch rather than disappearing");
        check(wheel.Consume(-360) == -3, "multi-notch downward wheel events retain their full distance");
        check(wheel.Consume(60) == 0 && wheel.Consume(-60) == 0 && wheel.Consume(-60) == -1,
            "reversing the wheel discards the previous direction's partial movement");
        wheel.Consume(60); wheel.Reset();
        check(wheel.Consume(60) == 0 && wheel.Consume(0) == 0,
            "configuration resets clear partial movement and zero wheel events do nothing");
        using var terminal = new GhosttyTerminal(40, 5);
        void Write(string text) { var bytes=Encoding.UTF8.GetBytes(text); terminal.Write(bytes,bytes.Length); }
        Write(string.Concat(Enumerable.Range(1,300).Select(i=>$"SCROLL_ROW_{i:D3}\r\n")));
        var bottom=terminal.ReadScrollState();
        check(bottom.Visible==5 && bottom.Maximum>=295 && bottom.Offset==bottom.Maximum,"Ghostty scrollbar reports retained history and the live bottom");
        terminal.ScrollTo(0);
        check(terminal.ReadScrollState().Offset==0,"absolute row zero reaches the oldest retained history");
        var cells=new TerminalCell[200]; var snapshot=terminal.ReadSnapshot(cells);
        check(string.Concat(cells.Select(c=>c.GetText())).Contains("SCROLL_ROW_001") && snapshot.Scrollbar?.Offset==0,"the rendered snapshot and scrollbar use the same history coordinates");
        terminal.ScrollTo(100); terminal.Scroll(-7);
        check(terminal.ReadScrollState().Offset==93,"absolute thumb positioning and relative wheel scrolling share one viewport");
        terminal.ScrollTo(ulong.MaxValue);
        check(terminal.ReadScrollState().Offset==bottom.Maximum,"out-of-range scrollbar positions clamp to the bottom");
        terminal.ScrollTo(20); Write("\x1b[?1049h"); terminal.ScrollTo(1000);
        check(terminal.ReadScrollState() is { Maximum:0, Offset:0 },"the alternate screen has no fake scrollback or scrollable thumb");
        Write("\x1b[?1049l");
        check(terminal.ReadScrollState().Maximum==bottom.Maximum,"leaving the alternate screen restores primary-screen history");
        terminal.SetScrollbackLimits(0,null);
        check(terminal.ReadScrollState().Maximum==0,"clearing history updates scrollbar bounds immediately");
        Write("\x1b[?1000h\x1b[?1006h");
        check(terminal.EncodeMouse(0,3,0,8,8) is { Length:>0 },"Ghostty still encodes middle-button events for mouse-reporting terminal apps");
        var reply=JsonSerializer.Deserialize<HostReply>(JsonSerializer.Serialize(new HostReply { Scrollbar=bottom }))!;
        check(reply.Scrollbar==bottom && JsonSerializer.Deserialize<HostReply>("{\"Protocol\":2}")!.Scrollbar is null,
            "scrollbar metadata round-trips while older host replies remain compatible");

        var scroll=new MiddleClickScroll(); scroll.Begin(100,0);
        check(scroll.Tick(100,20)==0 && scroll.Active,"a stationary middle click never emits Scroll(0) or jumps to the bottom");
        scroll.Move(107); check(scroll.Tick(200,20)==0,"small pointer movement stays inside the autoscroll dead zone");
        scroll.Release(); scroll.Move(160);
        check(scroll.Latched && scroll.Tick(300,20)>0,"click-and-release latches autoscroll and moving below the anchor scrolls down");
        scroll.Move(40); check(scroll.Tick(400,20)<0,"moving above the anchor reverses direction immediately");
        scroll.Stop(); check(!scroll.Active && scroll.Tick(1000,20)==0,"cancelling autoscroll clears both activity and fractional movement");
        scroll.Begin(100,1000); scroll.Move(180); scroll.Release();
        check(!scroll.Active && !scroll.Latched,"holding middle and dragging ends autoscroll when the button is released");
        scroll.Begin(100,0); scroll.Move(10000);
        check(scroll.Tick(60000,20)==18,"speed and elapsed-time caps prevent huge jumps after a stalled frame");
        scroll.Begin(100,0); scroll.Move(100+MiddleClickScroll.DeadZone+.2);
        int slow=0; for(int i=1;i<=1000;i++) slow+=scroll.Tick(i*33,20);
        check(slow>0,"fractional rows accumulate so slow autoscrolling does not stall");
    }

    public static async Task RunWindows(string executable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/scrolling-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var start=new ProcessStartInfo(executable) { UseShellExecute=false,WorkingDirectory=Environment.CurrentDirectory };
            start.ArgumentList.Add("--ui-smoke");
            start.Environment["TOIQTTY_DATA_DIR"]=folder;
            start.Environment["TOIQTTY_SESSION_UI_PROBE"]="scrolling";
            start.Environment["TOIQTTY_NOTIFICATION_SMOKE"]="0";
            start.Environment["TOIQTTY_SMOKE_EXE"]=Environment.ProcessPath!;
            start.Environment.Remove("NO_COLOR");
            using var window=Process.Start(start)!;
            try { await window.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            catch(TimeoutException) { if(!window.HasExited) { window.Kill(); await window.WaitForExitAsync(); } throw; }
            if(window.ExitCode!=0) throw new IOException($"Scrolling window exited abnormally ({window.ExitCode}).");
            using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"session-ui-scrolling.json")));
            if(!report.RootElement.GetProperty("passed").GetBoolean()) throw new IOException(report.RootElement.GetProperty("error").GetString());
            foreach(var item in report.RootElement.GetProperty("checks").EnumerateArray()) check(true,item.GetString()!);
            Console.WriteLine("Scrolling WinUI evidence: "+folder);
        }
        finally { try { SessionHostClient.TryConnect(folder)?.Shutdown(); } catch(IOException) { } }
    }
}
