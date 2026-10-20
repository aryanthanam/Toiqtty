using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Controls;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static async Task CheckAsciiShapeFonts(Action<bool, string> check, bool streamed = false)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        const int columns = 96, rows = 12;
        var random = new Random(20261020); var cells = new TerminalCell[columns * rows];
        for (int i = 0; i < cells.Length; i++) cells[i] = new() { Text = TerminalCell.Ascii((byte)random.Next(32, 127)), Length = 1, Width = 1, Foreground = 0xffcfd8e8 };
        string patterns = "office ffi fl => === != <= >= <-> -> <-- || && www WMWMWM @#$%_^!? /\\[]{}()";
        for (int i = 0; i < patterns.Length; i++) cells[i].Text = patterns[i].ToString();
        foreach (string family in new[] { TerminalFont.BundledFamily, "Consolas", "Cascadia Code", "Segoe UI", "Arial" })
            foreach (string locale in new[] { "en-US", "tr-TR", "ja-JP", "ar-SA" })
                foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f })
                    foreach (uint style in new[] { 0u, 1u })
                        foreach (bool ligatures in new[] { false, true })
                        {
                            using var fast = new TerminalTextRenderer { UseNativeLayouts = true, UseAsciiShapes = true, UseStreamedAscii = streamed };
                            using var reference = new TerminalTextRenderer { UseProjectedClusterMetricsForCheck = true };
                            using var font = new CanvasTextFormat
                            {
                                FontFamily = TerminalFont.Resolve(family, style),
                                FontSize = 14,
                                LocaleName = locale,
                                FontWeight = style == 1 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                                WordWrapping = CanvasWordWrapping.NoWrap
                            };
                            for (int i = 0; i < cells.Length; i++) cells[i].Flags = style;
                            using var actual = new CanvasRenderTarget(device, columns * 10, rows * 24, dpi);
                            using var expected = new CanvasRenderTarget(device, columns * 10, rows * 24, dpi);
                            Draw(fast, actual); Draw(reference, expected);
                            bool equal = actual.GetPixelBytes().SequenceEqual(expected.GetPixelBytes());
                            if (!equal)
                            {
                                string label = $"{family.Replace(' ', '_')}-{locale}-{dpi}-{style}-{ligatures}";
                                await actual.SaveAsync(Path.Combine(Toiqtty.Models.SessionStore.Folder, "ascii-actual-" + label + ".png"));
                                await expected.SaveAsync(Path.Combine(Toiqtty.Models.SessionStore.Folder, "ascii-expected-" + label + ".png"));
                            }
                            check(equal, $"ASCII shaping preserves random punctuation and ligature pixels: {family}, {locale}, {dpi} DPI, style {style}, ligatures {ligatures}, streamed {streamed}");
                            check(fast.AsciiShapesCreatedForCheck > 0, "The ASCII font comparison exercises the shaping prototype");
                            if (family == "Segoe UI" && locale == "tr-TR" && dpi == 96 && style == 0 && !ligatures)
                            {
                                int shaped = fast.AsciiShapesCreatedForCheck;
                                fast.ConfigureFeatures(new() { { "kern", 1 } }); reference.ConfigureFeatures(new() { { "kern", 1 } });
                                Draw(fast, actual); Draw(reference, expected);
                                check(actual.GetPixelBytes().SequenceEqual(expected.GetPixelBytes()) && fast.AsciiShapesCreatedForCheck == shaped,
                                    "Explicit kerning uses the DirectWrite reference path and preserves its exact grid-spacing behavior");
                            }
                            void Draw(TerminalTextRenderer text, CanvasRenderTarget target)
                            {
                                using var drawing = gpu.CreateDrawingSession(target); drawing.Clear(Microsoft.UI.Colors.Transparent);
                                for (int row = 0; row < rows; row++) text.DrawRow(target, drawing, cells, row, columns, 10, 24, _ => font, ligatures, true, null, (_, _, _, _, _, _) => { });
                            }
                        }
        if (!streamed) CheckAsciiShapeLifetimes(check);
    }
    private static void CheckAsciiShapeLifetimes(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        using var font = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, 0), FontSize = 14, WordWrapping = CanvasWordWrapping.NoWrap };
        using var target = new CanvasRenderTarget(device, 500, 32, 96);
        using var renderer = new TerminalTextRenderer { UseNativeLayouts = true, UseAsciiShapes = true };
        var cells = new TerminalCell[4000];
        for (int i = 0; i < cells.Length; i++) cells[i] = new() { Foreground = 0xffffffff, Width = 1, Length = 1, Text = "W" };
        for (int sequence = 0; sequence < 80; sequence++)
        {
            string key = sequence.ToString("D4"); for (int i = 0; i < key.Length; i++) cells[i].Text = key[i].ToString();
            using var drawing = gpu.CreateDrawingSession(target); drawing.Clear(Microsoft.UI.Colors.Transparent);
            renderer.DrawRow(target, drawing, cells, 0, cells.Length, 10, 24, _ => font, true, true, null, (_, _, _, _, _, _) => { });
        }
        check(renderer.AsciiShapesCreatedForCheck == 80 && renderer.AsciiShapeBytesForCheck <= 2 * 1024 * 1024 && renderer.CachedLayouts < 80,
            "ASCII shape eviction obeys the two MiB byte budget for long changing rows");
        check(gpu.GetStats().ShapeCacheBytes == (ulong)renderer.AsciiShapeBytesForCheck && gpu.GetLifetimeStats().ShapeArenaBytes == gpu.GetStats().ShapeCacheBytes,
            "Native cache and lifetime statistics include the actual owned ASCII shape arenas");
        renderer.Reset();
        check(renderer.AsciiShapeBytesForCheck == 0 && gpu.GetStats().ShapeCacheBytes == 0, "Reset releases ASCII shape arenas and font configuration owners together");
        // CPU shape storage owns its font reference, independent of font and GPU
        // lifetimes. Destruction after both owners have ended must stay valid.
        using var probe = new CanvasTextLayout(device, "M", font, 1000, 24);
        renderer.SetLineMetrics(probe, device, font, 24, 0);
        var format = gpu.CreateTerminalFormat(probe); var shape = gpu.TryCreateAsciiShape(format, "\u202dIndependent CPU owner\u202c", [], 10);
        check(shape is { NativeBytes: > 0 }, "A standalone ASCII shape reports its owned CPU storage");
        format.Dispose(); gpu.Dispose(); shape?.Dispose(); shape?.Dispose();
        check(shape is { IsClosed: true }, "ASCII shape release is idempotent after its font configuration and GPU renderer are destroyed");
    }
    private static async Task CheckStreamedAsciiDecorations(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        const int columns = 32, rows = 4; const float cellWidth = 9.25f, cellHeight = 23.5f;
        var cells = new TerminalCell[columns * rows]; const string pattern = "office ffi => != abcXYZ";
        var fonts = Enumerable.Range(0, 4).Select(style => new CanvasTextFormat
        {
            FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, (uint)style),
            FontSize = 14,
            WordWrapping = CanvasWordWrapping.NoWrap,
            FontWeight = (style & 1) != 0 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = (style & 2) != 0 ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal
        }).ToArray();
        try
        {
            foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f }) foreach (bool mixedUnicode in new[] { false, true })
            {
                for (int index = 0; index < cells.Length; index++)
                {
                    int row = index / columns, column = index % columns;
                    uint flags = row switch { 0 => 8u, 1 => 128u | 16u, 2 => 64u | 1u, _ => column % 3 == 0 ? 32u : column % 3 == 1 ? 256u : 2u };
                    cells[index] = new()
                    {
                        Text = pattern[column % pattern.Length].ToString(),
                        Length = 1,
                        Width = 1,
                        Flags = flags,
                        Foreground = column < 16 ? 0xffe0e8f0 : 0xff60d090,
                        UnderlineStyle = (uint)(column % 6),
                        UnderlineColor = 0xffa070e0
                    };
                }
                if (mixedUnicode)
                {
                    cells[12].Text = "é"; cells[12].Length = 2;
                    cells[13].Text = "é"; cells[13].Length = 3;
                    cells[columns + 12].Text = "🙂"; cells[columns + 12].Length = 4; cells[columns + 12].Width = 2;
                    cells[columns + 13].Text = ""; cells[columns + 13].Length = 0; cells[columns + 13].Width = 0;
                }
                using var fast = new TerminalTextRenderer { UseNativeLayouts = true, UseAsciiShapes = true, UseStreamedAscii = true };
                using var reference = new TerminalTextRenderer { UseNativeLayouts = true, UseAsciiShapes = true };
                using var actual = new CanvasRenderTarget(device, columns * cellWidth, rows * cellHeight, dpi);
                using var expected = new CanvasRenderTarget(device, columns * cellWidth, rows * cellHeight, dpi);
                Paint(fast, actual); Paint(reference, expected); bool equal = actual.GetPixelBytes().SequenceEqual(expected.GetPixelBytes());
                if (!equal)
                {
                    await actual.SaveAsync(Path.Combine(SessionStore.Folder, $"ascii-decoration-actual-{dpi}-{mixedUnicode}.png"));
                    await expected.SaveAsync(Path.Combine(SessionStore.Folder, $"ascii-decoration-reference-{dpi}-{mixedUnicode}.png"));
                }
                check(equal, $"Font-scratch ASCII preserves underline ordering, strike/overline, dim/selection color, hidden/blinking cells, italic and Unicode fallback at {dpi} DPI; mixed Unicode {mixedUnicode}");
                void Paint(TerminalTextRenderer renderer, CanvasRenderTarget target)
                {
                    using var draw = gpu.CreateDrawingSession(target); draw.Clear(Microsoft.UI.Colors.Transparent);
                    for (int row = 0; row < rows; row++) renderer.DrawRow(target, draw, cells, row, columns, cellWidth, cellHeight, style => fonts[style & 3], true, false, null,
                        (canvas, x, y, width, style, color) => { if (style != 0) canvas.DrawLine(x, y + 20.5f, x + width, y + 20.5f, color, .65f); }, 0xffffa500);
                }
            }
        }
        finally { foreach (var font in fonts) font.Dispose(); }
    }
}
