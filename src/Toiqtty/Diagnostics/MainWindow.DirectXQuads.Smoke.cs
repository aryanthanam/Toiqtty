using Microsoft.Graphics.Canvas;
using Toiqtty.Controls;
using Windows.Foundation;
using Windows.UI;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static void CheckDirectXQuadModes(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice(); using var gpu = new DirectXRenderer(device);
        byte[] pixels = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 128, 128, 128, 128];
        using var image = gpu.CreateBitmap(pixels, 2, 2);
        foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f })
        {
            byte[]? expanded = null;
            foreach (bool oldMode in new[] { true, false })
            {
                gpu.SetQuadModeForCheck(oldMode);
                using var target = new CanvasRenderTarget(device, 128, 96, dpi);
                using (var draw = gpu.CreateDrawingSession(target))
                {
                    draw.Clear(Microsoft.UI.Colors.Transparent);
                    draw.FillRectangle(0, 0, 128, 96, Color.FromArgb(190, 23, 45, 67));
                    using (draw.CreateLayer(1, new Rect(3.25, 4.5, 110, 80)))
                    {
                        draw.DrawImage(image, new Rect(-2.5, 1.25, 50.5, 60.75), new Rect(0, 0, 2, 2), .7f, CanvasImageInterpolation.Linear);
                        for (int shape = 1; shape <= 4; shape++) draw.FillPowerline(40 + shape * 12, 5, 13, 42, Color.FromArgb(170, 230, 130, 25), shape);
                        draw.FillTriangle(-20, 15, 100, 45, 35, 120, Color.FromArgb(160, 30, 200, 240));
                        draw.DrawLine(-3, 70, 105, 80, Microsoft.UI.Colors.Yellow, 1.25f);
                        draw.Blend = CanvasBlend.Copy; draw.FillRectangle(20, 30, 7, 9, Microsoft.UI.Colors.Transparent);
                        draw.Blend = CanvasBlend.SourceOver; draw.FillRectangle(20, 35, 12, 5, Microsoft.UI.Colors.White);
                    }
                }
                var actual = target.GetPixelBytes();
                if (oldMode) expanded = actual;
                else check(actual.SequenceEqual(expanded!), $"Instanced quads are pixel-identical to expanded triangles across clipping, images, alpha and primitive switches at {dpi} DPI");
            }
        }
    }
}
