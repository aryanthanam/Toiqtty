using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Controls;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static void CheckAtlasHashing(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        using var terminal = new GhosttyTerminal(100, 1);
        byte[] input = System.Text.Encoding.UTF8.GetBytes(new string(Enumerable.Range(32, 95).Select(value => (char)value).ToArray()));
        terminal.Write(input, input.Length); var cells = new TerminalCell[100]; _ = terminal.ReadSnapshot(cells);
        using var renderer = new TerminalTextRenderer { UseNativeLayouts = true };
        using var format = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, 0), FontSize = 14, WordWrapping = CanvasWordWrapping.NoWrap };
        using var target = new CanvasRenderTarget(device, 1000, 32, 96);
        byte[] Draw()
        {
            using (var drawing = gpu.CreateDrawingSession(target))
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                renderer.DrawRow(target, drawing, cells, 0, 100, 10, 24, _ => format, false, true, null, (_, _, _, _, _, _) => { });
            }
            return target.GetPixelBytes();
        }
        byte[] expected = Draw(); var mixed = gpu.AtlasBucketsForCheck;
        gpu.SetAtlasHashModeForCheck(true); var legacy = gpu.AtlasBucketsForCheck;
        check(mixed.Entries == legacy.Entries && mixed.MaxChain <= 8 && legacy.MaxChain >= 16,
            "Mixed glyph hashes avoid the long same-face ASCII chains created by the original low-bit hash");
        check(Draw().SequenceEqual(expected), "Rehashing a populated glyph atlas preserves every cached glyph and pixel");
        gpu.SetAtlasHashModeForCheck(false);
        check(Draw().SequenceEqual(expected), "Returning to mixed hashing preserves cached resource ownership and rendering");
        File.WriteAllText(Path.Combine(SessionStore.Folder, "atlas-hash.json"), System.Text.Json.JsonSerializer.Serialize(new { mixed, legacy }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
}
