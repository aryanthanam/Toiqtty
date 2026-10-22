using System.Text.Json;
using Toiqtty.Configuration;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunConfigurationSmoke(string? phase)
    {
        var checks=new List<string>(); string? error=null;
        void Check(bool condition,string text) { if (!condition) throw new InvalidOperationException(text); checks.Add(text); }
        async Task Wait(Func<bool> condition)
        {
            var until=DateTime.UtcNow.AddSeconds(15);
            while (!condition()) { if (DateTime.UtcNow>until) throw new TimeoutException("Lua UI check timed out: "+_luaError); await Task.Delay(100); }
        }
        try
        {
            Check(Title.Contains("toiqtty", StringComparison.Ordinal), "the window presents the toiqtty name while reusing compatible session identities");
            Check(_luaError is null,"Lua config loads before building the terminal window: "+_luaError);
            Check(_saved.Settings.FontSize==16 && LuaOwns("FontSize"),"Lua startup settings reach live WinUI terminal controls and are marked as Lua-owned");
            Check(! _saved.Settings.StatusBar.Enabled && StatusBar.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
                "Lua startup and window reconnect leave the bottom bar hidden when not configured");
            Check(_saved.Workspaces[0].Theme == SessionThemePresets.Find("Cobalt2") && _saved.Workspaces[1].Theme == SessionThemePresets.Find("Catppuccin Latte"),
                "Lua session themes reach WinUI and survive a complete window reconnect");
            await Wait(() => _views.All(v => v.FrameForCheck.Background == Controls.Chrome.For(_selected!.Theme).Background));
            Check(true, "The host applies Lua pane backgrounds across window reconnects");
            if (phase=="prepare")
            {
                Check(_persistent && !_reattached && _saved.Workspaces.Count==2 && _saved.Workspaces[0].Tabs.Count==2,
                    "Lua startup creates two sidebar sessions with configured tabs on a fresh launch");
                Check(_selected!.Root.Leaves().Count()==2 && _selected.Root.Leaves().Any(p=>p.IsBrowser) && _host is not null,
                    "Lua startup creates mixed browser/shell split layouts through the normal session host");
                string host=_host!.Identity; string[] panes=_saved.Workspaces.SelectMany(w=>w.AllPanes()).Select(p=>p.Id).ToArray();
                string[] sessions=_saved.Workspaces.Select(w=>w.Id).ToArray();
                string token=Guid.NewGuid().ToString("N");
                var shell=_selected.Root.Leaves().First(p=>!p.IsBrowser).Session!;
                shell.Paste("set TOIQTTY_LUA_KEEP="+token+"&echo LUA_CONFIG_READY"); shell.Key(13,0);
                await Wait(()=>_views.Any(v=>v.RenderedText.Contains("LUA_CONFIG_READY")));
                string path=LuaConfigService.ConfigPath, original=File.ReadAllText(path);
                await RunLuaAppearanceReloadChecks(path,original,Check,Wait);
                File.WriteAllText(path,"return {font_size=22,status_bar={enabled=true,left={'{session}'},right={'{panes} panes'},height=28,font_size=12},startup={mode='replace_saved',sessions={{name='Should not start',tabs={{}}}}}}");
                await Wait(()=>_saved.Settings.FontSize==22);
                _statusMessageUntil = 0; UpdateStatusBar(); Root.UpdateLayout();
                Check(StatusBar.Visibility == Microsoft.UI.Xaml.Visibility.Visible && StatusBar.ActualHeight == 28
                    && StatusText.Text == _selected!.Name && SessionStatus.Text == "2 panes" && LuaOwns("StatusBar"),
                    "Live Lua reload enables and customizes native status-bar layout and segments");
                Check(_saved.Workspaces.Select(w=>w.Id).SequenceEqual(sessions) && _host.Identity==host,
                    "automatic Lua reload changes settings without rerunning startup layouts or restarting the host");
                File.WriteAllText(path,"return {font_size=24,status_bar={left={'{typo}'}}}");
                await Wait(()=>_luaError is not null);
                Check(_saved.Settings.FontSize==22 && _saved.Settings.StatusBar.Enabled && LuaConfigBanner.IsOpen && _saved.Workspaces.Count==2,
                    "invalid live config shows a native error banner and retains the last working settings and sessions");
                File.WriteAllText(path,original); File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!,"shared.lua"),"return {font_size=19}");
                await Wait(()=>_saved.Settings.FontSize==19 && _luaError is null);
                Check(StatusBar.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
                    "Removing status_bar from a live Lua config restores the hidden default without restarting shells");
                Check(true,"editing a required Lua module reloads the effective settings and clears the error banner");
                _saved.Settings.AllowClipboardRead=true; Save();
                var preferences=UserPreferences.Load(SessionStore.Folder,new AppSettings());
                Check(preferences.FontSize==14 && preferences.AllowClipboardRead,
                    "UI preferences retain their baseline while unowned changes persist beneath Lua overrides");
                File.Delete(path); await Wait(()=>_saved.Settings.FontSize==14 && _luaConfig.Overrides.Length==0);
                Check(_saved.Settings.AllowClipboardRead && _saved.Workspaces.SelectMany(w=>w.AllPanes()).Select(p=>p.Id).SequenceEqual(panes),
                    "removing the Lua file restores normal preferences without changing live panes");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!,"shared.lua"),"return {font_size=16}"); File.WriteAllText(path,original);
                await Wait(()=>_saved.Settings.FontSize==16); Save();
                File.WriteAllText(Path.Combine(SessionStore.Folder,"lua-ui-identity.json"),JsonSerializer.Serialize(new {
                    host,token,sessions,panes,tabs=_saved.Workspaces.SelectMany(w=>w.Tabs).Select(t=>t.Id).ToArray() }));
            }
            else
            {
                using var identity=JsonDocument.Parse(File.ReadAllText(Path.Combine(SessionStore.Folder,"lua-ui-identity.json")));
                var saved=identity.RootElement;
                Check(_reattached && _host?.Identity==saved.GetProperty("host").GetString(),"reopening a Lua-configured window reconnects to the original shell host");
                Check(_saved.Workspaces.Select(w=>w.Id).SequenceEqual(saved.GetProperty("sessions").EnumerateArray().Select(v=>v.GetString())),
                    "startup replace_saved mode never replaces or duplicates live sessions on reconnect");
                Check(_saved.Workspaces.SelectMany(w=>w.Tabs).Select(t=>t.Id).SequenceEqual(saved.GetProperty("tabs").EnumerateArray().Select(v=>v.GetString())) &&
                    _saved.Workspaces.SelectMany(w=>w.AllPanes()).Select(p=>p.Id).SequenceEqual(saved.GetProperty("panes").EnumerateArray().Select(v=>v.GetString())),
                    "Lua-configured tab, shell, and browser pane identities survive the complete window restart");
                var shell=_selected!.Root.Leaves().First(p=>!p.IsBrowser).Session!;
                shell.Paste("echo LUA_RECONNECTED=%TOIQTTY_LUA_KEEP%"); shell.Key(13,0);
                await Wait(()=>_views.Any(v=>v.RenderedText.Contains("LUA_RECONNECTED="+saved.GetProperty("token").GetString())));
                Check(true,"the original shell's environment survives Lua reload and window closure");
                CreateConfiguredSessions();
                Check(_saved.Workspaces.Count==4 && _saved.Workspaces.SelectMany(w=>w.AllPanes()).Select(p=>p.Id).Distinct().Count()==8,
                    "explicitly creating configured sessions produces new identities without replacing existing sessions");
                foreach (var session in _saved.Workspaces.ToArray()) RemoveWorkspace(session);
                for (var stopped=DateTime.UtcNow.AddSeconds(15); await Hosting.SessionHostClient.TryConnectAsync(SessionStore.Folder) is not null; await Task.Delay(100))
                    if (DateTime.UtcNow>stopped) throw new TimeoutException("Lua UI check timed out waiting for the host to stop.");
                Check(_saved.Workspaces.Count==0,"closing every configured session stops the host without automatically replaying startup actions");
            }
        }
        catch (Exception ex) { error=ex.ToString(); }
        File.WriteAllText(Path.Combine(SessionStore.Folder,"lua-ui-"+phase+".json"),JsonSerializer.Serialize(new {passed=error is null,checks,error},new JsonSerializerOptions {WriteIndented=true}));
        Close();
    }
}
