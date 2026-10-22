using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty;

public sealed partial class MainWindow
{
    // Opt-in integration driver for a fresh test data directory. Exercises the
    // real WinUI layout and real ConPTY sessions without desktop input injection.
    private async Task RunUiSmoke()
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        string? error = null;
        try
        {
            await CheckCaptionInsetRecovery(Check);
            UiTasks.Observe(Task.FromException(new InvalidOperationException("ui-fault-probe")));
            UiTasks.Observe(async () => { await Task.Yield(); throw new IOException("ui-fault-probe-async"); });
            for (int i = 0; i < 100 && StatusNotice.Message != "ui-fault-probe-async"; i++) await Task.Delay(20);
            Check(StatusNotice.IsOpen && StatusNotice.Message == "ui-fault-probe-async", "Faulted UI tasks are reported in the window instead of ending the process");
            StatusNotice.IsOpen = false;
            var toast = Notifications.DesktopNotifications.Build("workspace-1", "pane-1", "Workspace", new(DateTimeOffset.Now, "Build <done> & ready", "café 中"));
            var toastXml = System.Xml.Linq.XDocument.Parse(toast.Payload);
            Check(toastXml.Descendants("text").Select(item => item.Value).SequenceEqual(["Build <done> & ready", "café 中"]),
                "native Windows notification builder preserves Unicode and escapes terminal text");
            Check(toastXml.Root?.Attribute("launch")?.Value.Contains("workspace=workspace-1") == true
                && toastXml.Root.Attribute("launch")!.Value.Contains("pane=pane-1"), "Windows notifications carry the workspace and pane activation target");
            Check(!new Notifications.DesktopNotifications(false).Show("w", "p", "Workspace", new(DateTimeOffset.Now, "Disabled", "")),
                "disabled desktop delivery preserves the application's silent smoke-test mode");
            if (Environment.GetEnvironmentVariable("TOIQTTY_NOTIFICATION_SMOKE") == "1")
            {
                Check(_notifications?.Available == true, "native Windows notification registration succeeds: " + _notifications?.UnavailableReason);
                Check(_notifications!.Show("smoke-workspace", "smoke-pane", "Toiqtty self-test", new(DateTimeOffset.Now, "Toiqtty notification test", "Shell integration and Windows notifications are ready.")),
                    "Windows accepts a real native desktop notification");
                await Microsoft.Windows.AppNotifications.AppNotificationManager.Default.RemoveAllAsync();
            }
            var dropPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dropPackage.SetText("drop café 中");
            dropPackage.SetHtmlFormat(Windows.ApplicationModel.DataTransfer.HtmlFormatHelper.CreateHtmlFormat("<b>café 中</b>"));
            dropPackage.SetRtf("{\\rtf1 drop}");
            var dropView = dropPackage.GetView();
            var offer = Controls.TerminalDropData.Offer(dropView);
            var (dropItems, dropFallback) = await Controls.TerminalDropData.ReadAsync(dropView, "pwsh.exe");
            Check(offer.Keys.SequenceEqual(dropItems.Keys) && offer.Count == 3, "WinUI drag MIME offers match the captured text/HTML/RTF representations");
            Check(System.Text.Encoding.UTF8.GetString(dropItems["text/html"]) == "<b>café 中</b>" && dropFallback == "drop café 中",
                "WinUI drop capture preserves Unicode HTML fragments and shell-paste fallback text");
            dropPackage.SetText(new string('x', Terminal.TerminalClipboardLimits.MaxBytes + 1));
            bool oversizedDrop = false;
            try { await Controls.TerminalDropData.ReadAsync(dropPackage.GetView(), "pwsh.exe"); }
            catch (ArgumentException) { oversizedDrop = true; }
            Check(oversizedDrop, "WinUI rejects oversized MIME drops before allocating the protocol byte payload");
            await Task.Delay(700);
            await CheckBatchedSpacing(Check);
            await CheckDirectXRendering(Check);
            foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f })
                Check(await CheckPowerlineSeams(dpi), $"Powerline color joins have no seam at {dpi / 96 * 100:0}% display scaling");
            for (uint style = 0; style < 4; style++)
            {
                using var fonts = new CanvasFontSet(TerminalFont.BundledUri(style));
                Check(fonts.Fonts.Count == 1 && fonts.Fonts[0].FamilyNames.Values.Contains(TerminalFont.BundledFamily)
                    && new uint[] { 0xe0b0, 0xe0b4, 0xe0b6, 0xe70f, 0xf418, 0xf0319 }.All(fonts.Fonts[0].HasCharacter),
                    $"bundled font face {style} loads directly from application files and contains Starship icons");
                using var format = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, style), FontSize = 14 };
                using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "M\ue70f\uf418\U000f0319", format, 1000, 100);
                Check(layout.LayoutBounds.Width > 0, $"bundled font face {style} creates a terminal text layout");
            }
            Check(_views.Count == 1 && _focused is not null, "initial terminal is rendered and focused");
            var initial = _selected!;
            NavigateNotification(initial.Id, initial.Root.Leaves().First().Id);
            await Task.Delay(150);
            Check(_selected == initial && _focused?.PaneId == initial.Root.Leaves().First().Id, "notification navigation restores the target workspace and pane focus");
            initial.Name = "toiqtty";
            Split(true); await Task.Delay(300);
            Split(false); await Task.Delay(300);
            Check(_views.Count == 3 && initial.Root.Leaves().Count() == 3, "nested horizontal and vertical splits render");
            var sessions = initial.Root.Leaves().Select(p => p.Session!).ToList();
            Check(sessions.All(s => s is not null) && sessions.Distinct().Count() == 3, "each split owns an independent shell");
            await Task.WhenAll(sessions.Select(s => s.Ready)).WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var session in sessions) { session.Paste("echo TOIQTTY_UI_SMOKE e\u0301 中🙂"); session.Key(13, 0); }
            await Task.Delay(500);
            Check(_views.All(v => v.RenderedText.Contains("TOIQTTY_UI_SMOKE")), "real shell output reaches each rendered pane");
            await CheckDisplayTransitions(Check);
            var accessible = _focused!.TextPeer.DocumentRange;
            Check(accessible.GetText(-1).Contains("TOIQTTY_UI_SMOKE"), "UI Automation exposes actual terminal output through its text pattern");
            var match = accessible.FindText("TOIQTTY_UI_SMOKE", false, false);
            match.GetBoundingRectangles(out var rectangles);
            Check(match.GetText(-1) == "TOIQTTY_UI_SMOKE" && rectangles.Length >= 4 && rectangles[2] > 0,
                "accessible text ranges support text search and physical screen geometry");
            var cloned = match.Clone();
            Check(cloned.Compare(match), "accessible range cloning preserves document and endpoints");
            await CheckExternalAccessibility(Check);
            var compositionView = _focused!;
            compositionView.SetPreeditForCheck("TOIQTTY_UNCOMMITTED"); await Task.Delay(100);
            Check(!compositionView.RenderedText.Contains("TOIQTTY_UNCOMMITTED"), "composition preedit stays in the input control and never reaches the PTY");
            compositionView.CompletePreeditForCheck(""); await Task.Delay(50);
            Check(!compositionView.RenderedText.Contains("TOIQTTY_UNCOMMITTED"), "canceling composition sends no preedit text");
            compositionView.SetPreeditForCheck("TOIQTTY_STALE_PREEDIT");
            compositionView.CompletePreeditForCheck("", endBeforeTextChange: true); await Task.Delay(100);
            Check(!compositionView.RenderedText.Contains("TOIQTTY_STALE_PREEDIT"), "composition cancellation waits for WinUI's final text update instead of leaking stale preedit");
            compositionView.SetPreeditForCheck("echo TOIQTTY_COMPOSE_");
            compositionView.CompletePreeditForCheck("echo TOIQTTY_COMPOSE_日本");
            await Task.Delay(50);
            initial.Root.Leaves().Single(p => p.Id == compositionView.PaneId).Session!.Key(13, 0);
            await Task.Delay(300);
            Check(compositionView.RenderedText.Contains("TOIQTTY_COMPOSE_日本"), "committed Unicode composition passes through the real shell and renderer");
            Check(CheckTextShaping(), "DirectWrite shapes JetBrains Mono ligatures while preserving terminal cell advances");
            Check(await CheckCellOrder(), "shaping preserves Ghostty cell order for mixed Hebrew and Latin text");
            NewWorkspace("cmd.exe /Q /K", "scratch"); await Task.Delay(300);
            Check(_views.Count == 1 && _selected != initial, "new workspace renders its own terminal");
            SelectWorkspace(initial); await Task.Delay(300);
            Check(_views.Count == 3 && initial.Root.Leaves().Select(p => p.Session).SequenceEqual(sessions), "workspace switch preserves running shells");
            ZoomClick(this, new()); await Task.Delay(300);
            Check(_views.Count == 1, "pane zoom renders only the focused pane");
            ZoomClick(this, new()); await Task.Delay(300);
            Check(_views.Count == 3, "unzoom restores split layout");
            _saved.Settings.FontSize = 16;
            foreach (var view in _views) view.ApplySettings();
            await Task.Delay(300);
            Check(_views.Count == 3 && _focused is not null, "font change resizes live terminal panes");
            var closedId = _focused!.PaneId;
            ClosePane(closedId); await Task.Delay(300);
            Check(_views.Count == 2 && initial.Root.Leaves().All(p => p.Id != closedId), "closing a pane collapses the split");
            Split(false); await Task.Delay(300);
            ToggleSidebar(); await Task.Delay(200); ToggleSidebar();
            Save();
            Check(File.Exists(Models.SessionStore.FilePath), "layout is saved to the isolated session directory");
            var state = Models.SessionStore.Load();
            Check(state.Workspaces.Count == 2 && state.Workspaces[0].Root.Leaves().Count() == 3, "saved workspace layout reloads");
            if (FindOnPath("pwsh.exe") is { } graphicsPwsh)
            {
                var script = Path.Combine(SessionStore.Folder, "graphics-preview.ps1");
                var fileImage = Path.Combine(SessionStore.Folder, "kitty-file.rgb");
                File.WriteAllBytes(fileImage, [0, 255, 0]);
                File.WriteAllText(script, """
                    $e = [char]27
                    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                    [Console]::Write("${e}[2J${e}[H${e}_Ga=T,i=101,f=100,c=10,r=4,C=1,q=2;iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==${e}\")
                    [Console]::Write("${e}[5;1H${e}[?2027h${e}_Ga=T,i=102,f=24,s=1,v=1,U=1,c=2,r=1,C=1,q=2;AP8A${e}\${e}[38;2;0;0;102m" + [char]::ConvertFromUtf32(0x10eeee) + [char]0x0305 + [char]0x0305 + [char]::ConvertFromUtf32(0x10eeee) + [char]0x0305 + [char]0x030d + "${e}[0m")
                    [Console]::Write("${e}[1;12H${e}_Ga=T,i=103,f=24,s=1,v=1,c=6,r=3,C=1,q=2;/wAA${e}\${e}_Ga=f,i=103,f=24,s=1,v=1,z=500,q=2;AAD/${e}\${e}_Ga=a,i=103,r=1,z=500,s=3,v=2;${e}\")
                    [Console]::Write("${e}[10;2H${e}_Ga=T,i=104,f=24,s=1,v=1,c=4,r=2,z=-1,C=1,q=2;AAD/${e}\${e}_Ga=T,i=105,f=32,s=1,v=1,c=4,r=2,z=1,C=1,q=2;/wAAgA==${e}\")
                    [Console]::Write("${e}[10;8H${e}_Ga=T,i=106,f=24,s=2,v=1,x=1,w=1,c=4,r=2,C=1,q=2;/wAAAP8A${e}\")
                    $file = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Join-Path $PSScriptRoot 'kitty-file.rgb')))
                    [Console]::Write("${e}[10;14H${e}_Ga=T,t=f,i=107,f=24,s=1,v=1,c=4,r=2,C=1,q=2;$file${e}\")
                    $temp = Join-Path ([IO.Path]::GetTempPath()) ('tty-graphics-protocol-' + (Split-Path $PSScriptRoot -Leaf) + '.rgba')
                    [IO.File]::WriteAllBytes($temp, [byte[]](0, 0, 255, 255))
                    $path = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($temp))
                    [Console]::Write("${e}[10;20H${e}_Ga=T,t=t,i=108,f=32,s=1,v=1,c=4,r=2,C=1,q=2;$path${e}\")
                    """);
                NewWorkspace($"\"{graphicsPwsh}\" -NoLogo -NoProfile -NoExit -File \"{script}\"", "Graphics");
                var imageView = _views.Single();
                // Shells start asynchronously; the output deadline starts once this one runs.
                await FocusedShellStarted();
                // A real pwsh child under load can take several seconds to start.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!imageView.HasImage(101) && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(imageView.HasImage(101), "Kitty PNG escapes emitted by a real PowerShell child survive ConPTY");
                Check(await imageView.CheckRenderedImage(101, Windows.UI.Color.FromArgb(255, 255, 0, 0), Path.Combine(SessionStore.Folder, "kitty-render.png")),
                    "actual terminal renderer draws decoded Kitty PNG pixels at Ghostty's placement");
                Check(imageView.HasImage(102) && await imageView.CheckRenderedImage(102, Windows.UI.Color.FromArgb(255, 0, 255, 0), Path.Combine(SessionStore.Folder, "kitty-placeholder-render.png")),
                    "real ConPTY Unicode-placeholder image renders its fragment without a visible placeholder glyph");
                while (!imageView.ImageIsBlue(103) && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(imageView.ImageIsBlue(103) && await imageView.CheckRenderedImage(103, Windows.UI.Color.FromArgb(255, 0, 0, 255), Path.Combine(SessionStore.Folder, "kitty-animation-render.png")),
                    "Kitty animation advances and repaints actual image pixels without further shell output");
                Check(await imageView.CheckRenderedImage(105, Windows.UI.Color.FromArgb(255, 128, 0, 127), Path.Combine(SessionStore.Folder, "kitty-alpha-render.png")),
                    "overlapping Kitty RGBA layers produce correct premultiplied transparency pixels");
                Check(await imageView.CheckRenderedImage(106, Windows.UI.Color.FromArgb(255, 0, 255, 0), Path.Combine(SessionStore.Folder, "kitty-crop-render.png")),
                    "actual graphics renderer samples the requested crop without adjacent source colors");
                Check(await imageView.CheckRenderedImage(107, Windows.UI.Color.FromArgb(255, 0, 255, 0), Path.Combine(SessionStore.Folder, "kitty-file-render.png")) && File.Exists(fileImage),
                    "real WinUI renderer draws Kitty file-medium pixels and preserves the source file");
                Check(await imageView.CheckRenderedImage(108, Windows.UI.Color.FromArgb(255, 0, 0, 255), Path.Combine(SessionStore.Folder, "kitty-temp-render.png")) &&
                    !File.Exists(Path.Combine(Path.GetTempPath(), "tty-graphics-protocol-" + Path.GetFileName(SessionStore.Folder) + ".rgba")),
                    "real WinUI renderer retains Kitty temporary-file pixels after Ghostty consumes the file");
                imageView.RecreateRenderResources(); await Task.Delay(100);
                Check(await imageView.CheckRenderedImage(105, Windows.UI.Color.FromArgb(255, 128, 0, 127), Path.Combine(SessionStore.Folder, "kitty-recreate-render.png")),
                    "recreating GPU resources rebuilds text layers and image textures from retained terminal state");
                int textures = imageView.ImageTextureCount;
                Check(textures == 8, "graphics textures are deduplicated and recreated only for visible image generations");
                int generation = imageView.ResourceGenerations;
                imageView.ReplaceRenderDeviceForCheck(true);
                deadline = DateTime.UtcNow.AddSeconds(8);
                while (imageView.ResourceGenerations == generation && DateTime.UtcNow < deadline) await Task.Delay(50);
                Check(imageView.ResourceGenerations > generation && await imageView.CheckRenderedImage(105, Windows.UI.Color.FromArgb(255, 128, 0, 127), Path.Combine(SessionStore.Folder, "kitty-device-loss.png")),
                    "switching Win2D to a software device recreates resources and retained image pixels");
                generation = imageView.ResourceGenerations;
                imageView.ReplaceRenderDeviceForCheck(false);
                while (imageView.ResourceGenerations == generation && DateTime.UtcNow < deadline) await Task.Delay(50);
                Check(imageView.ResourceGenerations > generation && await imageView.CheckRenderedImage(105, Windows.UI.Color.FromArgb(255, 128, 0, 127), Path.Combine(SessionStore.Folder, "kitty-device-restore.png")),
                    "returning from software rendering recreates resources on the normal CanvasDevice");
                foreach (float scale in new[] { .75f, 1.25f, 1.5f, 2f, 1f })
                {
                    generation = imageView.ResourceGenerations;
                    imageView.SetRenderDpiScale(scale); await Task.Delay(150);
                    Check(imageView.ResourceGenerations > generation && await imageView.CheckRenderedImage(105, Windows.UI.Color.FromArgb(255, 128, 0, 127), Path.Combine(SessionStore.Folder, $"kitty-dpi-{scale}.png")),
                        $"live Win2D DPI transition at scale {scale} preserves retained graphics");
                }
                int resumes = _resumeCount;
                _power.SendTestNotification(false); _power.SendTestNotification(true); await Task.Delay(150);
                Check(_resumeCount == resumes + 1 && await imageView.CheckRenderedImage(105, Windows.UI.Color.FromArgb(255, 128, 0, 127), Path.Combine(SessionStore.Folder, "kitty-resume.png")),
                    "window suspend/resume notifications rebuild retained graphics without losing the shell");
                if (int.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_UI_SOAK_SECONDS"), out int soak) && soak > 0)
                    await CheckRendererSoak(imageView, _selected!.Root.Leaves().Single().Session!, Math.Clamp(soak, 30, 86400), Check);
            }
            if (FindOnPath("pwsh.exe") is { } pwsh && FindOnPath("starship.exe") is { } starship)
            {
                var config = Path.Combine(SessionStore.Folder, "starship.toml");
                var script = Path.Combine(SessionStore.Folder, "starship-preview.ps1");
                File.WriteAllText(config, "format = '[](red)[ TOIQTTY_STARSHIP ](bg:red fg:white)[](fg:red bg:blue)[  main ](bg:blue fg:white)[](blue)$line_break$character'\n");
                string Quote(string value) => "'" + value.Replace("'", "''") + "'";
                File.WriteAllText(script, "$env:STARSHIP_CONFIG = " + Quote(config)
                    + "\nInvoke-Expression (& " + Quote(starship) + " init powershell)\n. "
                    + Quote(Path.Combine(AppContext.BaseDirectory, "Integration", "powershell.ps1")) + "\n");
                NewWorkspace($"\"{pwsh}\" -NoLogo -NoProfile -NoExit -Command \". {Quote(script)}\"", "Starship");
                var starshipView = _views.Single();
                await FocusedShellStarted();
                // A real pwsh child under load can take several seconds to start.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!starshipView.RenderedText.Contains("TOIQTTY_STARSHIP") && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(starshipView.RenderedText.Contains("\ue70f TOIQTTY_STARSHIP") && starshipView.RenderedText.Contains("\uf418 main"),
                    "real PowerShell 7 and Starship render a Powerline prompt with Nerd Font icons");
                Check(starshipView.HasColoredCells, "Starship Powerline segment backgrounds reach the renderer");
                Check(await starshipView.CheckRenderedColorJoins(Path.Combine(SessionStore.Folder, "starship-render.png")),
                    "actual terminal renderer has no background or Powerline seams in the Starship prompt");
                await Task.Delay(150);
                long rowsPainted = starshipView.RowsPainted; int runs = starshipView.ShapedRuns;
                Check(await starshipView.CheckRenderedColorJoins(Path.Combine(SessionStore.Folder, "starship-cached-render.png"))
                    && starshipView.RowsPainted == rowsPainted && starshipView.ShapedRuns == runs,
                    "an unchanged terminal reuses painted rows and shaped text instead of rebuilding the grid");
            }
            Title = "toiqtty"; RefreshSidebar();
            StatusText.Text = $"UI integration: {checks.Count} checks passed";
        }
        catch (Exception ex) { error = ex.ToString(); StatusText.Text = "UI smoke failed: " + ex.Message; }
        Directory.CreateDirectory(Models.SessionStore.Folder);
        File.WriteAllText(Path.Combine(Models.SessionStore.Folder, "ui-smoke.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        // Leave a bounded window for visual inspection, then release all test shells.
        await Task.Delay(15000);
        Close();
    }
    // A new pane's shell starts asynchronously; output deadlines begin once it runs.
    private Task FocusedShellStarted()
        => (_selected?.Root.Leaves().FirstOrDefault(p => p.Id == _selected.FocusedPaneId)?.Session?.Ready ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));
    private static async Task CheckExternalAccessibility(Action<bool, string> check)
    {
        string executable = Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE") ?? Path.Combine(Environment.CurrentDirectory, "tests/Toiqtty.Smoke/bin/Release/net10.0-windows/win-x64/Toiqtty.Smoke.exe");
        string report = Path.Combine(SessionStore.Folder, "uia-client.json");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--uia-window"); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(report);
        using var process = System.Diagnostics.Process.Start(start)!;
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
        catch (TimeoutException) { process.Kill(); throw new InvalidOperationException("External accessibility client timed out."); }
        using var result = JsonDocument.Parse(File.ReadAllText(report));
        check(process.ExitCode == 0 && result.RootElement.GetProperty("passed").GetBoolean(), "external UIA client: " + (result.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "terminal text, ranges, attributes and selection pass across processes"));
    }

    private async Task CheckDisplayTransitions(Action<bool, string> check)
    {
        var position = AppWindow.Position;
        var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
        var observations = new List<object>();
        try
        {
            // WinUI's returned IVectorView does not expose IIterable on some
            // SDK versions; access the documented vector by index.
            for (int index = 0; index < displays.Count; index++)
            {
                var display = displays[index];
                var area = display.WorkArea;
                AppWindow.Move(new Windows.Graphics.PointInt32(area.X + 20, area.Y + 20));
                await Task.Delay(300);
                double scale = Root.XamlRoot.RasterizationScale;
                check(_views.All(view=>view.PointerGeometryIsAligned), $"Mouse hit testing matches the rendered grid at {scale*100:0}% scaling");
                observations.Add(new { area.X, area.Y, area.Width, area.Height, scale });
                check(_views.All(view => view.PhysicalCellsAreIntegral && view.RenderedText.Contains("TOIQTTY_UI_SMOKE")),
                    $"display at {area.X},{area.Y} with scale {scale} retains output and integral physical cell metrics");
            }
        }
        finally { AppWindow.Move(position); }
        File.WriteAllText(Path.Combine(SessionStore.Folder, "display-matrix.json"), JsonSerializer.Serialize(new { displayCount = displays.Count, mixedMonitorTestAvailable = displays.Count > 1, observations }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task CheckRendererSoak(Controls.TerminalView view, ITerminalSession session, int seconds, Action<bool, string> check)
    {
        string script = Path.Combine(SessionStore.Folder, "graphics-soak.ps1");
        File.WriteAllText(script, """
            $e = [char]27
            $pixels = [byte[]]::new(128 * 128 * 3)
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $i = 0
            [Console]::Write("${e}_Ga=d,d=A;${e}\")
            foreach ($id in 101..106) { [Console]::Write("${e}_Ga=d,d=I,i=$id;${e}\") }
            while ($clock.Elapsed.TotalSeconds -lt __SECONDS__) {
                $id = 201 + ($i % 16)
                [Array]::Fill[byte]($pixels, [byte]($i % 256))
                $data = [Convert]::ToBase64String($pixels)
                [Console]::Write("${e}[HUI_SOAK_$i 中🙂")
                for ($offset = 0; $offset -lt $data.Length; $offset += 4096) {
                    $count = [Math]::Min(4096, $data.Length - $offset)
                    $more = [int]($offset + $count -lt $data.Length)
                    $metadata = if ($offset -eq 0) { "a=T,i=$id,f=24,s=128,v=128,c=4,r=2,C=1,q=2,m=$more" } else { "m=$more,q=2" }
                    [Console]::Write("${e}_G$metadata;" + $data.Substring($offset, $count) + "${e}\")
                }
                $i++; Start-Sleep -Milliseconds 75
            }
            [Console]::Write("${e}[8;1HUI_SOAK_COMPLETE")
            """.Replace("__SECONDS__", seconds.ToString()));
        session.Paste("& '" + script.Replace("'", "''") + "'"); session.Key(13, 0);
        var samples = new List<object>(); var memory = new List<long>();
        long frames = view.FramesPainted;
        int maxTextures = 0, maxLayouts = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool offscreen = Environment.GetEnvironmentVariable("TOIQTTY_UI_SOAK_OFFSCREEN") == "1";
        using var target = offscreen ? view.CreateRenderTargetForCheck() : null;
        long nextSample = 0, nextReport = 0;
        while (timer.Elapsed.TotalSeconds < seconds + 2)
        {
            await Task.Delay(offscreen ? 100 : 1000);
            if (target is not null) view.RenderFrameForCheck(target);
            maxTextures = Math.Max(maxTextures, view.ImageTextureCount);
            maxLayouts = Math.Max(maxLayouts, view.CachedTextLayouts);
            using var process = System.Diagnostics.Process.GetCurrentProcess(); process.Refresh();
            if (timer.ElapsedMilliseconds >= nextSample)
            {
                if (timer.Elapsed.TotalSeconds >= 10) memory.Add(process.PrivateMemorySize64);
                samples.Add(new { seconds = timer.Elapsed.TotalSeconds, privateBytes = process.PrivateMemorySize64, textures = view.ImageTextureCount, layouts = view.CachedTextLayouts, frames = view.FramesPainted });
                nextSample += 1000;
            }
            if (timer.ElapsedMilliseconds >= nextReport)
            {
                File.WriteAllText(Path.Combine(SessionStore.Folder, "renderer-soak.json"), JsonSerializer.Serialize(new {
                    passed = false, status = "running", elapsedSeconds = timer.Elapsed.TotalSeconds, requestedSeconds = seconds,
                    renderMode = offscreen ? "offscreen GPU" : "onscreen", maxTextures, maxLayouts, samples }));
                nextReport += 30000;
            }
            if (view.ImageTextureCount > 16 || view.CachedTextLayouts > 2048) throw new InvalidOperationException($"GPU/text caches exceeded the live-image or layout limit: {view.ImageTextureCount} textures, {view.CachedTextLayouts} layouts.");
        }
        double growth = memory.Count >= 6 ? memory.TakeLast(3).Average() - memory.Take(3).Average() : 0;
        check(view.RenderedText.Contains("UI_SOAK_COMPLETE") && view.FramesPainted - frames > seconds * 3,
            "renderer soak drains real chunked graphics and Unicode while continuously rendering frames");
        check(maxTextures == 16 && maxLayouts <= 2048 && growth < 128 * 1048576,
            "renderer soak bounds GPU textures, shaped layouts and private-memory growth after warmup");
        File.WriteAllText(Path.Combine(SessionStore.Folder, "renderer-soak.json"), JsonSerializer.Serialize(new { passed = true, durationSeconds = timer.Elapsed.TotalSeconds,
            renderMode = offscreen ? "offscreen GPU" : "onscreen", maxTextures, maxLayouts, privateMemoryGrowthBytes = growth, frames = view.FramesPainted - frames, samples }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<bool> CheckPowerlineSeams(float dpi)
    {
        using var gpu=new Controls.DirectXRenderer(CanvasDevice.GetSharedDevice());
        var red = Windows.UI.Color.FromArgb(255, 220, 60, 80);
        var blue = Windows.UI.Color.FromArgb(255, 40, 90, 180);
        float scale = dpi / 96;
        foreach (float size in new[] { 14f, 16f })
        foreach (char symbol in new[] { '\ue0b0', '\ue0b2', '\ue0b4', '\ue0b6' })
        {
            float width = MathF.Ceiling(size * .6f * scale) / scale;
            float height = MathF.Ceiling(size * 1.5f * scale) / scale;
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 8 * width, height, dpi);
            using var separator = new Controls.PowerlineRenderer(target, width, height);
            bool left = symbol is '\ue0b2' or '\ue0b6';
            using (var drawing = gpu.CreateDrawingSession(target))
            {
                drawing.Clear(blue);
                drawing.Antialiasing = CanvasAntialiasing.Aliased;
                for (int cell = left ? 4 : 0; cell < (left ? 8 : 3); cell++)
                    drawing.FillRectangle(cell * width, 0, width, height, red);
                drawing.Antialiasing = CanvasAntialiasing.Antialiased;
                separator.TryDraw(drawing, symbol.ToString(), 3 * width, 0, red);
            }
            var pixels = target.GetPixelColors();
            int stride = (int)target.SizeInPixels.Width;
            int boundary = (int)MathF.Round((left ? 4 : 3) * width * scale);
            // Both sides of the flat join must remain fully opaque red. The top
            // and bottom corners belong to the intentionally antialiased curve.
            for (int y = 2; y < (int)target.SizeInPixels.Height - 2; y++)
                if (pixels[y * stride + boundary - 1] != red || pixels[y * stride + boundary] != red)
                {
                    await target.SaveAsync(Path.Combine(SessionStore.Folder, $"powerline-failure-{dpi}-{(int)symbol}.png"));
                    return false;
                }
        }
        return true;
    }
    private static async Task<bool> CheckCellOrder()
    {
        using var gpu=new Controls.DirectXRenderer(CanvasDevice.GetSharedDevice());
        using var terminal = new GhosttyTerminal(30, 1);
        var bytes = System.Text.Encoding.UTF8.GetBytes("אבג abc דהו"); terminal.Write(bytes, bytes.Length);
        var cells = new TerminalCell[30]; terminal.ReadFrame(cells);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 300, 24, 96);
        using var isolated = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 300, 24, 96);
        using var renderer = new Controls.TerminalTextRenderer();
        using var reference = new Controls.TerminalTextRenderer();
        using var format = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, 0), FontSize = 14, WordWrapping = CanvasWordWrapping.NoWrap };
        using (var drawing = gpu.CreateDrawingSession(target))
        {
            drawing.Clear(Microsoft.UI.Colors.Black);
            renderer.DrawRow(target, drawing, cells, 0, 30, 10, 24, _ => format, false, true, null, (_, _, _, _, _, _) => { });
        }
        // Selection is not a shaping feature; alternating it makes one-cell runs
        // without changing their glyphs, giving an independent visual reference.
        for (int i = 0; i < cells.Length; i++) if ((i & 1) != 0) cells[i].Flags |= 64;
        using (var drawing = gpu.CreateDrawingSession(isolated))
        {
            drawing.Clear(Microsoft.UI.Colors.Black);
            reference.DrawRow(isolated, drawing, cells, 0, 30, 10, 24, _ => format, false, true, null, (_, _, _, _, _, _) => { });
        }
        await target.SaveAsync(Path.Combine(SessionStore.Folder, "bidi-shaped.png"));
        await isolated.SaveAsync(Path.Combine(SessionStore.Folder, "bidi-isolated.png"));
        File.WriteAllText(Path.Combine(SessionStore.Folder, "bidi-error.txt"), renderer.MaxAdvanceError.ToString());
        return renderer.MaxAdvanceError < .1f && target.GetPixelBytes().SequenceEqual(isolated.GetPixelBytes());
    }
    private static bool CheckTextShaping()
    {
        using var gpu=new Controls.DirectXRenderer(CanvasDevice.GetSharedDevice());
        using var terminal = new GhosttyTerminal(30, 1);
        var bytes = System.Text.Encoding.UTF8.GetBytes("-> == != >= =>"); terminal.Write(bytes, bytes.Length);
        var cells = new TerminalCell[30]; terminal.ReadFrame(cells);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 300, 24, 96);
        using var plain = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 300, 24, 96);
        using var renderer = new Controls.TerminalTextRenderer();
        using var disabled = new Controls.TerminalTextRenderer();
        using var format = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, 0), FontSize = 14, WordWrapping = CanvasWordWrapping.NoWrap };
        using (var drawing = gpu.CreateDrawingSession(target))
        {
            drawing.Clear(Microsoft.UI.Colors.Black);
            renderer.DrawRow(target, drawing, cells, 0, 30, 10, 24, _ => format, true, true, null, (_, _, _, _, _, _) => { });
        }
        using (var drawing = gpu.CreateDrawingSession(plain))
        {
            drawing.Clear(Microsoft.UI.Colors.Black);
            disabled.DrawRow(plain, drawing, cells, 0, 30, 10, 24, _ => format, false, true, null, (_, _, _, _, _, _) => { });
        }
        return renderer.ShapedRuns > 0 && renderer.MaxAdvanceError < .1f && !target.GetPixelBytes().SequenceEqual(plain.GetPixelBytes());
    }
}
