using Toiqtty.Hosting;
using Toiqtty.Models;

/// <summary>Blocking test conveniences over the asynchronous host API. Later protocol
/// steps delete the client's synchronous calls; tests depend only on these wrappers.</summary>
internal static class HostTestCalls
{
    public static HostReply CallForTest(this SessionHostClient client, HostRequest request, TimeSpan? timeout = null)
    {
        try { return client.CallAsync(request, timeout).GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { throw new IOException("Session host request timed out. Input was not retried.", ex); }
    }
    public static HostReply PingForTest(this SessionHostClient client) => client.CallForTest(HostRequests.Ping());
    public static void ShutdownForTest(this SessionHostClient client) => client.CallForTest(HostRequests.Shutdown());
    public static void DetachForTest(this SessionHostClient client) => client.CallForTest(HostRequests.Detach());
    public static void SaveForTest(this SessionHostClient client, SavedSession layout) => client.SaveAsync(layout).GetAwaiter().GetResult();
}
