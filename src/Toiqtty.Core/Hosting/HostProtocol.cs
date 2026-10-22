using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

internal sealed class HostMessageTooLargeException() : IOException("Session message exceeds the size limit.");

/// <summary>Structured host error, carried beside the human-readable Error text. Every
/// protocol-3 error reply sets it; it is a string, omitted when None, which a version-2
/// peer ignores. Disconnected is client-side only: the transport failed and no reply was read.</summary>
[JsonConverter(typeof(HostErrorCodeConverter))]
public enum HostErrorCode
{
    None, ProtocolMismatch, HostChanged, InvalidRequest, UnknownOperation, SessionMissing, SessionExited, InputBusy, LimitExceeded,
    RequestTooLarge, ReplyTooLarge, ShellStartFailed, ConsoleFailed, TerminalFailed, StorageFailed, Timeout, ShuttingDown, Internal, Disconnected
}

/// <summary>Tolerant string form: a code this build does not know (a newer peer) reads as
/// None (the client classifies it Internal) instead of failing the whole reply.</summary>
public sealed class HostErrorCodeConverter : JsonConverter<HostErrorCode>
{
    public override HostErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) { reader.Skip(); return HostErrorCode.None; }
        return reader.TokenType == JsonTokenType.String && Enum.TryParse(reader.GetString(), false, out HostErrorCode code) && Enum.IsDefined(code) ? code : HostErrorCode.None;
    }
    public override void Write(Utf8JsonWriter writer, HostErrorCode value, JsonSerializerOptions options)
        => writer.WriteStringValue(Enum.GetName(value) ?? nameof(HostErrorCode.Internal));
}

/// <summary>A session-host failure with a structured code. Still an IOException so every
/// existing catch site keeps working; the client's rejected/transport/version kinds derive from it.</summary>
public class SessionHostException(HostErrorCode code, string message, Exception? inner = null) : IOException(message, inner)
{
    public HostErrorCode Code { get; } = code;
}
public sealed class PaneNotFoundException() : SessionHostException(HostErrorCode.SessionMissing, "This shell session no longer exists.");
public sealed class SessionExitedException(string message) : SessionHostException(HostErrorCode.SessionExited, message);
public sealed class InputBusyException(string message) : SessionHostException(HostErrorCode.InputBusy, message);

/// <summary>Flat request envelope. Protocol, Host and Operation are read first; each
/// operation then sets only its own typed payload (HostOperations.cs). Names shared with
/// protocol 2 keep their JSON types so either version's bytes parse as the other's.</summary>
public sealed class HostRequest
{
    public int Protocol { get; set; } = HostProtocol.Version;
    public string Host { get; set; } = "";
    public string Operation { get; set; } = "ping";
    public string Pane { get; set; } = "";
    // Key text, paste/send-text content and the find needle.
    public string Text { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AppSettings? Settings { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SavedSession? Layout { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool ReuseReadConnection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public HostNavigation? Navigation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, int>? StateVersions { get; set; }
    // Orders "create" against "sync" from the same frontend: a layout snapshot
    // never removes a shell that frontend created after taking the snapshot.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LayoutEpoch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long LayoutRevision { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public KeyArgs? Key { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ResizeArgs? Resize { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GestureArgs? Gesture { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MouseArgs? Mouse { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ScrollArgs? Scroll { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ScrollToArgs? ScrollTo { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SelectArgs? Select { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FocusArgs? Focus { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PasteArgs? Paste { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FindArgs? Find { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CellArgs? Cell { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DropQueryArgs? DropQuery { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DropCommitArgs? DropCommit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CaptureArgs? Capture { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public WatchArgs? Watch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CreateArgs? Create { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AttachArgs? Attach { get; set; }
}

public sealed record HostNavigation(string? SelectedWorkspace, HostWorkspaceNavigation[] Workspaces);
public sealed record HostWorkspaceNavigation(string Id, string? ActiveTabId, Dictionary<string, string?> FocusedPanes);

/// <summary>Flat reply envelope. Protocol, Host, ProcessId and Error are always written so
/// any peer can check the version and failure; every other field is omitted when absent
/// and reads back as its default (version 2 declares the same defaults), which keeps the
/// capture and watch replies small.</summary>
public sealed class HostReply
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int Protocol { get; set; } = HostProtocol.Version;
    public string Host { get; set; } = "";
    public int ProcessId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Error { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public HostErrorCode ErrorCode { get; set; }
    // Text, Panes and CapturedVersion keep their version-2 defaults ("", [], -1) for readers;
    // the wire form behind each is null, so absent, at that default.
    [JsonIgnore] public string Text { get => TextJson ?? ""; set => TextJson = value.Length == 0 ? null : value; }
    [JsonInclude, JsonPropertyName("Text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] internal string? TextJson { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ShellState? State { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SavedSession? Layout { get; set; }
    // Live panes (ping) or panes the states batch could not find.
    [JsonIgnore] public string[] Panes { get => PanesJson ?? []; set => PanesJson = value.Length == 0 ? null : value; }
    [JsonInclude, JsonPropertyName("Panes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] internal string[]? PanesJson { get; set; }
    // Set with a frame; -1 is "no frame captured".
    [JsonIgnore] public int CapturedVersion { get => CapturedVersionJson ?? -1; set => CapturedVersionJson = value < 0 ? null : value; }
    [JsonInclude, JsonPropertyName("CapturedVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] internal int? CapturedVersionJson { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long FrameRevision { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DeltaBaseRevision { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TerminalScrollState? Scrollbar { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int BinaryLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? OutputRevisions { get; set; }
    // The host's global output revision when a watch completes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long OutputRevision { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ShellState>? States { get; set; }
    // Typed results: gesture/mouse/drop values, find counts, copy-or-key's outcome,
    // whether create spawned a shell, and attach's absent panes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? Value { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FindResult? Find { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Copied { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Created { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Missing { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TransportAcceptRetries { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepAlive { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StatusRevision { get; set; }
    [JsonIgnore] internal int JsonPayloadLength { get; set; }
    [JsonIgnore] public byte[] Binary { get; set; } = [];
}

public sealed record ShellState(int Version, bool HasExited, int ExitCode, string Title, string StatusSummary,
    string Directory, string WorkingDirectory, string? LocalDirectory, TerminalPerformance Performance,
    TerminalStatusSnapshot? Status, long Attention, bool PendingAttention)
{
    // Always written by protocol-3 hosts; a version-2 reader's bool? accepts it.
    public bool MouseTracking { get; init; }
}

public static class HostProtocol
{
    internal static bool LegacyJsonForCheck = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_JSON") == "legacy";
    // Version 3 replaces positional Numbers with typed per-operation payloads and
    // results, and requires an ErrorCode on every error reply. Version 2 preserved
    // browser leaves/settings when the host saves layouts.
    public const int Version = 3;
    public const int MaxJsonBytes = 16 * 1024 * 1024;
    public const int MaxSnapshotBytes = 96 * 1024 * 1024;
    public const int MaxMetadataPanes = 256;
    public const string ReplyTooLarge = "Session host reply exceeds the message size limit.";
    /// <summary>The small error a host sends in place of a reply over a frame limit. The
    /// limit is checked before any header is written, so the stream stays framed.</summary>
    internal static HostReply OversizedReply(string host, int processId)
        => new() { Host = host, ProcessId = processId, Error = ReplyTooLarge, ErrorCode = HostErrorCode.ReplyTooLarge };
    public static string PipeName(string folder) => "Toiqtty-Sessions-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
    public static async Task WriteJson<T>(Stream stream, T value, CancellationToken token)
    {
        if (!LegacyJsonForCheck)
        {
            using var frame = new HostJsonFrame(); using var writer = new Utf8JsonWriter(frame);
            if (MessageInfo<T>() is { } info) JsonSerializer.Serialize(writer, value, info); else JsonSerializer.Serialize(writer, value);
            writer.Flush(); frame.SetLength(); await stream.WriteAsync(frame.Message, token).ConfigureAwait(false); return;
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaxJsonBytes) throw new HostMessageTooLargeException();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }
    public static async Task<T> ReadJson<T>(Stream stream, CancellationToken token)
    {
        if (!LegacyJsonForCheck) return await ReadPooledJson<T>(stream, token).ConfigureAwait(false);
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > MaxJsonBytes) throw new IOException("Invalid session message length.");
        byte[] payload = new byte[count]; await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        try { return Received(JsonSerializer.Deserialize<T>(payload), count); }
        catch (JsonException ex) { throw new IOException("Invalid session JSON.", ex); }
    }
    private static JsonTypeInfo<T>? MessageInfo<T>() => typeof(T) == typeof(HostRequest) ? (JsonTypeInfo<T>)(object)HostJsonContext.Default.HostRequest
        : typeof(T) == typeof(HostReply) ? (JsonTypeInfo<T>)(object)HostJsonContext.Default.HostReply : null;
    private static T Received<T>(T? value, int bytes)
    {
        if (value is null) throw new IOException("Empty session message.");
        if (value is HostReply reply) reply.JsonPayloadLength = bytes; return value;
    }
    private static async Task<T> ReadPooledJson<T>(Stream stream, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096); bool pooled = true;
        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, 4), token).ConfigureAwait(false);
            int count = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            if (count is <= 0 or > MaxJsonBytes) throw new IOException("Invalid session message length.");
            if (count > buffer.Length)
            {
                ArrayPool<byte>.Shared.Return(buffer, true); pooled = false;
                buffer = count <= 65536 ? ArrayPool<byte>.Shared.Rent(count) : new byte[count]; pooled = count <= 65536;
            }
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            try { return Received(MessageInfo<T>() is { } info ? JsonSerializer.Deserialize(buffer.AsSpan(0, count), info) : JsonSerializer.Deserialize<T>(buffer.AsSpan(0, count)), count); }
            catch (JsonException ex) { throw new IOException("Invalid session JSON.", ex); }
        }
        finally { if (pooled) ArrayPool<byte>.Shared.Return(buffer, true); }
    }
}
