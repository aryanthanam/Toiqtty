using Microsoft.UI.Xaml.Documents;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunLuaAppearanceReloadChecks(string path,string original,Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        var workspace=_selected!; var theme=workspace.Theme; string host=_host!.Identity;
        var panes=workspace.AllPanes().Select(p=>p.Session).ToArray();
        File.WriteAllText(path,"""
            local oro=require('toiqtty')
            return {font_size=16,font_weight='Medium',cursor={style='underline',blink=true,blink_interval=650},
              color_schemes={Mine={foreground='#CCDDEE',background='#182838',accent='#FFAACC',indexed={[16]='#112233'}}},color_scheme='Mine',
              status_bar={enabled=true},events={
                format_tab_title=function(tab) return {{text='Lua:'..tab.name,foreground='#FFAA00',bold=true}} end,
                format_status=function(ctx) return {left='Live '..ctx.session,right=oro.strftime('%H:%M:%S')} end
              }}
            """);
        await wait(()=>_saved.Settings.FontWeight==500 && _luaRuntime is not null && _luaPresentation.Titles.Count>0);
        _statusMessageUntil=0; UpdateStatusBar();
        check(_tabCards[workspace.ActiveTab.Id].Label.Inlines.OfType<Run>().Any(r=>r.Text=="Lua:"+workspace.ActiveTab.Name)
            && StatusText.Inlines.OfType<Run>().Any(r=>r.Text=="Live "+workspace.Name),
            "Persistent Lua callbacks format actual native tab labels and status text after live reload");
        check(_views.All(v=>v.FontWeightForCheck==500),"Live font-weight reload reaches existing terminal renderers");
        SetSessionTheme(workspace,null);
        await wait(()=>_views.All(v=>v.FrameForCheck.Background==0xff182838));
        check(true,"Global custom palettes reach existing host-owned panes without a restart");
        string name=workspace.ActiveTab.Name; workspace.ActiveTab.Name="Callback rename"; RefreshTabs(); UpdateChrome();
        await wait(()=>_tabCards[workspace.ActiveTab.Id].Label.Inlines.OfType<Run>().Any(r=>r.Text=="Lua:Callback rename"));
        check(true,"Runtime formatting follows tab metadata changes without re-evaluating configuration");
        workspace.ActiveTab.Name=name; RefreshTabs();
        var retained=_luaRuntime; File.WriteAllText(path,"return {font_weight='invalid'}");
        await wait(()=>_luaError is not null);
        check(ReferenceEquals(retained,_luaRuntime) && _saved.Settings.FontWeight==500,"Invalid reload retains the already-loaded callback worker and last working appearance");
        File.WriteAllText(path,"return {events={format_status=function(ctx) while true do end end}}");
        await wait(()=>_luaRuntime is null && _luaError?.Contains("500 ms")==true);
        check(_host.Identity==host && panes.SequenceEqual(workspace.AllPanes().Select(p=>p.Session))
            && _tabCards[workspace.ActiveTab.Id].Label.Text==name,
            "A hung runtime callback falls back to native formatting and leaves the session host and shell identities intact");
        File.WriteAllText(path,original); await wait(()=>_luaError is null && _saved.Settings.FontSize==16 && _saved.Settings.FontWeight==400);
        SetSessionTheme(workspace,theme);
        check(_luaRuntime is null && !_saved.Settings.StatusBar.Enabled,"Removing runtime hooks restores ordinary formatting and the hidden status-bar default");
    }
}
