using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oroterm.Models;
using Oroterm.Terminal;

namespace Oroterm.Hosting;

internal sealed class HostMessageTooLargeException():IOException("Session message exceeds the size limit.");

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
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowFrameDelta { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? WatchPanes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,long>? WatchRevisions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HostNavigation? Navigation { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,int>? StateVersions { get; set; }
}

public sealed record HostNavigation(string? SelectedWorkspace,HostWorkspaceNavigation[] Workspaces);
public sealed record HostWorkspaceNavigation(string Id,string? ActiveTabId,Dictionary<string,string?> FocusedPanes);

public sealed class HostReply
{
    public int Protocol { get; set; } = HostProtocol.Version;
    public string Host { get; set; } = "";
    public int ProcessId { get; set; }
    public string? Error { get; set; }
    public string Text { get; set; } = "";
    public long[] Numbers { get; set; } = [];
    public ShellState? State { get; set; }
    public SavedSession? Layout { get; set; }
    public string[] Panes { get; set; } = [];
    public int CapturedVersion { get; set; } = -1;
    public long FrameRevision { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? DeltaBaseRevision { get; set; }
    // Optional metadata preserves compatibility with older version-2 hosts/clients.
    public TerminalScrollState? Scrollbar { get; set; }
    public int BinaryLength { get; set; }
    public string[] Capabilities { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,long>? OutputRevisions { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,ShellState>? States { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long TransportAcceptRetries { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public byte[] Binary { get; set; } = [];
}

public sealed record ShellState(int Version, bool HasExited, int ExitCode, string Title, string StatusSummary,
    string Directory, string WorkingDirectory, string? LocalDirectory, TerminalPerformance Performance,
    TerminalStatusSnapshot Status, long Attention, bool PendingAttention)
{
    public bool? MouseTracking { get; init; }
}

public static class HostProtocol
{
    // Version 2 preserves browser leaves/settings when the host saves layouts.
    public const int Version = 2;
    public const int MaxJsonBytes = 16 * 1024 * 1024;
    public const int MaxSnapshotBytes = 96 * 1024 * 1024;
    public const int MaxMetadataPanes = 256;
    public const string MetadataBatchCapability = "metadata-batch-v2";
    public const string MetadataBatchTooLarge = "Metadata batch response exceeds the message size limit.";
    public static string PipeName(string folder) => "Oroterm-Sessions-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
    public static async Task WriteJson<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaxJsonBytes) throw new HostMessageTooLargeException();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }
    public static async Task<T> ReadJson<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > MaxJsonBytes) throw new IOException("Invalid session message length.");
        byte[] payload = new byte[count]; await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<T>(payload) ?? throw new IOException("Empty session message."); }
        catch(JsonException ex) { throw new IOException("Invalid session JSON.",ex); }
    }
}

