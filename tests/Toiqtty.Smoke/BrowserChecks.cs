using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Toiqtty.Browser;
using Toiqtty.Models;
using Toiqtty.Hosting;
using System.IO.Pipes;

internal static class BrowserChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        check(BrowserAddress.Resolve("localhost:3000/app") == "http://localhost:3000/app", "browser resolves local development addresses without treating the port as a scheme");
        check(BrowserAddress.Resolve("example.com") == "https://example.com", "browser defaults public hostnames to HTTPS");
        check(BrowserAddress.Resolve("terminal browser") == "https://www.bing.com/search?q=terminal%20browser", "browser address bar accepts search queries");
        check(!BrowserAddress.IsAllowed("javascript:alert(1)") && !BrowserAddress.IsAllowed("file:///C:/Windows/win.ini") && !BrowserAddress.IsAllowed("https://user:pass@example.com"), "embedded browser rejects script, file, and credential-bearing navigation");
        check(BrowserAddress.Origin("https://EXAMPLE.com:443/") == "https://example.com", "cookie origins normalize scheme, host, and default port");
        bool bad = false; try { BrowserAddress.Origin("https://example.com/private"); } catch (ArgumentException) { bad = true; }
        check(bad, "cookie sharing requires an origin rather than silently expanding a path to the whole site");
        var leaf = new PaneNode { Kind = "browser", BrowserUrl = "https://example.com/app", BrowserSourceId = "profile-a" };
        string id = leaf.Id; leaf.Split(id, true, "cmd.exe");
        check(leaf.First!.IsBrowser && leaf.First.Id == id && leaf.First.BrowserSourceId == "profile-a" && leaf.First.BrowserUrl.EndsWith("/app") && !leaf.Second!.IsBrowser, "splitting a browser preserves its identity, URL, and profile beside a new shell");
        check(BrowserAddress.ProfileName("a") != BrowserAddress.ProfileName("b") && BrowserAddress.ProfileName("") == "Toiqtty", "browser sources have separate stable WebView2 profiles");
        string folder = Path.Combine(Path.GetTempPath(), "toiqtty-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string? prior = Environment.GetEnvironmentVariable("TOIQTTY_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("TOIQTTY_DATA_DIR", folder);
            SessionStore.Save(new SavedSession { Workspaces = [new Workspace { Root = leaf }] });
            var restored = SessionStore.Load().Workspaces.Single().Root;
            check(restored.First!.IsBrowser && restored.First.BrowserSourceId == "profile-a" && restored.Second!.Command == "cmd.exe", "saved sessions restore mixed browser and terminal layouts");
            using var bridge = new BrowserCookieBridge(folder);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bridge.Port}"), Timeout = TimeSpan.FromSeconds(5) };
            int unproven = 0;
            async Task<HttpResponseMessage> SendWith(HttpClient http, string path, string token, object? body = null, string? origin = null)
            {
                var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
                byte[] bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                string key = token.Length == 64 ? token : new string('0', 64);
                request.Headers.TryAddWithoutValidation("Authorization", BrowserCookieBridge.RequestAuthorization(key, request.Method.Method, path, nonce, bytes));
                if (origin is not null) request.Headers.Add("Origin", origin);
                if (body is not null) { request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; }
                var response = await http.SendAsync(request);
                byte[] reply = await response.Content.ReadAsByteArrayAsync();
                if (response.IsSuccessStatusCode && (!response.Headers.TryGetValues("X-Toiqtty-Proof", out var proofs) ||
                    proofs.Single() != BrowserCookieBridge.ResponseProof(key, nonce, (int)response.StatusCode, reply))) unproven++;
                return response;
            }
            Task<HttpResponseMessage> Send(string path, string token, object? body = null, string? origin = null) => SendWith(client, path, token, body, origin);
            var code = JsonDocument.Parse(Convert.FromBase64String(bridge.BeginPairing()));
            string pairing = code.RootElement.GetProperty("token").GetString()!;
            check(code.RootElement.GetProperty("port").GetInt32() == bridge.Port, "pairing code points only to the active loopback bridge");
            using var denied = await Send("/config", "wrong");
            check(denied.StatusCode == HttpStatusCode.Unauthorized, "cookie bridge rejects unauthenticated requests");
            using var webpage = await Send("/pair", pairing, new { name = "Forbidden page" }, "https://example.com");
            check(webpage.StatusCode == HttpStatusCode.Forbidden, "cookie bridge rejects webpage origins even with a valid pairing token");
            using var paired = await Send("/pair", pairing, new { name = "Chrome · Test" }, "chrome-extension://fixture");
            var result = await paired.Content.ReadFromJsonAsync<JsonElement>();
            check(paired.IsSuccessStatusCode && bridge.Sources.Single().Name == "Chrome · Test", "Chromium extension pairs a named browser profile");
            string token = result.GetProperty("token").GetString()!, source = result.GetProperty("sourceId").GetString()!;
            using var reused = await Send("/pair", pairing, new { name = "Replay" });
            check(reused.StatusCode == HttpStatusCode.Unauthorized, "pairing codes are single use");
            using var config = await Send("/config", token);
            var empty = await config.Content.ReadFromJsonAsync<JsonElement>();
            check(empty.GetProperty("origins").GetArrayLength() == 0, "newly connected profiles share no sites by default");
            bridge.Configure(source, "Chrome · Test", ["https://app.example.com", "http://localhost:3000"]);
            var cookie = new SharedCookie("session", "fixture-only", ".example.com", "/", true, true, false, false, DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), "lax");
            using var accepted = await Send("/cookies", token, new { origin = "https://app.example.com", cookies = new[] { cookie } });
            check(accepted.IsSuccessStatusCode && bridge.Latest(source).Single().Cookies.Single().HttpOnly, "selected-site sync accepts secure HttpOnly parent-domain cookies with their attributes");
            using var wrongSite = await Send("/cookies", token, new { origin = "https://other.example.com", cookies = new[] { cookie } });
            check(wrongSite.StatusCode == HttpStatusCode.BadRequest && bridge.Latest(source).Length == 1, "cookie bridge rejects snapshots for sites outside the configured allowlist");
            using var wrongDomain = await Send("/cookies", token, new { origin = "https://app.example.com", cookies = new[] { cookie with { Domain = ".evil.test" } } });
            check(wrongDomain.StatusCode == HttpStatusCode.BadRequest, "cookie bridge rejects unrelated cookie domains");
            using var wrongHostOnly = await Send("/cookies", token, new { origin = "https://app.example.com", cookies = new[] { cookie with { HostOnly = true, Domain = "example.com" } } });
            check(wrongHostOnly.StatusCode == HttpStatusCode.BadRequest, "host-only cookies cannot be widened to subdomains");
            using var invalid = await Send("/cookies", token, new { origin = "https://app.example.com", cookies = new[] { cookie with { Name = "bad\r\nname" } } });
            check(invalid.StatusCode == HttpStatusCode.BadRequest, "cookie bridge rejects malformed cookie attributes before WebView2 sees them");
            var partition = JsonSerializer.SerializeToElement(new { topLevelSite = "https://example.com" });
            using var partitioned = await Send("/cookies", token, new { origin = "https://app.example.com", cookies = new[] { cookie with { PartitionKey = partition } } });
            check(partitioned.IsSuccessStatusCode && bridge.Latest(source).Single().Skipped == 1 && bridge.Latest(source).Single().Cookies.Length == 0, "unsupported partitioned cookies are reported and never flattened into ordinary cookies");
            using var deleted = await Send("/cookies", token, new { origin = "https://app.example.com", cookies = Array.Empty<SharedCookie>() });
            check(deleted.IsSuccessStatusCode && bridge.Latest(source).Single().Cookies.Length == 0, "empty full snapshots propagate source-side deletion");
            string registry = File.ReadAllText(Path.Combine(folder, "browser-connections.json"));
            check(!registry.Contains(token) && !registry.Contains("fixture-only") && registry.Contains("protectedToken"), "saved browser connections protect tokens with DPAPI and never persist cookie values");
            var secondCode = JsonDocument.Parse(Convert.FromBase64String(bridge.BeginPairing()));
            using var second = await Send("/pair", secondCode.RootElement.GetProperty("token").GetString()!, new { name = "Firefox · Test" }, "moz-extension://fixture");
            var secondResult = await second.Content.ReadFromJsonAsync<JsonElement>();
            string secondToken = secondResult.GetProperty("token").GetString()!;
            check(second.IsSuccessStatusCode && bridge.Sources.Length == 2 && secondToken != token, "Firefox extensions pair with independent connection tokens and browser profiles");
            bridge.Configure(source, "Chrome · Test", []);
            check(bridge.Latest(source).Length == 0 && !bridge.IsAllowed(source, "https://app.example.com"), "removing a site clears pending cookie snapshots and stops further sync");
            bridge.Disconnect(source);
            using var revoked = await Send("/config", token);
            check(revoked.StatusCode == HttpStatusCode.Unauthorized, "disconnecting a browser profile immediately revokes its token");
            int port = bridge.Port; bridge.Dispose();
            using var reopened = new BrowserCookieBridge(folder);
            using var reconnectedClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{reopened.Port}") };
            using var resumed = await SendWith(reconnectedClient, "/config", secondToken);
            check(reopened.Port == port && resumed.IsSuccessStatusCode, "paired profiles reconnect after Toiqtty restarts without new pairing");
            using var bearer = new HttpRequestMessage(HttpMethod.Get, "/config");
            bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secondToken);
            using var bearerReply = await reconnectedClient.SendAsync(bearer);
            check(bearerReply.StatusCode == HttpStatusCode.Unauthorized, "the bridge never accepts a bearer token sent over the socket");
            check(unproven == 0, "every successful bridge reply proves the connection key to the extension");
            string legacyFolder = Path.Combine(folder, "old-host");
            using var oldPipe = new NamedPipeServerStream(HostProtocol.PipeName(legacyFolder), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var oldReply = Task.Run(async () =>
            {
                await oldPipe.WaitForConnectionAsync();
                var request = await HostProtocol.ReadJson<HostRequest>(oldPipe, CancellationToken.None);
                await HostProtocol.WriteJson(oldPipe, new HostReply { Protocol = 1, Host = "legacy", Error = "Incompatible session-host protocol." }, CancellationToken.None);
                return request.Operation;
            });
            bool incompatible = false;
            try { SessionHostClient.TryConnect(legacyFolder); } catch (SessionHostVersionException) { incompatible = true; }
            check(incompatible && await oldReply == "ping", "an older session host raises an explicit version error instead of being treated as missing or receiving layout mutations");
        }
        finally { Environment.SetEnvironmentVariable("TOIQTTY_DATA_DIR", prior); }
    }
}
