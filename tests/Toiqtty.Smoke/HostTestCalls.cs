using Toiqtty.Hosting;
using Toiqtty.Models;

/// <summary>Blocking test conveniences over the asynchronous host API. The client has no
/// synchronous calls (ArchitectureChecks enforces that); tests block only through these.</summary>
internal static class HostTestCalls
{
    public static SessionHostClient ConnectOrStartForTest(string folder, string? executable = null) => SessionHostClient.ConnectOrStartAsync(folder, executable).GetAwaiter().GetResult();
    public static SessionHostClient? TryConnectForTest(string folder) => SessionHostClient.TryConnectAsync(folder).GetAwaiter().GetResult();
    public static HostReply CallForTest(this SessionHostClient client, HostRequest request, TimeSpan? timeout = null)
    {
        try { return client.CallAsync(request, timeout).GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { throw new IOException("Session host request timed out. Input was not retried.", ex); }
    }
    public static HostReply PingForTest(this SessionHostClient client) => client.CallForTest(HostRequests.Ping());
    public static void ShutdownForTest(this SessionHostClient client) => client.CallForTest(HostRequests.Shutdown());
    public static void DetachForTest(this SessionHostClient client) => client.CallForTest(HostRequests.Detach());
    public static void SaveForTest(this SessionHostClient client, SavedSession layout) => client.SaveAsync(layout).GetAwaiter().GetResult();
    // Shell creation is asynchronous; tests that need the shell before continuing block on Ready.
    public static RemoteTerminalSession StartForTest(this SessionHostClient client, string pane, string command, string directory, AppSettings settings)
    {
        var session = RemoteTerminalSession.Start(Task.FromResult(client), pane, command, directory, settings, Task.CompletedTask);
        session.Ready.GetAwaiter().GetResult(); return session;
    }
    // ITerminalSession is async-only; these block a test thread (never a UI thread) on the same ordered input queue.
    public static void ResizeForTest(this RemoteTerminalSession session, ushort columns, ushort rows, uint width, uint height)
    {
        session.QueueResize(columns, rows, width, height);
        if (!SpinWait.SpinUntil(() => !session.ResizePending, TimeSpan.FromSeconds(15))) throw new TimeoutException("The queued resize was not acknowledged.");
    }
    public static Toiqtty.Terminal.TerminalSnapshot ReadSnapshotForTest(this RemoteTerminalSession session, Toiqtty.Terminal.TerminalCell[] cells) => session.ReadSnapshotAsync(cells).GetAwaiter().GetResult();
    public static (int Count, int Index) FindForTest(this RemoteTerminalSession session, string text, int direction) => session.FindAsync(text, direction).GetAwaiter().GetResult();
    public static string CopySelectionForTest(this RemoteTerminalSession session) => session.CopySelectionAsync().GetAwaiter().GetResult();
}
