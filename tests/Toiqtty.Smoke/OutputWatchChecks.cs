using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class OutputWatchChecks
{
    internal static async Task Run(string executable, Action<bool, string> check)
    {
        string folder = Path.GetFullPath("artifacts/output-watch-" + Guid.NewGuid().ToString("N"));
        var host = SessionHostClient.ConnectOrStart(folder, executable);
        string pane = Guid.NewGuid().ToString("N"), other = Guid.NewGuid().ToString("N");
        int demand = 0, notifications = 0, otherNotifications = 0, cancellations = 0;
        Action<string> listener = _ => Interlocked.Increment(ref notifications);
        Action<string> otherListener = _ => Interlocked.Increment(ref otherNotifications);
        void Thrown(object? _, FirstChanceExceptionEventArgs args)
        { if (args.Exception is OperationCanceledException) Interlocked.Increment(ref cancellations); }
        async Task Wait(Func<bool> condition)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition()) { if (elapsed.Elapsed.TotalSeconds > 5) throw new TimeoutException("Output-watch notification did not arrive."); await Task.Delay(5); }
        }
        async Task Ping(string token)
        {
            await host.CallAsync(new() { Operation = "paste", Pane = pane, Text = "ping " + token });
            await host.CallAsync(new() { Operation = "key", Pane = pane, Numbers = [13, 0] });
        }
        try
        {
            string command = $"\"{Environment.ProcessPath}\" --performance-client";
            await host.CallAsync(new() { Operation = "create", Pane = pane, Text = command, Directory = Environment.CurrentDirectory, Settings = new AppSettings() });
            host.AddOutputListener(pane, listener, () => Volatile.Read(ref demand) != 0);
            await Task.Delay(100);
            check(notifications == 0, "An output subscriber without demand stays asleep");
            Volatile.Write(ref demand, 1); host.OutputDemandChanged(); await Wait(() => Volatile.Read(ref notifications) > 0);
            await Task.Delay(100);
            AppDomain.CurrentDomain.FirstChanceException += Thrown;
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    int before = Volatile.Read(ref notifications);
                    await Ping("steady_" + i); await Wait(() => Volatile.Read(ref notifications) > before);
                }
                check(cancellations == 0, "Fifty ordinary output replies deliver notifications without canceling independent demand waits");
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= Thrown; }
            long opens = host.ReadConnectionsOpenedForCheck;
            for (int i = 0; i < 20; i++)
            {
                Volatile.Write(ref demand, 0); host.OutputDemandChanged(); await Task.Delay(2);
                Volatile.Write(ref demand, 1); host.OutputDemandChanged(); await Task.Delay(2);
            }
            check(host.ReadConnectionsOpenedForCheck <= opens + 1, "Twenty temporary demand pauses retain the bounded outstanding watch channel");
            Volatile.Write(ref demand, 0); host.OutputDemandChanged(); await Task.Delay(100);
            int retired = Volatile.Read(ref notifications); await Ping("hidden"); await Task.Delay(100);
            check(notifications == retired, "Retiring demand stops notifications while the shell keeps running");
            Volatile.Write(ref demand, 1); host.OutputDemandChanged(); await Wait(() => Volatile.Read(ref notifications) > retired);
            check(true, "Restoring demand wakes the same subscription after an idle interval");
            host.RemoveOutputListener(pane, listener);
            host.AddOutputListener(pane, listener, () => true);
            int restarted = Volatile.Read(ref notifications); await Ping("restart"); await Wait(() => Volatile.Read(ref notifications) > restarted);
            check(true, "Replacing the final subscriber leaves no abandoned waiter that can steal its notification");
            await host.CallAsync(new() { Operation = "create", Pane = other, Text = command, Directory = Environment.CurrentDirectory, Settings = new AppSettings() });
            host.AddOutputListener(other, otherListener, () => true); await Wait(() => Volatile.Read(ref otherNotifications) > 0);
            check(true, "A newly interested pane interrupts an older watch and receives its first output");
            host.RemoveOutputListener(pane, listener);
            int captureDemand = 0;
            host.AddOutputListener(pane, listener, () => Volatile.Read(ref captureDemand) != 0, () => true);
            await Task.Delay(100); int pausedNotifications = Volatile.Read(ref notifications); long requests = host.WatchRequestsForCheck;
            await Ping("visible_paused"); await Task.Delay(200);
            check(notifications > pausedNotifications && host.WatchRequestsForCheck - requests < 16,
                "A visible paused owner receives its coalescible output wake without spinning while another pane has demand");
            opens = host.ReadConnectionsOpenedForCheck;
            for (int i = 0; i < 20; i++)
            {
                Volatile.Write(ref captureDemand, 1); host.OutputDemandChanged(); await Task.Delay(2);
                Volatile.Write(ref captureDemand, 0); host.OutputDemandChanged(); await Task.Delay(2);
            }
            check(host.ReadConnectionsOpenedForCheck <= opens + 1,
                "New demand in an already visible pane reuses the existing watch instead of reconnecting its channel");
            await CaptureWakeRace(host, command, check);
        }
        finally
        {
            host.RemoveOutputListener(pane, listener); host.RemoveOutputListener(other, otherListener);
            host.Shutdown();
        }
        Console.WriteLine("Output-watch evidence: " + folder);
    }
    private static async Task CaptureWakeRace(SessionHostClient client, string command, Action<bool, string> check)
    {
        string pane = Guid.NewGuid().ToString("N");
        var session = new RemoteTerminalSession(client, pane, command, Environment.CurrentDirectory, new AppSettings());
        using var release = new ManualResetEventSlim();
        var noticed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string> watch = _ => noticed.TrySetResult();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int intercept = 0;
        var cells = new TerminalCell[3000]; session.AttachView();
        async Task Wait(Func<bool> condition, int milliseconds = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) { if (clock.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Capture/output race did not settle."); await Task.Delay(5); }
        }
        bool Contains(string text)
        {
            if (!session.TryReadSnapshot(cells, out _)) return false;
            var builder = new System.Text.StringBuilder(cells.Length); foreach (ref var cell in cells.AsSpan()) builder.Append(cell.GetText());
            return builder.ToString().Contains(text, StringComparison.Ordinal);
        }
        try
        {
            await Wait(() => Contains("PERF_READY")); await Task.Delay(150); Contains("PERF_READY"); await Task.Delay(100);
            session.CaptureReplyForCheck += reply =>
            {
                if (reply.BinaryLength == 0 && Interlocked.CompareExchange(ref intercept, 2, 1) == 1)
                { held.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The empty capture was not released."); }
            };
            Volatile.Write(ref intercept, 1); session.WakeCaptureForCheck(); await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.AddOutputListener(pane, watch, () => true);
            await client.CallAsync(new() { Operation = "paste", Pane = pane, Text = "ping RACE_WAKE" });
            await client.CallAsync(new() { Operation = "key", Pane = pane, Numbers = [13, 0] });
            await noticed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The only wake is the output watch. Input bypasses this remote
            // owner's input queue, and the held reply contains no new frame.
            var elapsed = Stopwatch.StartNew(); release.Set(); await Wait(() => session.Title == "PING_RACE_WAKE", 600);
            check(elapsed.ElapsedMilliseconds < 600, "Output arriving during an unchanged capture wakes the next capture without the one-second idle fallback");
        }
        finally { release.Set(); client.RemoveOutputListener(pane, watch); session.DetachView(); await session.CloseAsync(); }
    }
}
