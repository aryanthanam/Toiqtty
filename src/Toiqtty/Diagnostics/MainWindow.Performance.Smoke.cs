using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Toiqtty.Controls;
using Toiqtty.Hosting;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private sealed record LoadLatency(string Pane, double ChildReadMs, double ChildWriteMs, double? HostStateMs, double PresentMs, double UiContinuationMs, double DrawStartMs, double DrawDurationMs, int Draws, long OriginQpc, long DrawStartQpc, long PresentReturnQpc);

    private async Task RunPerformanceProbe(Action<bool, string> check, Func<Func<bool>, Task> wait)
    {
        if (!Path.GetFileName(SessionStore.Folder).StartsWith("performance-", StringComparison.Ordinal))
            throw new InvalidOperationException("Performance checks require an isolated performance-* directory.");
        _metadataTimer.Stop();
        _host!.DisablePaneOutputWatchForCheck = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_WATCH_MODE") == "legacy";
        _host.CancelRetiredWatchForCheck = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_WATCH_CANCEL") == "retired";
        _host.DisableStyleRunsForCheck = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_CELLS") == "legacy";
        _host.DisableReusableReadsForCheck = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_READS") == "ephemeral";
        _host.DisableStatusRevisionsForCheck = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_STATUS") == "legacy";
        bool expandedQuads = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_QUADS") == "expanded";
        bool compositionRetry = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_RETRY") == "composition";
        bool nativeLayouts = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_LAYOUTS") == "native";
        bool asciiShapes = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_ASCII") == "harfbuzz";
        bool streamedAscii = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_ASCII_STORAGE") == "font-scratch";
        if (asciiShapes && !DirectXRenderer.AsciiShapesAvailable) throw new InvalidOperationException("The diagnostic HarfBuzz native renderer is required.");
        bool compositionBulk = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_BULK") == "composition";
        bool legacyAtlasHash = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_ATLAS_HASH") == "legacy";
        bool reserveResize = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_RESIZE") == "capacity";
        uint frameLatency = uint.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_FRAME_LATENCY"), out var latency) ? latency : 2;
        uint presentInterval = uint.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_PRESENT_INTERVAL"), out var interval) ? interval : 0;
        string client = Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE") ?? throw new InvalidOperationException("Performance client missing.");
        string command = $"\"{client}\" --performance-client";
        var original = _selected!; _saved.Settings.Shell = command;
        NewWorkspace(command, "Performance"); RemoveWorkspace(original);
        await wait(() => _focused?.RenderedText.Contains("PERF_READY") == true);
        var measurements = new List<object>();
        var profiles = new List<object>();
        bool profileRendering = Environment.GetEnvironmentVariable("TOIQTTY_RENDER_PROFILE") == "1";
        int[] paneCounts = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_SCALE") == "16" ? [1, 4, 8, 16] : [1, 4, 8];
        if (int.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_PANES"), out int requestedPanes))
        {
            if (requestedPanes is not (1 or 4 or 8 or 16)) throw new ArgumentException("Unsupported benchmark pane count.");
            paneCounts = [requestedPanes];
        }
        string[] phases = ["quiet", "controlled-output", "saturated-output", "mixed-output"];
        if (Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_PHASE") is { Length: > 0 } requestedPhase)
        {
            if (!phases.Contains(requestedPhase)) throw new ArgumentException("Unsupported benchmark phase.");
            phases = [requestedPhase];
        }
        int outputMilliseconds = 5000;
        if (int.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_OUTPUT_MS"), out int requestedMilliseconds))
        {
            if (requestedMilliseconds is < 1000 or > 30000) throw new ArgumentException("Unsupported benchmark output duration.");
            outputMilliseconds = requestedMilliseconds;
        }
        foreach (int paneCount in paneCounts)
        {
            while (_views.Count < paneCount)
            {
                int previous = _views.Count;
                var current = _selected!; var target = current.Root.Leaves().Last();
                current.Root.Split(target.Id, previous % 2 == 1, _saved.Settings.Shell);
                var leaves = current.Root.Leaves().ToArray();
                current.Root = Balance(leaves, 0); RebuildPanes(leaves[^1].Id); Save();
                await wait(() => _views.Count == previous + 1 && _focused?.InputHasFocus == true && _views.All(v => v.IsLoaded && !v.ResizePendingForCheck));
            }
            if (paneCount > 1) { _selected!.Root = Balance(_selected.Root.Leaves().ToArray(), 0); RebuildPanes(); }
            await wait(() => _views.Count == paneCount && _views.All(v => v.FramesPainted > 0 && !v.ResizePendingForCheck));
            var panes = _selected!.Root.Leaves().ToArray();
            var shells = panes.Select(p => (RemoteTerminalSession)p.Session!).ToArray();
            var views = panes.Select(p => _views.Single(v => v.PaneId == p.Id)).ToArray();
            foreach (var view in views) view.SetPresentationModesForCheck(expandedQuads, compositionRetry, nativeLayouts, compositionBulk);
            foreach (var view in views) view.SetAtlasHashForCheck(legacyAtlasHash);
            foreach (var view in views) view.SetAsciiShapesForCheck(asciiShapes);
            foreach (var view in views) view.SetStreamedAsciiForCheck(streamedAscii);
            foreach (var view in views) view.SetEagerBackgroundForCheck(Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_BACKGROUND_LAYERS") == "eager");
            foreach (var view in views) view.SetReserveResizeForCheck(reserveResize);
            foreach (var view in views) view.SetPresentationPolicyForCheck(frameLatency, presentInterval);
            check(views.All(v => int.Parse(v.ViewportLabel.Split(' ')[0]) >= 24), "The balanced " + paneCount + "-pane viewport fits the latency response marker");
            await Task.Delay(500);
            using var ui = Process.GetCurrentProcess(); using var host = Process.GetProcessById(_host!.ProcessId);
            int idleSettle = int.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_IDLE_SETTLE_MS"), out int settle) ? Math.Clamp(settle, 0, 30000) : 0;
            int idleDuration = int.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_IDLE_MS"), out int duration) ? Math.Clamp(duration, 2000, 30000) : 2000;
            if (idleSettle > 0) await Task.Delay(idleSettle);
            var (UiCpu, HostCpu, Allocated, UiPrivate, HostPrivate) = Sample(ui, host); var timer = Stopwatch.StartNew(); await Task.Delay(idleDuration); var idleEnd = Sample(ui, host);
            measurements.Add(new
            {
                phase = "idle",
                paneCount,
                settleMilliseconds = idleSettle,
                seconds = timer.Elapsed.TotalSeconds,
                uiCpuMs = idleEnd.UiCpu - UiCpu,
                hostCpuMs = idleEnd.HostCpu - HostCpu,
                uiAllocatedBytes = idleEnd.Allocated - Allocated,
                uiPrivateBytes = idleEnd.UiPrivate,
                hostPrivateBytes = idleEnd.HostPrivate
            });
            foreach (string phase in phases)
            {
                check(_power.DisplayState != 0, "The display is powered on during " + phase + " with " + paneCount + " panes");
                var samples = new List<LoadLatency>();
                long bytesBefore = shells.Sum(s => s.Performance.BytesRead), rowsBefore = views.Sum(v => v.RowsPainted);
                long snapshotBytesBefore = shells.Sum(s => s.FrameBytesForCheck);
                long metadataBefore = shells.Sum(s => s.FrameMetadataBytesForCheck), statusesBefore = shells.Sum(s => s.FullStatusesForCheck), statusReuseBefore = shells.Sum(s => s.ReusedStatusesForCheck);
                long capturesBefore = shells.Sum(s => s.CapturesForCheck), textPresentsBefore = views.Sum(v => v.TextPresentationsForCheck);
                long readBatchesBefore = shells.Sum(s => s.Performance.ReadBatches), nativeSnapshotsBefore = shells.Sum(s => s.Performance.Snapshots);
                var presentationBefore = views.Select(v => v.PresentationForCheck).ToArray();
                int runsBefore = views.Sum(v => v.ShapedRuns); long spacingBefore = views.Sum(v => v.CharacterSpacingCalls);
                var gpuBefore = views.Select(v => v.NativeRenderStatsForCheck).ToArray();
                Profile("start", phase, paneCount, views);
                var start = Sample(ui, host); long qpcStart = Stopwatch.GetTimestamp(); timer.Restart();
                long watchesBefore = _host.WatchRequestsForCheck, notificationsBefore = _host.WatchNotificationsForCheck;
                long openedBefore = _host.ReadConnectionsOpenedForCheck, reusedBefore = _host.ReadConnectionsReusedForCheck;
                var finished = new List<Task>(); var subscriptions = new List<(RemoteTerminalSession Shell, Action<ShellState, long> Handler)>();
                long kibPerPane = phase == "controlled-output" ? 32768 / paneCount : phase == "mixed-output" ? 32768 : 0;
                string run = Guid.NewGuid().ToString("N");
                try
                {
                    if (phase != "quiet")
                    {
                        foreach (var shell in phase == "mixed-output" ? shells[..1] : shells)
                        {
                            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            void State(ShellState state, long ticks) { if (state.Title.StartsWith("FLOOD_DONE_" + run + "_", StringComparison.Ordinal)) done.TrySetResult(); }
                            shell.StateObservedForCheck += State; subscriptions.Add((shell, State)); finished.Add(done.Task);
                            shell.Paste("flood2 " + outputMilliseconds + " " + kibPerPane + " " + run); shell.Key(13, 0);
                        }
                        await Task.WhenAll(shells.Select(s => s.InputDrained));
                    }
                    // A resize can reset terminal margins and scroll away the
                    // fixed response marker. Continuous resize validates
                    // output/resource churn; fixed-viewport runs own latency.
                    int count = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_RESIZE_STRESS") == "1" ? 0 : phase == "quiet" ? 64 : 32;
                    for (int i = 0; i < count; i++)
                    {
                        samples.Add(await Probe(shells[i % paneCount], views[i % paneCount]));
                        await Task.Delay(phase == "quiet" ? 5 : 35);
                    }
                    if (finished.Count > 0) await Task.WhenAll(finished).WaitAsync(TimeSpan.FromMilliseconds(outputMilliseconds + 10000));
                }
                finally { foreach (var (Shell, Handler) in subscriptions) Shell.StateObservedForCheck -= Handler; }
                double seconds = timer.Elapsed.TotalSeconds; var end = Sample(ui, host);
                var gpuAfter = views.Select(v => v.NativeRenderStatsForCheck).ToArray();
                var presentationAfter = views.Select(v => v.PresentationForCheck).ToArray();
                Profile("end", phase, paneCount, views);
                check(phase == "quiet" || seconds >= (outputMilliseconds - 100) / 1000d, "Output measurements wait for their own flood completion in " + phase + " with " + paneCount + " panes");
                bool resizeStress = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_RESIZE_STRESS") == "1";
                check(samples.Count == (resizeStress ? 0 : phase == "quiet" ? 64 : 32), resizeStress ? "Continuous resize records output and resource churn without invalid fixed-position latency markers" : "Timestamped input and response-frame presentation complete during " + phase + " with " + paneCount + " panes");
                measurements.Add(new
                {
                    phase,
                    paneCount,
                    seconds,
                    resizeStress,
                    requestedOutputMilliseconds = outputMilliseconds,
                    requestedKibPerPane = kibPerPane,
                    busyPanes = phase == "quiet" ? 0 : phase == "mixed-output" ? 1 : paneCount,
                    watchRequests = _host.WatchRequestsForCheck - watchesBefore,
                    watchNotifications = _host.WatchNotificationsForCheck - notificationsBefore,
                    readConnectionsOpened = _host.ReadConnectionsOpenedForCheck - openedBefore,
                    readConnectionsReused = _host.ReadConnectionsReusedForCheck - reusedBefore,
                    bytesRead = shells.Sum(s => s.Performance.BytesRead) - bytesBefore,
                    snapshotBytes = shells.Sum(s => s.FrameBytesForCheck) - snapshotBytesBefore,
                    metadataBytes = shells.Sum(s => s.FrameMetadataBytesForCheck) - metadataBefore,
                    captureRequests = shells.Sum(s => s.CapturesForCheck) - capturesBefore,
                    textPresentations = views.Sum(v => v.TextPresentationsForCheck) - textPresentsBefore,
                    readBatches = shells.Sum(s => s.Performance.ReadBatches) - readBatchesBefore,
                    largestRead = shells.Max(s => s.Performance.LargestRead),
                    nativeSnapshots = shells.Sum(s => s.Performance.Snapshots) - nativeSnapshotsBefore,
                    fullStatuses = shells.Sum(s => s.FullStatusesForCheck) - statusesBefore,
                    reusedStatuses = shells.Sum(s => s.ReusedStatusesForCheck) - statusReuseBefore,
                    frames = presentationAfter.Sum(p => p.SubmittedFrames) - presentationBefore.Sum(p => p.SubmittedFrames),
                    presentation = new
                    {
                        drawAttempts = presentationAfter.Sum(p => p.DrawAttempts) - presentationBefore.Sum(p => p.DrawAttempts),
                        acquireMisses = presentationAfter.Sum(p => p.AcquireMisses) - presentationBefore.Sum(p => p.AcquireMisses),
                        presentMisses = presentationAfter.Sum(p => p.PresentMisses) - presentationBefore.Sum(p => p.PresentMisses),
                        readyWaits = presentationAfter.Sum(p => p.ReadyWaits) - presentationBefore.Sum(p => p.ReadyWaits),
                        readyTimeouts = presentationAfter.Sum(p => p.ReadyTimeouts) - presentationBefore.Sum(p => p.ReadyTimeouts)
                    },
                    rowsPainted = views.Sum(v => v.RowsPainted) - rowsBefore,
                    shapedRuns = views.Sum(v => v.ShapedRuns) - runsBefore,
                    characterSpacingCalls = views.Sum(v => v.CharacterSpacingCalls) - spacingBefore,
                    nativeGpu = new
                    {
                        drawCalls = gpuAfter.Sum(s => (long)s.DrawCalls) - gpuBefore.Sum(s => (long)s.DrawCalls),
                        vertices = gpuAfter.Sum(s => (long)s.Vertices) - gpuBefore.Sum(s => (long)s.Vertices),
                        atlasHits = gpuAfter.Sum(s => (long)s.AtlasHits) - gpuBefore.Sum(s => (long)s.AtlasHits),
                        atlasMisses = gpuAfter.Sum(s => (long)s.AtlasMisses) - gpuBefore.Sum(s => (long)s.AtlasMisses),
                        textureBytes = gpuAfter.Sum(s => (long)s.TextureBytes),
                        vertexBufferBytes = gpuAfter.Sum(s => (long)s.VertexBufferBytes),
                        shapeCacheBytes = gpuAfter.Sum(s => (long)s.ShapeCacheBytes)
                    },
                    atlasBuckets = views.Select(view => view.NativeAtlasBucketsForCheck).ToArray(),
                    qpcStart,
                    qpcEnd = Stopwatch.GetTimestamp(),
                    qpcFrequency = Stopwatch.Frequency,
                    swapchains = views.Select(view => new { view.PaneId, address = view.SwapchainAddressForCheck }).ToArray(),
                    panes = views.Select((view, index) => new
                    {
                        view.PaneId,
                        submittedFrames = presentationAfter[index].SubmittedFrames - presentationBefore[index].SubmittedFrames,
                        nativeFrames = gpuAfter[index].Frames - gpuBefore[index].Frames,
                        view.ActualWidth,
                        view.ActualHeight,
                        view.ContentOpacityForCheck,
                        view.Visibility,
                        view.IsLoaded,
                        position = view.TransformToVisual(Root).TransformPoint(new(0, 0)),
                        presentResults = view.PresentResultsForCheck,
                        colorGlyphs = view.NativeColorGlyphsForCheck,
                        preparedLayouts = view.NativePreparedLayoutsForCheck,
                        resizes = view.ResizeStatsForCheck
                    }).ToArray(),
                    childRead = Distribution(samples.Select(s => s.ChildReadMs)),
                    childWrite = Distribution(samples.Select(s => s.ChildWriteMs)),
                    hostState = Distribution(samples.Where(s => s.HostStateMs.HasValue).Select(s => s.HostStateMs!.Value)),
                    requestToPresent = Distribution(samples.Select(s => s.PresentMs)),
                    uiContinuation = Distribution(samples.Select(s => s.UiContinuationMs)),
                    responseDraw = Distribution(samples.Select(s => s.DrawDurationMs)),
                    responseDrawStart = Distribution(samples.Select(s => s.DrawStartMs)),
                    uiCpuMs = end.UiCpu - start.UiCpu,
                    hostCpuMs = end.HostCpu - start.HostCpu,
                    uiAllocatedBytes = end.Allocated - start.Allocated,
                    uiPrivateBytes = end.UiPrivate,
                    hostPrivateBytes = end.HostPrivate,
                    samples
                });
                SaveReport();
            }
        }
        SaveReport();
        foreach (var workspace in _saved.Workspaces.ToArray()) RemoveWorkspace(workspace);
        void Profile(string stage, string phase, int paneCount, TerminalView[] views)
        {
            if (!profileRendering) return;
            profiles.Add(new { stage, phase, paneCount, renderers = views.Select(v => v.NativeRenderProfileForCheck).ToArray() });
            if (stage == "end") File.WriteAllText(Path.Combine(SessionStore.Folder, "performance-render-profile.json"), JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
        void SaveReport() => File.WriteAllText(Path.Combine(SessionStore.Folder, "performance.json"), JsonSerializer.Serialize(new
        {
            version = BuildInfo.Version,
            benchmarkVersion = 4,
            watchMode = _host!.DisablePaneOutputWatchForCheck ? "legacy" : "pane-interest",
            timestamp = DateTimeOffset.Now,
            textCorpus = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_TEXT_CORPUS") ?? "ascii",
            measurements,
            supportsOutputWatch = _host.SupportsOutputWatch,
            supportsPaneOutputWatch = _host.SupportsPaneOutputWatch,
            adapters = _views.Select(view => view.NativeAdapterNameForCheck).Distinct().ToArray(),
            displayState = _power.DisplayState,
            styleRuns = _host.SupportsStyleRuns && !_host.DisableStyleRunsForCheck,
            quadMode = expandedQuads ? "expanded" : "instanced",
            retryMode = compositionRetry ? "composition" : "readiness",
            textLayoutMode = nativeLayouts ? "native" : "projected",
            asciiShapingMode = asciiShapes ? "harfbuzz" : "directwrite",
            asciiStorage = streamedAscii ? "font-scratch" : "shape-cache",
            reusableReads = _host.SupportsReusableReads && !_host.DisableReusableReadsForCheck,
            cancelRetiredWatch = _host.CancelRetiredWatchForCheck,
            jsonCodec = HostProtocol.LegacyJsonForCheck ? "legacy" : "generated-pooled",
            statusRevisions = _host.SupportsStatusRevisions && !_host.DisableStatusRevisionsForCheck,
            historyCommands = Environment.GetEnvironmentVariable("TOIQTTY_PERFORMANCE_HISTORY_COMMANDS") ?? "0",
            readBufferKiB = ConPtyProcess.OutputBufferBytes / 1024,
            alphaPresentation = "drawing-surface",
            resizeCapacity = reserveResize,
            frameLatency,
            presentInterval,
            bulkMode = compositionBulk ? "composition" : "readiness",
            atlasHashMode = legacyAtlasHash ? "legacy" : "mixed",
            meaning = "Stopwatch/QPC timestamps: input admission to child ReadLine, response emission, host state arrival and successful response-marker submission return. Opaque submission uses DXGI Present(0); translucent submission uses CompositionDrawingSurface.EndDraw. These are different completion boundaries; neither measures scanout. Frames count successful submissions, not draw attempts or display refreshes. UI continuation timing is separate. Controlled output targets 32MiB/s aggregate; actual bytes and duration are recorded."
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
    private async Task<LoadLatency> Probe(RemoteTerminalSession shell, TerminalView view)
    {
        string token = Guid.NewGuid().ToString("N")[..6], prefix = "PONG_" + token + "_";
        long origin = Stopwatch.GetTimestamp(), hostTicks = 0, drawBegin = 0, drawEnd = 0; int draws = 0;
        var presented = new TaskCompletionSource<(long Present, long ReadUs, long WriteUs, long DrawBegin, long DrawEnd, int Draws)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = Environment.GetEnvironmentVariable("TOIQTTY_LATENCY_STAGES") == "1" ? new List<(string Stage, double Ms)>() : null;
        void Stage(string stage, long ticks)
        {
            if (stage == "draw-begin") { drawBegin = ticks; draws++; } else if (stage == "draw-end") drawEnd = ticks;
            if (stages is not null) lock (stages) stages.Add((stage, Stopwatch.GetElapsedTime(origin, ticks).TotalMilliseconds));
        }
        void State(ShellState state, long ticks) { if (state.Title == "PING_" + token) Interlocked.CompareExchange(ref hostTicks, ticks, 0); }
        void Painted()
        {
            long ticks = Stopwatch.GetTimestamp(); string row = view.RenderedRowForCheck(1);
            int start = row.IndexOf(prefix, StringComparison.Ordinal); if (start < 0) return;
            var fields = row[(start + prefix.Length)..].Trim().Split('_');
            if (fields.Length >= 2 && long.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long read)
                && long.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long write)) presented.TrySetResult((ticks, read, write, drawBegin, drawEnd, draws));
        }
        shell.StateObservedForCheck += State; view.FramePaintedForCheck += Painted; view.LatencyStageForCheck += Stage;
        if (stages is not null) shell.LatencyStageForCheck += Stage;
        var statsBefore = stages is not null ? view.NativeRenderStatsForCheck : default;
        var profileBefore = stages is not null ? view.NativeRenderProfileForCheck : default;
        try
        {
            shell.Paste("ping2 " + token + " " + origin); shell.Key(13, 0);
            var (Present, ReadUs, WriteUs, DrawBegin, DrawEnd, Draws) = await WaitForProbePresentation(presented.Task);
            if (stages is not null) lock (stages)
                File.AppendAllText(Path.Combine(SessionStore.Folder, "latency-stages.jsonl"), JsonSerializer.Serialize(new {
                    pane = view.PaneId, presentMs = Stopwatch.GetElapsedTime(origin, Present).TotalMilliseconds, readMs = ReadUs / 1000d, writeMs = WriteUs / 1000d,
                    hostStateMs = hostTicks == 0 ? (double?)null : Stopwatch.GetElapsedTime(origin, hostTicks).TotalMilliseconds,
                    atlasHits = view.NativeRenderStatsForCheck.AtlasHits - statsBefore.AtlasHits, atlasMisses = view.NativeRenderStatsForCheck.AtlasMisses - statsBefore.AtlasMisses,
                    rasterTransfers = view.NativeRenderProfileForCheck.RasterTransfers - profileBefore.RasterTransfers, fenceWaits = view.NativeRenderProfileForCheck.FenceWaits - profileBefore.FenceWaits,
                    stages = stages.Select(s => new { s.Stage, s.Ms }) }) + Environment.NewLine);
            return new(view.PaneId, ReadUs / 1000d, WriteUs / 1000d, hostTicks == 0 ? null : Stopwatch.GetElapsedTime(origin, hostTicks).TotalMilliseconds,
                Stopwatch.GetElapsedTime(origin, Present).TotalMilliseconds, Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                Stopwatch.GetElapsedTime(origin, DrawBegin).TotalMilliseconds, Stopwatch.GetElapsedTime(DrawBegin, DrawEnd).TotalMilliseconds, Draws, origin, DrawBegin, Present);
        }
        catch (TimeoutException)
        {
            var reply = await shell.CaptureDiagnosticsForCheck();
            var (snapshot, cells) = HostSnapshotCodec.Decode(reply.Binary, []);
            File.WriteAllText(Path.Combine(SessionStore.Folder, "performance-timeout.json"), JsonSerializer.Serialize(new
            {
                token,
                view.PaneId,
                hostState = reply.State,
                view.FramesPainted,
                view.IsSuspendedForCheck,
                view.SuspensionForCheck,
                WindowRenderingActive,
                _windowPresentationHidden,
                _powerSuspended,
                nativeVisible = IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(this)),
                nativeMinimized = IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(this)),
                cachedRow = view.RenderedRowForCheck(1),
                hostRows = Enumerable.Range(0, (int)snapshot.Frame.Rows)
                    .Select(row => string.Concat(cells.Skip(row * (int)snapshot.Frame.Columns).Take((int)snapshot.Frame.Columns).Select(cell => cell.GetText()))).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally { shell.StateObservedForCheck -= State; view.FramePaintedForCheck -= Painted; view.LatencyStageForCheck -= Stage; if (stages is not null) shell.LatencyStageForCheck -= Stage; }
    }
    private static object Distribution(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        if (values.Length == 0) return new { samples = 0 };
        return new
        {
            samples = values.Length,
            medianMs = values[values.Length / 2],
            p95Ms = values[(int)((values.Length - 1) * .95)],
            p99Ms = values[(int)((values.Length - 1) * .99)],
            maxMs = values[^1],
            within8_33ms = values.Count(value => value <= 1000d / 120)
        };
    }
    private static PaneNode Balance(PaneNode[] leaves, int depth)
    {
        if (leaves.Length == 1) return leaves[0]; int middle = leaves.Length / 2;
        return new() { Vertical = depth % 2 == 0, First = Balance(leaves[..middle], depth + 1), Second = Balance(leaves[middle..], depth + 1) };
    }
    private static (double UiCpu, double HostCpu, long Allocated, long UiPrivate, long HostPrivate) Sample(Process ui, Process host)
    { ui.Refresh(); host.Refresh(); return (ui.TotalProcessorTime.TotalMilliseconds, host.TotalProcessorTime.TotalMilliseconds, GC.GetTotalAllocatedBytes(), ui.PrivateMemorySize64, host.PrivateMemorySize64); }
}
