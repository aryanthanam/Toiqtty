using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Toiqtty.Models;

/// <summary>One writer per data directory, with activation forwarded to its existing window.</summary>
public sealed class InstanceCoordinator : IDisposable
{
    private readonly FileStream _lease;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipe;
    private readonly Task _listener;
    private int _disposed;
    public event Action? Activated;
    public event Action<string, string>? NavigationRequested;
    private static string PipeName(string folder) => "Toiqtty-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
    private InstanceCoordinator(FileStream lease, string folder)
    {
        _lease = lease; _pipe = PipeName(folder);
        _listener = Listen();
    }
    public static InstanceCoordinator? TryAcquire(string folder)
    {
        Directory.CreateDirectory(folder);
        try { return new(new FileStream(Path.Combine(folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), folder); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }
    public static async Task<bool> ActivateExisting(string folder, string workspace = "", string pane = "")
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(folder), PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            if (workspace.Length == 0 && pane.Length == 0) await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            else
            {
                var payload = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[] { workspace, pane }));
                if (payload.Length > 1024) return false;
                await pipe.WriteAsync(new byte[] { 2, (byte)payload.Length, (byte)(payload.Length >> 8) }, timeout.Token);
                await pipe.WriteAsync(payload, timeout.Token);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return false; }
    }
    private async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var message = new byte[1];
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                if (await pipe.ReadAsync(message, request.Token).ConfigureAwait(false) != 1) continue;
                if (message[0] == 1) Activated?.Invoke();
                else if (message[0] == 2)
                {
                    var length = new byte[2]; await pipe.ReadExactlyAsync(length, request.Token).ConfigureAwait(false);
                    int count = length[0] | (length[1] << 8);
                    if (count is <= 0 or > 1024) continue;
                    var payload = new byte[count]; await pipe.ReadExactlyAsync(payload, request.Token).ConfigureAwait(false);
                    var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(payload);
                    if (ids is { Length: 2 } && ids[0] is not null && ids[1] is not null) NavigationRequested?.Invoke(ids[0], ids[1]);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException) { }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener.GetAwaiter().GetResult();
        _lease.Dispose(); _stop.Dispose();
    }
}
