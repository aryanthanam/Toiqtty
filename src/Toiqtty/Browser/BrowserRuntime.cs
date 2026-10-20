using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Toiqtty.Models;

namespace Toiqtty.Browser;

internal sealed class BrowserRuntime : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, List<CoreWebView2>> _profiles = [];
    private readonly Dictionary<string, SemaphoreSlim> _locks = [];
    private Task<CoreWebView2Environment>? _environment;
    private bool _disposed;
    private long _cookieRevision;
    private int _syncScheduled;
    public BrowserCookieBridge? Bridge { get; }
    public string? BridgeError { get; }
    public event Action<string, bool>? Status;
    public BrowserRuntime(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        try
        {
            Bridge = new(SessionStore.Folder);
            Bridge.CookiesReceived += _ => { Interlocked.Increment(ref _cookieRevision); ScheduleSync(); };
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or FormatException or ArgumentException)
        { BridgeError = "Browser cookie sharing unavailable: " + ex.Message; }
    }
    private void ScheduleSync()
    {
        if (Interlocked.Exchange(ref _syncScheduled, 1) == 0 && !_dispatcher.TryEnqueue(() => _ = DrainSync())) Interlocked.Exchange(ref _syncScheduled, 0);
    }
    private async Task DrainSync()
    {
        long observed = -1;
        try
        {
            while (!_disposed && observed != Interlocked.Read(ref _cookieRevision))
            {
                observed = Interlocked.Read(ref _cookieRevision);
                foreach (var source in Bridge?.Sources ?? []) foreach (var snapshot in Bridge!.Latest(source.Id)) await Apply(snapshot);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _syncScheduled, 0);
            if (!_disposed && observed != Interlocked.Read(ref _cookieRevision)) ScheduleSync();
        }
    }
    public Task<CoreWebView2Environment> Environment() => _environment ??= CreateEnvironment();
    private static async Task<CoreWebView2Environment> CreateEnvironment() => await CoreWebView2Environment.CreateWithOptionsAsync(
        null, Path.Combine(SessionStore.Folder, "BrowserData"), null);

    public async Task Register(string source, CoreWebView2 core)
    {
        if (!_profiles.TryGetValue(source, out var views)) _profiles[source] = views = [];
        views.Add(core);
        foreach (var snapshot in Bridge?.Latest(source) ?? []) await Apply(snapshot);
    }
    public void Unregister(string source, CoreWebView2? core)
    {
        if (core is not null && _profiles.TryGetValue(source, out var views)) views.Remove(core);
    }
    private async Task Apply(CookieSnapshot snapshot)
    {
        if (_disposed || Bridge?.IsAllowed(snapshot.SourceId, snapshot.Origin) != true || !_profiles.TryGetValue(snapshot.SourceId, out var views) || views.Count == 0) return;
        if (!_locks.TryGetValue(snapshot.SourceId, out var gate)) _locks[snapshot.SourceId] = gate = new(1);
        await gate.WaitAsync();
        try
        {
            if (_disposed || !Bridge.IsAllowed(snapshot.SourceId, snapshot.Origin) || views.Count == 0 || !Bridge.Latest(snapshot.SourceId).Contains(snapshot)) return;
            var manager = views[0].CookieManager;
            var existing = await manager.GetCookiesAsync(null);
            if (_disposed || !Bridge.IsAllowed(snapshot.SourceId, snapshot.Origin)) return;
            var site = new Uri(snapshot.Origin);
            var incoming = new HashSet<(string, string, string)>();
            // Add/update first, so a rejected cookie cannot erase a working login.
            foreach (var item in snapshot.Cookies)
            {
                if (!item.Session && item.ExpirationDate <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
                var cookie = manager.CreateCookie(item.Name, item.Value, item.HostOnly ? item.Domain.TrimStart('.') : "." + item.Domain.TrimStart('.'), item.Path);
                cookie.IsSecure = item.Secure; cookie.IsHttpOnly = item.HttpOnly;
                cookie.SameSite = item.SameSite switch { "strict" => CoreWebView2CookieSameSiteKind.Strict, "no_restriction" => CoreWebView2CookieSameSiteKind.None, _ => CoreWebView2CookieSameSiteKind.Lax };
                if (!item.Session) cookie.Expires = item.ExpirationDate!.Value;
                manager.AddOrUpdateCookie(cookie);
                incoming.Add((cookie.Name, cookie.Domain, cookie.Path));
            }
            // Full site snapshots also propagate source-side cookie deletion.
            foreach (var cookie in existing)
            {
                var item = new SharedCookie(cookie.Name, "", cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly, !cookie.Domain.StartsWith('.'), true, null, "unspecified");
                if (item.Matches(site) && !incoming.Contains((cookie.Name, cookie.Domain, cookie.Path))) manager.DeleteCookie(cookie);
            }
            Status?.Invoke($"Cookies synced from {Bridge.Sources.FirstOrDefault(s => s.Id == snapshot.SourceId)?.Name} · {snapshot.Origin}" +
                (snapshot.Skipped > 0 ? $" · {snapshot.Skipped} partitioned cookies unsupported" : ""), false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        { Status?.Invoke("Could not apply shared cookies: " + ex.Message, true); }
        finally { gate.Release(); }
    }
    public void Dispose() { _disposed = true; Bridge?.Dispose(); }
}
