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
    // The protocol-2 bytes each operation's request produced at c957788, frozen.
    // Settings and Layout bodies are normalized to {}: they are shared models, not envelope.
    internal static readonly (string Label, string Golden)[] V2Requests =
    [        ("ping", """{"Protocol":2,"Host":"","Operation":"ping","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("states", """{"Protocol":2,"Host":"h0st","Operation":"states","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null,"StateVersions":{"p1":3}}"""),
        ("watch", """{"Protocol":2,"Host":"h0st","Operation":"watch","Pane":"","Text":"","Directory":"","Numbers":[9],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("watch-panes", """{"Protocol":2,"Host":"h0st","Operation":"watch","Pane":"","Text":"","Directory":"","Numbers":[9],"Images":[],"Settings":null,"Layout":null,"Items":null,"ReuseReadConnection":true,"WatchPanes":["p1"],"WatchRevisions":{"p1":4}}"""),
        ("navigate", """{"Protocol":2,"Host":"h0st","Operation":"navigate","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null,"Navigation":{"SelectedWorkspace":"w1","Workspaces":[{"Id":"w1","ActiveTabId":"t1","FocusedPanes":{"t1":"p1"}}]}}"""),
        ("sync", """{"Protocol":2,"Host":"h0st","Operation":"sync","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":{},"Items":null,"LayoutEpoch":"e1","LayoutRevision":2}"""),
        ("shutdown", """{"Protocol":2,"Host":"h0st","Operation":"shutdown","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("detach", """{"Protocol":2,"Host":"h0st","Operation":"detach","Pane":"","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("create", """{"Protocol":2,"Host":"h0st","Operation":"create","Pane":"p1","Text":"cmd.exe","Directory":"C:\\work","Numbers":[],"Images":[],"Settings":{},"Layout":null,"Items":null,"LayoutEpoch":"e1","LayoutRevision":1}"""),
        ("create-layout", """{"Protocol":2,"Host":"h0st","Operation":"create","Pane":"p1","Text":"cmd.exe","Directory":"C:\\work","Numbers":[],"Images":[],"Settings":{},"Layout":{},"Items":null,"LayoutEpoch":"e1","LayoutRevision":3}"""),
        ("close", """{"Protocol":2,"Host":"h0st","Operation":"close","Pane":"p1","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("state", """{"Protocol":2,"Host":"h0st","Operation":"state","Pane":"p1","Text":"","Directory":"","Numbers":[3,7],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("capture", """{"Protocol":2,"Host":"h0st","Operation":"capture","Pane":"p1","Text":"","Directory":"","Numbers":[3,7],"Images":[11],"Settings":null,"Layout":null,"Items":null,"AllowFrameDelta":true,"AllowStyleRuns":true,"ReuseReadConnection":true,"KnownStatusRevision":5}"""),
        ("capture-full", """{"Protocol":2,"Host":"h0st","Operation":"capture","Pane":"p1","Text":"","Directory":"","Numbers":[-1],"Images":[],"Settings":null,"Layout":null,"Items":null,"AllowStyleRuns":true,"KnownStatusRevision":-1}"""),
        ("resize", """{"Protocol":2,"Host":"h0st","Operation":"resize","Pane":"p1","Text":"","Directory":"","Numbers":[100,30,9,19],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("key", """{"Protocol":2,"Host":"h0st","Operation":"key","Pane":"p1","Text":"a","Directory":"","Numbers":[65,2,1,-1,0],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("paste", """{"Protocol":2,"Host":"h0st","Operation":"paste","Pane":"p1","Text":"x","Directory":"","Numbers":[1],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("send-text", """{"Protocol":2,"Host":"h0st","Operation":"send-text","Pane":"p1","Text":"x","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("paste-checked", """{"Protocol":2,"Host":"h0st","Operation":"paste-checked","Pane":"p1","Text":"x","Directory":"","Numbers":[0],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("clipboard", """{"Protocol":2,"Host":"h0st","Operation":"clipboard","Pane":"p1","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("scroll", """{"Protocol":2,"Host":"h0st","Operation":"scroll","Pane":"p1","Text":"","Directory":"","Numbers":[-3],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("scroll-to", """{"Protocol":2,"Host":"h0st","Operation":"scroll-to","Pane":"p1","Text":"","Directory":"","Numbers":[42],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("focus", """{"Protocol":2,"Host":"h0st","Operation":"focus","Pane":"p1","Text":"","Directory":"","Numbers":[1],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("select", """{"Protocol":2,"Host":"h0st","Operation":"select","Pane":"p1","Text":"","Directory":"","Numbers":[-1,-1],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("gesture", """{"Protocol":2,"Host":"h0st","Operation":"gesture","Pane":"p1","Text":"","Directory":"","Numbers":[0,1500,2250,0],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("drop", """{"Protocol":2,"Host":"h0st","Operation":"drop","Pane":"p1","Text":"","Directory":"","Numbers":[1,10,20,3],"Images":[],"Settings":null,"Layout":null,"Items":{"text/plain":"aGk="}}"""),
        ("drop-async", """{"Protocol":2,"Host":"h0st","Operation":"drop-async","Pane":"p1","Text":"","Directory":"","Numbers":[10,20,3,15000],"Images":[],"Settings":null,"Layout":null,"Items":{"text/plain":"aGk="}}"""),
        ("copy", """{"Protocol":2,"Host":"h0st","Operation":"copy","Pane":"p1","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("hyperlink", """{"Protocol":2,"Host":"h0st","Operation":"hyperlink","Pane":"p1","Text":"","Directory":"","Numbers":[4,2],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("find", """{"Protocol":2,"Host":"h0st","Operation":"find","Pane":"p1","Text":"needle","Directory":"","Numbers":[1],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("mouse", """{"Protocol":2,"Host":"h0st","Operation":"mouse","Pane":"p1","Text":"","Directory":"","Numbers":[0,1,0,1500,2250],"Images":[],"Settings":null,"Layout":null,"Items":null}"""),
        ("settings", """{"Protocol":2,"Host":"h0st","Operation":"settings","Pane":"p1","Text":"","Directory":"","Numbers":[],"Images":[],"Settings":{},"Layout":null,"Items":null}"""),
    ];
    private static HostRequest At(HostRequest request) { request.Host = Identity; return request; }
    private static Dictionary<string, byte[]> Items() => new() { ["text/plain"] = [104, 105] };
    // One protocol-3 request per operation, built through the client's factories,
    // with the bytes it produces. These goldens freeze the v3 wire the same way.
    internal static readonly (string Label, Func<HostRequest> Build, string Golden)[] Requests =
    [
        ("ping", HostRequests.Ping, """{"Protocol":3,"Host":"","Operation":"ping","Pane":"","Text":""}"""),
        ("states", () => At(HostRequests.States(new() { ["p1"] = 3 })), """{"Protocol":3,"Host":"h0st","Operation":"states","Pane":"","Text":"","StateVersions":{"p1":3}}"""),
        ("watch", () => { var r = At(HostRequests.Watch(new() { ["p1"] = 4, ["p2"] = -1 })); r.ReuseReadConnection = true; return r; }, """{"Protocol":3,"Host":"h0st","Operation":"watch","Pane":"","Text":"","ReuseReadConnection":true,"Watch":{"Panes":{"p1":4,"p2":-1}}}"""),
        ("attach", () => At(HostRequests.Attach([new()], [new("p1", 0), new("p2", -1)])), """{"Protocol":3,"Host":"h0st","Operation":"attach","Pane":"","Text":"","Attach":{"Settings":[{}],"Panes":[{"Id":"p1","SettingsIndex":0},{"Id":"p2","SettingsIndex":-1}]}}"""),
        ("navigate", () => At(HostRequests.Navigate(new("w1", [new("w1", "t1", new() { ["t1"] = "p1" })]))), """{"Protocol":3,"Host":"h0st","Operation":"navigate","Pane":"","Text":"","Navigation":{"SelectedWorkspace":"w1","Workspaces":[{"Id":"w1","ActiveTabId":"t1","FocusedPanes":{"t1":"p1"}}]}}"""),
        ("sync", () => At(HostRequests.Sync(new(), "e1", 2)), """{"Protocol":3,"Host":"h0st","Operation":"sync","Pane":"","Text":"","Layout":{},"LayoutEpoch":"e1","LayoutRevision":2}"""),
        ("shutdown", () => At(HostRequests.Shutdown()), """{"Protocol":3,"Host":"h0st","Operation":"shutdown","Pane":"","Text":""}"""),
        ("detach", () => At(HostRequests.Detach()), """{"Protocol":3,"Host":"h0st","Operation":"detach","Pane":"","Text":""}"""),
        ("create", () => At(HostRequests.Create("p1", "cmd.exe", @"C:\work", new(), null, "e1", 1)), """{"Protocol":3,"Host":"h0st","Operation":"create","Pane":"p1","Text":"","Settings":{},"LayoutEpoch":"e1","LayoutRevision":1,"Create":{"Command":"cmd.exe","Directory":"C:\\work","Columns":0,"Rows":0}}"""),
        ("create-layout", () => At(HostRequests.Create("p1", "cmd.exe", @"C:\work", new(), new(), "e1", 3, 120, 40)), """{"Protocol":3,"Host":"h0st","Operation":"create","Pane":"p1","Text":"","Settings":{},"Layout":{},"LayoutEpoch":"e1","LayoutRevision":3,"Create":{"Command":"cmd.exe","Directory":"C:\\work","Columns":120,"Rows":40}}"""),
        ("close", () => At(HostRequests.Close("p1")), """{"Protocol":3,"Host":"h0st","Operation":"close","Pane":"p1","Text":""}"""),
        ("capture", () => { var r = At(HostRequests.Capture("p1", 3, 7, 5, [11])); r.ReuseReadConnection = true; return r; }, """{"Protocol":3,"Host":"h0st","Operation":"capture","Pane":"p1","Text":"","ReuseReadConnection":true,"Capture":{"KnownVersion":3,"KnownFrameRevision":7,"KnownStatusRevision":5,"Images":[11]}}"""),
        ("capture-full", () => At(HostRequests.Capture("p1")), """{"Protocol":3,"Host":"h0st","Operation":"capture","Pane":"p1","Text":"","Capture":{"KnownVersion":-1,"KnownFrameRevision":-1,"KnownStatusRevision":-1}}"""),
        ("resize", () => At(HostRequests.Resize("p1", 100, 30, 9, 19)), """{"Protocol":3,"Host":"h0st","Operation":"resize","Pane":"p1","Text":"","Resize":{"Columns":100,"Rows":30,"CellWidth":9,"CellHeight":19}}"""),
        ("key", () => At(HostRequests.Key("p1", 65, 2, "a")), """{"Protocol":3,"Host":"h0st","Operation":"key","Pane":"p1","Text":"a","Key":{"Key":65,"Modifiers":2,"Action":1,"Consumed":0}}"""),
        ("key-release", () => At(HostRequests.Key("p1", 65, 2, "a", TerminalKeyAction.Release, 97, 2)), """{"Protocol":3,"Host":"h0st","Operation":"key","Pane":"p1","Text":"a","Key":{"Key":65,"Modifiers":2,"Action":0,"Unshifted":97,"Consumed":2}}"""),
        ("copy-or-key", () => At(HostRequests.CopyOrKey("p1", 67, 2, "c", TerminalKeyAction.Press, 99, 0)), """{"Protocol":3,"Host":"h0st","Operation":"copy-or-key","Pane":"p1","Text":"c","Key":{"Key":67,"Modifiers":2,"Action":1,"Unshifted":99,"Consumed":0}}"""),
        ("paste", () => At(HostRequests.Paste("p1", "x", true)), """{"Protocol":3,"Host":"h0st","Operation":"paste","Pane":"p1","Text":"x","Paste":{"Clipboard":true}}"""),
        ("send-text", () => At(HostRequests.SendText("p1", "x")), """{"Protocol":3,"Host":"h0st","Operation":"send-text","Pane":"p1","Text":"x"}"""),
        ("paste-checked", () => At(HostRequests.PasteChecked("p1", "x")), """{"Protocol":3,"Host":"h0st","Operation":"paste-checked","Pane":"p1","Text":"x"}"""),
        ("clipboard", () => At(HostRequests.Clipboard("p1")), """{"Protocol":3,"Host":"h0st","Operation":"clipboard","Pane":"p1","Text":""}"""),
        ("scroll", () => At(HostRequests.Scroll("p1", -3)), """{"Protocol":3,"Host":"h0st","Operation":"scroll","Pane":"p1","Text":"","Scroll":{"Delta":-3}}"""),
        ("scroll-to", () => At(HostRequests.ScrollTo("p1", 42)), """{"Protocol":3,"Host":"h0st","Operation":"scroll-to","Pane":"p1","Text":"","ScrollTo":{"Row":42}}"""),
        ("focus", () => At(HostRequests.Focus("p1", true)), """{"Protocol":3,"Host":"h0st","Operation":"focus","Pane":"p1","Text":"","Focus":{"Focused":true}}"""),
        ("select", () => At(HostRequests.Select("p1", -1, -1)), """{"Protocol":3,"Host":"h0st","Operation":"select","Pane":"p1","Text":"","Select":{"Start":-1,"End":-1}}"""),
        ("gesture", () => At(HostRequests.Gesture("p1", 0, 1.5f, 2.25f, false)), """{"Protocol":3,"Host":"h0st","Operation":"gesture","Pane":"p1","Text":"","Gesture":{"Action":0,"XMilli":1500,"YMilli":2250,"Rectangle":false}}"""),
        ("drop", () => At(HostRequests.DropQuery("p1", 1, 10, 20, 3, Items())), """{"Protocol":3,"Host":"h0st","Operation":"drop","Pane":"p1","Text":"","DropQuery":{"Action":1,"X":10,"Y":20,"Operations":3,"Items":{"text/plain":"aGk="}}}"""),
        ("drop-async", () => At(HostRequests.DropCommit("p1", 10, 20, 3, TimeSpan.FromSeconds(15), Items())), """{"Protocol":3,"Host":"h0st","Operation":"drop-async","Pane":"p1","Text":"","DropCommit":{"X":10,"Y":20,"Operations":3,"TimeoutMilliseconds":15000,"Items":{"text/plain":"aGk="}}}"""),
        ("copy", () => At(HostRequests.Copy("p1")), """{"Protocol":3,"Host":"h0st","Operation":"copy","Pane":"p1","Text":""}"""),
        ("hyperlink", () => At(HostRequests.Hyperlink("p1", 4, 2)), """{"Protocol":3,"Host":"h0st","Operation":"hyperlink","Pane":"p1","Text":"","Cell":{"Column":4,"Row":2}}"""),
        ("find", () => At(HostRequests.Find("p1", "needle", 1)), """{"Protocol":3,"Host":"h0st","Operation":"find","Pane":"p1","Text":"needle","Find":{"Direction":1}}"""),
        ("mouse", () => At(HostRequests.Mouse("p1", 0, 1, 0, 1.5f, 2.25f)), """{"Protocol":3,"Host":"h0st","Operation":"mouse","Pane":"p1","Text":"","Mouse":{"Action":0,"Button":1,"Modifiers":0,"XMilli":1500,"YMilli":2250}}"""),
        ("settings", () => At(HostRequests.Settings("p1", new())), """{"Protocol":3,"Host":"h0st","Operation":"settings","Pane":"p1","Text":"","Settings":{}}"""),
    ];    internal static string Normalize(ReadOnlySpan<byte> json)
    {
        var node = JsonNode.Parse(json)!.AsObject(); Shared(node);
        return node.ToJsonString();
        static void Shared(JsonObject node)
        {
            foreach (var pair in node.ToArray())
            {
                if (pair.Key is "Settings" or "Layout" && pair.Value is JsonObject) node[pair.Key] = new JsonObject();
                else if (pair.Key == "Settings" && pair.Value is JsonArray array) for (int i = 0; i < array.Count; i++) array[i] = new JsonObject();
                else if (pair.Value is JsonObject child) Shared(child);
            }
        }
    }
    private static HostReply[] Replies()
    {
        var time = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var state = new ShellState(4, false, 0, "Title", "running", "D:/repo", "D:/repo", null, new(1024, 7, 8, 9, 0, 100),
            new(1, 45, true, null, [new(TerminalEventKind.Progress, Progress: 45)], [new(time, "notice", "body")], [new("echo", time, time.AddSeconds(1), 0)]), 3, true)
        { MouseTracking = true };
        return
        [
            new() { Host = Identity, ProcessId = 1, Layout = new(), Panes = ["p1"], TransportAcceptRetries = 2 },
            new() { Host = Identity, ProcessId = 1, State = state, CapturedVersion = 4, FrameRevision = 7, DeltaBaseRevision = 6, Scrollbar = new(100, 2, 30), BinaryLength = 64, KeepAlive = true, StatusRevision = 9 },
            new() { Host = Identity, ProcessId = 1, States = new() { ["p1"] = state }, Panes = ["gone"], KeepAlive = true },
            new() { Host = Identity, ProcessId = 1, States = new() { ["p1"] = state }, Missing = ["gone"] },
            new() { Host = Identity, ProcessId = 1, Panes = ["p1"], OutputRevisions = new() { ["p1"] = 12 }, OutputRevision = 12 },
            new() { Host = Identity, ProcessId = 1, Value = 1 },
            new() { Host = Identity, ProcessId = 1, Find = new(3, 1) },
            new() { Host = Identity, ProcessId = 1, Text = "copied", Copied = true, State = state },
            new() { Host = Identity, ProcessId = 1, State = state, Created = false },
            new() { Host = Identity, ProcessId = 1, Error = "Incompatible session-host protocol.", ErrorCode = HostErrorCode.ProtocolMismatch },
            new() { Host = Identity, ProcessId = 1, Error = "This shell session no longer exists.", ErrorCode = HostErrorCode.SessionMissing },
            HostProtocol.OversizedReply(Identity, 1),
        ];
    }
    // A v2 host's mismatch reply, as the current client must read it.
    private const string V2MismatchReply = """{"Protocol":2,"Host":"h0st","ProcessId":1,"Error":"Incompatible session-host protocol. Stop existing sessions before upgrading the host.","Text":"","Numbers":[],"State":null,"Layout":null,"Panes":[],"CapturedVersion":-1,"FrameRevision":0,"Scrollbar":null,"BinaryLength":0,"Capabilities":[]}""";
    internal static void Run(Action<bool, string> check)
    {
        var request = HostJsonContext.Default.HostRequest; var reply = HostJsonContext.Default.HostReply;
        var v2Request = HostV2WireContext.Default.V2HostRequest; var v2Reply = HostV2WireContext.Default.V2HostReply;
        check(HostProtocol.Version == 3 && HostProtocol.MaxJsonBytes == 16 * 1024 * 1024 && HostProtocol.MaxSnapshotBytes == 96 * 1024 * 1024
            && HostProtocol.PipeName(@"C:\toiqtty-wire-fixture") == "Toiqtty-Sessions-86678A5FF261A2531731B132", "protocol 3 keeps the protocol-2 framing limits and pipe naming");
        bool frozen = true, reaches = true;
        foreach (var (label, golden) in V2Requests)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(golden);
            var old = JsonSerializer.Deserialize(bytes, v2Request)!;
            if (Normalize(JsonSerializer.SerializeToUtf8Bytes(old, v2Request)) != golden) { frozen = false; Console.Error.WriteLine("frozen v2 copy drift: " + label); }
            try
            {
                var current = JsonSerializer.Deserialize(bytes, request)!;
                if (current.Protocol != 2 || current.Operation != old.Operation || current.Host != old.Host) reaches = false;
            }
            catch (JsonException) { reaches = false; Console.Error.WriteLine("v2 request unreadable: " + label); }
        }
        check(frozen, "the frozen protocol-2 envelope round-trips every golden request byte for byte");
        check(reaches, "protocol-2 request bytes for every operation parse far enough to reach the host's protocol check");
        bool same = true, parses = true, single = true, covered = true;
        string[] payloads = ["Key", "Resize", "Gesture", "Mouse", "Scroll", "ScrollTo", "Select", "Focus", "Paste", "Find", "Cell", "DropQuery", "DropCommit", "Capture", "Watch", "Create", "Attach"];
        foreach (var (label, build, golden) in Requests)
        {
            var sent = build(); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(sent, request);
            if (Normalize(bytes) != golden) { same = false; Console.Error.WriteLine("v3 request drift: " + label + " " + Normalize(bytes)); }
            if (JsonNode.Parse(bytes)!.AsObject().Count(pair => Array.IndexOf(payloads, pair.Key) >= 0) > 1) { single = false; Console.Error.WriteLine("several payloads: " + label); }
            try
            {
                var parsed = JsonSerializer.Deserialize(bytes, v2Request)!;
                if (parsed.Protocol != 3 || parsed.Operation != sent.Operation || parsed.Host != sent.Host) parses = false;
            }
            catch (JsonException) { parses = false; Console.Error.WriteLine("current request unreadable by v2: " + label); }
            covered &= V2Requests.Any(old => old.Label == label) || label is "attach" or "copy-or-key" or "key-release";
        }
        check(same, "every protocol-3 request is byte-identical to its frozen protocol-3 golden");
        check(single, "every protocol-3 request carries at most one typed payload");
        check(parses, "protocol-3 request bytes for every operation parse as protocol 2, so an old host answers with a mismatch");
        check(covered && V2Requests.All(old => old.Label == "watch" || old.Label == "watch-panes" || old.Label == "state" || Requests.Any(item => item.Label == old.Label)),
            "the protocol-3 table covers every protocol-2 operation except the deleted state and global watch");
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
        check(replies, "protocol-3 reply bytes parse as protocol 2 with the envelope, error, binary length and state intact");
        var mismatch = JsonSerializer.Deserialize(V2MismatchReply, reply)!;
        check(mismatch.Protocol == 2 && mismatch.Error is not null && mismatch.Host == Identity, "a protocol-2 host's error reply stays readable by the current client");
        // ErrorCode is a string, omitted when None; unknown or malformed values read as None.
        var coded = new HostReply { Host = Identity, Error = "busy", ErrorCode = HostErrorCode.InputBusy };
        string codedJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(coded, reply));
        bool omitted = !Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new HostReply(), reply)).Contains("ErrorCode");
        var future = JsonSerializer.Deserialize("""{"Protocol":3,"Error":"x","ErrorCode":"FutureCode"}""", reply)!;
        var numeric = JsonSerializer.Deserialize("""{"Protocol":3,"Error":"x","ErrorCode":7,"Text":"kept"}""", reply)!;
        var nested = JsonSerializer.Deserialize("""{"Protocol":3,"Error":"x","ErrorCode":{"a":[1]},"Text":"kept"}""", reply)!;
        check(codedJson.Contains("\"ErrorCode\":\"InputBusy\"") && omitted && JsonSerializer.Deserialize(codedJson, reply)!.ErrorCode == HostErrorCode.InputBusy
            && JsonSerializer.Deserialize(codedJson, v2Reply)!.Error == "busy" && future.ErrorCode == HostErrorCode.None && numeric.ErrorCode == HostErrorCode.None
            && numeric.Text == "kept" && nested.ErrorCode == HostErrorCode.None && nested.Text == "kept",
            "ErrorCode is a string a protocol-2 reply parser ignores, omitted when None, and unknown values read as None");
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
        // A value protocol 3 always writes is readable by v2's nullable form (ShellState.MouseTracking).
        if (Nullable.GetUnderlyingType(v2) is { } underlying && current == underlying) return true;
        if (v2.IsArray) return current.IsArray && SameType(v2.GetElementType()!, current.GetElementType()!);
        if (v2.IsGenericType) return current.IsGenericType && v2.GetGenericTypeDefinition() == current.GetGenericTypeDefinition()
            && v2.GetGenericArguments().Zip(current.GetGenericArguments()).All(pair => SameType(pair.First, pair.Second));
        return v2 == current;
    }
    /// <summary>Sends every golden v2 request as raw bytes to a live host. Each one must
    /// get a framed ProtocolMismatch reply a v2 client parses; none may close the pipe.</summary>
    internal static async Task RunHost(string folder, Action<bool, string> check)
    {
        bool answered = true;
        foreach (var (label, golden) in V2Requests)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var pipe = new NamedPipeClientStream(".", HostProtocol.PipeName(folder), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(timeout.Token);
                byte[] body = await Exchange(pipe, Encoding.UTF8.GetBytes(golden), timeout.Token);
                var parsed = JsonSerializer.Deserialize(body, HostV2WireContext.Default.V2HostReply)!;
                var code = JsonSerializer.Deserialize(body, HostJsonContext.Default.HostReply)!.ErrorCode;
                // The protocol check precedes identity and payload decode, so every
                // protocol-2 operation, shutdown included, is refused the same way.
                if (parsed.Protocol != HostProtocol.Version || parsed.Error is null || code != HostErrorCode.ProtocolMismatch)
                { answered = false; Console.Error.WriteLine($"v2 {label}: protocol {parsed.Protocol}, error {parsed.Error}, code {code}"); }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { answered = false; Console.Error.WriteLine($"v2 {label}: {ex.Message}"); }
        }
        check(answered, "a live protocol-3 host answers raw protocol-2 bytes for every operation with a framed ProtocolMismatch reply a v2 client parses");
    }
    private static async Task<byte[]> Exchange(Stream pipe, byte[] payload, CancellationToken token)
    {
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length); payload.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, token);
        byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header, token);
        byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await pipe.ReadExactlyAsync(body, token);
        return body;
    }
    /// <summary>The current client against scripted peers: a protocol-2 host's raw mismatch
    /// bytes raise the version error, and an uncoded protocol-3 error reads as Internal.</summary>
    internal static async Task RunClient(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "toiqtty-v2-fake-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<string> Serve(Func<int, string> answer, int count)
        {
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var served = Task.Run(() => Answer(answer, count, listening));
            listening.Task.Wait(timeout.Token); return served;
        }
        async Task<string> Answer(Func<int, string> answer, int count, TaskCompletionSource listening)
        {
            string operations = "";
            for (int index = 0; index < count; index++)
            {
                await using var pipe = new NamedPipeServerStream(HostProtocol.PipeName(folder), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                listening.TrySetResult();
                await pipe.WaitForConnectionAsync(timeout.Token);
                byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header, timeout.Token);
                byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await pipe.ReadExactlyAsync(body, timeout.Token);
                var sent = JsonSerializer.Deserialize(body, HostV2WireContext.Default.V2HostRequest)!; operations += sent.Operation + sent.Protocol + ";";
                byte[] reply = Encoding.UTF8.GetBytes(answer(index)), frame = new byte[reply.Length + 4];
                BinaryPrimitives.WriteInt32LittleEndian(frame, reply.Length); reply.CopyTo(frame, 4); await pipe.WriteAsync(frame, timeout.Token);
            }
            return operations;
        }
        var v2 = Serve(_ => V2MismatchReply, 1);
        bool version = false;
        try { SessionHostClient.TryConnect(folder); } catch (SessionHostVersionException) { version = true; }
        check(version && await v2 == "ping3;", "the client raises the version error against a protocol-2 host's raw mismatch reply");
        var v3 = Serve(index => index == 0 ? """{"Protocol":3,"Host":"fake","ProcessId":1}""" : """{"Protocol":3,"Host":"fake","ProcessId":1,"Error":"uncoded"}""", 2);
        var client = SessionHostClient.TryConnect(folder) ?? throw new IOException("The scripted protocol-3 host did not answer.");
        HostErrorCode code = HostErrorCode.None;
        try { await client.CallAsync(HostRequests.Copy("p1")); } catch (HostRejectedException ex) { code = ex.Code; }
        await v3;
        check(code == HostErrorCode.Internal, "an error reply without a code is classified Internal rather than by its message text");
    }
}
