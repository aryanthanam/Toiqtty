using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oroterm.Models;

namespace Oroterm.Browser;

public sealed record SharedCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly,
    bool HostOnly, bool Session, double? ExpirationDate, string SameSite, JsonElement? PartitionKey = null)
{
    public bool Matches(Uri origin) => HostOnly ? origin.Host.Equals(Domain, StringComparison.OrdinalIgnoreCase) :
        origin.Host.Equals(Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase) || origin.Host.EndsWith("." + Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
    public bool ValidFor(Uri origin) => Name.Length is > 0 and <= 4096 && Value.Length <= 16384 && Domain.Length is > 0 and <= 253 &&
        Uri.CheckHostName(Domain.TrimStart('.')) != UriHostNameType.Unknown && (Domain.TrimStart('.').Contains('.') || ValidLocal(origin));
    private bool ValidLocal(Uri origin) => Domain is "localhost" or "127.0.0.1" && origin.Host == Domain;
    public void Validate(Uri origin)
    {
        if (!ValidFor(origin) || !Matches(origin) || Path.Length is < 1 or > 4096 || !Path.StartsWith('/') ||
            Name.Any(c => c < 33 || c is ';' or '=' or '\u007f') || Value.Any(c => c is '\r' or '\n' or '\0') ||
            SameSite is not ("unspecified" or "no_restriction" or "lax" or "strict") ||
            (!Session && (ExpirationDate is null || !double.IsFinite(ExpirationDate.Value) || ExpirationDate < 0 || ExpirationDate > 253402300799)))
            throw new ArgumentException("Invalid cookie snapshot.");
    }
}

public sealed class BrowserSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Browser profile";
    public string ProtectedToken { get; set; } = "";
    public string[] Origins { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset? LastSync { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] internal string Token { get; set; } = "";
    public override string ToString() => Name;
}

public sealed record CookieSnapshot(string SourceId, string Origin, SharedCookie[] Cookies, int Skipped, int Size = 0);

// A small, bounded HTTP endpoint. It binds only to loopback, accepts extension
// origins, and authenticates every request. No cookies or credentials are logged.
public sealed class BrowserCookieBridge : IDisposable
{
    private sealed class Registry
    {
        public int Port { get; set; }
        public List<BrowserSource> Sources { get; set; } = [];
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _file;
    private readonly Registry _registry;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(8);
    private readonly Dictionary<(string Source, string Origin), CookieSnapshot> _latest = [];
    private int _snapshotBytes;
    private string _pairingToken = "";
    private DateTimeOffset _pairingExpires;
    public event Action<CookieSnapshot>? CookiesReceived;
    public event Action? SourcesChanged;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public BrowserSource[] Sources { get { lock (_gate) return _registry.Sources.ToArray(); } }

    public BrowserCookieBridge(string folder)
    {
        Directory.CreateDirectory(folder);
        _file = System.IO.Path.Combine(folder, "browser-connections.json");
        if (File.Exists(_file) && new FileInfo(_file).Length > 1024 * 1024) throw new InvalidDataException("Browser connections file is too large.");
        _registry = File.Exists(_file) ? JsonSerializer.Deserialize<Registry>(File.ReadAllText(_file), Json) ?? new() : new();
        if (_registry.Sources is null || _registry.Sources.Count > 64 || _registry.Port is < 0 or > 65535) throw new InvalidDataException("Invalid browser connections.");
        var ids = new HashSet<string>();
        foreach (var source in _registry.Sources)
        {
            if (source is null || source.Id is null || !Guid.TryParseExact(source.Id, "N", out _) || !ids.Add(source.Id) || source.Name is null || source.Name.Length is < 1 or > 100 ||
                source.ProtectedToken is null || source.ProtectedToken.Length is < 1 or > 8192 || source.Origins is null || source.Origins.Length > 64)
                throw new InvalidDataException("Invalid browser connection.");
            source.Token = BrowserSecret.Unprotect(source.ProtectedToken);
            if (source.Token.Length != 64 || !source.Token.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Invalid browser connection token.");
            source.Origins = source.Origins.Select(BrowserAddress.Origin).Distinct().ToArray();
        }
        _listener = new(IPAddress.Loopback, _registry.Port);
        _listener.Start(8); _registry.Port = Port;
        try { Save(); } catch { _listener.Stop(); throw; }
        _ = Listen();
    }

    public string BeginPairing()
    {
        lock (_gate)
        {
            _pairingToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _pairingExpires = DateTimeOffset.UtcNow.AddMinutes(10);
            return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { port = Port, token = _pairingToken }, Json));
        }
    }
    public void EndPairing() { lock (_gate) _pairingToken = ""; }
    public void Configure(string id, string name, IEnumerable<string> origins)
    {
        var sites = origins.Select(BrowserAddress.Origin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sites.Length > 64 || string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("Use a profile name and at most 64 sites.");
        lock (_gate)
        {
            var source = _registry.Sources.Single(s => s.Id == id);
            source.Name = name.Trim(); source.Origins = sites;
            foreach (var key in _latest.Keys.Where(k => k.Source == id && !sites.Contains(k.Origin)).ToArray()) RemoveSnapshot(key);
            Save();
        }
        SourcesChanged?.Invoke();
    }
    public void Disconnect(string id)
    {
        lock (_gate)
        {
            _registry.Sources.RemoveAll(s => s.Id == id);
            foreach (var key in _latest.Keys.Where(k => k.Source == id).ToArray()) RemoveSnapshot(key);
            Save();
        }
        SourcesChanged?.Invoke();
    }
    public CookieSnapshot[] Latest(string id) { lock (_gate) return _latest.Where(p => p.Key.Source == id).Select(p => p.Value).ToArray(); }
    public bool IsAllowed(string id, string origin) { lock (_gate) return _registry.Sources.Any(s => s.Id == id && s.Origins.Contains(origin)); }
    private void RemoveSnapshot((string Source, string Origin) key) { if (_latest.Remove(key, out var snapshot)) _snapshotBytes -= snapshot.Size; }

    private void Save()
    {
        string temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_registry, Json));
        File.Move(temporary, _file, true);
    }
    private async Task Listen()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
                catch { _slots.Release(); throw; }
                _ = Handle(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Handle(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream(); var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                if (header.Count >= 16384) { await Reply(stream, 413, new { error = "Request too large" }, timeout.Token).ConfigureAwait(false); return; }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var request = lines[0].Split(' ');
                if (request.Length != 3) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) return;
                }
                // Drain bounded request bodies before sending an error. Closing
                // a socket with unread data can reset even a valid HTTP response.
                byte[] body = [];
                if (!headers.ContainsKey("Transfer-Encoding") && int.TryParse(headers.GetValueOrDefault("Content-Length"), out int length) && length is >= 0 and <= 2097152)
                { body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false); }
                if (headers.GetValueOrDefault("Host") != $"127.0.0.1:{Port}" || headers.ContainsKey("Transfer-Encoding") ||
                    (headers.TryGetValue("Origin", out string? origin) && !origin.StartsWith("chrome-extension://", StringComparison.Ordinal) && !origin.StartsWith("moz-extension://", StringComparison.Ordinal)))
                { await Reply(stream, 403, new { error = "Extension requests only" }, timeout.Token).ConfigureAwait(false); return; }
                string token = headers.GetValueOrDefault("Authorization", "");
                if (!token.StartsWith("Bearer ", StringComparison.Ordinal)) { await Reply(stream, 401, new { error = "Unauthorized" }, timeout.Token).ConfigureAwait(false); return; }
                token = token[7..];
                BrowserSource? source;
                bool pairing;
                lock (_gate)
                {
                    pairing = request[1] == "/pair" && _pairingToken.Length > 0 && _pairingExpires > DateTimeOffset.UtcNow && Equal(token, _pairingToken);
                    source = _registry.Sources.FirstOrDefault(s => Equal(token, s.Token));
                }
                if (!pairing && source is null) { await Reply(stream, 401, new { error = "Connection expired or revoked" }, timeout.Token).ConfigureAwait(false); return; }
                if (request[0] == "GET" && request[1] == "/config" && source is not null)
                {
                    string[] sites; lock (_gate) sites = source.Origins.ToArray();
                    await Reply(stream, 200, new { sourceId = source.Id, origins = sites }, timeout.Token).ConfigureAwait(false); return;
                }
                if (request[0] != "POST" || !int.TryParse(headers.GetValueOrDefault("Content-Length"), out int size) || size is < 2 or > 2097152 ||
                    !headers.GetValueOrDefault("Content-Type", "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                { await Reply(stream, 400, new { error = "Expected a bounded JSON request" }, timeout.Token).ConfigureAwait(false); return; }
                using var document = JsonDocument.Parse(body);
                if (request[1] == "/pair" && pairing)
                {
                    string name = document.RootElement.GetProperty("name").GetString() ?? "";
                    if (name.Length is < 1 or > 100) throw new ArgumentException("Enter a browser profile name.");
                    lock (_gate)
                    {
                        if (_registry.Sources.Count >= 64 || !Equal(token, _pairingToken)) throw new ArgumentException("Pairing is unavailable.");
                        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                        source = new() { Name = name, Token = secret, ProtectedToken = BrowserSecret.Protect(secret) };
                        _registry.Sources.Add(source); _pairingToken = ""; Save();
                    }
                    SourcesChanged?.Invoke();
                    await Reply(stream, 200, new { sourceId = source.Id, token = source.Token }, timeout.Token).ConfigureAwait(false); return;
                }
                if (request[1] != "/cookies" || source is null) { await Reply(stream, 404, new { error = "Unknown endpoint" }, timeout.Token).ConfigureAwait(false); return; }
                string site = BrowserAddress.Origin(document.RootElement.GetProperty("origin").GetString() ?? "");
                var cookies = document.RootElement.GetProperty("cookies").Deserialize<SharedCookie[]>(Json) ?? [];
                if (cookies.Length > 4096) throw new ArgumentException("Too many cookies.");
                var uri = new Uri(site); int skipped = 0;
                foreach (var cookie in cookies) { cookie.Validate(uri); if (cookie.PartitionKey is { ValueKind: not JsonValueKind.Null }) skipped++; }
                var snapshot = new CookieSnapshot(source.Id, site, cookies.Where(c => c.PartitionKey is null or { ValueKind: JsonValueKind.Null }).ToArray(), skipped, body.Length);
                lock (_gate)
                {
                    if (!_registry.Sources.Contains(source) || !source.Origins.Contains(site)) throw new ArgumentException("This site is not enabled for this connection.");
                    int previous = _latest.GetValueOrDefault((source.Id, site))?.Size ?? 0;
                    if (_snapshotBytes - previous + snapshot.Size > 16 * 1024 * 1024) throw new ArgumentException("Browser cookie cache is full.");
                    _snapshotBytes += snapshot.Size - previous;
                    _latest[(source.Id, site)] = snapshot; source.LastSync = DateTimeOffset.UtcNow;
                }
                CookiesReceived?.Invoke(snapshot);
                await Reply(stream, 200, new { accepted = snapshot.Cookies.Length, skipped }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException)
            {
                try { await Reply(client.GetStream(), 400, new { error = "Invalid browser data or site is not enabled" }, timeout.Token).ConfigureAwait(false); }
                catch (Exception nested) when (nested is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            finally { _slots.Release(); }
        }
    }
    private static bool Equal(string a, string b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    private static async Task Reply(NetworkStream stream, int status, object body, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}
