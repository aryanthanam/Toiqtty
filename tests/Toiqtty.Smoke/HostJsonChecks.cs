using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;
using Toiqtty.Configuration;

internal static class HostJsonChecks
{
    private static HostRequest Request() => new()
    {
        Operation = "capture",
        Host = "fixture",
        Pane = "pane",
        Numbers = [7, 18],
        Images = [1, 4],
        AllowFrameDelta = true,
        AllowStyleRuns = true,
        ReuseReadConnection = true,
        WatchPanes = ["pane"],
        WatchRevisions = new() { { "pane", 8 } },
        Items = new() { { "text/plain", [0, 1, 255] } },
        Settings = new() { FontFamily = "Consolas", Bindings = [new() { Key = "Ctrl+A", Action = new() { Kind = "sequence", Steps = [new() { Kind = "paste", Value = "café 中🙂" }, new() { Value = "newTab" }] } }] },
        Layout = new() { Workspaces = [new() { Name = "Unicode 中🙂" }] },
        Navigation = new("workspace", [new("workspace", "tab", new() { { "tab", "pane" } })])
    };
    private static HostReply Reply()
    {
        var time = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var state = new ShellState(4, false, 0, "Title café 中🙂", "running", "D:/repo", "D:/repo", "D:/repo", new(1024, 7, 8, 9, 0, 100),
            new(1, 45, true, null, [new(TerminalEventKind.Progress, Progress: 45)], [new(time, "notice", "body")], [new("echo café", time, time.AddSeconds(1), 0)]), 3, true)
        { MouseTracking = true };
        return new()
        {
            Host = "fixture",
            ProcessId = 1,
            State = state,
            States = new() { { "pane", state } },
            Numbers = [4],
            OutputRevisions = new() { { "pane", 5 } },
            CapturedVersion = 4,
            FrameRevision = 7,
            DeltaBaseRevision = 6,
            Scrollbar = new(100, 2, 30),
            KeepAlive = true,
            Capabilities = [HostProtocol.ReusableReadCapability],
            Layout = Request().Layout
        };
    }
    internal static async Task Run(Action<bool, string> check)
    {
        Compare(new HostRequest(), HostJsonContext.Default.HostRequest, "Default request");
        Compare(Request(), HostJsonContext.Default.HostRequest, "Settings, recursive action bindings, Unicode layout, navigation and binary items");
        Compare(new HostReply(), HostJsonContext.Default.HostReply, "Default reply");
        Compare(Reply(), HostJsonContext.Default.HostReply, "Shell metadata, command timestamps, arrays, nullable fields and capabilities");
        Compare(new HostReply { Error = "Invalid <request> & \"input\"", Binary = [1, 2, 3] }, HostJsonContext.Default.HostReply, "Errors and excluded binary snapshot storage");
        Compare(Request().Settings!, ConfigurationJsonContext.Default.AppSettings, "Configuration settings with recursive actions");
        Compare(Request().Layout!, ConfigurationJsonContext.Default.SavedSession, "Persisted sessions and pane layout");
        Compare(new LuaConfigRequest { Path = "config.lua", DataDirectory = "D:/fixture", Baseline = Request().Settings! }, ConfigurationJsonContext.Default.LuaConfigRequest, "Lua worker configuration request");
        Compare(new LuaConfigResult { Settings = Request().Settings!, StartupSessions = Request().Layout!.Workspaces, Hooks = ["format_status"] }, ConfigurationJsonContext.Default.LuaConfigResult, "Lua worker startup result and hooks");
        Compare(new LuaPresentationRequest { Status = new() { ["title"] = "café 中🙂", ["count"] = 3, ["revision"] = 123L, ["font_size"] = 14.5, ["active"] = true, ["missing"] = null }, Tabs = [new() { ["id"] = "tab", ["is_active"] = true, ["panes"] = 2 }] }, ConfigurationJsonContext.Default.LuaPresentationRequest, "Mixed runtime callback context values");
        Compare(new LuaPresentationResult { Titles = new() { ["tab"] = [new("café 中🙂", "#abcdef", true)] }, Action = Request().Settings!.Bindings[0].Action }, ConfigurationJsonContext.Default.LuaPresentationResult, "Styled callback results and recursive actions");
        await CheckFrames(check);
        void Compare<T>(T value, JsonTypeInfo<T> info, string label)
        {
            byte[] reference = JsonSerializer.SerializeToUtf8Bytes(value), generated = JsonSerializer.SerializeToUtf8Bytes(value, info);
            check(reference.SequenceEqual(generated), label + ": generated JSON preserves exact legacy wire bytes");
            var decoded = JsonSerializer.Deserialize(reference, info) ?? throw new Exception("Decoded JSON is null.");
            check(JsonSerializer.SerializeToUtf8Bytes(decoded, info).SequenceEqual(reference), label + ": generated metadata preserves the complete round trip");
            string text = System.Text.Encoding.UTF8.GetString(reference);
            string extra = text.Insert(text.Length - 1, ",\"future_field\":{\"x\":[1,2]}");
            check(JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize(extra, info) ?? throw new Exception("Decoded JSON is null."), info).SequenceEqual(reference), label + ": unknown future fields retain forward compatibility");
        }
    }
    private static byte[] Frame<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value), frame = new byte[json.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length); json.CopyTo(frame, 4); return frame;
    }
    private static async Task CheckFrames(Action<bool, string> check)
    {
        var value = Request(); byte[] expected = Frame(value);
        using var output = new CountWrites(); await HostProtocol.WriteJson(output, value, CancellationToken.None);
        check(output.ToArray().SequenceEqual(expected) && output.Writes == 1, "The optimized JSON transport writes byte-identical header and payload in one operation");
        using var input = new FragmentedRead(expected);
        var decoded = await HostProtocol.ReadJson<HostRequest>(input, CancellationToken.None);
        check(JsonSerializer.SerializeToUtf8Bytes(decoded).SequenceEqual(expected.AsSpan(4).ToArray()), "Pooled JSON reads handle fragmented headers and payloads without borrowing model storage");
        using var blocked = new BlockedWrite(); var first = HostProtocol.WriteJson(blocked, value, CancellationToken.None);
        await blocked.Started.Task;
        using var second = new MemoryStream(); await HostProtocol.WriteJson(second, Reply(), CancellationToken.None);
        blocked.Release.TrySetResult(); await first;
        check(blocked.Bytes!.SequenceEqual(expected), "An asynchronous write retains its pooled JSON frame until the receiver completes");
        var large = new HostRequest { Text = new string('x', 70000) }; using var largeFrame = new MemoryStream(); await HostProtocol.WriteJson(largeFrame, large, CancellationToken.None);
        largeFrame.Position = 0; var largeRead = await HostProtocol.ReadJson<HostRequest>(largeFrame, CancellationToken.None);
        check(largeRead.Text == large.Text, "Large JSON messages use transient storage and preserve their full payload");
        bool rejected = false; using var tooLarge = new MemoryStream();
        try { await HostProtocol.WriteJson(tooLarge, new HostRequest { Text = new string('x', HostProtocol.MaxJsonBytes) }, CancellationToken.None); } catch (IOException) { rejected = true; }
        check(rejected && tooLarge.Length == 0, "An oversized optimized JSON message is rejected before writing any header");
        using var fallback = new MemoryStream(); var map = new Dictionary<string, int> { { "test", 42 } }; await HostProtocol.WriteJson(fallback, map, CancellationToken.None); fallback.Position = 0;
        check((await HostProtocol.ReadJson<Dictionary<string, int>>(fallback, CancellationToken.None))["test"] == 42, "Generic non-host JSON retains its reflection-compatible transport path");
    }
    private sealed class CountWrites : MemoryStream
    {
        internal int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) { Writes++; return base.WriteAsync(bytes, cancellationToken); }
    }
    private sealed class FragmentedRead(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
    private sealed class BlockedWrite : MemoryStream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? Bytes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); Bytes = bytes.ToArray(); }
    }
    internal static void Benchmark(string output)
    {
        var request = Request(); var reply = Reply(); var samples = new List<object>(); const int count = 10000;
        Measure(request, HostJsonContext.Default.HostRequest, "request"); Measure(reply, HostJsonContext.Default.HostReply, "reply");
        File.WriteAllText(output, JsonSerializer.Serialize(new { samples, meaning = "Same-binary reflection/generated JSON codec microbenchmark; no pipes or rendering." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
        void Measure<T>(T value, JsonTypeInfo<T> info, string name)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
            for (int round = 0; round < 3; round++) foreach (bool generated in round % 2 == 0 ? new[] { false, true } : new[] { true, false }) foreach (bool decode in new[] { false, true })
            {
                void Once() { if (decode) { if (generated) _ = JsonSerializer.Deserialize(data, info); else _ = JsonSerializer.Deserialize<T>(data); } else { if (generated) _ = JsonSerializer.SerializeToUtf8Bytes(value, info); else _ = JsonSerializer.SerializeToUtf8Bytes(value); } }
                for (int warm = 0; warm < 100; warm++) Once(); long allocation = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                for (int i = 0; i < count; i++) Once(); timer.Stop(); samples.Add(new { name, round, generated, decode, iterations = count, milliseconds = timer.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocation });
            }
        }
    }
}
