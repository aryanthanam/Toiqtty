using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Controls;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static async Task CheckColorGlyphCache(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        if (gpu.ColorGlyphsForCheck.Enabled == 0) return;
        using var font = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, 0), FontSize = 14, WordWrapping = CanvasWordWrapping.NoWrap };
        using var renderer = new TerminalTextRenderer { UseNativeLayouts = true };
        foreach (ushort columns in new ushort[] { 48, 400 }) foreach (bool emoji in new[] { false, true })
        {
            using var terminal = new GhosttyTerminal(columns, 2);
            string pattern = emoji ? "P café 中🙂 é 👩🏽‍💻 🙂 " : "office ffi => café 中 é 日本語 ";
            string line = string.Concat(Enumerable.Repeat(pattern, columns == 48 ? 1 : 15));
            byte[] input = System.Text.Encoding.UTF8.GetBytes("\x1b[?2027h" + line + "\r\n" + line);
            terminal.Write(input, input.Length); var cells = new TerminalCell[columns * 2]; _ = terminal.ReadSnapshot(cells);
            foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f }) foreach (uint foreground in new[] { 0xffd0e0f0u, 0x8c60d090u })
            {
                renderer.Reset(); float scale = dpi / 96, cellWidth = MathF.Ceiling(10 * scale) / scale, cellHeight = MathF.Ceiling(24 * scale) / scale;
                for (int index = 0; index < cells.Length; index++) cells[index].Foreground = foreground;
                cells[0].Text = dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)[0].ToString();
                using var actual = new CanvasRenderTarget(device, columns * cellWidth, 2 * cellHeight, dpi);
                using var expected = new CanvasRenderTarget(device, columns * cellWidth, 2 * cellHeight, dpi);
                Draw(actual, false); Draw(expected, true); gpu.SetTextModeForCheck(false);
                var first = actual.GetPixelBytes(); var second = expected.GetPixelBytes(); int max = 0;
                for (int index = 0; index < first.Length; index++) max = Math.Max(max, Math.Abs(first[index] - second[index]));
                if (max > 12)
                {
                    await actual.SaveAsync(Path.Combine(SessionStore.Folder, $"color-grid-actual-{columns}-{emoji}-{dpi}-{foreground:x}.png"));
                    await expected.SaveAsync(Path.Combine(SessionStore.Folder, $"color-grid-reference-{columns}-{emoji}-{dpi}-{foreground:x}.png"));
                    using var canvas = new CanvasRenderTarget(device, columns * cellWidth, 2 * cellHeight, dpi);
                    using (var draw = canvas.CreateDrawingSession())
                    {
                        draw.Clear(Microsoft.UI.Colors.Transparent);
                        for (int row = 0; row < 2; row++)
                        {
                            var value = new System.Text.StringBuilder("\u202d"); var widths = new List<uint>();
                            for (int column = 0; column < columns; column++)
                            {
                                ref var cell = ref cells[row * columns + column]; if (cell.Width == 0) continue;
                                string text = cell.GetText(); if (text.Length == 0) text = " "; value.Append(text);
                                for (int index = 0; index < text.Length; index++) widths.Add(index == text.Length - 1 ? cell.Width : 0);
                            }
                            value.Append('\u202c'); using var layout = new CanvasTextLayout(device, value.ToString(), font, 100000, cellHeight);
                            renderer.SetLineMetrics(layout, device, font, cellHeight, 0);
                            using var typography = new CanvasTypography();
                            typography.AddFeature(CanvasTypographyFeatureName.StandardLigatures, 1); typography.AddFeature(CanvasTypographyFeatureName.ContextualLigatures, 1); typography.AddFeature(CanvasTypographyFeatureName.ContextualAlternates, 1);
                            layout.SetTypography(0, value.Length, typography); int offset = 0;
                            foreach (var cluster in layout.ClusterMetrics)
                            {
                                float advance = 0;
                                for (int index = offset; index < offset + cluster.CharacterCount; index++) if (index > 0 && index <= widths.Count) advance += cellWidth * widths[index - 1];
                                layout.SetCharacterSpacing(offset, cluster.CharacterCount, 0, advance - cluster.Width, 0); offset += cluster.CharacterCount;
                            }
                            draw.DrawTextLayout(layout, 0, row * cellHeight + 1, Windows.UI.Color.FromArgb((byte)(foreground >> 24), (byte)(foreground >> 16), (byte)(foreground >> 8), (byte)foreground));
                        }
                    }
                    await canvas.SaveAsync(Path.Combine(SessionStore.Folder, $"color-grid-canvas-{columns}-{emoji}-{dpi}-{foreground:x}.png"));
                    var independent = canvas.GetPixelBytes(); int actualError = 0, referenceError = 0;
                    for (int index = 0; index < first.Length; index++) { actualError = Math.Max(actualError, Math.Abs(first[index] - independent[index])); referenceError = Math.Max(referenceError, Math.Abs(second[index] - independent[index])); }
                    File.WriteAllText(Path.Combine(SessionStore.Folder, "wide-grid-reference.json"), System.Text.Json.JsonSerializer.Serialize(new { columns, emoji, dpi, actualError, referenceError }));
                }
                check(max <= 12, $"Integral-grid glyphs preserve DirectWrite shaping and opacity at {dpi} DPI, {columns} columns, emoji {emoji}; maximum channel difference {max}");
                void Draw(CanvasRenderTarget target, bool whole)
                {
                    gpu.SetTextModeForCheck(whole); using var drawing = gpu.CreateDrawingSession(target); drawing.Clear(Microsoft.UI.Colors.Transparent);
                    for (int row = 0; row < 2; row++) renderer.DrawRow(target, drawing, cells, row, columns, cellWidth, cellHeight, _ => font, true, true, null, (_, _, _, _, _, _) => { });
                }
            }
        }
        var stats = gpu.ColorGlyphsForCheck;
        check(stats.Available != 0 && stats.Hits > 0 && stats.Misses > 0, "The color-glyph fixture exercises cache misses and reuses resolved glyphs across changing layouts");
        check(gpu.GetStats().DebugErrors == 0, "Color-glyph scratch copies leave the Direct3D debug queue clean");
    }
}
