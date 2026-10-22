using System.Diagnostics;
using Toiqtty.Configuration;
using Toiqtty.Models;

internal static class LuaConfigChecks
{
    public static async Task RunWindows(string appExecutable,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/lua-ui-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"shared.lua"),"return {font_size=16}");
        File.WriteAllText(Path.Combine(folder,"toiqtty.lua"),"""
            local oro=require('toiqtty')
            return {font_size=require('shared').font_size,startup={mode='replace_saved',sessions={
              {name='Development',theme='Cobalt2',tabs={
                {name='Mixed',root={split='right',first={shell='cmd.exe /Q /K'},second={kind='browser',url='about:blank'}}},
                {name='Logs',shell='cmd.exe /Q /K'}}},
              {name='Auxiliary',theme='Catppuccin Latte',tabs={{shell='cmd.exe /Q /K'}}}
            }}}
            """);
        try
        {
            foreach(string phase in new[]{"prepare","reconnect"})
            {
                var start=new ProcessStartInfo(appExecutable) {UseShellExecute=false,WorkingDirectory=Environment.CurrentDirectory,WindowStyle=ProcessWindowStyle.Hidden};
                start.ArgumentList.Add("--config-smoke"); start.Environment["TOIQTTY_DATA_DIR"]=folder;
                start.Environment["TOIQTTY_CONFIG"]=Path.Combine(folder,"toiqtty.lua"); start.Environment["TOIQTTY_CONFIG_UI_PROBE"]=phase;
                using var window=Process.Start(start)!;
                try { await window.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
                catch(TimeoutException) { if(!window.HasExited) {window.Kill();await window.WaitForExitAsync();} throw; }
                string reportPath=Path.Combine(folder,"lua-ui-"+phase+".json");
                if(!File.Exists(reportPath)) throw new IOException("No Lua UI report: "+folder);
                using var report=System.Text.Json.JsonDocument.Parse(File.ReadAllText(reportPath));
                if(!report.RootElement.GetProperty("passed").GetBoolean()) throw new InvalidOperationException(report.RootElement.GetProperty("error").GetString());
                foreach(var item in report.RootElement.GetProperty("checks").EnumerateArray()) check(true,item.GetString()!);
            }
            Console.WriteLine("Lua WinUI evidence: "+folder);
        }
        finally { try { Toiqtty.Hosting.SessionHostClient.TryConnect(folder)?.ShutdownForTest(); } catch(IOException){} }
    }
    public static async Task Run(string helper, Action<bool,string> check)
    {
        string folder = Path.GetFullPath("artifacts/lua-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder,"toiqtty.lua"); var baseline = new AppSettings { Shell="cmd.exe /Q /K", StartDirectory=Environment.CurrentDirectory, FontSize=14 };
        string identityRoot = Path.Combine(folder, "identity"); Directory.CreateDirectory(identityRoot);
        check(SessionStore.DefaultFolder(identityRoot) == Path.Combine(identityRoot, "toiqtty"), "fresh installs use the toiqtty data directory");
        Directory.CreateDirectory(Path.Combine(identityRoot, "unrelated-app"));
        check(SessionStore.DefaultFolder(identityRoot) == Path.Combine(identityRoot, "toiqtty"), "other product directories never change the toiqtty data location");
        check(LuaConfigService.DefaultConfigPath(folder) == Path.Combine(folder, "toiqtty.lua"), "new configuration files use the toiqtty name");
        async Task<LuaConfigResult> Load(string source)
        {
            File.WriteAllText(path,source); return await LuaConfigService.Load(path,folder,baseline,helper);
        }
        var result = await LuaConfigService.Load(path,folder,baseline,helper+"-missing");
        check(result.Error is null && result.Settings.FontSize==14 && result.Overrides.Length==0,"a missing Lua config uses normal settings without starting a helper");
        File.WriteAllText(Path.Combine(folder,"shared.lua"),"local M = {}; function M.size(n) return math.floor(n * 1.5) end; return M");
        result = await Load("""
            local oro = require('toiqtty')
            local shared = require('shared')
            local c = oro.config_builder()
            c.font_size = shared.size(12)
            c.font = 'Consolas'
            c.shell = oro.default_shell
            c.start_directory = oro.home_dir
            c.desktop_notifications = false
            c.keybindings = { newBrowser = 'Ctrl+Shift+Y', openDirectory = 'Ctrl+Alt+O' }
            c.browser = {home_page='localhost:3000',profile='independent',open_terminal_links=false}
            c.startup = { sessions = {
              { name='Dev', tabs={
                { name='Code', root={split='right',ratio=.6,
                  first={shell='cmd.exe /Q /K',cwd=oro.home_dir},
                  second={split='down',first={kind='browser',url='http://localhost:3000'},second={shell='cmd.exe /Q /K'}}}},
                {name='Logs',shell='cmd.exe /Q /K'} }},
              {name='Other',tabs={{kind='browser',url='about:blank'}}}
            }}
            return c
            """);
        check(result.Error is null,"the constrained worker evaluates a real Lua config: "+result.Error);
        check(result.Settings.FontSize==18 && result.Settings.FontFamily=="Consolas","Lua functions, math, local modules and config-builder assignment compute settings");
        check(result.Settings.Shell==ShellDefaults.Command && Directory.Exists(result.Settings.StartDirectory),"Lua exposes detected default shell and the current user's home directory");
        check(!result.Settings.DesktopNotifications && result.Settings.Keybindings["newBrowser"]=="Ctrl+Shift+Y","Lua sets booleans and validated custom keyboard shortcuts");
        check(Keybindings.Match(result.Settings,'O',6)=="openDirectory","Lua can configure the open-current-folder Explorer action");
        check(result.Settings.Browser.HomePage=="http://localhost:3000" && !result.Settings.Browser.OpenTerminalLinks && result.Settings.Browser.DefaultSourceId=="","Lua configures browser defaults and the independent browser profile");
        check(result.StartupSessions.Count==2 && result.StartupSessions[0].Tabs.Count==2 && result.StartupSessions[0].Tabs[0].Root.Leaves().Count()==3,"Lua describes sessions, tabs, and nested horizontal/vertical split trees");
        check(result.StartupSessions.SelectMany(w=>w.AllPanes()).Count()==5 && result.StartupSessions.SelectMany(w=>w.AllPanes()).All(p=>p.Session is null),"evaluating startup layouts never starts a shell or browser");
        check(!result.ReplaceSavedStartup && result.Dependencies.Length==2,"startup defaults to when-empty and tracks the main file plus required modules");
        var current = LuaConfigResult.Clone(baseline); var identity = current;
        ConfigurationSettings.Apply(result.Settings,current);
        current.AllowClipboardRead=true;
        ConfigurationSettings.CapturePreferences(current,baseline,result.Overrides);
        check(ReferenceEquals(identity,current) && baseline.FontSize==14 && baseline.AllowClipboardRead,"config application retains settings object identity and keeps Lua overrides out of UI preferences");
        UserPreferences.Save(folder,baseline);
        var restored = UserPreferences.Load(folder,new AppSettings());
        check(restored.FontSize==14 && restored.AllowClipboardRead,"normal preferences persist separately from effective Lua settings");
        File.Delete(path); result = await LuaConfigService.Load(path,folder,restored,helper);
        check(result.Settings.FontSize==14 && result.Settings.AllowClipboardRead && result.Overrides.Length==0,"removing a config restores the underlying preferences rather than stale Lua overrides");
        result = await Load("return {startup={mode='replace_saved',sessions={{name='Fresh',tabs={{shell='cmd.exe'}}}}}}");
        check(result.Error is null && result.ReplaceSavedStartup,"cold startup layout replacement is an explicit Lua option");
        var pacing = await Load("return { frame_pacing = true, allow_image_files = false }");
        check(pacing.Error is null && pacing.Settings.FramePacing && !pacing.Settings.AllowImageFiles
            && pacing.Overrides.Contains("FramePacing") && pacing.Overrides.Contains("AllowImageFiles"), "Lua sets frame pacing and image-file transfers and owns those preferences");
        result = await Load("local c={}; for i=1,2 do c.font_size=14+i end; if os.getenv('USERPROFILE') then c.ligatures=false end; return c");
        check(result.Error is null && result.Settings.FontSize==16 && !result.Settings.Ligatures,"Lua supports loops, conditions, and environment lookups");
        result = await Load("assert(io==nil and os.execute==nil and os.exit==nil and dofile==nil and loadfile==nil); return {}");
        check(result.Error is null,"the config API exposes no file mutation, process execution, or application-exit libraries");
        result = await Load("local tty=require('toiqtty'); assert(tty==toiqtty); return {font_size=17}");
        check(result.Error is null && result.Settings.FontSize == 17, "the toiqtty Lua module and global expose the same API");
        check(LuaConfigService.DefaultConfigPath(folder) == path, "the Lua entry point is always toiqtty.lua");
        result = await Load(File.ReadAllText(Path.Combine(Path.GetDirectoryName(helper)!,"Configuration","toiqtty.example.lua")));
        check(result.Error is null && result.Settings.ScrollbackLimitBytes==50_000_000 && result.Settings.ScrollbackLimitLines is null,"the shipped Lua example evaluates with Ghostty-compatible scrollback defaults");
        result = await Load("return {scrollback_limit_bytes=100000000,scrollback_limit_lines=5000}");
        check(result.Error is null && result.Settings.ScrollbackLimitBytes==100_000_000 && result.Settings.ScrollbackLimitLines==5000 && result.Overrides.Contains("ScrollbackLimitBytes") && result.Overrides.Contains("ScrollbackLimitLines"),"Lua sets independent byte and line scrollback limits");
        result = await Load("return {scrolling={lines_per_notch=8,app_events_per_notch=4,reverse=true}}");
        check(result.Error is null && result.Settings.Scrolling is { LinesPerNotch: 8, AppEventsPerNotch: 4, Reverse: true } && result.Overrides.Contains("Scrolling"),
            "Lua controls wheel speed and direction independently for history and mouse-reporting apps");
        UserPreferences.Save(folder, result.Settings);
        check(UserPreferences.Load(folder, new()).Scrolling is { LinesPerNotch: 8, AppEventsPerNotch: 4, Reverse: true },
            "wheel settings survive preference save and reload");
        foreach (string scrolling in new[] { "{lines_per_notch=0}", "{lines_per_notch=1.5}", "{app_events_per_notch=101}", "{app_events_per_notch='3'}", "{reverse=1}", "{typo=3}" })
        {
            result = await Load("return {scrolling=" + scrolling + "}");
            check(result.Error is not null, "invalid wheel configuration is rejected: " + scrolling);
        }
        result = await Load("return {}");
        check(result.Error is null && result.Settings.Scrolling is { LinesPerNotch: 3, AppEventsPerNotch: 1, Reverse: false },
            "removing the wheel configuration restores existing default speeds");
        result = await Load("return {scrollback_limit_bytes='unlimited',scrollback_limit_lines='unlimited'}");
        check(result.Error is null && result.Settings.ScrollbackLimitBytes is null && result.Settings.ScrollbackLimitLines is null,"unlimited scrollback survives the isolated worker's JSON result");
        UserPreferences.Save(folder,result.Settings);
        var unlimitedRestored = UserPreferences.Load(folder,new AppSettings());
        check(unlimitedRestored.ScrollbackLimitBytes is null && unlimitedRestored.ScrollbackLimitLines is null,"unlimited scrollback remains unlimited after preference save and load");
        result = await Load("return {startup={sessions={{name='Themed',theme='Dracula',tabs={{}}}}}}");
        check(result.Error is null && result.StartupSessions[0].Theme == SessionThemePresets.Find("Dracula"), "Lua startup sessions select a bundled theme by name");
        result = await Load("return {startup={sessions={{name='Default',theme='default',tabs={{}}}}}}");
        check(result.Error is null && result.StartupSessions[0].Theme is null, "Lua can explicitly select the default session appearance");
        result = await Load("return {startup={sessions={{name='Bad',theme='not a theme',tabs={{}}}}}}");
        check(result.Error is not null, "Unknown session theme names reject the new Lua configuration");
        result = await Load("return {startup={sessions={{name='Bad',theme={accent='#FF0000'},tabs={{}}}}}}");
        check(result.Error is not null, "Session themes use preset names instead of an unsupported color-editor schema");
        result = await Load("return {scrollback_limit_bytes=0,scrollback_limit_lines=0}");
        check(result.Error is null && result.Settings.ScrollbackLimitBytes==0 && result.Settings.ScrollbackLimitLines==0,"zero scrollback limits are accepted without being treated as unlimited");
        result = await Load("return {}");
        check(result.Error is null && result.Settings.ScrollbackLimitBytes==50_000_000 && result.Settings.ScrollbackLimitLines is null,"omitting scrollback overrides restores the Ghostty-compatible defaults");
        check(!result.Settings.StatusBar.Enabled, "The bottom status bar is hidden by default, including old settings without the new field");
        result = await Load("return {status_bar={enabled=true,left={'{session}', '{directory}', '{branch}'},right={'{panes} panes','{font_size}px'},separator=' | ',height=28,font_size=12,background='#102030',foreground='#F0E0D0'}}");
        check(result.Error is null && result.Settings.StatusBar.Enabled && result.Settings.StatusBar.Height == 28
            && result.Settings.StatusBar.FontSize == 12 && result.Settings.StatusBar.Foreground == "#F0E0D0" && result.Overrides.Contains("StatusBar"),
            "Lua configures status-bar visibility, ordered segments, separator, dimensions and colors across the worker boundary");
        var bar = result.Settings.StatusBar;
        var values = new Dictionary<string,string> { ["session"]="Development", ["directory"]="D:\\repo\\{branch}", ["branch"]="" };
        check(bar.Format(bar.Left, values) == "Development | D:\\repo\\{branch}", "Status formatting omits empty segments and treats braces in terminal metadata literally");
        check(bar.Format(["Exit {exit_code}", "fixed text", "{session}"], values) == "fixed text | Development", "Missing fields omit their whole segment, including labels and separators");
        values["session"] = "unsafe\nname\x1b";
        check(bar.Format(["{session}"], values) == "unsafe name ", "Control characters from terminal metadata cannot create multiline status content");
        var statusBaseline = LuaConfigResult.Clone(baseline);
        var effective = LuaConfigResult.Clone(baseline); ConfigurationSettings.Apply(result.Settings, effective);
        ConfigurationSettings.CapturePreferences(effective, statusBaseline, result.Overrides);
        check(!statusBaseline.StatusBar.Enabled && !ReferenceEquals(effective.StatusBar, result.Settings.StatusBar), "Lua status-bar settings are deeply copied and do not overwrite underlying preferences");
        UserPreferences.Save(folder, result.Settings);
        check(UserPreferences.Load(folder, new()).StatusBar.Left.SequenceEqual(bar.Left), "Status-bar settings survive preference serialization");
        result = await Load("return {status_bar={enabled=true,left={},right={},foreground='',background=''}}");
        check(result.Error is null && result.Settings.StatusBar.Left.Length == 0, "Either status side can be empty and colors can explicitly follow the session theme");
        result=await Load("""
            return {font_weight='Medium',cursor={style='bar',blink=true,blink_interval=650},
              color_schemes={Mine={foreground='#CDD6F4',background='#1F1F28',cursor='#F5E0DC',accent='#89B4FA',
                selection_background='#585B70',selection_foreground='#FFFFFF',cursor_foreground='#11111B',indexed={[16]='#FAB387',[17]='#F5E0DC'}}},
              color_scheme='Mine',startup={sessions={{name='Custom',theme='Mine',tabs={{}}}}},
              events={format_tab_title=function(tab) return tab.name end,format_status=function(ctx) return {right=ctx.time} end}}
            """);
        check(result.Error is null && result.Settings.FontWeight==500 && result.Settings.Cursor is { Style:"bar",Blink:true,BlinkInterval:650 },
            "Lua configures font weight and upstream cursor defaults with a separate UI blink interval");
        check(result.Settings.TerminalTheme?.Name=="Mine" && result.StartupSessions[0].Theme==result.Settings.TerminalTheme
            && result.Settings.TerminalTheme.Indexed=="16=#FAB387 17=#F5E0DC" && result.Settings.TerminalTheme.SelectionBackground=="#585B70",
            "Custom named palettes apply globally or per session and include indexed, selection and cursor text colors");
        check(result.Hooks.Length==2,"Configuration evaluation validates and reports runtime hooks without invoking them");
        result=await Load("return {wallpaper={images={},opacity=.04,fit='cover',random=false}} ");
        check(result.Error is null && result.Settings.Wallpaper is { Opacity:.04,Fit:"cover",Random:false },"Lua configures optional wallpaper appearance without requiring an image");
        result=await Load(File.ReadAllText(Path.Combine(Path.GetDirectoryName(helper)!,"Configuration","appearance.example.lua")));
        check(result.Error is null && result.Settings.FontWeight==500 && result.Hooks.Length==2 && result.Settings.TerminalTheme?.Name=="Personal Mocha",
            "The shipped appearance example evaluates with a complete custom palette, real Medium font and both runtime hooks");
        foreach (var (source,message) in new[] {
            ("return {font_weight='huge'}","unknown font weights are rejected"),
            ("return {font_weight=500.5}","fractional font weights are rejected"),
            ("return {cursor={style='triangle'}}","unknown cursor styles are rejected"),
            ("return {cursor={blink_interval=5}}","unsafe cursor blink intervals are rejected"),
            ("return {color_scheme='missing'}","unknown global palettes are rejected"),
            ("return {color_schemes={Dracula={}}}","custom palettes cannot silently replace bundled presets"),
            ("return {color_schemes={Bad={foreground='red'}}}","invalid custom palette colors are rejected"),
            ("return {color_schemes={Bad={ansi={'#000000'}}}}","partial ANSI palettes are rejected"),
            ("return {color_schemes={Bad={indexed={[256]='#000000'}}}}","out-of-range extended palette indices are rejected"),
            ("return {events={format_status='oops'}}","runtime hooks require functions"),
            ("return {events={on_key=function() end}}","unsupported runtime event names are rejected"),
            ("return {wallpaper={opacity=2}}","out-of-range wallpaper opacity is rejected"),
            ("return {wallpaper={fit='tile'}}","unsupported wallpaper fitting modes are rejected"),
            ("return {status_bar=true}","status_bar requires a settings table"),
            ("return {status_bar={enabled='yes'}}","status-bar visibility requires a boolean"),
            ("return {status_bar={left={'{unknown}'}}}","unknown status placeholders are rejected"),
            ("return {status_bar={left={'{session'}}}","unmatched status placeholder braces are rejected"),
            ("return {status_bar={right={[1]='one',[3]='three'}}}","sparse status segment arrays are rejected"),
            ("return {status_bar={left={42}}}","non-string status segments are rejected"),
            ("return {status_bar={left={string.rep('a',257)}}}","oversized status segments are rejected"),
            ("return {status_bar={left={string.rep('a\\n',2)}}}","multiline status segments are rejected"),
            ("return {status_bar={height=200}}","oversized status bar heights are rejected"),
            ("return {status_bar={height=18,font_size=24}}","status text must fit inside the bar height"),
            ("return {status_bar={font_size=0/0}}","non-finite status font sizes are rejected"),
            ("return {status_bar={background='purple'}}","invalid status colors are rejected"),
            ("return {status_bar={separator='\\n'}}","multiline status separators are rejected"),
            ("return {status_bar={typo=true}}","unknown status-bar settings are rejected"),
            ("return {scrollback_limit_bytes=-1}","negative byte budgets are rejected"),
            ("return {scrollback_limit_lines=-1}","negative line limits are rejected"),
            ("return {scrollback_limit_bytes=1.5}","fractional byte budgets are rejected"),
            ("return {scrollback_limit_lines=1.5}","fractional line limits are rejected"),
            ("return {scrollback_limit_bytes=0/0}","non-finite scrollback limits are rejected"),
            ("return {scrollback_limit_bytes=9007199254740992}","inexactly representable oversized Lua limits are rejected"),
            ("return {scrollback_limit_lines='infinite'}","unsupported scrollback strings are rejected"),
            ("return {scrollback_limit_bytes=false}","boolean scrollback limits are rejected"),
            ("return {font_sze=18}","unknown settings fields are rejected"),
            ("return {font_size='large'}","wrong Lua value types are rejected"),
            ("return {font_size=0/0}","non-finite font sizes are rejected"),
            ("return {font_size=90}","out-of-range font sizes are rejected"),
            ("return {shell=''}","empty shell commands are rejected"),
            ("return {start_directory='Z:/toiqtty-path-that-does-not-exist'}","missing launch directories are rejected"),
            ("return {keybindings={newTab='Ctrl+Shift+C'}}","duplicate keyboard shortcuts are rejected"),
            ("return {keybindings={typo='Ctrl+Shift+Y'}}","unknown keyboard actions are rejected"),
            ("return {browser={home_page='javascript:alert(1)'}}","unsafe browser schemes are rejected"),
            ("return {startup={mode='always'}}","unsupported startup modes are rejected"),
            ("return {startup={sessions={{name='Empty',tabs={}}}}}","startup sessions require a tab"),
            ("return {startup={sessions={{name='Sparse',tabs={[1]={},[3]={}}}}}}","sparse startup arrays cannot silently lose tabs"),
            ("return {startup={sessions={{name='Bad',tabs={{root={split='diagonal',first={},second={}}}}}}}}","split directions are validated"),
            ("local p={split='right',second={}}; p.first=p; return {startup={sessions={{name='Cycle',tabs={{root=p}}}}}}","cyclic startup split trees are bounded"),
            ("return {startup={sessions={{name='Bad',tabs={{kind='browser',shell='cmd.exe'}}}}}}","browser leaves cannot accidentally create shell processes"),
            ("require('../outside'); return {}","require cannot escape the configuration folder"),
            ("return 42","Lua config must return a settings table"),
            ("return {}, {}","multiple returned configs are rejected"),
            ("return {font_size = }","syntax errors are reported without producing settings") })
        {
            result = await Load(source); check(result.Error is not null,message);
        }
        result = await Load("local tabs={}; for i=1,65 do tabs[i]={} end; return {startup={sessions={{name='Too many',tabs=tabs}}}}");
        check(result.Error is not null,"startup tab counts are bounded");
        result = await Load(new string(' ',LuaConfigParser.MaxSourceBytes+1)+"return {}");
        check(result.Error is not null,"Lua source size is bounded before compilation");
        var elapsed=Stopwatch.StartNew(); result=await Load("while true do end");
        check(result.Error?.Contains("execution limit")==true && elapsed.Elapsed<TimeSpan.FromSeconds(7),"an infinite Lua loop is terminated in the worker without hanging the caller");
        result=await Load("local s=string.rep('x',512*1024*1024); return {font=s}");
        check(result.Error is not null,"the Windows job memory limit contains oversized Lua allocations");
        var signal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var watcher=new LuaConfigWatcher(path,()=>signal.TrySetResult()))
        { File.WriteAllText(Path.Combine(folder,"shared.lua"),"return {size=16}"); await signal.Task.WaitAsync(TimeSpan.FromSeconds(4)); }
        check(true,"editing a required Lua module triggers configuration reload notification");
        Console.WriteLine("Lua configuration evidence: "+folder);
    }
}
