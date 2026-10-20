using System.Diagnostics;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class StatusRevisionChecks
{
    internal static async Task Run(string executable, Action<bool, string> check)
    {
        string folder = Path.GetFullPath("artifacts/status-revisions-" + Guid.NewGuid().ToString("N"));
        var client = SessionHostClient.ConnectOrStart(folder, executable); string pane = Guid.NewGuid().ToString("N");
        var session = new RemoteTerminalSession(client, pane, $"\"{Environment.ProcessPath}\" --status-snapshot-client", Environment.CurrentDirectory, new AppSettings());
        var cells = new TerminalCell[3000];
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew(); while (!condition())
            { session.TryReadSnapshot(cells, out _); if (timer.Elapsed.TotalSeconds > 8) throw new TimeoutException("Status revisions did not settle."); await Task.Delay(5); }
        }
        async Task Send(string text) { await session.PasteAsync(text); session.Key(13, 0); await session.InputDrained; }
        HostRequest Request(long? known) => new() { Operation = "capture", Pane = pane, Numbers = [-1], KnownStatusRevision = known, AllowStyleRuns = true };
        try
        {
            session.AttachView(); await Wait(() => session.Status.Progress == 75);
            check(client.SupportsStatusRevisions, "The host negotiates immutable status revisions independently of frame encoding");
            await Send("history"); await Wait(() => session.Status.Commands.Count == 100);
            var full = await client.CallAsync(Request(-1)); long revision = full.StatusRevision ?? throw new Exception("Status revision missing.");
            var reused = await client.CallAsync(Request(revision));
            check(full.State?.Status?.Commands.Count == 100 && reused.State is { Status: null } && reused.StatusRevision == revision,
                "A matching explicit baseline omits unchanged command history while carrying its revision");
            check(full.JsonPayloadLength > reused.JsonPayloadLength * 10, "A warmed 100-command capture avoids more than 90 percent of repeated JSON metadata");
            var legacy = await client.CallAsync(Request(null));
            check(legacy.State?.Status?.Commands.Count == 100 && legacy.StatusRevision is null, "Legacy capture requests retain complete status lists and unchanged wire semantics");
            long before = session.ReusedStatusesForCheck;
            for (int i = 1; i <= 3; i++) { await Send("text"); await Wait(() => string.Concat(cells.Select(cell => cell.GetText())).Contains("STATUS_TEXT_" + i)); }
            check(session.ReusedStatusesForCheck > before && session.Status.Commands.Count == 100 && session.Status.Progress == 75,
                "The frontend restores cached status for new text frames without losing history or progress");
            long fullBefore = session.FullStatusesForCheck;
            await Send("next"); await Wait(() => session.Status.Progress == 25 && session.FullStatusesForCheck > fullBefore);
            var next = await client.CallAsync(Request(revision));
            check(next.StatusRevision > revision && next.State?.Status?.Progress == 25 && next.State.MouseTracking == false, "A changed progress status advances the revision and preserves independent input-mode metadata");
            int corrupt = 1; long recoveries = session.FullFrameRecoveriesForCheck;
            session.CaptureReplyForCheck += reply =>
            {
                if (reply.StatusRevision is not null && reply.State is { Status: null } && Interlocked.CompareExchange(ref corrupt, 0, 1) == 1)
                    reply.StatusRevision = long.MaxValue;
            };
            await Send("text"); await Wait(() => session.FullFrameRecoveriesForCheck > recoveries && corrupt == 0 && session.FullStatusesForCheck > fullBefore + 1);
            check(session.Status.Commands.Count == 100 && session.Status.Progress == 25, "A missing status baseline recovers with a full snapshot and preserves the published state");
            long previousRevision = next.StatusRevision!.Value; await Send("reset"); await Wait(() => session.Status.Commands.Count == 0 && session.Status.ProgressState == 0);
            var reset = await client.CallAsync(Request(previousRevision));
            check(reset.StatusRevision > previousRevision && reset.State?.Status?.Commands.Count == 0, "Terminal reset advances status identity rather than reusing a pre-reset baseline");
            Console.WriteLine("Status revision evidence: " + folder);
        }
        finally { await session.CloseAsync(); try { client.Shutdown(); } catch (IOException) { } }
    }
}
