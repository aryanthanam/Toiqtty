namespace Toiqtty.Hosting;

/// <summary>Single-flight connection to one data directory's session host. Connect,
/// start and liveness run on the thread pool; concurrent callers share one attempt.
/// A failure is reused for a short backoff (or until Retry), a host exit clears the
/// cached client, and a protocol mismatch is reported once and kept until Retry.</summary>
public sealed class SessionHostConnector(string folder, string? executable = null)
{
    private const long BackoffMilliseconds = 5000;
    private readonly Lock _gate = new();
    private Task<SessionHostClient>? _connection;
    private long _failedAt;
    private int _versionReported;
    public string Folder => folder;
    /// <summary>Raised once, on the thread pool, when the running host speaks another protocol.</summary>
    public event Action<SessionHostVersionException>? VersionMismatch;
    /// <summary>The connected client, or null while none is resolved or after its host exited.</summary>
    public SessionHostClient? Current
    {
        get { var connection = Volatile.Read(ref _connection); return connection is { IsCompletedSuccessfully: true } ? connection.Result : null; }
    }
    public Task<SessionHostClient> ConnectAsync()
    {
        lock (_gate)
        {
            if (_connection is { } connection)
            {
                if (!connection.IsCompleted) return connection;
                if (connection.IsCompletedSuccessfully) { if (connection.Result.IsRunning) return connection; }
                else if (connection.Exception?.InnerException is SessionHostVersionException
                    || Environment.TickCount64 - Volatile.Read(ref _failedAt) < BackoffMilliseconds) return connection;
            }
            return _connection = Connect();
        }
    }
    /// <summary>Use a client the caller already resolved, such as the startup probe.</summary>
    public void Adopt(SessionHostClient client)
    {
        lock (_gate) _connection = Task.FromResult(client);
        Watch(client);
    }
    /// <summary>Forget a remembered failure so the next ConnectAsync tries again now.</summary>
    public void Retry()
    {
        lock (_gate) if (_connection is { IsCompleted: true, IsCompletedSuccessfully: false }) _connection = null;
        Interlocked.Exchange(ref _versionReported, 0);
    }
    /// <summary>Drop the cached client, for example once no shell needs the host.</summary>
    public void Reset() { lock (_gate) _connection = null; }
    // Runs synchronously only up to the first await, so callers hold _gate briefly.
    private async Task<SessionHostClient> Connect()
    {
        try
        {
            var client = await SessionHostClient.ConnectOrStartAsync(folder, executable).ConfigureAwait(false);
            Watch(client); return client;
        }
        catch (SessionHostVersionException ex)
        {
            if (Interlocked.Exchange(ref _versionReported, 1) == 0) VersionMismatch?.Invoke(ex);
            throw;
        }
        catch { Volatile.Write(ref _failedAt, Environment.TickCount64); throw; }
    }
    private void Watch(SessionHostClient client)
    {
        client.Exited += () => Forget(client);
        if (!client.IsRunning) Forget(client);
    }
    private void Forget(SessionHostClient client)
    {
        lock (_gate) if (_connection is { IsCompletedSuccessfully: true } connection && ReferenceEquals(connection.Result, client)) _connection = null;
    }
}
