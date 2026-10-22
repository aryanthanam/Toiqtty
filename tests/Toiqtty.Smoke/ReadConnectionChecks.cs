using System.IO.Pipes;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class ReadConnectionChecks
{
    internal static async Task Run(string executable, Action<bool, string> check)
    {
        string folder = Path.GetFullPath("artifacts/read-connections-" + Guid.NewGuid().ToString("N"));
        var client = SessionHostClient.ConnectOrStart(folder, executable);
        var held = new List<NamedPipeClientStream>();
        var ids = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        SessionHostClient.CaptureReadLease? lease = null;
        try
        {
            check(SessionHostClient.RequiredCapabilities.All((await client.CallAsync(new())).Capabilities.Contains),
                "The host advertises every capability the client requires, including reusable read connections");
            var markers = new Dictionary<string, string>();
            foreach (string id in ids)
            {
                await client.CallAsync(new() { Operation = "create", Pane = id, Text = $"\"{Environment.ProcessPath}\" --persistent-shell-client", Directory = Environment.CurrentDirectory });
                var until = DateTime.UtcNow.AddSeconds(8);
                while (true)
                {
                    var reply = await client.CallAsync(Request(id)); string text = Text(reply);
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"PERSIST_READY PID=(\d+)");
                    if (match.Success) { markers[id] = match.Value; break; }
                    if (DateTime.UtcNow > until) throw new TimeoutException("Read-connection shell did not become ready."); await Task.Delay(10);
                }
            }
            lease = client.AcquireCaptureReads() ?? throw new Exception("Capture pool not acquired.");
            var pending = Enumerable.Range(0, 64).Select(async index =>
            {
                string id = ids[index % 2]; var reply = await client.CaptureAsync(Request(id), [], CancellationToken.None, lease);
                return reply.KeepAlive && (reply.BinaryLength == 0 || Text(reply).Contains(markers[id], StringComparison.Ordinal));
            });
            check((await Task.WhenAll(pending)).All(value => value), "Concurrent pooled captures preserve reply boundaries and the requested shell identity");
            check(client.ReadConnectionsOpenedForCheck is > 0 and <= 8 && client.ReadConnectionsReusedForCheck >= 32,
                "Capture traffic reuses at most eight authenticated connections");
            bool rejected = false;
            try { await client.CaptureAsync(new() { Operation = "paste", Pane = ids[0], Text = "mark\r" }, [], CancellationToken.None, lease); } catch (ArgumentException) { rejected = true; }
            check(rejected && !Text(await client.CallAsync(Request(ids[0]))).Contains("PERSIST_MARK="), "The read pool rejects input requests before sending or retrying them");
            lease.Dispose(); lease.Dispose(); lease = null;
            check(!client.HasCapturePoolForCheck, "The last capture lease retires its pool and idle pipe handles");
            await Task.Delay(100);
            for (int i = 0; i < 20; i++)
            {
                var pipe = new NamedPipeClientStream(".", HostProtocol.PipeName(folder), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(3000); var request = Request(ids[0]); request.Host = client.Identity; request.ReuseReadConnection = true;
                await HostProtocol.WriteJson(pipe, request, CancellationToken.None);
                var reply = await HostProtocol.ReadJson<HostReply>(pipe, CancellationToken.None);
                if (reply.BinaryLength > 0) await pipe.ReadExactlyAsync(new byte[reply.BinaryLength]);
                if (reply.KeepAlive) held.Add(pipe); else pipe.Dispose();
            }
            check(held.Count == 16, "The host caps retained read connections globally and declines excess keep-alive requests");
            await client.CallAsync(new() { Operation = "paste", Pane = ids[0], Text = "mark\r" }, TimeSpan.FromSeconds(3));
            var deadline = DateTime.UtcNow.AddSeconds(5); bool observed = false;
            while (DateTime.UtcNow < deadline)
            {
                if (Text(await client.CallAsync(Request(ids[0]))).Contains("PERSIST_MARK=1")) { observed = true; break; }
                await Task.Delay(10);
            }
            check(observed, "Input and one-shot captures remain responsive while all persistent read slots are occupied");
            foreach (var pipe in held) pipe.Dispose(); held.Clear();
            await Task.Delay(100);
            using (var replacement = client.AcquireCaptureReads())
                check((await client.CaptureAsync(Request(ids[0]), [], CancellationToken.None, replacement)).KeepAlive, "A new owner creates a fresh pool after the previous lifetime ends");
            check(!client.HasCapturePoolForCheck, "A replacement pool also releases every connection when its final lease ends");
            Console.WriteLine("Read connection evidence: " + folder);
        }
        finally
        {
            lease?.Dispose(); foreach (var pipe in held) pipe.Dispose();
            foreach (string id in ids) try { await client.CallAsync(new() { Operation = "close", Pane = id }); } catch (IOException) { }
            try { client.ShutdownForTest(); } catch (IOException) { }
        }
        await StaleConnection(false, check); await StaleConnection(true, check);
        await EphemeralReads(check); await MissingCapability(check);
    }
    private static HostRequest Request(string pane) => new() { Operation = "capture", Pane = pane, Numbers = [-1], AllowStyleRuns = true };
    private static string Text(HostReply reply)
    {
        if (reply.BinaryLength == 0) return "";
        var (Snapshot, Cells) = HostSnapshotCodec.Decode(reply.Binary, [], payloadLength: reply.BinaryLength);
        try { return string.Concat(Cells.Select(cell => cell.GetText())); }
        finally { TerminalCellPool.Shared.Return(Cells); }
    }
    // A scripted single-instance host: one reply per connection, in order.
    private static (string Folder, Task Server) Fixture(string prefix, int connections, Func<int, HostRequest, HostReply> answer, CancellationToken token)
    {
        string folder = Path.GetFullPath("artifacts/" + prefix + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string name = HostProtocol.PipeName(folder);
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            for (int index = 0; index < connections; index++)
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                listening.TrySetResult();
                await pipe.WaitForConnectionAsync(token); var request = await HostProtocol.ReadJson<HostRequest>(pipe, token);
                var reply = answer(index, request); reply.ProcessId = Environment.ProcessId;
                await HostProtocol.WriteJson(pipe, reply, token);
            }
        });
        listening.Task.Wait(token);
        return (folder, server);
    }
    private static async Task MissingCapability(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var partial = SessionHostClient.RequiredCapabilities.Where(name => name != "literal-input").ToArray();
        var (folder, server) = Fixture("read-partial-", 1, (_, _) => new() { Host = "partial", Capabilities = partial }, deadline.Token);
        bool upgrade = false;
        try { SessionHostClient.TryConnect(folder); } catch (SessionHostVersionException) { upgrade = true; }
        await server;
        check(upgrade, "A version-2 host lacking any consumed capability leads to the version window instead of client fallbacks");
    }
    private static async Task EphemeralReads(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string identity = Guid.NewGuid().ToString("N"); bool reuseRequested = true;
        // Ping, then two captures whose keep-alive the full-capability host denies.
        var (folder, server) = Fixture("read-ephemeral-", 3, (index, request) =>
        {
            if (index > 0) reuseRequested &= request.Operation == "capture" && request.ReuseReadConnection;
            return new() { Host = identity, Capabilities = SessionHostClient.RequiredCapabilities, Text = "read" + index };
        }, deadline.Token);
        var client = SessionHostClient.TryConnect(folder) ?? throw new Exception("Fixture did not answer ping.");
        using var lease = client.AcquireCaptureReads() ?? throw new Exception("Capture pool not acquired.");
        string first = (await client.CaptureAsync(Request("test"), [], deadline.Token, lease)).Text;
        string second = (await client.CaptureAsync(Request("test"), [], deadline.Token, lease)).Text;
        await server;
        check(first == "read1" && second == "read2" && reuseRequested && client.ReadConnectionsOpenedForCheck == 2 && client.ReadConnectionsReusedForCheck == 0,
            "A denied keep-alive falls back to one ephemeral read connection per request");
    }
    private static async Task StaleConnection(bool replaceIdentity, Action<bool, string> check)
    {
        string identity = Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Ping, first capture (server closes its advertised keep-alive), retry.
        var (folder, server) = Fixture("read-reconnect-", 3, (index, request) =>
        {
            if (index > 0 && request.Operation != "capture") throw new Exception("A non-read request reached the reusable channel.");
            return new()
            {
                Host = index == 2 && replaceIdentity ? "replaced" : identity,
                Capabilities = SessionHostClient.RequiredCapabilities,
                KeepAlive = index > 0,
                Text = index == 1 ? "first" : "reconnected"
            };
        }, deadline.Token);
        var client = SessionHostClient.TryConnect(folder) ?? throw new Exception("Fixture did not answer ping.");
        using var lease = client.AcquireCaptureReads();
        check((await client.CaptureAsync(Request("test"), [], deadline.Token, lease)).Text == "first", "The controlled read fixture establishes a reusable connection");
        bool changed = false; HostReply? reply = null;
        try { reply = await client.CaptureAsync(Request("test"), [], deadline.Token, lease); } catch (IOException) when (replaceIdentity) { changed = true; }
        await server;
        check(replaceIdentity ? changed : reply?.Text == "reconnected", replaceIdentity
            ? "A restarted host identity is rejected after stale-read reconnect, without replaying input"
            : "An EOF on a previously authenticated read channel reconnects once and completes the read-only request");
    }
}
