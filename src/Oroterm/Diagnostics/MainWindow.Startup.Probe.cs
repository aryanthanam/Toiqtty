using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunStartupProbe()
    {
        try
        {
            string expected = Environment.GetEnvironmentVariable("OROTERM_STARTUP_EXPECT") ?? "PS ";
            var until = DateTime.UtcNow.AddSeconds(20);
            bool browserOnly=_saved.Workspaces.SelectMany(w=>w.AllPanes()).All(p=>p.IsBrowser);
            if(browserOnly) foreach(var browser in _browsers.Values) await browser.Initialize();
            while (!browserOnly && (_focused is null || _focused.FramesPainted == 0 || !_focused.RenderedText.Contains(expected)))
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("The startup prompt did not render.");
                await Task.Delay(10);
            }
            StartupTrace.Mark("prompt-ready");
            File.WriteAllText(Path.Combine(SessionStore.Folder,"startup-layout.json"),System.Text.Json.JsonSerializer.Serialize(new {
                reattached=_reattached,host=_host?.Identity,fontSize=_saved.Settings.FontSize,shell=_saved.Settings.Shell,configError=_luaError,
                sessions=_saved.Workspaces.Select(workspace=>new { workspace.Id,workspace.Name,
                    tabs=workspace.Tabs.Select(tab=>new { tab.Id,panes=tab.Root.Leaves().Select(pane=>new { pane.Id,pane.Kind }) }) })
            }));
            StartupTrace.Finish(true);
        }
        catch (Exception ex) { StartupTrace.Finish(false, ex.ToString()); }
        finally { Close(); }
    }
}
