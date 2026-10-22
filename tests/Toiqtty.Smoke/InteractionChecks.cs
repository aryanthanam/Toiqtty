using Toiqtty.Configuration;
using Toiqtty.Models;
using Toiqtty.Hosting;

internal static class InteractionChecks
{
    internal static void Run(Action<bool,string> check)
    {
        foreach (var (chord, key, modifiers) in new[] { ("Alt+,",188u,4u),("Alt+.",190u,4u),("Alt+/",191u,4u),
            ("Ctrl+Shift+;",186u,3u),("Alt+\\",220u,4u),("Ctrl++",187u,3u),("Alt+?",191u,5u),("Alt+~",192u,5u),("Alt+{",219u,5u),("Alt+<",188u,5u),("Alt+>",190u,5u) })
            check(Keybindings.Parse(chord)==(key,modifiers),"Punctuation shortcut resolves: "+chord);
        var settings=new AppSettings { Leader=new() { Key="Ctrl+Space",TimeoutMs=500 },
            Bindings=[new() { Key="LEADER+R",Action=new() { Kind="activate_key_table",Value="resize" } },new() { Key="Alt+N",Action=new() { Value="newTab" } }],
            KeyTables=new() { ["resize"]=new() { TimeoutMs=500,Bindings=[new() { Key="H",Action=new() { Value="resizeLeft" } }] } } };
        LuaConfigParser.Validate(settings); var state=new ActionBindingState();
        check(state.Match(settings,32,2,1000,out var leader) && leader is null && state.LeaderActive(1200),"Leader activation consumes only its chord and has a deadline");
        check(state.Match(settings,82,0,1200,out var enter) && enter is { Kind:"activate_key_table",Value:"resize" },"Leader chord resolves its configured action");
        state.Push(settings,"resize",1200);
        check(state.Match(settings,72,0,1300,out var resize) && resize?.Value=="resizeLeft","Modal key tables resolve their own actions");
        check(state.Match(settings,88,0,1400,out var unknown) && unknown is null,"Unbound modal keys cannot leak into a shell");
        check(state.ActiveTable(1900)=="","Inactive key tables expire without polling timers");
        state.Match(settings,32,2,2000,out _);
        check(!state.Match(settings,90,0,2600,out _) && !state.LeaderActive(2600),"Expired leader chords restore ordinary terminal input");
        settings.KeyTables["resize"].OneShot=true; state.Push(settings,"resize",3000); state.Match(settings,72,0,3100,out _);
        check(state.ActiveTable(3100)=="","One-shot tables leave the mode after one matching action");
        state.Push(settings,"resize",4000); state.Match(settings,27,0,4100,out _);
        check(state.ActiveTable(4100)=="","Escape exits modal key tables");
        check(FuzzyMatch.Score("dvp","Development")>=0 && FuzzyMatch.Score("xyz","Development")==-1,"Session switching supports subsequence matching and excludes nonmatches");
        check(FuzzyMatch.Score("dev","dev")>FuzzyMatch.Score("dev","Development"),"Exact session names rank above partial matches");
        check(FuzzyMatch.Score("日本","日本語")>=0,"Session matching preserves Unicode names");
    }
    internal static async Task RunConfig(string helper,Action<bool,string> check)
    {
        string folder=Path.GetFullPath("artifacts/interactions-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"toiqtty.lua");
        async Task<LuaConfigResult> Load(string source)
        { File.WriteAllText(path,source); return await LuaConfigService.Load(path,folder,new(),helper); }
        var result=await Load("""
            return {
              keybindings={randomWallpaper='Alt+/',previousWallpaper='Alt+,',nextWallpaper='Alt+.'},
              leader={key='Ctrl+Alt+Space',timeout_ms=1500},
              bindings={{key='Shift+Enter',action={send_text='\27\r'}},{key='LEADER+R',action={activate_key_table='resize'}},
                        {key='Alt+X',action={sequence={'newTab',{paste='hello'},{lua='custom'}}}}},
              key_tables={resize={timeout_ms=3000,bindings={{key='H',action='resizeLeft'},{key='Enter',action={pop_key_table=true}}}}},
              actions={custom=function(ctx) return {paste=ctx.session} end},
              appearance={padding={left=15,right=15,top=10,bottom=8},line_height=1.8,cell_width=1.1,
                          inactive_pane_opacity=0.7,font_features={liga=0,ss01=1}},
              window={width=1100,height=700,state='maximized',always_on_top=true,backdrop='acrylic',background_opacity=0.8}
            }
            """);
        check(result.Error is null,"Complete action, punctuation, appearance and window configuration evaluates: "+result.Error);
        check(result.Settings.Bindings[0].Action.Value=="\x1b\r" && result.Settings.KeyTables["resize"].TimeoutMs==3000,"Literal controls and key-table metadata survive isolated Lua evaluation");
        check(result.Settings.Appearance is { LineHeight:1.8,CellWidth:1.1,InactivePaneOpacity:.7 } && result.Settings.Appearance.FontFeatures["ss01"]==1,"Lua visual controls preserve validated numeric and OpenType values");
        check(result.Settings.Window is { Width:1100,Height:700,State:"maximized",Backdrop:"acrylic",BackgroundOpacity:.8 },"Window geometry, material and opacity survive the worker boundary");
        check(new[] { "Bindings","KeyTables","Leader","Appearance","Window" }.All(result.Overrides.Contains),"New settings participate in Lua preference ownership");
        var example=await LuaConfigService.Load(Path.Combine(Path.GetDirectoryName(helper)!,"Configuration","interactions.example.lua"),folder,new(),helper);
        check(example.Error is null && example.Settings.KeyTables.Count==2 && example.Hooks.Contains("action:scratch"),"The shipped interactions example evaluates with action callbacks and both key tables: "+example.Error);
        check((await Load("return {bindings={{key='Alt+Z',action={send_text='\\0'}}}}" )).Settings.Bindings[0].Action.Value=="\0","Literal bindings preserve NUL controls instead of applying path-string validation");
        check((await Load("local a={}; a.sequence={a}; return {bindings={{key='Alt+Z',action=a}}}")).Error is not null,"Cyclic Lua action tables reject safely at the nesting limit");
        foreach(int length in new[] { -1, Toiqtty.Automation.ControlProtocol.RequestLimit+1 })
        {
            using var stream=new MemoryStream(BitConverter.GetBytes(length)); bool rejected=false;
            try { await Toiqtty.Automation.ControlProtocol.Read<Toiqtty.Automation.ControlRequest>(stream,Toiqtty.Automation.ControlProtocol.RequestLimit,CancellationToken.None); }
            catch(IOException) { rejected=true; }
            check(rejected,"Public CLI transport rejects invalid message length before allocating a payload: "+length);
        }
        foreach (string invalid in new[] {
            "bindings={{key='Alt+X',action='missing'}}", "bindings={{key='LEADER+X',action='newTab'}}",
            "bindings={{key='Alt+,',action='newTab'},{key='Alt+Comma',action='newWorkspace'}}",
            "bindings={{key='Alt+X',action={send_text='x',paste='y'}}}", "bindings={{key='Alt+X',action={activate_key_table='missing'}}}",
            "bindings={{key='Alt+X',action={lua='missing'}}}", "appearance={line_height=0}", "appearance={font_features={bad=1}}",
            "appearance={padding={left=-1}}", "window={background_opacity=2}", "window={width=900.5}", "leader={key='Ctrl+Space',timeout_ms=0}" })
        {
            var rejected=await Load("return {"+invalid+"}"); check(rejected.Error is not null,"Invalid interaction config is rejected: "+invalid);
        }
        File.WriteAllText(path,"local n=0; return {actions={tick=function(ctx) n=n+1; return {paste=ctx.session..n} end,bad=function() return 42 end}}");
        using(var runtime=await LuaRuntimeClient.Start(new() { Path=path,DataDirectory=folder },helper))
        {
            var first=await runtime.Invoke(new() { Action="tick",Status=new() { ["session"]="Work" } });
            var second=await runtime.Invoke(new() { Action="tick",Status=new() { ["session"]="Work" } });
            check(first.Action is { Kind:"paste",Value:"Work1" } && second.Action?.Value=="Work2","Lua action callbacks retain state and return structured actions with context");
            check((await runtime.Invoke(new() { Action="bad" })).Error is not null,"Invalid Lua action results report errors without executing UI operations");
        }
        var host=SessionHostClient.ConnectOrStart(folder,helper);
        string paneId=Guid.NewGuid().ToString("N");
        using var shell=new RemoteTerminalSession(host,paneId,ShellDefaults.Command+" -NoProfile",Environment.CurrentDirectory,new());
        shell.AttachView();
        string marker="RAW_"+Guid.NewGuid().ToString("N"); shell.SendText("Write-Output ('"+marker[..20]+"'+'"+marker[20..]+"')\r");
        var cells=new Toiqtty.Terminal.TerminalCell[3000]; var deadline=DateTime.UtcNow.AddSeconds(10); bool found=false;
        while(!found && DateTime.UtcNow<deadline)
        {
            if(shell.TryReadSnapshot(cells,out var snapshot)) found=string.Concat(cells.Take((int)(snapshot!.Frame.Columns*snapshot.Frame.Rows)).Select(c=>c.GetText())).Contains(marker);
            if(!found) await Task.Delay(50);
        }
        check(found,"Literal carriage return reaches real PowerShell through the new host operation and executes exactly as input");
        await host.SaveAsync(new() { Workspaces=[new() { Name="Detached CLI",Root=new() { Id=paneId,Command=ShellDefaults.Command,Directory=Environment.CurrentDirectory } }] });
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(helper)!,"toiq.exe")) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(string argument in new[] { "sessions","--json","--data-dir",folder }) start.ArgumentList.Add(argument);
        using var cli=System.Diagnostics.Process.Start(start)!; var output=cli.StandardOutput.ReadToEndAsync(); var errors=cli.StandardError.ReadToEndAsync();
        try { await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); } catch { if(!cli.HasExited) cli.Kill(); throw; }
        var json=await output; var error=await errors;
        check(cli.ExitCode==0 && System.Text.Json.JsonDocument.Parse(json).RootElement[0].GetProperty("name").GetString()=="Detached CLI",
            "The CLI lists a detached persistent host without opening a window: "+error);
    }
}
