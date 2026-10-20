using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Controls;
using Toiqtty.Models;
using Windows.UI;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunDirectXSmoke()
    {
        var checks = new List<string>(); string? error = null;
        try
        {
            if (!Path.GetFileName(SessionStore.Folder).StartsWith("directx-", StringComparison.Ordinal)) throw new InvalidOperationException("Native renderer checks require an isolated directx-* directory.");
            if (Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_OFFSCREEN") == "1") AppWindow.Hide();
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
            if (Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_COLOR_GRID") == "1")
            { await CheckColorGlyphCache(Check); }
            else if (Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_ASCII_DECORATIONS") == "1")
            { await CheckStreamedAsciiDecorations(Check); }
            else if (Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_RESIZE_PRESENTATION") == "1")
            { await CheckResizePresentation(Check); }
            else
            {
                await CheckDirectXRendering(Check);
                await CheckColorGlyphCache(Check);
                await CheckDirectXPrimitives(Check);
                CheckDirectXQuadModes(Check);
                await CheckNativeTerminalLayouts(Check);
                if (DirectXRenderer.AsciiShapesAvailable) { await CheckNativeTerminalLayouts(Check, true); await CheckAsciiShapeFonts(Check); await CheckNativeTerminalLayouts(Check, true, true); await CheckAsciiShapeFonts(Check, true); await CheckStreamedAsciiDecorations(Check); }
                CheckAtlasHashing(Check);
                CheckTerminalTextLifetimes(Check);
                CheckDirectX12Ownership(Check);
                CheckDirectX12FrameBatch(Check);
                CheckDirectX12DescriptorPages(Check);
                CheckDirectX12Lifetimes(Check);
                CheckRetainedPresentationResize(Check);
                CheckResizeCapacity(Check);
                await CheckDirectXReadiness(Check);
                await CheckSurfaceScheduling(Check);
                if (_focused is { } backgroundView) backgroundView.CheckBackgroundLayersForCheck(Check);
                await CheckDirectXConcurrentReadback(Check);
                CheckDirectXLayoutRetirement(Check);
                await CheckDirectXLowFeatureFallback(Check);
                foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f }) Check(await CheckPowerlineSeams(dpi), $"Native antialiased Powerline shapes retain opaque flat joins at {dpi} DPI");
                if (int.TryParse(Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_STRESS_SECONDS"), out int seconds) && seconds > 0) await CheckDirectXStress(seconds, Check);
            }
        }
        catch (Exception ex) { error = ex.ToString(); try { using var diagnostic = new DirectXRenderer(CanvasDevice.GetSharedDevice()); File.WriteAllText(Path.Combine(SessionStore.Folder, "directx-failure-debug.txt"), diagnostic.DebugMessagesForCheck()); } catch (Exception debugError) { error += "\nDebug collection: " + debugError; } }
        File.WriteAllText(Path.Combine(SessionStore.Folder, "directx-smoke.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = error is null, checks, error }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_DEBUG") == "1") await Task.Delay(2000);
        Close();
    }
    private static async Task CheckDirectXRendering(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var gpu = new DirectXRenderer(device);
        var measurements = new List<object>();
        int worstDelta = 0;
        string[] corpus = ["ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz 0123456789", "office ffi => === != <= >= <->", "café 中🙂 é אבג abc 日本語 👩🏽‍💻", "a" + new string('\u0301', 70)];
        foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f })
            foreach (uint style in new uint[] { 0, 1, 2, 3 })
            {
                using var target = new CanvasRenderTarget(device, 1000, 160, dpi);
                using var reference = new CanvasRenderTarget(device, 1000, 160, dpi);
                using var format = new CanvasTextFormat
                {
                    FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, style),
                    FontSize = 14,
                    FontWeight = (style & 1) != 0 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    FontStyle = (style & 2) != 0 ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                    WordWrapping = CanvasWordWrapping.NoWrap,
                    Options = CanvasDrawTextOptions.EnableColorFont
                };
                using var metrics = new TerminalTextRenderer();
                var layouts = new List<CanvasTextLayout>();
                try
                {
                    foreach (string text in corpus)
                    {
                        var layout = new CanvasTextLayout(device, "\u202d" + text + "\u202c", format, 10000, 36);
                        layout.Options = CanvasDrawTextOptions.EnableColorFont;
                        metrics.SetLineMetrics(layout, device, format, 36, style); layouts.Add(layout);
                    }
                    var foreground = Color.FromArgb(255, 220, 230, 240);
                    using (var draw = reference.CreateDrawingSession())
                    {
                        draw.Clear(Color.FromArgb(255, 20, 22, 26));
                        for (int row = 0; row < layouts.Count; row++) draw.DrawTextLayout(layouts[row], 3, row * 36 + 1, foreground);
                    }
                    using (var draw = gpu.CreateDrawingSession(target))
                    {
                        draw.Clear(Color.FromArgb(255, 20, 22, 26));
                        for (int row = 0; row < layouts.Count; row++) draw.DrawTextLayout(layouts[row], 3, row * 36 + 1, foreground);
                    }
                    using var wholeLayout = new CanvasRenderTarget(device, 1000, 160, dpi);
                    gpu.SetTextModeForCheck(true);
                    using (var draw = gpu.CreateDrawingSession(wholeLayout))
                    {
                        draw.Clear(Color.FromArgb(255, 20, 22, 26));
                        for (int row = 0; row < layouts.Count; row++) draw.DrawTextLayout(layouts[row], 3, row * 36 + 1, foreground);
                    }
                    gpu.SetTextModeForCheck(false);
                    var actual = target.GetPixelColors(); var expected = reference.GetPixelColors();
                    int different = 0, maxDelta = 0, large = 0; long total = 0;
                    for (int i = 0; i < actual.Length; i++)
                    {
                        int delta = Math.Max(Math.Abs(actual[i].R - expected[i].R), Math.Max(Math.Abs(actual[i].G - expected[i].G), Math.Abs(actual[i].B - expected[i].B)));
                        if (delta > 0) different++; if (delta > 32) large++; maxDelta = Math.Max(maxDelta, delta); total += delta;
                    }
                    measurements.Add(new { dpi, style, different, maxDelta, large, meanDelta = (double)total / actual.Length, pixels = actual.Length });
                    worstDelta = Math.Max(worstDelta, maxDelta);
                    await target.SaveAsync(Path.Combine(SessionStore.Folder, $"directx-glyphs-{dpi}-{style}.png"));
                    await reference.SaveAsync(Path.Combine(SessionStore.Folder, $"directwrite-reference-{dpi}-{style}.png"));
                    await wholeLayout.SaveAsync(Path.Combine(SessionStore.Folder, $"directx-layout-{dpi}-{style}.png"));
                }
                finally { foreach (var layout in layouts) layout.Dispose(); }
            }
        var stats = gpu.GetStats();
        File.WriteAllText(Path.Combine(SessionStore.Folder, "directx-pixels.json"), System.Text.Json.JsonSerializer.Serialize(new { measurements, stats }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        check(stats.BackendApi == 12 && stats.SubmissionSlots == 3 && stats.Submissions > 0 && stats.DrawCalls > 0 && stats.Vertices > 0 && stats.AtlasHits > 0 && stats.AtlasMisses > 0, "Native D3D12 command lists draw GPU triangles and reuse a populated glyph atlas with three fence-owned submission slots");
        check(stats.AtlasEntries <= 2048 && stats.AtlasBytes <= 2UL * 2048 * 2048 * 4 && stats.ShapeCacheBytes <= 8UL * 1024 * 1024, "Native glyph/color atlas and shaped-run caches obey explicit entry, GPU-memory and CPU-memory bounds");
        check(stats.VertexBufferBytes > 0 && stats.VertexBufferBytes <= 4UL * 1024 * 1024, "Native dynamic vertex streams grow only for actual batches and retain less than four MiB");
        check(worstDelta <= 12, "Native glyph pixels preserve DirectWrite geometry, color emoji and combining overhangs across four font styles and five DPIs; maximum grayscale contrast difference is at most 12/255");
        File.WriteAllText(Path.Combine(SessionStore.Folder, "directx-debug.txt"), gpu.DebugMessagesForCheck());
        if (Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_DEBUG") == "1") check(stats.DebugLayer == 1 && stats.DebugErrors == 0 && stats.DebugWarnings == stats.InteropWarnings, "Direct3D debug layer reports no errors or unexpected warnings during text atlas rendering");
    }

    private static async Task CheckDirectXPrimitives(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        using var target = new CanvasRenderTarget(device, 64, 64, 96);
        using (var draw = gpu.CreateDrawingSession(target))
        {
            draw.Clear(Microsoft.UI.Colors.Black);
            using (draw.CreateLayer(1, new(8, 8, 16, 16)))
            {
                draw.FillRectangle(0, 0, 64, 64, Microsoft.UI.Colors.Red);
                using (draw.CreateLayer(1, new(12, 12, 4, 4))) draw.FillRectangle(0, 0, 64, 64, Microsoft.UI.Colors.Blue);
                draw.FillRectangle(8, 8, 2, 2, Microsoft.UI.Colors.Lime);
            }
            draw.FillRectangle(30, 30, 4, 4, Microsoft.UI.Colors.White);
            draw.Blend = CanvasBlend.Copy; draw.FillRectangle(40, 40, 4, 4, Microsoft.UI.Colors.Transparent); draw.Blend = CanvasBlend.SourceOver;
        }
        var pixels = target.GetPixelColors();
        check(pixels[0] == Microsoft.UI.Colors.Black && pixels[8 * 64 + 8] == Microsoft.UI.Colors.Lime && pixels[12 * 64 + 12] == Microsoft.UI.Colors.Blue && pixels[20 * 64 + 20] == Microsoft.UI.Colors.Red && pixels[30 * 64 + 30] == Microsoft.UI.Colors.White,
            "Native nested scissor clips intersect and restore without leaking into later draws");
        check(pixels[40 * 64 + 40].A == 0, "Native copy blending clears a retained row region to transparent pixels");
        var bgra = new byte[3 * 3 * 4]; for (int i = 0; i < 9; i++) { bgra[i * 4 + 2] = 255; bgra[i * 4 + 3] = 255; }
        bgra[4 * 4 + 1] = 255; bgra[4 * 4 + 2] = 0;
        using var bitmap = gpu.CreateBitmap(bgra, 3, 3);
        using (var shared = new DirectXRenderer(device))
        {
            using (var draw = shared.CreateDrawingSession(target)) { draw.Clear(Microsoft.UI.Colors.Black); draw.DrawImage(bitmap, new(0, 0, 3, 3), new(0, 0, 3, 3), 1, CanvasImageInterpolation.NearestNeighbor); }
            check(target.GetPixelColors()[1 * 64 + 1] == Microsoft.UI.Colors.Lime, "Native pane renderers safely share a wallpaper texture on the same graphics device");
        }
        using var legacyBitmap = CanvasBitmap.CreateFromBytes(device, bgra, 3, 3, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var reference = new CanvasRenderTarget(device, 64, 64, 96);
        using (var draw = reference.CreateDrawingSession()) { draw.Clear(Microsoft.UI.Colors.Black); draw.DrawImage(legacyBitmap, new Windows.Foundation.Rect(0, 0, 32, 32), new Windows.Foundation.Rect(1, 1, 1, 1), 1, CanvasImageInterpolation.Linear); }
        using (var draw = gpu.CreateDrawingSession(target)) { draw.Clear(Microsoft.UI.Colors.Black); draw.DrawImage(bitmap, new(0, 0, 32, 32), new(1, 1, 1, 1), 1, CanvasImageInterpolation.Linear); }
        await target.SaveAsync(Path.Combine(SessionStore.Folder, "directx-crop-edges.png")); await reference.SaveAsync(Path.Combine(SessionStore.Folder, "direct2d-crop-edges.png"));
        var actual = target.GetPixelColors(); var expected = reference.GetPixelColors(); int maxDelta = 0;
        for (int i = 0; i < actual.Length; i++) maxDelta = Math.Max(maxDelta, Math.Max(Math.Abs(actual[i].R - expected[i].R), Math.Max(Math.Abs(actual[i].G - expected[i].G), Math.Abs(actual[i].B - expected[i].B))));
        File.WriteAllText(Path.Combine(SessionStore.Folder, "directx-primitives.json"), System.Text.Json.JsonSerializer.Serialize(new { cropMaxDelta = maxDelta, stats = gpu.GetStats() }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        check(maxDelta <= 2, "Native linear image sampling matches Direct2D at every cropped source edge");
        using var opacityFormat = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, 0), FontSize = 14 };
        using var opacityLayout = new CanvasTextLayout(device, "Faint 中🙂 é 👩🏽‍💻", opacityFormat, 400, 40);
        using var opaque = new CanvasRenderTarget(device, 400, 40, 168); using var faint = new CanvasRenderTarget(device, 400, 40, 168);
        using (var draw = gpu.CreateDrawingSession(opaque)) { draw.Clear(Microsoft.UI.Colors.Transparent); draw.DrawTextLayout(opacityLayout, 2, 2, Color.FromArgb(255, 220, 230, 240)); }
        using (var draw = gpu.CreateDrawingSession(faint)) { draw.Clear(Microsoft.UI.Colors.Transparent); draw.DrawTextLayout(opacityLayout, 2, 2, Color.FromArgb(140, 220, 230, 240)); }
        var opaqueBytes = opaque.GetPixelBytes(); var faintBytes = faint.GetPixelBytes(); int opacityDelta = 0;
        for (int i = 0; i < opaqueBytes.Length; i++) opacityDelta = Math.Max(opacityDelta, Math.Abs(faintBytes[i] - (opaqueBytes[i] * 140 + 127) / 255));
        check(opaqueBytes.Any(value => value > 0) && opacityDelta <= 2, "Faint text applies the same premultiplied opacity to monochrome glyphs and color emoji");
        var stats = gpu.GetStats(); File.AppendAllText(Path.Combine(SessionStore.Folder, "directx-debug.txt"), gpu.DebugMessagesForCheck());
        if (Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_DEBUG") == "1") check(stats.DebugLayer == 1 && stats.DebugErrors == 0 && stats.DebugWarnings == stats.InteropWarnings, "Direct3D debug layer reports no errors or unexpected warnings during clipping, row clearing and cropped-image sampling");
    }
}
