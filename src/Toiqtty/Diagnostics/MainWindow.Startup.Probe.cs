using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunStartupProbe()
    {
        try
        {
            string expected = Environment.GetEnvironmentVariable("TOIQTTY_STARTUP_EXPECT") ?? "PS ";
            var until = DateTime.UtcNow.AddSeconds(20);
            bool browserOnly=_saved.Workspaces.SelectMany(w=>w.AllPanes()).All(p=>p.IsBrowser);
            if(browserOnly) foreach(var browser in _browsers.Values) await browser.Initialize();
            while (!browserOnly && (_focused is null || _focused.FramesPainted == 0 || !_focused.RenderedText.Contains(expected)))
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("The startup prompt did not render.");
                await Task.Delay(10);
            }
            StartupTrace.Mark("prompt-ready");
            if (Environment.GetEnvironmentVariable("TOIQTTY_STARTUP_EXPECT_TAB_TITLE") is { } title)
            {
                while (_selected is null || !_tabCards.TryGetValue(_selected.ActiveTab.Id, out var card)
                    || !card.Label.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Any(run => run.Text == title))
                {
                    if (DateTime.UtcNow > until || _luaError is not null) throw new InvalidOperationException("Initial Lua hook did not format the tab: " + _luaError);
                    await Task.Delay(10);
                }
                if (_luaRuntime is null) throw new InvalidOperationException("Initial Lua callback worker was not retained.");
                StartupTrace.Mark("initial-lua-hook-ready");
            }
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
