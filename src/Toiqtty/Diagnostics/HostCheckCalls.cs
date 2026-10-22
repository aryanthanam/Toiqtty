using Toiqtty.Hosting;

namespace Toiqtty;

/// <summary>Blocking host round trips for UI smoke checks only. The client exposes no synchronous
/// call; product code awaits CallAsync, and ArchitectureChecks keeps blocking waits out of it.</summary>
internal static class HostCheckCalls
{
    internal static HostReply CallForCheck(this SessionHostClient client, HostRequest request, TimeSpan? timeout = null)
    {
        try { return client.CallAsync(request, timeout).GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { throw new HostTransportException(HostErrorCode.Timeout, "Session host request timed out. Input was not retried.", ex); }
    }
}
