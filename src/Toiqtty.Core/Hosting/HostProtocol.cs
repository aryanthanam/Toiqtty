using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

internal sealed class HostMessageTooLargeException() : IOException("Session message exceeds the size limit.");

/// <summary>Structured host error, carried beside the human-readable Error text. Written
/// as a string and omitted when None, so a version-2 peer without it ignores the field.
/// Disconnected is client-side only: the transport failed and no reply was read.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(HostErrorCodeConverter))]
public enum HostErrorCode
{
    None, ProtocolMismatch, HostChanged, InvalidRequest, UnknownOperation, SessionMissing, SessionExited, InputBusy, LimitExceeded,
    RequestTooLarge, ReplyTooLarge, ShellStartFailed, ConsoleFailed, TerminalFailed, StorageFailed, Timeout, ShuttingDown, Internal, Disconnected
}

/// <summary>Tolerant string form: a code this build does not know (a newer peer) reads as
/// None, so callers fall back to the message instead of failing the whole reply.</summary>
public sealed class HostErrorCodeConverter : System.Text.Json.Serialization.JsonConverter<HostErrorCode>
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

public sealed class HostRequest
{
    public int Protocol { get; set; } = HostProtocol.Version;
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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowFrameDelta { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowStyleRuns { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReuseReadConnection { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? KnownStatusRevision { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? WatchPanes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? WatchRevisions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HostNavigation? Navigation { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? StateVersions { get; set; }
    // Orders "create" against "sync" from the same frontend: a layout snapshot
    // never removes a shell that frontend created after taking the snapshot.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutEpoch { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long LayoutRevision { get; set; }
}

public sealed record HostNavigation(string? SelectedWorkspace, HostWorkspaceNavigation[] Workspaces);
public sealed record HostWorkspaceNavigation(string Id, string? ActiveTabId, Dictionary<string, string?> FocusedPanes);

public sealed class HostReply
{
    public int Protocol { get; set; } = HostProtocol.Version;
    public string Host { get; set; } = "";
    public int ProcessId { get; set; }
    public string? Error { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public HostErrorCode ErrorCode { get; set; }
    public string Text { get; set; } = "";
    public long[] Numbers { get; set; } = [];
    public ShellState? State { get; set; }
    public SavedSession? Layout { get; set; }
    public string[] Panes { get; set; } = [];
    public int CapturedVersion { get; set; } = -1;
    public long FrameRevision { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? DeltaBaseRevision { get; set; }
    // Optional metadata preserves compatibility with older version-2 hosts/clients.
    public TerminalScrollState? Scrollbar { get; set; }
    public int BinaryLength { get; set; }
    public string[] Capabilities { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? OutputRevisions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ShellState>? States { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long TransportAcceptRetries { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepAlive { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? StatusRevision { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] internal int JsonPayloadLength { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public byte[] Binary { get; set; } = [];
}

public sealed record ShellState(int Version, bool HasExited, int ExitCode, string Title, string StatusSummary,
    string Directory, string WorkingDirectory, string? LocalDirectory, TerminalPerformance Performance,
    TerminalStatusSnapshot? Status, long Attention, bool PendingAttention)
{
    public bool? MouseTracking { get; init; }
}

public static class HostProtocol
{
    internal static bool LegacyJsonForCheck = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_JSON") == "legacy";
    // Version 2 preserves browser leaves/settings when the host saves layouts.
    public const int Version = 2;
    public const int MaxJsonBytes = 16 * 1024 * 1024;
    public const int MaxSnapshotBytes = 96 * 1024 * 1024;
    public const int MaxMetadataPanes = 256;
    public const string MetadataBatchCapability = "metadata-batch-v2";
    public const string ReusableReadCapability = "reusable-read-v1";
    public const string StatusRevisionCapability = "status-revisions-v1";
    public const string MetadataBatchTooLarge = "Metadata batch response exceeds the message size limit.";
    public const string ReplyTooLarge = "Session host reply exceeds the message size limit.";
    /// <summary>The small error a host sends in place of a reply over a frame limit. The
    /// limit is checked before any header is written, so the stream stays framed.</summary>
    internal static HostReply OversizedReply(string operation, string host, int processId)
        => new() { Host = host, ProcessId = processId, Error = operation == "states" ? MetadataBatchTooLarge : ReplyTooLarge, ErrorCode = HostErrorCode.ReplyTooLarge };
    /// <summary>The reply's code, or for a host that predates codes the code its fixed
    /// message implies. The only place that still reads version-2 error text.</summary>
    public static HostErrorCode ErrorCodeOf(HostReply reply) => reply.ErrorCode != HostErrorCode.None ? reply.ErrorCode : reply.Error switch
    {
        null => HostErrorCode.None,
        MetadataBatchTooLarge or ReplyTooLarge => HostErrorCode.ReplyTooLarge,
        "This shell session no longer exists." => HostErrorCode.SessionMissing,
        "This shell session has closed." => HostErrorCode.SessionExited,
        "Unknown session operation." => HostErrorCode.UnknownOperation,
        "Shell creation exceeds the session limits." => HostErrorCode.LimitExceeded,
        { } error when error.StartsWith("Input busy", StringComparison.Ordinal) || error.StartsWith("Terminal input is busy", StringComparison.Ordinal) => HostErrorCode.InputBusy,
        { } error when error.StartsWith("The terminal has exited", StringComparison.Ordinal) => HostErrorCode.SessionExited,
        { } error when error.StartsWith("Incompatible session-host protocol", StringComparison.Ordinal) => HostErrorCode.ProtocolMismatch,
        { } error when error.StartsWith("The session host changed", StringComparison.Ordinal) => HostErrorCode.HostChanged,
        _ => HostErrorCode.None
    };
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
