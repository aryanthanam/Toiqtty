using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Toiqtty.Configuration;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private LuaRuntimeClient? _luaRuntime;
    private LuaPresentationResult _luaPresentation = new();
    private readonly DispatcherTimer _luaPresentationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _luaPresentationBusy, _luaPresentationPending;
    private void InstallLuaRuntime(LuaRuntimeClient? runtime)
    {
        _luaRuntime?.Dispose(); _luaRuntime = runtime;
        _luaPresentation = new();
        if (runtime is null || !_luaConfig.Hooks.Any(h => h is "format_status" or "format_tab_title")) _luaPresentationTimer.Stop(); else _luaPresentationTimer.Start();
        ApplyLuaTabTitles(); QueueLuaPresentation();
    }
    private async void QueueLuaPresentation()
    {
        if (_closed || _closing || _luaRuntime is null || !WindowRenderingActive || !_luaConfig.Hooks.Any(h => h is "format_status" or "format_tab_title")) return;
        if (_luaPresentationBusy) { _luaPresentationPending=true; return; }
        _luaPresentationBusy=true; _luaPresentationPending=false;
        var runtime=_luaRuntime;
        string? paneId=ActivePaneId, sessionId=_selected?.Id;
        var request=new LuaPresentationRequest { Status=StatusValues().ToDictionary(p=>p.Key,p=>(object?)p.Value) };
        foreach(string name in new[] { "panes","tabs","sessions","shells","font_size","exit_code" })
            request.Status[name]=double.TryParse(request.Status[name]?.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double number)?number:null;
        request.Status["pane_id"]=paneId; request.Status["session_id"]=sessionId; request.Status["tab_id"]=_selected?.ActiveTab.Id;
        request.Status["time"]=DateTime.Now.ToString("HH:mm:ss");
        request.Status["date"]=DateTime.Now.ToString("yyyy-MM-dd");
        request.Status["key_table"]=_bindingState.ActiveTable(Environment.TickCount64);
        request.Status["leader_active"]=_bindingState.LeaderActive(Environment.TickCount64);
        if (GetSystemPowerStatus(out var power) && power.BatteryLifePercent <= 100 && (power.BatteryFlag & 128)==0)
        {
            request.Status["battery_percent"]=(int)power.BatteryLifePercent;
            request.Status["battery_charging"]=(power.BatteryFlag & 8)!=0;
        }
        foreach(var tab in _selected?.Tabs ?? [])
        {
            var pane=tab.Root.Leaves().FirstOrDefault(p=>p.Id==tab.FocusedPaneId) ?? tab.Root.Leaves().First();
            request.Tabs.Add(new() { ["id"]=tab.Id,["name"]=tab.Name,["session"]=_selected!.Name,["is_active"]=tab==_selected.ActiveTab,
                ["panes"]=tab.Root.Leaves().Count(),["attention_count"]=tab.Root.Leaves().Count(p=>p.Attention),["has_attention"]=tab.Root.Leaves().Any(p=>p.Attention),
                ["title"]=pane.Session?.Title ?? tab.Name,["directory"]=pane.Session?.Directory ?? pane.Directory,
                ["shell"]=pane.Command,["url"]=pane.IsBrowser?pane.BrowserUrl:"",["has_exited"]=pane.Session?.HasExited ?? false });
        }
        try
        {
            var result=await runtime.Invoke(request);
            if (_closed || !ReferenceEquals(runtime,_luaRuntime)) return;
            if (result.Error is not null) throw new InvalidOperationException(result.Error);
            if (paneId==ActivePaneId && sessionId==_selected?.Id) _luaPresentation=result;
            ApplyLuaTabTitles(); UpdateStatusBar();
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or InvalidOperationException or ArgumentException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            if (!_closed && ReferenceEquals(runtime,_luaRuntime))
            {
                InstallLuaRuntime(null); UpdateStatusBar();
                ConfigError("Lua formatting disabled; terminal sessions are unaffected. " + (ex is OperationCanceledException ? "A callback exceeded its 500 ms execution limit." : ex.Message));
            }
        }
        finally { _luaPresentationBusy=false; if(_luaPresentationPending) QueueLuaPresentation(); }
    }
    private void ApplyLuaTabTitles()
    {
        if(_selected is null) return;
        foreach(var tab in _selected.Tabs)
        {
            if(!_tabCards.TryGetValue(tab.Id,out var card)) continue;
            if(_luaPresentation.Titles.TryGetValue(tab.Id,out var title)) ApplyLuaText(card.Label,title);
            else { card.Label.Inlines.Clear(); card.Label.Text=tab.Name; }
        }
    }
    private void ApplyLuaText(TextBlock block,IEnumerable<LuaTextSegment> segments)
    {
        block.Text=""; block.Inlines.Clear();
        foreach(var segment in segments)
        {
            var run=new Run { Text=segment.Text,FontWeight=segment.Bold?Microsoft.UI.Text.FontWeights.Bold:Microsoft.UI.Text.FontWeights.Normal,
                FontStyle=segment.Italic?Windows.UI.Text.FontStyle.Italic:Windows.UI.Text.FontStyle.Normal };
            run.FontFamily=new Microsoft.UI.Xaml.Media.FontFamily(TerminalFont.Resolve(_saved.Settings.FontFamily,(segment.Bold?1u:0)|(segment.Italic?2u:0),_saved.Settings.FontWeight));
            if(SessionTheme.TryColor(segment.Foreground,out uint color)) run.Foreground=Chrome.Brush(color);
            block.Inlines.Add(run);
        }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLineStatus,BatteryFlag,BatteryLifePercent,SystemStatusFlag; public uint BatteryLifeTime,BatteryFullLifeTime; }
    [System.Runtime.InteropServices.DllImport("kernel32")] [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
