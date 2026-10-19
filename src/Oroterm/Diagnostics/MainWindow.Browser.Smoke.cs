using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Oroterm.Browser;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunBrowserSmoke()
    {
        var checks = new List<string>(); string? error = null;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        async Task Wait(Func<Task<bool>> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!await condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Browser check timed out."); await Task.Delay(100); }
        }
        using var fixture = new BrowserFixture();
        try
        {
            var bridge = _browserRuntime.Bridge ?? throw new InvalidOperationException(_browserRuntime.BridgeError);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bridge.Port}") };
            var pairing = JsonDocument.Parse(Convert.FromBase64String(bridge.BeginPairing()));
            http.DefaultRequestHeaders.Authorization = new("Bearer", pairing.RootElement.GetProperty("token").GetString());
            async Task<JsonElement> Post(string path, object data)
            {
                using var response = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            }
            var paired = await Post("/pair", new { name = "Browser smoke fixture" });
            string source = paired.GetProperty("sourceId").GetString()!;
            http.DefaultRequestHeaders.Authorization = new("Bearer", paired.GetProperty("token").GetString());
            bridge.Configure(source, "Browser smoke fixture", [fixture.Origin]);
            NewBrowser(fixture.Origin + "/first", split: true, sourceId: source);
            var workspace = _selected!; var tab = workspace.ActiveTab;
            var browser = _browsers.Values.Single();
            await browser.Initialize();
            Check(browser.Web.CoreWebView2 is not null, "real WinUI WebView2 initializes with the Oroterm data folder");
            var core = browser.Web.CoreWebView2!;
            await Wait(async () => await core.ExecuteScriptAsync("document.title") == "\"Oroterm browser fixture /first\"");
            Check(workspace.Root.Leaves().Count() == 2 && workspace.Root.Leaves().Single(p => p.IsBrowser).Session is null && _views.Count == 1,
                "mixed split displays a real browser beside a shell without allocating a browser PTY");
            await core.ExecuteScriptAsync("window.orotermRetainedState = 'tab-state'; localStorage.setItem('fixture-storage','persistent')");
            NewTab(); SelectTab(tab);
            Check(ReferenceEquals(browser, _browsers[browser.PaneId]) && await core.ExecuteScriptAsync("window.orotermRetainedState") == "\"tab-state\"",
                "switching tabs preserves the live WebView and in-page state");
            browser.Navigate(fixture.Origin + "/second");
            await Wait(async () => await core.ExecuteScriptAsync("document.title") == "\"Oroterm browser fixture /second\"");
            Check(core.CanGoBack, "embedded navigation records page history");
            core.GoBack(); await Wait(async () => await core.ExecuteScriptAsync("document.title") == "\"Oroterm browser fixture /first\"");
            Check(core.CanGoForward, "embedded back navigation retains forward history");
            var cookie = new SharedCookie("source_login", "fixture-login", "127.0.0.1", "/", false, true, true, false,
                DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), "lax");
            await Post("/cookies", new { origin = fixture.Origin, cookies = new[] { cookie } });
            await Wait(async () => (await core.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Name == "source_login" && c.Value == "fixture-login"));
            var received = (await core.CookieManager.GetCookiesAsync(fixture.Origin)).Single(c => c.Name == "source_login");
            Check(received.IsHttpOnly && !received.IsSecure && received.SameSite == Microsoft.Web.WebView2.Core.CoreWebView2CookieSameSiteKind.Lax,
                "companion cookie snapshots reach the real WebView2 cookie store with HttpOnly and SameSite attributes");
            browser.Navigate(fixture.Origin + "/cookie-request");
            await Wait(async () => (await core.ExecuteScriptAsync("document.body.innerText")).Contains("source_login=fixture-login"));
            Check(!(await core.ExecuteScriptAsync("document.cookie")).Contains("source_login"), "imported HttpOnly cookies reach HTTP requests while remaining hidden from page JavaScript");
            browser.FocusBrowser();
            string oldId = browser.PaneId;
            NewBrowser(fixture.Origin + "/nested", split: true, vertical: false, sourceId: "");
            var independent = _browsers.Values.Single(b => b.PaneId != oldId);
            await independent.Initialize();
            Check(workspace.Root.Leaves().Count() == 3 && _browsers.Count == 2 && ReferenceEquals(browser, _browsers[oldId]),
                "nested browser splits preserve an existing browser through leaf cloning");
            Check(!(await independent.Web.CoreWebView2.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Name == "source_login"),
                "independent WebView2 profiles cannot see connected-browser cookies");
            browser.Navigate(fixture.Origin + "/saved-after-split");
            await Wait(async () => core.Source.EndsWith("/saved-after-split")); Save();
            var savedPane = SessionStore.Load().Workspaces.Single(w => w.Id == workspace.Id).AllPanes().Single(p => p.Id == oldId);
            Check(savedPane.IsBrowser && savedPane.BrowserSourceId == source && savedPane.BrowserUrl.EndsWith("/saved-after-split"),
                "navigation after splitting updates the persisted leaf URL and source profile");
            await Post("/cookies", new { origin = fixture.Origin, cookies = Array.Empty<SharedCookie>() });
            await Wait(async () => !(await core.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Name == "source_login"));
            Check(true, "source-side cookie deletion removes previously imported WebView2 cookies");
            var changed = cookie with { Value = "changed-login" };
            await Post("/cookies", new { origin = fixture.Origin, cookies = new[] { changed } });
            await Wait(async () => (await core.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Value == "changed-login"));
            Check(true, "cookie updates are applied without recreating or navigating the browser pane");
            RecreateBrowser(independent, source);
            var changedProfile = _browsers[independent.PaneId]; await changedProfile.Initialize();
            Check((await changedProfile.Web.CoreWebView2.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Value == "changed-login"),
                "an existing browser pane switches to the selected source profile and its cookies");
            RecreateBrowser(changedProfile, ""); independent = _browsers[independent.PaneId]; await independent.Initialize();
            int tabs = workspace.Tabs.Count;
            await core.ExecuteScriptAsync("window.open('/popup','_blank')");
            await Wait(() => Task.FromResult(workspace.Tabs.Count == tabs + 1));
            Check(workspace.ActiveTab.Root.IsBrowser && workspace.ActiveTab.Root.BrowserSourceId == source,
                "new-window requests become browser tabs with the same source profile");
            SelectTab(tab);
            var independentCore = independent.Web.CoreWebView2;
            await Wait(async () => await independentCore.ExecuteScriptAsync("document.title") == "\"Oroterm browser fixture /nested\"");
            var persist = independentCore.CookieManager.CreateCookie("persistent", "disk-cookie", "127.0.0.1", "/"); persist.Expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(); independentCore.CookieManager.AddOrUpdateCookie(persist);
            await independentCore.ExecuteScriptAsync("localStorage.setItem('fixture-storage','persistent')");
            string independentId = independent.PaneId;
            _browsers.Remove(independentId); independent.Dispose(); RebuildPanes(independentId);
            var recreated = _browsers[independentId]; await recreated.Initialize();
            await Wait(async () => recreated.Web.CoreWebView2.Source.EndsWith("/nested"));
            Check((await recreated.Web.CoreWebView2.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Name == "persistent"),
                "closing and recreating a WebView retains cookies in its persistent profile");
            await Wait(async () => await recreated.Web.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('fixture-storage')") == "\"persistent\"");
            Check(true, "browser-owned local storage survives WebView recreation");
            int shellCount = workspace.AllPanes().Count(p => p.Session is not null);
            ClosePane(independentId);
            Check(!_browsers.ContainsKey(independentId) && workspace.AllPanes().Count(p => p.Session is not null) == shellCount,
                "closing a browser split releases its WebView without stopping sibling shells");
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(Path.Combine(SessionStore.Folder, "browser-smoke.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        Close();
    }

    private sealed class BrowserFixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public string Origin { get; }
        public BrowserFixture(int port = 0) { _listener = new(IPAddress.Loopback, port); _listener.Start(); Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}"; _ = Serve(); }
        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Respond(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        }
        private async Task Respond(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
                    string first = await reader.ReadLineAsync(_stop.Token) ?? ""; string cookies = "";
                    for (int n = 0; n < 64; n++)
                    {
                        string? line = await reader.ReadLineAsync(_stop.Token); if (string.IsNullOrEmpty(line)) break;
                        if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookies = line;
                    }
                    string path = first.Split(' ').ElementAtOrDefault(1) ?? "/";
                    byte[] html = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>Oroterm browser fixture " + WebUtility.HtmlEncode(path) + "</title></head><body><h1>Oroterm browser</h1><p>" + WebUtility.HtmlEncode(cookies) + "</p></body></html>");
                    byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token); await stream.WriteAsync(html, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    private async Task RunBrowserPersistenceProbe(string phase)
    {
        var checks = new List<string>(); string? error = null;
        string identityFile = Path.Combine(SessionStore.Folder, "browser-identity.json");
        void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); checks.Add(text); }
        async Task Wait(Func<Task<bool>> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!await condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Browser persistence check timed out."); await Task.Delay(100); }
        }
        try
        {
            if (phase == "prepare")
            {
                using var fixture = new BrowserFixture();
                var shellTab = _selected!.ActiveTab;
                NewBrowser(fixture.Origin + "/window-persistence", split: true);
                var browser = _browsers.Values.Single(); await browser.Initialize();
                var core = browser.Web.CoreWebView2;
                await Wait(async () => await core.ExecuteScriptAsync("document.title") == "\"Oroterm browser fixture /window-persistence\"");
                var cookie = core.CookieManager.CreateCookie("window_cookie", "window-cookie-fixture", "127.0.0.1", "/");
                cookie.Expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(); core.CookieManager.AddOrUpdateCookie(cookie);
                await core.ExecuteScriptAsync("localStorage.setItem('window_storage','window-storage-fixture')");
                Check(_persistent && _host is not null && _selected.Root.Leaves().Single(p => !p.IsBrowser).Session is not null,
                    "persistent mode owns only the shell side of a mixed browser split");
                File.WriteAllText(identityFile, JsonSerializer.Serialize(new { pane = browser.PaneId, tab = shellTab.Id, host = _host!.Identity, port = new Uri(fixture.Origin).Port }));
                Save();
            }
            else
            {
                using var identity = JsonDocument.Parse(File.ReadAllText(identityFile));
                var value = identity.RootElement;
                using var fixture = new BrowserFixture(value.GetProperty("port").GetInt32());
                string pane = value.GetProperty("pane").GetString()!;
                Check(_reattached && _host?.Identity == value.GetProperty("host").GetString(), "a new window reconnects the shell host while restoring its browser layout");
                var browser = _browsers[pane]; await browser.Initialize(); var core = browser.Web.CoreWebView2;
                await Wait(async () => await core.ExecuteScriptAsync("document.title") == "\"Oroterm browser fixture /window-persistence\"");
                Check(_selected!.ActiveTab.Id == value.GetProperty("tab").GetString() && _selected.Root.Leaves().Count() == 2,
                    "mixed browser and terminal split identities survive a full WinUI process restart");
                Check((await core.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Name == "window_cookie" && c.Value == "window-cookie-fixture"),
                    "persistent browser cookies survive closing and reopening the complete window process");
                Check(await core.ExecuteScriptAsync("localStorage.getItem('window_storage')") == "\"window-storage-fixture\"",
                    "browser-owned local storage survives a complete window restart");
                string shell = _selected.Root.Leaves().Single(p => !p.IsBrowser).Id;
                ClosePane(shell);
                await Wait(() => Task.FromResult(Hosting.SessionHostClient.TryConnect(SessionStore.Folder) is null));
                browser = _browsers[pane]; browser.Navigate(fixture.Origin + "/browser-only-saved");
                await Wait(() => Task.FromResult(SessionStore.Load().Workspaces.Single().Root.BrowserUrl.EndsWith("/browser-only-saved")));
                Check(_selected.Root.IsBrowser && _host is null, "browser-only navigation keeps saving after the final shell and session host have stopped");
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(Path.Combine(SessionStore.Folder, "browser-" + phase + ".json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        Close();
    }
}
