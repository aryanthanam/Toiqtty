using System.Diagnostics;
using System.Text;
using Oroterm.Configuration;
using Oroterm.Models;
using Oroterm.Terminal;

internal static class AppearanceChecks
{
    internal static void Run(Action<bool,string> check)
    {
        using var terminal=new GhosttyTerminal(20,3);
        var cells=new TerminalCell[60];
        void Write(string text) { byte[] bytes=Encoding.UTF8.GetBytes(text); terminal.Write(bytes,bytes.Length); }
        foreach(var (name,style) in new[] { ("bar",0u),("block",1u),("underline",2u),("hollow",3u) })
        {
            terminal.SetCursorDefaults(new() { Style=name,Blink=true });
            var frame=terminal.ReadFrame(cells);
            check(frame.CursorStyle==style && frame.CursorBlinking!=0,"Ghostty applies configured "+name+" cursor and blink defaults");
        }
        Write("\x1b[2 q"); terminal.SetCursorDefaults(new() { Style="bar",Blink=true });
        var overridden=terminal.ReadFrame(cells);
        check(overridden.CursorStyle==1 && overridden.CursorBlinking==0,"Application cursor sequences retain precedence over live configuration changes");
        Write("\x1b[0 q"); var reset=terminal.ReadFrame(cells);
        check(reset.CursorStyle==0 && reset.CursorBlinking!=0,"DECSCUSR reset restores the configured cursor defaults");
        Write("\x1b[38;5;16mA\x1b[38;5;17mB\x1b[38;2;12;34;56mC"); terminal.ReadFrame(cells);
        uint original=cells[0].Foreground;
        var theme=new SessionTheme { Name="Custom test",Indexed="16=#112233 17=#445566",SelectionBackground="#556677",SelectionForeground="#AABBCC" };
        terminal.SetTheme(theme); terminal.ReadFrame(cells);
        check(cells[0].Foreground==0xff112233 && cells[1].Foreground==0xff445566 && cells[2].Foreground==0xff0c2238,
            "Custom indexed colors recolor existing indexed text while preserving application truecolor");
        Write("\x1b]4;16;#ABCDEF\x07"); terminal.SetTheme(theme with { Indexed="16=#010203" }); terminal.ReadFrame(cells);
        check(cells[0].Foreground==0xffabcdef,"Application OSC palette overrides retain precedence over custom indexed defaults");
        Write("\x1b]104;16\x07"); terminal.ReadFrame(cells);
        check(cells[0].Foreground==0xff010203,"Resetting an OSC override restores the current custom indexed color");
        terminal.SetTheme(null); terminal.ReadFrame(cells);
        check(cells[0].Foreground==original,"Returning to the default theme removes custom extended indexed colors");
        check(! (theme with { Indexed="16=#000000 16=#FFFFFF" }).IsValid && !(theme with { Indexed="256=#FFFFFF" }).IsValid,
            "Indexed palette validation rejects duplicate and out-of-range indices");
        check(TerminalFont.BundledFile(0,500).EndsWith("Medium.ttf") && TerminalFont.BundledFile(2,500).EndsWith("MediumItalic.ttf")
            && TerminalFont.BundledFile(1,500).EndsWith("Bold.ttf"),"Bundled Medium and Medium Italic use real faces while ANSI bold remains bold");
        var defaults=new AppSettings { TerminalTheme=theme };
        check(defaults.WithTerminalTheme(null).TerminalTheme==theme && defaults.WithTerminalTheme(SessionThemePresets.All[0]).TerminalTheme==SessionThemePresets.All[0],
            "Global palettes are inherited by default sessions and explicit session themes take precedence");
    }
    internal static async Task RunRuntime(string helper,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/lua-runtime-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"oroterm.lua");
        async Task<LuaRuntimeClient> Start(string source)
        {
            File.WriteAllText(path,source);
            return await LuaRuntimeClient.Start(new() { Path=path,DataDirectory=folder,Baseline=new() },helper);
        }
        var request=new LuaPresentationRequest { Status=new() { ["session"]="Development",["panes"]=2,["time"]="12:34:56" },
            Tabs=[new() { ["id"]="one",["name"]="Shell",["is_active"]=true }] };
        using(var runtime=await Start("""
            local oro=require('oroterm')
            local n=0
            return {events={
              format_tab_title=function(tab) return {{text=tab.name..' active='..tostring(tab.is_active),foreground='#FFAA00',bold=true}} end,
              format_status=function(ctx) n=n+1; return {left=ctx.session..' '..ctx.panes,right={ctx.time,' '..n..' '..oro.strftime('%Y')}} end
            }}
            """))
        {
            check(runtime.Config.Error is null && runtime.Config.Hooks.Length==2,"Runtime workers retain both Lua presentation callbacks after evaluating configuration");
            var first=await runtime.Invoke(request); var second=await runtime.Invoke(request);
            check(first.Error is null && first.Titles["one"][0] is { Bold:true,Foreground:"#FFAA00" }
                && first.Titles["one"][0].Text=="Shell active=true","Typed tab metadata reaches real Lua callbacks and styled titles cross the worker boundary");
            check(first.Left![0].Text=="Development 2" && second.Right![1].Text.StartsWith(" 2 "),"Runtime status callbacks receive current metadata and retain Lua local state across updates");
            int pid=runtime.ProcessIdForCheck; runtime.Dispose();
            await WaitForExit(pid);
            check(true,"Disposing a runtime evaluator terminates its owned worker process");
        }
        foreach(string body in new[] { "return 42", "return {left={{text='x',foreground='bad'}}}", "return {left=string.rep('x',1025)}", "return {left='bad\\nline'}", "error('expected callback error')" })
        {
            using var runtime=await Start("return {events={format_status=function(ctx) "+body+" end}}");
            check((await runtime.Invoke(request)).Error is not null,"Invalid runtime output is contained: "+body);
        }
        using(var runtime=await Start("return {events={format_tab_title=function(tab) local t={}; for i=1,16 do t[i]=string.rep('x',1024) end; return t end}}"))
        {
            var many=new LuaPresentationRequest { Tabs=Enumerable.Range(0,64).Select(i=>new Dictionary<string,object?> { ["id"]=i.ToString() }).ToList() };
            var large=await runtime.Invoke(many);
            check(large.Error is null && large.Titles.Count==64 && large.Titles["63"].Length==16,
                "Bounded buffered transport handles the maximum supported 64-tab styled-text batch within its deadline");
        }
        using(var runtime=await Start("return {events={format_status=function(ctx) while true do end end}}"))
        {
            int pid=runtime.ProcessIdForCheck; var watch=Stopwatch.StartNew(); bool stopped=false;
            try { await runtime.Invoke(request); } catch(OperationCanceledException) { stopped=true; }
            await WaitForExit(pid);
            check(stopped && watch.Elapsed<TimeSpan.FromSeconds(3),"An infinite runtime callback is killed within a bounded deadline and cannot hang the caller");
        }
        static async Task WaitForExit(int pid)
        {
            try { using var process=Process.GetProcessById(pid); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch(ArgumentException) { }
        }
    }
}
