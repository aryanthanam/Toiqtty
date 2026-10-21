using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Controls;
using Windows.Foundation;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static void CheckResizeCapacity(Action<bool, string> check)
    {
        var device = CanvasDevice.GetSharedDevice(); using var reference = new DirectXRenderer(device) { ReserveResizeForCheck = false };
        using var reserved = new DirectXRenderer(device) { ReserveResizeForCheck = true };
        using var font = new CanvasTextFormat { FontFamily = "Consolas", FontSize = 14, WordWrapping = CanvasWordWrapping.NoWrap };
        using var layout = new CanvasTextLayout(device, "Resize café 中🙂 !=", font, 1000, 32);
        using var imageA = reference.CreateBitmap([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 128, 128, 128, 128], 2, 2);
        using var imageB = reserved.CreateBitmap([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 128, 128, 128, 128], 2, 2);
        var sizes = new (uint W, uint H)[]{(320,180),(321,181),(322,182),(340,190),(330,180),(315,172),(400,240),(390,230),
            (385,232),(380,228),(250,130),(240,128),(245,132),(120,90),(124,92),(128,96),(130,98),(500,300),(510,310),(512,312)};
        bool exact = true, bounded = true; int comparisons = 0;
        foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f }) foreach (var (width, height) in sizes)
        {
            bool alpha = comparisons % 2 == 0; reference.Resize(width, height, dpi, alpha); reserved.Resize(width, height, dpi, alpha);
            Draw(reference, imageA); Draw(reserved, imageB);
            var first = new byte[width * height * 4]; var second = new byte[first.Length]; reference.ReadBackbufferForCheck(first); reserved.ReadBackbufferForCheck(second);
            exact &= first.SequenceEqual(second); comparisons++;
            var stats = reserved.ResizeStatsForCheck;
            bounded &= stats.Width == width && stats.Height == height && stats.CapacityWidth >= width && stats.CapacityHeight >= height
                && stats.CapacityWidth <= Math.Max(128, width * 2) && stats.CapacityHeight <= Math.Max(128, height * 2);
            void Draw(DirectXRenderer gpu, DirectXBitmap image)
            {
                float w = width * 96 / dpi, h = height * 96 / dpi; using var draw = gpu.CreateDrawingSession(w, h);
                draw.Clear(Windows.UI.Color.FromArgb(alpha ? (byte)128 : (byte)255, 20, 40, 80));
                draw.FillRectangle(0, 0, w, 2, Microsoft.UI.Colors.Cyan); draw.FillRectangle(w - 3, h - 3, 3, 3, Microsoft.UI.Colors.Red);
                draw.SetClip(new Rect(1, 1, w - 2, h - 2)); draw.DrawTextLayout(layout, 3, 4, Microsoft.UI.Colors.White);
                draw.DrawLine(2.5f, h - 8.5f, w - 2.5f, 3.5f, Microsoft.UI.Colors.Orange);
                draw.DrawImage(image, new Rect(w / 2, 12, 20, 18), new Rect(0, 0, 2, 2), .7f, CanvasImageInterpolation.NearestNeighbor);
            }
        }
        var old = reference.ResizeStatsForCheck; var current = reserved.ResizeStatsForCheck;
        check(exact && comparisons == 100, "Capacity resizing preserves exact glyph, image, clip, line, alpha and corner pixels across 100 size/DPI transitions");
        check(bounded, "Retained backbuffer capacity stays bounded and large shrinks release the old peak");
        check(current.LogicalResizes > current.BufferResizes && current.BufferResizes < old.BufferResizes / 2,
            "Most resize steps update the logical size without GPU-idle buffer destruction");
        check(reference.GetStats().DebugErrors == 0 && reserved.GetStats().DebugErrors == 0, "Both resize strategies leave the Direct3D debug queue clean");
        if (Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_RESIZE_BENCHMARK") == "1")
        {
            var results = new List<object>();
            for (int pair = 0; pair < 3; pair++) foreach (bool capacity in pair % 2 == 0 ? new[] { false, true } : [true, false])
            {
                using var gpu = new DirectXRenderer(device) { ReserveResizeForCheck = capacity };
                gpu.Resize(768, 512, 96, false); Paint(768, 512);
                var pixels = new byte[768 * 512 * 4]; gpu.ReadBackbufferForCheck(pixels);
                using var process = System.Diagnostics.Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (uint step = 0; step < 300; step++)
                {
                    uint w = 650 + step % 60, h = 390 + step % 40; gpu.Resize(w, h, 96, false); Paint(w, h);
                }
                gpu.ReadBackbufferForCheck(pixels); clock.Stop();
                var stats = gpu.ResizeStatsForCheck;
                results.Add(new
                {
                    pair,
                    capacity,
                    steps = 300,
                    elapsedMs = clock.Elapsed.TotalMilliseconds,
                    cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    stats.BufferResizes,
                    stats.LogicalResizes,
                    stats.CapacityWidth,
                    stats.CapacityHeight,
                    textureBytes = gpu.GetStats().TextureBytes
                });
                void Paint(uint w, uint h)
                {
                    using var draw = gpu.CreateDrawingSession(w, h); draw.Clear(Microsoft.UI.Colors.Black);
                    draw.DrawTextLayout(layout, 3, 4, Microsoft.UI.Colors.White); draw.FillRectangle(w - 3, h - 3, 3, 3, Microsoft.UI.Colors.Red);
                }
            }
            File.WriteAllText(Path.Combine(Toiqtty.Models.SessionStore.Folder, "resize-capacity-benchmark.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                meaning = "Native resize, command submission and final completed readback; no presentation or input. Exact pixels are checked separately. Paired order alternates after warming glyphs.",
                results
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
