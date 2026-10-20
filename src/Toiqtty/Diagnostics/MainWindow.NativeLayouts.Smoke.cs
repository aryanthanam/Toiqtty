using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Controls;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static async Task CheckNativeTerminalLayouts(Action<bool, string> check, bool asciiShapes = false, bool streamed = false)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        using var terminal = new GhosttyTerminal(100, 4);
        byte[] input = System.Text.Encoding.UTF8.GetBytes("\x1b[?2027h" + new string('a', 90) + "\r\n"
            + "office ffi => === != <= >= <-> abcXYZ012345\r\n"
            + "café 中🙂 é אבג abc 日本語 👩🏽‍💻\r\n" + "a" + new string('\u0301', 70));
        terminal.Write(input, input.Length); var cells = new TerminalCell[400]; _ = terminal.ReadSnapshot(cells);
        foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f })
            foreach (uint style in new uint[] { 0, 1, 2, 3 })
                foreach (bool ligatures in new[] { false, true })
                    foreach (bool customFeatures in new[] { false, true })
                    {
                        using var native = new TerminalTextRenderer { UseNativeLayouts = true, UseAsciiShapes = asciiShapes, UseStreamedAscii = streamed };
                        using var projected = new TerminalTextRenderer { UseProjectedClusterMetricsForCheck = true };
                        if (customFeatures)
                        {
                            var features = new Dictionary<string, uint> { { "liga", ligatures ? 0u : 1u }, { "calt", 1 }, { "cv01", 1 } };
                            native.ConfigureFeatures(features); projected.ConfigureFeatures(features);
                        }
                        using var font = new CanvasTextFormat
                        {
                            FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, style),
                            FontSize = 14,
                            FontWeight = (style & 1) != 0 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                            FontStyle = (style & 2) != 0 ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                            WordWrapping = CanvasWordWrapping.NoWrap
                        };
                        for (int index = 0; index < cells.Length; index++) cells[index].Flags = (cells[index].Flags & ~3u) | style;
                        using var target = new CanvasRenderTarget(device, 1000, 96, dpi);
                        using var reference = new CanvasRenderTarget(device, 1000, 96, dpi);
                        Draw(native, target); Draw(projected, reference);
                        bool equal = target.GetPixelBytes().SequenceEqual(reference.GetPixelBytes());
                        if (!equal)
                        {
                            string label = $"{dpi}-{style}-{ligatures}-{customFeatures}";
                            await target.SaveAsync(Path.Combine(SessionStore.Folder, "native-layout-" + label + ".png"));
                            await reference.SaveAsync(Path.Combine(SessionStore.Folder, "projected-layout-" + label + ".png"));
                        }
                        check(equal, $"{(asciiShapes ? "HarfBuzz ASCII + native fallback" : "Native layouts")} preserve exact Unicode, fallback, ligature and grapheme pixels at {dpi} DPI, style {style}, ligatures {ligatures}, custom features {customFeatures}");
                        if (asciiShapes) check((native.AsciiShapesCreatedForCheck > 0) == ((style & 2) == 0), "Eligible ASCII uses the shaping prototype while italic keeps joint DirectWrite coverage");
                        check(native.CachedLayouts > 0 && native.NativeClusterReadsForCheck == 0 && gpu.LayoutScratchCapacityForCheck > 0,
                            "The native layout path bypasses managed layout and cluster-metric projections");
                        void Draw(TerminalTextRenderer renderer, CanvasRenderTarget surface)
                        {
                            using var drawing = gpu.CreateDrawingSession(surface); drawing.Clear(Microsoft.UI.Colors.Transparent);
                            for (int row = 0; row < 4; row++) renderer.DrawRow(surface, drawing, cells, row, 100, 10, 24, _ => font, ligatures, true, null, (_, _, _, _, _, _) => { });
                        }
                    }
        using var format = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, 0), FontSize = 14 };
        using var probe = new CanvasTextLayout(device, "M", format, 1000, 24);
        using var metrics = new TerminalTextRenderer(); metrics.SetLineMetrics(probe, device, format, 24, 0);
        using var nativeFormat = gpu.CreateTerminalFormat(probe);
        string huge = "\u202d" + new string('W', 9000) + "\u202c";
        using var large = gpu.CreateTerminalLayout(nativeFormat, huge, "", [], 10, 24, 90000, true, out _);
        check(gpu.LayoutScratchCapacityForCheck <= 8192, "Exceptionally long native layouts use call-lifetime scratch without permanently growing the pane buffer");
    }
}
