using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunInteractionUiChecks(Action<bool,string> check)
    {
        var original = _selected!; var appearance = _saved.Settings.Appearance; var window = _saved.Settings.Window;
        var bindings = _saved.Settings.Bindings; var leader = _saved.Settings.Leader; var tables = _saved.Settings.KeyTables;
        float originalFont = _saved.Settings.FontSize;
        var originalPanes = _saved.Workspaces.SelectMany(w => w.AllPanes()).Select(p => p.Session).ToArray();
        var created = new List<Workspace>();
        async Task Wait(Func<bool> condition)
        { var until=DateTime.UtcNow.AddSeconds(8); while(!condition()) { if(DateTime.UtcNow>until) throw new TimeoutException("Interaction UI state did not settle."); await Task.Delay(30); } }
        ContentDialog? Dialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).SelectMany(p=>Walk(p.Child)).OfType<ContentDialog>().FirstOrDefault();
        try
        {
            var view = _focused!;
            float beforeHeight = view.CellHeightForCheck;
            _saved.Settings.Appearance = new() { Padding = new() { Left=21,Right=13,Top=17,Bottom=9 },LineHeight=1.8,CellWidth=1.1,InactivePaneOpacity=.6,
                FontFeatures=new() { ["liga"]=0,["ss01"]=1 } };
            foreach(var terminal in _views) terminal.ApplySettings();
            await Wait(()=>_views.All(v=>!v.ResizePendingForCheck) && view.CellHeightForCheck>beforeHeight);
            var origin = view.ContentOriginForCheck(view);
            check(origin.X>=21 && origin.Y>=17 && view.CellHeightForCheck>beforeHeight,"Configured padding and line spacing reach the accepted native terminal geometry");
            check(_views.Where(v=>v!=view).All(v=>Math.Abs(v.ContentOpacityForCheck-.6)<.01) && view.ContentOpacityForCheck==1,
                "Inactive-pane dimming follows focus while the active terminal remains fully visible");
            _saved.Settings.Window = new() { Backdrop="acrylic",BackgroundOpacity=.5 };
            ApplyWindowSettings(false); foreach(var terminal in _views) terminal.ApplySettings();
            await Wait(()=>_views.All(v=>!v.ResizePendingForCheck));
            using(var target=view.CreateRenderTargetForCheck())
            {
                view.RenderFrameForCheck(target);
                check(SystemBackdrop is DesktopAcrylicBackdrop && target.GetPixelColors()[^1].A is >= 126 and <= 129,
                    "Translucent swap chains preserve background alpha over the selected Windows Acrylic backdrop");
            }
            _saved.Settings.Window=window; ApplyWindowSettings(false); foreach(var terminal in _views) terminal.ApplySettings();
            _saved.Settings.Leader=new() { Key="Ctrl+Alt+Space",TimeoutMs=1000 };
            _saved.Settings.Bindings=[new() { Key="LEADER+F",Action=new() { Kind="activate_key_table",Value="font" } }];
            _saved.Settings.KeyTables=new() { ["font"]=new() { Bindings=[new() { Key="J",Action=new() { Value="increaseFontSize" } }] } };
            float font=_saved.Settings.FontSize;
            check(TryHandleActionKey(32,6) && TryHandleActionKey(70,0) && TryHandleActionKey(74,0),"Leader and modal actions travel through the actual window shortcut dispatcher");
            await _actionTail;
            check(_saved.Settings.FontSize==font+1 && _views.All(v=>v.FontSizeForCheck==font+1),"A modal font action updates existing terminal views without recreating shells");
            TryHandleActionKey(27,0); _bindingState.Reset(); _saved.Settings.FontSize=font;
            foreach(var terminal in _views) terminal.ApplySettings();

            var listed=await Cli("sessions");
            check(listed.Exit==0 && JsonDocument.Parse(listed.Output).RootElement.EnumerateArray().Any(w=>w.GetProperty("id").GetString()==original.Id),
                "The public CLI lists real sessions through the window-owned control endpoint");
            check(!(await Automation.ControlProtocol.Call(SessionStore.Folder,new() { Version=99 })).Ok,
                "The control endpoint rejects incompatible public protocol versions");
            check((await Cli("split","sideways")).Exit!=0 && (await Cli("sessions","--text","invalid")).Exit!=0,
                "CLI validation rejects invalid directions and irrelevant options before changing layout");
            string cwd=Path.Combine(SessionStore.Folder,"cli directory with spaces"); Directory.CreateDirectory(cwd);
            var made=await Cli("new-session","--name","CLI Workspace","--cwd",cwd,"--shell",ShellDefaults.Command+" -NoProfile");
            check(made.Exit==0,"The CLI creates a session using structured name, shell and directory arguments: "+made.Output);
            var added=_selected!; created.Add(added);
            check(added.Name=="CLI Workspace" && added.Root.Directory==cwd,"CLI directory and session names preserve spaces");
            check((await Cli("new-tab","--session",added.Id,"--name","CLI Tab")).Exit==0 && added.Tabs.Count==2,"The CLI creates a tab inside an explicitly targeted session");
            var splitResult=await Cli("split","down","--session",added.Id);
            check(splitResult.Exit==0 && added.Root.Leaves().Count()==2,"The CLI creates a split through the normal pane ownership path: "+splitResult.Output);
            check((await Cli("rename-session",added.Id,"CLI Workbench")).Exit==0 && added.Name=="CLI Workbench","The CLI renames sessions by stable ID");
            check((await Cli("focus",original.Id)).Exit==0 && _selected==original,"The CLI focuses an existing session without replacing its shells");
            var switching=SwitchSession(); await Wait(()=>Dialog() is not null);
            var chooser=Dialog()!; var panel=(StackPanel)chooser.Content; var search=(TextBox)panel.Children[0]; var list=(ListView)panel.Children[1];
            search.Text="cliwrk";
            await Wait(()=>list.Items.Count>0 && list.Items[0] is SessionChoice choice && choice.Session==added);
            check(list.Items.Count>0 && list.Items[0] is SessionChoice choice && choice.Session==added,"Native session switching ranks fuzzy session-name matches");
            list.UpdateLayout(); await Wait(()=>list.ContainerFromIndex(0) is ListViewItem);
            var peer=FrameworkElementAutomationPeer.CreatePeerForElement((ListViewItem)list.ContainerFromIndex(0))!;
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke(); await switching.WaitAsync(TimeSpan.FromSeconds(3));
            check(_selected==added,"Invoking a native fuzzy-switcher result selects the correct existing session");
            switching=SwitchSession(); await Wait(()=>Dialog() is not null); Dialog()!.Hide(); await switching;
            check(_selected==added,"Cancelling fuzzy switching preserves the active session");
            check((await Cli("focus","missing-session")).Exit!=0 && _selected==added,"Invalid CLI targets fail without changing the selected session");
            string marker="CLI_RAW_"+Guid.NewGuid().ToString("N");
            var sent=await Cli("send-text","--pane",ActivePaneId!,"--text","Write-Output ('"+marker[..20]+"'+'"+marker[20..]+"')\r");
            check(sent.Exit==0,"The CLI admits literal input to the explicitly targeted isolated terminal");
            await Wait(()=>_focused?.RenderedText.Contains(marker)==true);
            check(true,"CLI literal input reaches PowerShell and its output returns through the terminal renderer");
            string sequenceMarker="SEQUENCE_"+Guid.NewGuid().ToString("N"); int tabCount=added.Tabs.Count;
            await QueueAction(new() { Kind="sequence",Steps=[new() { Value="newTab" },new() { Kind="send_text",Value="Write-Output ('"+sequenceMarker[..20]+"'+'"+sequenceMarker[20..]+"')\r" }] });
            await Wait(()=>_focused?.RenderedText.Contains(sequenceMarker)==true);
            check(added.Tabs.Count==tabCount+1,"An ordered new-tab and send-text sequence targets its new shell before asynchronous UI focus settles");
            string actionPath=Path.Combine(SessionStore.Folder,"ui-action-callback.lua");
            File.WriteAllText(actionPath,"return {actions={grow=function(ctx) if ctx.session_id and ctx.pane_id then return 'increaseFontSize' else error('missing pane context') end end}}");
            using(var runtime=await Configuration.LuaRuntimeClient.Start(new() { Path=actionPath,DataDirectory=SessionStore.Folder }))
            {
                var previous=_luaRuntime; _luaRuntime=runtime; float size=_saved.Settings.FontSize;
                try
                {
                    var callback=await Cli("action","lua:grow");
                    check(callback.Exit==0 && _saved.Settings.FontSize==size+1,"A real Lua worker returns an action through the CLI that updates the live UI with stable pane/session context: "+callback.Output);
                }
                finally { _luaRuntime=previous; _saved.Settings.FontSize=size; foreach(var terminal in _views) terminal.ApplySettings(); }
            }
            await QueueAction(new() { Value="toggleFullscreen" });
            check(AppWindow.Presenter.Kind==AppWindowPresenterKind.FullScreen,"The fullscreen action changes the native window presenter");
            await QueueAction(new() { Value="toggleFullscreen" });
            check(AppWindow.Presenter is OverlappedPresenter,"Fullscreen toggles back to the normal native presenter");
            await QueueAction(new() { Kind="sequence",Steps=[new() { Value="previousTab" },new() { Value="nextTab" }] });
            check(originalPanes.All(p=>_saved.Workspaces.SelectMany(w=>w.AllPanes()).Any(n=>ReferenceEquals(n.Session,p))),
                "CLI, fuzzy switching and ordered actions preserve every preexisting shell identity");
        }
        finally
        {
            _saved.Settings.Appearance=appearance; _saved.Settings.Window=window; _saved.Settings.Bindings=bindings; _saved.Settings.Leader=leader; _saved.Settings.KeyTables=tables;
            _saved.Settings.FontSize=originalFont;
            _bindingState.Reset(); ApplyWindowSettings(false);
            foreach(var session in created) RemoveWorkspace(session);
            SelectWorkspace(original); foreach(var view in _views) view.ApplySettings();
        }
        async Task<(int Exit,string Output)> Cli(params string[] arguments)
        {
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"oro.exe")) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach(string argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--json"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(SessionStore.Folder);
            using var process=Process.Start(start)!; var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
            catch { if(!process.HasExited) process.Kill(); throw; }
            return(process.ExitCode,await output+await error);
        }
        static IEnumerable<DependencyObject> Walk(DependencyObject node)
        { yield return node; for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) foreach(var child in Walk(VisualTreeHelper.GetChild(node,i))) yield return child; }
    }
}
