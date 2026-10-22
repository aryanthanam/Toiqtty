using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class ArchitectureChecks
{
    internal static async Task RunHost(string hostExecutable, Action<bool, string> check)
    {
        string folder = Path.GetFullPath("artifacts/architecture-host-" + Guid.NewGuid().ToString("N"));
        var client = SessionHostClient.ConnectOrStart(folder, hostExecutable);
        string id = Guid.NewGuid().ToString("N");
        var session = new RemoteTerminalSession(client, id, $"\"{Environment.ProcessPath}\" --persistent-shell-client", Environment.CurrentDirectory, new());
        var cells = new TerminalCell[3000];
        using var release = new ManualResetEventSlim();
        var intercepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int hold = 0, oldReply = 0;
        static async Task Wait(Func<bool> condition)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition()) { if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException("Geometry transition did not settle."); await Task.Delay(10); }
        }
        try
        {
            session.AttachView(); session.QueueResize(100, 30, 8, 21);
            await Wait(() => session.TryReadSnapshot(cells, out var frame) && frame!.Frame.CellWidth == 8);
            session.CaptureReplyForCheck += reply =>
            {
                if (reply.BinaryLength > 0 && Interlocked.CompareExchange(ref hold, 2, 1) == 1)
                { intercepted.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Old frame release timed out."); Volatile.Write(ref oldReply, 1); }
            };
            session.FrameAvailable += () =>
            {
                if (Interlocked.Exchange(ref oldReply, 0) == 1)
                    rejected.TrySetResult(!session.TryReadSnapshot(cells, out _) && !session.FrameAwaitingReadForCheck);
            };
            Volatile.Write(ref hold, 1); session.Paste("mark"); session.Key(13, 0);
            await intercepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.QueueResize(100, 30, 16, 42); await session.InputDrained;
            release.Set();
            check(await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                "A delayed frame with unchanged rows/columns but obsolete pixel metrics is rejected without capture backpressure");
            await Wait(() => session.TryReadSnapshot(cells, out var frame) && frame!.Frame.CellWidth == 16 && frame.Frame.CellHeight == 42);
            check(true, "The next frame publishes the new physical geometry without recreating the shell");
            SavedSession Only(string pane) => new() { Workspaces = [new() { Name = "ordering", Root = new() { Id = pane, Command = "ignored", Directory = Environment.CurrentDirectory } }] };
            long stale = client.NextLayoutRevision();
            string late = Guid.NewGuid().ToString("N");
            var created = new RemoteTerminalSession(client, late, $"\"{Environment.ProcessPath}\" --persistent-shell-client", Environment.CurrentDirectory, new());
            await client.SaveAsync(Only(id), stale);
            check((await client.CallAsync(new())).Panes.Contains(late), "A layout snapshot taken before a shell was created never removes that shell");
            await client.SaveAsync(Only(id));
            check(!(await client.CallAsync(new())).Panes.Contains(late) && (await client.CallAsync(new())).Panes.Contains(id),
                "A newer layout snapshot still removes shells it no longer lists");
            await created.DetachAsync();
            session.DetachView(); await session.DetachAsync();
            check((await client.CallAsync(new())).Panes.Contains(id), "Awaited detach completes frontend cleanup while preserving the owned shell");
            var attached = new RemoteTerminalSession(client, id, "ignored", Environment.CurrentDirectory, new());
            Task close = attached.CloseAsync().AsTask();
            check(ReferenceEquals(close, attached.CloseAsync().AsTask()), "Repeated explicit closure shares one teardown completion");
            await close.WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine("Architecture host evidence: " + folder);
        }
        finally { release.Set(); await session.DetachAsync(); try { client.Shutdown(); } catch (IOException) { } }
    }

    internal static async Task Run(Action<bool, string> check)
    {
        check(typeof(GhosttyTerminal).Assembly.GetName().Name == "Toiqtty.Core"
            && typeof(RemoteTerminalSession).Assembly.GetName().Name == "Toiqtty.Client",
            "Core and client checks exercise the shared production assemblies");
        var rejecting = new OrderedHostInput(request => request.Operation == "busy"
            ? Task.FromException<HostReply>(new HostRejectedException("Input busy: fixture")) : Task.FromResult(new HostReply()));
        var busy = await rejecting.Enqueue(new() { Operation = "busy" });
        var after = await rejecting.Enqueue(new() { Operation = "send-text", Text = "x" });
        check(busy.Error == "Input busy: fixture" && after.Error is null, "A request the host rejected leaves later input deliverable");
        var broken = new OrderedHostInput(_ => Task.FromException<HostReply>(new IOException("pipe broke")));
        var ambiguous = await broken.Enqueue(new() { Operation = "send-text", Text = "x" });
        var closed = await broken.Enqueue(new() { Operation = "send-text", Text = "y" });
        check(ambiguous.Error is not null && closed.Error is not null && closed.Error.Contains("closed"), "A transport failure still closes input without replay");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<(SavedSession Layout, bool Navigation)>();
        var queue = new SessionPersistenceQueue(async (layout, navigation, _) =>
        {
            writes.Add((layout, navigation));
            if (writes.Count == 1) { started.SetResult(); await release.Task; }
        });
        var source = new SavedSession { Workspaces = [new Workspace { Name = "first" }] };
        var first = queue.Enqueue(source, false, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Workspaces[0].Name = "second";
        _ = queue.Enqueue(source, false, null);
        source.Workspaces[0].Name = "latest";
        var latest = queue.Enqueue(source, true, null);
        source.Workspaces[0].Name = "mutated after enqueue";
        check(!latest.IsCompleted, "Save admission remains asynchronous while persistence is blocked");
        release.SetResult();
        await Task.WhenAll(first, latest, queue.FlushAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        check(writes.Count == 2 && writes[0].Layout.Workspaces[0].Name == "first" && writes[1].Layout.Workspaces[0].Name == "latest",
            "Persistence coalesces pending saves and isolates snapshots from later UI mutations");
        check(!writes[1].Navigation, "A later navigation update cannot downgrade a pending structural save");
        int attempts = 0;
        var failing = new SessionPersistenceQueue((_, _, _) => ++attempts == 1 ? Task.FromException(new IOException("fixture failure")) : Task.CompletedTask);
        bool observed = false;
        try { await failing.Enqueue(source, false, null).WaitAsync(TimeSpan.FromSeconds(5)); } catch (IOException) { observed = true; }
        await failing.Enqueue(source, false, null).WaitAsync(TimeSpan.FromSeconds(5));
        check(observed && attempts == 2, "Persistence exposes failures and accepts a subsequent save without stranding its drain task");

        await using var shell = new TerminalSession("cmd.exe /D /Q /K", Environment.CurrentDirectory);
        var leaf = new PaneNode { Session = shell };
        var lifetime = new SessionLifetimeCoordinator();
        check(PaneNode.Remove(leaf, leaf.Id) is null, "Removing the final leaf changes only the layout");
        await ((ITerminalSession)shell).DetachAsync();
        shell.Paste("echo ARCHITECTURE_SHELL_STILL_ALIVE"); shell.Key(13, 0);
        var cells = new TerminalCell[3000]; var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!shell.TryReadSnapshot(cells, out _) || !string.Concat(cells.Select(c => c.GetText())).Contains("ARCHITECTURE_SHELL_STILL_ALIVE"))
        { if (watch.Elapsed.TotalSeconds > 5) throw new TimeoutException("Layout removal closed its shell."); await Task.Delay(10); }
        check(true, "Layout removal and presentation detachment preserve shell input until its owner explicitly closes it");
        lifetime.ClosePanes([leaf]); await lifetime.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(shell.HasExited, "The lifetime coordinator awaits explicit shell teardown");
    }
}
