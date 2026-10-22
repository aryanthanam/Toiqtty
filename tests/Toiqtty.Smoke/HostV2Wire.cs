using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

// Verbatim protocol-2 envelope as released through toiqtty 0.10.6 (c957788).
// Never edit these types or the golden requests: they are what an installed v2
// host or client parses during the upgrade window. Nested models (settings,
// layouts, status) are shared with the product and are not envelope shape.
internal sealed class V2HostRequest
{
    public int Protocol { get; set; } = 2;
    public string Host { get; set; } = "";
    public string Operation { get; set; } = "ping";
    public string Pane { get; set; } = "";
    public string Text { get; set; } = "";
    public string Directory { get; set; } = "";
    public long[] Numbers { get; set; } = [];
    public ulong[] Images { get; set; } = [];
    public AppSettings? Settings { get; set; }
    public SavedSession? Layout { get; set; }
    public Dictionary<string, byte[]>? Items { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AllowFrameDelta { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AllowStyleRuns { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool ReuseReadConnection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? KnownStatusRevision { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? WatchPanes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, long>? WatchRevisions { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public HostNavigation? Navigation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, int>? StateVersions { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LayoutEpoch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long LayoutRevision { get; set; }
}

internal sealed class V2HostReply
{
    public int Protocol { get; set; } = 2;
    public string Host { get; set; } = "";
    public int ProcessId { get; set; }
    public string? Error { get; set; }
    public string Text { get; set; } = "";
    public long[] Numbers { get; set; } = [];
    public V2ShellState? State { get; set; }
    public SavedSession? Layout { get; set; }
    public string[] Panes { get; set; } = [];
    public int CapturedVersion { get; set; } = -1;
    public long FrameRevision { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? DeltaBaseRevision { get; set; }
    public TerminalScrollState? Scrollbar { get; set; }
    public int BinaryLength { get; set; }
    public string[] Capabilities { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, long>? OutputRevisions { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, V2ShellState>? States { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long TransportAcceptRetries { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool KeepAlive { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? StatusRevision { get; set; }
    [JsonIgnore] public byte[] Binary { get; set; } = [];
}

internal sealed record V2ShellState(int Version, bool HasExited, int ExitCode, string Title, string StatusSummary,
    string Directory, string WorkingDirectory, string? LocalDirectory, TerminalPerformance Performance,
    TerminalStatusSnapshot? Status, long Attention, bool PendingAttention)
{
    public bool? MouseTracking { get; init; }
}

[JsonSerializable(typeof(V2HostRequest))]
[JsonSerializable(typeof(V2HostReply))]
internal partial class HostV2WireContext : JsonSerializerContext;

/// <summary>Wire-compatibility fixture for the life of protocol 3: v2 request
/// bytes must reach the current host's protocol check, and current request and
/// reply bytes must parse as v2 so an old peer answers or reports a mismatch
/// instead of dropping the pipe.</summary>
internal static class HostV2Wire
{
    private const string Identity = "h0st";
    private static HostRequest Pane(string operation, long[]? numbers = null, string text = "")
        => new() { Host = Identity, Operation = operation, Pane = "p1", Numbers = numbers ?? [], Text = text };
    private static HostRequest Capture(long[] numbers, long status)
    {
        // RemoteTerminalSession.Request("capture") plus the poll/read-connection fields.
        var request = Pane("capture", numbers); request.AllowStyleRuns = true; request.KnownStatusRevision = status;
        if (numbers[0] >= 0) { request.AllowFrameDelta = true; request.Images = [11]; request.ReuseReadConnection = true; }
        return request;
    }
    private static Dictionary<string, byte[]> Items() => new() { ["text/plain"] = [104, 105] };
    // One request per operation, shaped as today's client call sites build them,
    // with the frozen protocol-2 bytes each one produced at c957788. Settings and
    // Layout bodies are normalized to {}: they are shared models, not envelope.
    internal static readonly (string Label, Func<HostRequest> Build, string Golden)[] Requests =
    [
        ("ping", () => new(), """{"Protocol":2,"Host":"","Operation":"ping","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("states", () => new() { Host = Identity, Operation = "states", StateVersions = new() { ["p1"] = 3 } }, """{"Protocol":2,"Host":"h0st","Operation":"states","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null,"StateVersions":{"p1":3}}"""),
        ("watch", () => new() { Host = Identity, Operation = "watch", Numbers = [9] }, """{"Protocol":2,"Host":"h0st","Operation":"watch","Pane":"","Text":"","Directory":"","Numbers":[9],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("watch-panes", () => new() { Host = Identity, Operation = "watch", Numbers = [9], WatchPanes = ["p1"], WatchRevisions = new() { ["p1"] = 4 }, ReuseReadConnection = true }, """{"Protocol":2,"Host":"h0st","Operation":"watch","Pane":"","Text":"","Directory":"","Numbers":[9],"Images":[],"Settings":null,"Layout":null,"Items":null,"ReuseReadConnection":true,"WatchPanes":["p1"],"WatchRevisions":{"p1":4}}"""),
        ("navigate", () => new() { Host = Identity, Operation = "navigate", Navigation = new("w1", [new("w1", "t1", new() { ["t1"] = "p1" })]) }, """{"Protocol":2,"Host":"h0st","Operation":"navigate","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null,"Navigation":{"SelectedWorkspace":"w1","Workspaces":[{"Id":"w1","ActiveTabId":"t1","FocusedPanes":{"t1":"p1"}}]}}"""),
        ("sync", () => new() { Host = Identity, Operation = "sync", Layout = new(), LayoutEpoch = "e1", LayoutRevision = 2 }, """{"Protocol":2,"Host":"h0st","Operation":"sync","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":{},"Items":null,"LayoutEpoch":"e1","LayoutRevision":2}"""),
        ("shutdown", () => new() { Host = Identity, Operation = "shutdown" }, """{"Protocol":2,"Host":"h0st","Operation":"shutdown","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("detach", () => new() { Host = Identity, Operation = "detach" }, """{"Protocol":2,"Host":"h0st","Operation":"detach","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("create", () => new() { Host = Identity, Operation = "create", Pane = "p1", Text = "cmd.exe", Directory = @"C:\work", Settings = new(), LayoutEpoch = "e1", LayoutRevision = 1 }, """{"Protocol":2,"Host":"h0st","Operation":"create","Pane":"p1","Text":"cmd.exe","Directory":"C:\\work","Numbers":[],"Images":[],"Settings":{},"Layout":null,"Items":null,"LayoutEpoch":"e1","LayoutRevision":1}"""),
        ("create-layout", () => new() { Host = Identity, Operation = "create", Pane = "p1", Text = "cmd.exe", Directory = @"C:\work", Settings = new(), Layout = new(), LayoutEpoch = "e1", LayoutRevision = 3 }, """{"Protocol":2,"Host":"h0st","Operation":"create","Pane":"p1","Text":"cmd.exe","Directory":"C:\\work","Numbers":[],"Images":[],"Settings":{},"Layout":{},"Items":null,"LayoutEpoch":"e1","LayoutRevision":3}"""),
        ("close", () => Pane("close"), """{"Protocol":2,"Host":"h0st","Operation":"close","Pane":"p1","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("state", () => Pane("state", [3, 7]), """{"Protocol":2,"Host":"h0st","Operation":"state","Pane":"p1","Text":"","Directory":"","Numbers":[3,7],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("capture", () => Capture([3, 7], 5), """{"Protocol":2,"Host":"h0st","Operation":"capture","Pane":"p1","Text":"","Directory":"","Numbers":[3,7],"Images":[11],"Settings":null,"Layout":null,"Items":null,"AllowFrameDelta":true,"AllowStyleRuns":true,"ReuseReadConnection":true,"KnownStatusRevision":5}"""),
        ("capture-full", () => Capture([-1], -1), """{"Protocol":2,"Host":"h0st","Operation":"capture","Pane":"p1","Text":"","Directory":"","Numbers":[-1],"Images":[],"Settings":null,"Layout":null,"Items":null,"AllowStyleRuns":true,"KnownStatusRevision":-1}"""),
        ("resize", () => Pane("resize", [100, 30, 9, 19]), """{"Protocol":2,"Host":"h0st","Operation":"resize","Pane":"p1","Text":"","Directory":"","Numbers":[100,30,9,19],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("key", () => Pane("key", [65, 2, 1, -1, 0], "a"), """{"Protocol":2,"Host":"h0st","Operation":"key","Pane":"p1","Text":"a","Directory":"","Numbers":[65,2,1,-1,0],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("paste", () => Pane("paste", [1], "x"), """{"Protocol":2,"Host":"h0st","Operation":"paste","Pane":"p1","Text":"x","Directory":"","Numbers":[1],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("send-text", () => Pane("send-text", text: "x"), """{"Protocol":2,"Host":"h0st","Operation":"send-text","Pane":"p1","Text":"x","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("paste-checked", () => Pane("paste-checked", [0], "x"), """{"Protocol":2,"Host":"h0st","Operation":"paste-checked","Pane":"p1","Text":"x","Directory":"","Numbers":[0],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("clipboard", () => Pane("clipboard"), """{"Protocol":2,"Host":"h0st","Operation":"clipboard","Pane":"p1","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("scroll", () => Pane("scroll", [-3]), """{"Protocol":2,"Host":"h0st","Operation":"scroll","Pane":"p1","Text":"","Directory":"","Numbers":[-3],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("scroll-to", () => Pane("scroll-to", [42]), """{"Protocol":2,"Host":"h0st","Operation":"scroll-to","Pane":"p1","Text":"","Directory":"","Numbers":[42],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("focus", () => Pane("focus", [1]), """{"Protocol":2,"Host":"h0st","Operation":"focus","Pane":"p1","Text":"","Directory":"","Numbers":[1],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("select", () => Pane("select", [-1, -1]), """{"Protocol":2,"Host":"h0st","Operation":"select","Pane":"p1","Text":"","Directory":"","Numbers":[-1,-1],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("gesture", () => Pane("gesture", [0, 1500, 2250, 0]), """{"Protocol":2,"Host":"h0st","Operation":"gesture","Pane":"p1","Text":"","Directory":"","Numbers":[0,1500,2250,0],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("drop", () => { var r = Pane("drop", [1, 10, 20, 3]); r.Items = Items(); return r; }, """{"Protocol":2,"Host":"h0st","Operation":"drop","Pane":"p1","Text":"","Directory":"","Numbers":[1,10,20,3],"Images":[],"Settings":null,"Layout":null,"Items":{"text/plain":"aGk="}}"""),
        ("drop-async", () => { var r = Pane("drop-async", [10, 20, 3, 15000]); r.Items = Items(); return r; }, """{"Protocol":2,"Host":"h0st","Operation":"drop-async","Pane":"p1","Text":"","Directory":"","Numbers":[10,20,3,15000],"Images":[],"Settings":null,"Layout":null,"Items":{"text/plain":"aGk="}}"""),
        ("copy", () => Pane("copy"), """{"Protocol":2,"Host":"h0st","Operation":"copy","Pane":"p1","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("hyperlink", () => Pane("hyperlink", [4, 2]), """{"Protocol":2,"Host":"h0st","Operation":"hyperlink","Pane":"p1","Text":"","Directory":"","Numbers":[4,2],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("find", () => Pane("find", [1], "needle"), """{"Protocol":2,"Host":"h0st","Operation":"find","Pane":"p1","Text":"needle","Directory":"","Numbers":[1],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("mouse", () => Pane("mouse", [0, 1, 0, 1500, 2250]), """{"Protocol":2,"Host":"h0st","Operation":"mouse","Pane":"p1","Text":"","Directory":"","Numbers":[0,1,0,1500,2250],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("settings", () => { var r = Pane("settings"); r.Settings = new(); return r; }, """{"Protocol":2,"Host":"h0st","Operation":"settings","Pane":"p1","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":{},"Layout":null,"Items":null}"""),
    ];
    internal static string Normalize(ReadOnlySpan<byte> json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (string name in (string[])["Settings", "Layout"]) if (node[name] is JsonObject) node[name] = new JsonObject();
        return node.ToJsonString();
    }
    private static HostReply[] Replies()
    {
        var time = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var state = new ShellState(4, false, 0, "Title", "running", "D:/repo", "D:/repo", null, new(1024, 7, 8, 9, 0, 100),
            new(1, 45, true, null, [new(TerminalEventKind.Progress, Progress: 45)], [new(time, "notice", "body")], [new("echo", time, time.AddSeconds(1), 0)]), 3, true)
        { MouseTracking = true };
        return
        [
            new() { Host = Identity, ProcessId = 1, Layout = new(), Panes = ["p1"], TransportAcceptRetries = 2, Capabilities = ["output-watch", HostProtocol.MetadataBatchCapability] },
            new() { Host = Identity, ProcessId = 1, State = state, CapturedVersion = 4, FrameRevision = 7, DeltaBaseRevision = 6, Scrollbar = new(100, 2, 30), BinaryLength = 64, KeepAlive = true, StatusRevision = 9 },
            new() { Host = Identity, ProcessId = 1, States = new() { ["p1"] = state }, Panes = ["gone"], KeepAlive = true },
            new() { Host = Identity, ProcessId = 1, Numbers = [12], Panes = ["p1"], OutputRevisions = new() { ["p1"] = 12 } },
            new() { Host = Identity, ProcessId = 1, Numbers = [1] },
            new() { Host = Identity, ProcessId = 1, Text = "copied" },
            new() { Host = Identity, ProcessId = 1, Error = "Incompatible session-host protocol." },
            new() { Host = Identity, ProcessId = 1, Error = "This shell session no longer exists.", ErrorCode = HostErrorCode.SessionMissing },
            HostProtocol.OversizedReply("states", Identity, 1),
        ];
    }
    // A v2 host's mismatch and identity replies, as the current client must read them.
    private const string V2MismatchReply = """{"Protocol":2,"Host":"h0st","ProcessId":1,"Error":"Incompatible session-host protocol. Stop existing sessions before upgrading the host.","Text":"","Numbers":[],"State":null,"Layout":null,"Panes":[],"CapturedVersion":-1,"FrameRevision":0,"Scrollbar":null,"BinaryLength":0,"Capabilities":[]}""";
    internal static void Run(Action<bool, string> check)
    {
        var request = HostJsonContext.Default.HostRequest; var reply = HostJsonContext.Default.HostReply;
        var v2Request = HostV2WireContext.Default.V2HostRequest; var v2Reply = HostV2WireContext.Default.V2HostReply;
        check(HostProtocol.MaxJsonBytes == 16 * 1024 * 1024 && HostProtocol.MaxSnapshotBytes == 96 * 1024 * 1024
            && HostProtocol.PipeName(@"C:\toiqtty-wire-fixture") == "Toiqtty-Sessions-86678A5FF261A2531731B132", "host framing limits and pipe naming match protocol 2");
        bool same = true, frozen = true, reaches = true, parses = true;
        foreach (var (label, build, golden) in Requests)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(golden);
            // Today's client still speaks v2: its bytes must stay identical until the bump.
            if (HostProtocol.Version == 2 && Normalize(JsonSerializer.SerializeToUtf8Bytes(build(), request)) != golden) { same = false; Console.Error.WriteLine("v2 request drift: " + label); }
            var old = JsonSerializer.Deserialize(bytes, v2Request)!;
            if (Normalize(JsonSerializer.SerializeToUtf8Bytes(old, v2Request)) != golden) { frozen = false; Console.Error.WriteLine("frozen v2 copy drift: " + label); }
            try
            {
                var current = JsonSerializer.Deserialize(bytes, request)!;
                if (current.Protocol != 2 || current.Operation != old.Operation || current.Host != old.Host) reaches = false;
            }
            catch (JsonException) { reaches = false; Console.Error.WriteLine("v2 request unreadable: " + label); }
            try
            {
                var sent = build(); var parsed = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(sent, request), v2Request)!;
                if (parsed.Protocol != sent.Protocol || parsed.Operation != sent.Operation || parsed.Host != sent.Host) parses = false;
            }
            catch (JsonException) { parses = false; Console.Error.WriteLine("current request unreadable by v2: " + label); }
        }
        check(same, "every current request is byte-identical to its frozen protocol-2 golden while the protocol is 2");
        check(frozen, "the frozen protocol-2 envelope round-trips every golden request byte for byte");
        check(reaches, "protocol-2 request bytes for every operation parse far enough to reach the host's protocol check");
        check(parses, "current request bytes for every operation parse as protocol 2, so an old host answers with a mismatch");
        bool replies = true;
        foreach (var value in Replies())
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, reply), v2Reply)!;
                replies &= parsed.Protocol == value.Protocol && parsed.Host == value.Host && parsed.Error == value.Error && parsed.BinaryLength == value.BinaryLength
                    && parsed.KeepAlive == value.KeepAlive && (parsed.State?.Version ?? -1) == (value.State?.Version ?? -1);
            }
            catch (JsonException) { replies = false; }
        }
        check(replies, "current reply bytes parse as protocol 2 with the envelope, error, binary length and state intact");
        var mismatch = JsonSerializer.Deserialize(V2MismatchReply, reply)!;
        check(mismatch.Protocol == 2 && mismatch.Error is not null && mismatch.Host == Identity, "a protocol-2 host's error reply stays readable by the current client");
        // ErrorCode is additive: a string, omitted when None, unknown or malformed values read
        // as None, and a host that predates codes still classifies through its fixed messages.
        var coded = new HostReply { Host = Identity, Error = "busy", ErrorCode = HostErrorCode.InputBusy };
        string codedJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(coded, reply));
        bool omitted = !Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new HostReply(), reply)).Contains("ErrorCode");
        var future = JsonSerializer.Deserialize("""{"Protocol":2,"Error":"x","ErrorCode":"FutureCode"}""", reply)!;
        var numeric = JsonSerializer.Deserialize("""{"Protocol":2,"Error":"x","ErrorCode":7,"Text":"kept"}""", reply)!;
        var nested = JsonSerializer.Deserialize("""{"Protocol":2,"Error":"x","ErrorCode":{"a":[1]},"Text":"kept"}""", reply)!;
        check(codedJson.Contains("\"ErrorCode\":\"InputBusy\"") && omitted && JsonSerializer.Deserialize(codedJson, reply)!.ErrorCode == HostErrorCode.InputBusy
            && JsonSerializer.Deserialize(codedJson, v2Reply)!.Error == "busy" && future.ErrorCode == HostErrorCode.None && numeric.ErrorCode == HostErrorCode.None
            && numeric.Text == "kept" && nested.ErrorCode == HostErrorCode.None && nested.Text == "kept",
            "ErrorCode is a string a protocol-2 reply parser ignores, omitted when None, and unknown values read as None");
        check(HostProtocol.ErrorCodeOf(mismatch) == HostErrorCode.ProtocolMismatch && HostProtocol.ErrorCodeOf(new() { Error = HostProtocol.MetadataBatchTooLarge }) == HostErrorCode.ReplyTooLarge
            && HostProtocol.ErrorCodeOf(new() { Error = "This shell session no longer exists." }) == HostErrorCode.SessionMissing
            && HostProtocol.ErrorCodeOf(new() { Error = "Input busy: the complete paste was not queued." }) == HostErrorCode.InputBusy
            && HostProtocol.ErrorCodeOf(new() { Error = "x", ErrorCode = HostErrorCode.StorageFailed }) == HostErrorCode.StorageFailed && HostProtocol.ErrorCodeOf(new()) == HostErrorCode.None,
            "replies from hosts without codes classify through their fixed protocol-2 messages");
        check(Compatible(v2Request, request) && Compatible(v2Reply, reply) && Compatible(HostV2WireContext.Default.GetTypeInfo(typeof(V2ShellState))!, HostJsonContext.Default.GetTypeInfo(typeof(ShellState))!),
            "no current envelope property reuses a protocol-2 name with a different JSON type");
    }
    private static bool Compatible(JsonTypeInfo v2, JsonTypeInfo current)
    {
        bool ok = true;
        foreach (var property in v2.Properties)
            foreach (var match in current.Properties)
                if (match.Name == property.Name && !SameType(property.PropertyType, match.PropertyType))
                { ok = false; Console.Error.WriteLine($"{current.Type.Name}.{match.Name} changed JSON type from {property.PropertyType} to {match.PropertyType}"); }
        return ok;
    }
    private static bool SameType(Type v2, Type current)
    {
        if (v2 == typeof(V2ShellState)) return current == typeof(ShellState);
        if (v2.IsArray) return current.IsArray && SameType(v2.GetElementType()!, current.GetElementType()!);
        if (v2.IsGenericType) return current.IsGenericType && v2.GetGenericTypeDefinition() == current.GetGenericTypeDefinition()
            && v2.GetGenericArguments().Zip(current.GetGenericArguments()).All(pair => SameType(pair.First, pair.Second));
        return v2 == current;
    }
    /// <summary>Sends every golden v2 request as raw bytes to a live host. Each one must
    /// get a framed reply a v2 client can parse; none may close the pipe unanswered.</summary>
    internal static async Task RunHost(string folder, Action<bool, string> check)
    {
        bool answered = true;
        foreach (var (label, _, golden) in Requests)
        {
            if (label == "shutdown") continue; // Rejected by identity today, but never risk the live fixture.
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var pipe = new NamedPipeClientStream(".", HostProtocol.PipeName(folder), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(timeout.Token);
                byte[] payload = Encoding.UTF8.GetBytes(golden), frame = new byte[payload.Length + 4];
                BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length); payload.CopyTo(frame, 4);
                await pipe.WriteAsync(frame, timeout.Token);
                byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header, timeout.Token);
                byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await pipe.ReadExactlyAsync(body, timeout.Token);
                var parsed = JsonSerializer.Deserialize(body, HostV2WireContext.Default.V2HostReply)!;
                // Only a v2 ping from the right protocol is served; everything else is
                // refused by the protocol or identity check before any dispatch.
                bool served = label == "ping" && HostProtocol.Version == 2;
                var code = JsonSerializer.Deserialize(body, HostJsonContext.Default.HostReply)!.ErrorCode;
                if (parsed.Protocol != HostProtocol.Version || (parsed.Error is null) != served || served != (code == HostErrorCode.None)
                    || !served && code is not (HostErrorCode.HostChanged or HostErrorCode.ProtocolMismatch))
                { answered = false; Console.Error.WriteLine($"v2 {label}: protocol {parsed.Protocol}, error {parsed.Error}, code {code}"); }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { answered = false; Console.Error.WriteLine($"v2 {label}: {ex.Message}"); }
        }
        check(answered, "a live host answers raw protocol-2 bytes for every operation with a framed reply a v2 client parses, refusals coded");
    }
}
