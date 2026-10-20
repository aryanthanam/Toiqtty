using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Toiqtty.Automation;

public sealed class ControlRequest
{
    public int Version { get; set; } = 1;
    public string Command { get; set; } = "sessions";
    public string Session { get; set; } = "";
    public string Pane { get; set; } = "";
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Shell { get; set; } = "";
    public string Direction { get; set; } = "right";
    public string Text { get; set; } = "";
    public string Action { get; set; } = "";
}
public sealed class ControlReply
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }
    public static ControlReply Success(object? data = null) => new() { Ok = true, Data = data is null ? null : JsonSerializer.SerializeToElement(data) };
}
public static class ControlProtocol
{
    public const int RequestLimit = 128 * 1024, ReplyLimit = 1024 * 1024;
    public static string PipeName(string folder) => "Toiqtty-Control-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
    public static async Task Write<T>(Stream stream, T value, int limit, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value); if (bytes.Length > limit) throw new IOException("CLI message exceeds its size limit.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }
    public static async Task<T> Read<T>(Stream stream, int limit, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header); if (count <= 0 || count > limit) throw new IOException("Invalid CLI message length.");
        byte[] bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new IOException("Empty CLI message.");
    }
    public static async Task<ControlReply> Call(string folder, ControlRequest request, CancellationToken cancellation = default)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(folder), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation); connect.CancelAfter(TimeSpan.FromSeconds(2));
        try { await pipe.ConnectAsync(connect.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new IOException("No compatible toiqtty control endpoint is listening. Open toiqtty, then retry the command."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await Write(pipe, request, RequestLimit, deadline.Token);
        try { return await Read<ControlReply>(pipe, ReplyLimit, deadline.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new IOException("CLI response timed out. The command may have executed; it has not been retried."); }
    }
}
public sealed class ControlServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private int _disposed;
    public ControlServer(string folder, Func<ControlRequest, CancellationToken, Task<ControlReply>> handle)
        => _listener = Listen(folder, handle);
    private async Task Listen(string folder, Func<ControlRequest, CancellationToken, Task<ControlReply>> handle)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(ControlProtocol.PipeName(folder), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(18));
                ControlReply reply;
                try
                {
                    var request = await ControlProtocol.Read<ControlRequest>(pipe, ControlProtocol.RequestLimit, deadline.Token).ConfigureAwait(false);
                    reply = await handle(request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
                { reply = new() { Error = ex.Message }; }
                await ControlProtocol.Write(pipe, reply, ControlProtocol.ReplyLimit, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed,1)!=0) return; await _stop.CancelAsync(); await _listener.ConfigureAwait(false); _stop.Dispose(); }
}
