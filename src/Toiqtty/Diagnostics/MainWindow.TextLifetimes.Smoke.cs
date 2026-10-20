using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Controls;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static void CheckTerminalTextLifetimes(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        using var target = new CanvasRenderTarget(device, 500, 32, 96);
        using var font = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, 0), FontSize = 14, WordWrapping = CanvasWordWrapping.NoWrap };
        using var renderer = new TerminalTextRenderer { UseNativeLayouts = true };
        var cells = new TerminalCell[40]; for (int index = 0; index < cells.Length; index++) cells[index] = new() { Foreground = 0xffffffff, Width = 1, Length = 1, Text = "W" };
        byte[] Draw()
        {
            using (var drawing = gpu.CreateDrawingSession(target))
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                renderer.DrawRow(target, drawing, cells, 0, 40, 10, 24, _ => font, true, true, null, (_, _, _, _, _, _) => { });
            }
            return target.GetPixelBytes();
        }
        byte[] expected = Draw();
        check(renderer.CachedLayouts == 1 && gpu.GetStats().ShapeCacheBytes > 0, "A native text run owns its layout and shaped glyph storage");
        renderer.Reset();
        check(renderer.CachedLayouts == 0 && gpu.GetStats().ShapeCacheBytes == 0, "Reset ends the font-configuration lifetime and releases native layout/shape owners");
        check(Draw().SequenceEqual(expected), "A reset text renderer creates a fresh font configuration and preserves exact pixels");
        renderer.Dispose(); renderer.Dispose();
        bool rejected = false;
        try { _ = Draw(); } catch (ObjectDisposedException) { rejected = true; }
        check(rejected, "Final text-renderer disposal is idempotent and prevents resource resurrection through late drawing");
    }
}
