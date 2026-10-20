using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task CheckResizePresentation(Action<bool, string> check)
    {
        _power.ObserveDisplayState(); await Task.Delay(200);
        check(_power.DisplayState != 0, "Desktop resize pixel comparison has a powered display");
        var host = new Grid
        {
            Width = 500,
            Height = 120,
            Margin = new(20, 150, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            IsHitTestVisible = false
        };
        host.ColumnDefinitions.Add(new() { Width = new(240) }); host.ColumnDefinitions.Add(new() { Width = new(20) }); host.ColumnDefinitions.Add(new() { Width = new(240) });
        var first = new Grid(); var second = new Grid(); host.Children.Add(first); host.Children.Add(second); Grid.SetColumn(second, 2);
        Grid.SetRowSpan(host, 4); Root.Children.Add(host);
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this); bool topmost = (DirectXDesktop.GetWindowLongPtr(window, -20).ToInt64() & 8) != 0;
        if (!DirectXDesktop.SetWindowPos(window, -1, 0, 0, 0, 0, 0x13)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        var evidence = new List<object>(); var visibility = PaneHost.Visibility; PaneHost.Visibility = Visibility.Collapsed;
        try
        {
            Root.UpdateLayout(); await Task.Delay(100);
            foreach (bool software in new[] { false, true })
            {
                using var device = new CanvasDevice(software); using var reference = new DirectXRenderer(device) { ReserveResizeForCheck = false };
                using var retained = new DirectXRenderer(device) { ReserveResizeForCheck = true };
                using var font = new CanvasTextFormat { FontFamily = "Consolas", FontSize = 14, WordWrapping = CanvasWordWrapping.NoWrap };
                using var text = new CanvasTextLayout(device, "Resize ffi != café 中🙂", font, 1000, 32);
                reference.Resize(480, 224, 96, false); retained.Resize(480, 224, 96, false);
                foreach (float dpi in new[] { 96f, 120f, 144f, 168f, 192f }) foreach (bool alpha in new[] { false, true })
                {
                    bool controlScene = false;
                    uint width = (uint)Math.Round(240 * dpi / 96), height = (uint)Math.Round(120 * dpi / 96);
                    reference.Resize(width, height, dpi, alpha); retained.Resize(width, height, dpi, alpha);
                    reference.Attach(first); retained.Attach(second);
                    await Present(reference); await Present(retained); await Task.Delay(100); System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(DirectXDesktop.DwmFlush());
                    byte[] expected = Read(first), actual = Read(second);
                    int pixelWidth = (int)Math.Round(240 * Root.XamlRoot.RasterizationScale), pixelHeight = (int)Math.Round(120 * Root.XamlRoot.RasterizationScale), inside = 0, maxError = 0;
                    // The outer physical pixel can sample XAML's snapped parent
                    // boundary, whose background differs between columns. The
                    // separate native comparison covers every backbuffer edge.
                    for (int y = 1; y < pixelHeight - 1; y++) for (int x = 1; x < pixelWidth - 1; x++) for (int channel = 0; channel < 4; channel++)
                    { int index = (y * pixelWidth + x) * 4 + channel; int error = Math.Abs(expected[index] - actual[index]); if (error != 0) inside++; maxError = Math.Max(maxError, error); }
                    // Fractional-DPI surface transforms can round one channel
                    // level differently after resampling; glyph position and
                    // coverage remain checked, with no geometric tolerance.
                    int referenceGreen = GreenPixels(expected), actualGreen = GreenPixels(actual);
                    bool sceneVisible = referenceGreen >= 128 && actualGreen >= 128;
                    bool equal = maxError <= 1 && sceneVisible;
                    var capacity = retained.ResizeStatsForCheck;
                    evidence.Add(new
                    {
                        software,
                        dpi,
                        alpha,
                        equal,
                        sceneVisible,
                        referenceGreen,
                        actualGreen,
                        differentInteriorBytes = inside,
                        maxInteriorChannelError = maxError,
                        channelTolerance = 1,
                        capacity.Width,
                        capacity.Height,
                        capacity.CapacityWidth,
                        capacity.CapacityHeight,
                        capacity.BufferResizes,
                        capacity.LogicalResizes,
                        differentBytes = expected.Zip(actual).Count(pair => pair.First != pair.Second),
                        excludedPhysicalBorderPixels = 1
                    });
                    File.WriteAllText(Path.Combine(SessionStore.Folder, "resize-presentation.json"), System.Text.Json.JsonSerializer.Serialize(evidence, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    if (!equal)
                    {
                        await Save(expected, "resize-presentation-reference.png"); await Save(actual, "resize-presentation-retained.png");
                    }
                    check(equal, $"Retained allocation preserves desktop interior glyph, geometry and alpha pixels within one channel level: WARP {software}, {dpi} DPI, alpha {alpha}");
                    if (dpi == 96 && !alpha)
                    {
                        controlScene = true; await Present(retained); await Task.Delay(100);
                        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(DirectXDesktop.DwmFlush());
                        check(GreenPixels(Read(second)) < 128, $"Desktop capture detects an independently changed retained scene: WARP {software}");
                    }
                    async Task Present(DirectXRenderer gpu)
                    {
                        for (int retry = 0; retry < 20; retry++)
                        {
                            gpu.BeginFrame(); using (var draw = gpu.CreateDrawingSession(240, 120))
                            {
                                draw.Clear(Windows.UI.Color.FromArgb(alpha ? (byte)128 : (byte)255, 10, 30, 100));
                                draw.DrawTextLayout(text, 4, 5, Microsoft.UI.Colors.White); draw.FillRectangle(7, 45, 43, 17, controlScene ? Microsoft.UI.Colors.Magenta : Microsoft.UI.Colors.Lime);
                                draw.DrawLine(2.5f, 75.5f, 231.5f, 92.5f, Microsoft.UI.Colors.Orange);
                                draw.FillRectangle(0, 0, 4, 4, Microsoft.UI.Colors.White); draw.FillRectangle(236, 116, 4, 4, Microsoft.UI.Colors.Red);
                            }
                            if (gpu.Present()) return; await Task.Delay(20);
                        }
                        throw new TimeoutException("Resize comparison could not present its owned scene.");
                    }
                    static int GreenPixels(byte[] bytes)
                    {
                        int count = 0;
                        for (int index = 0; index < bytes.Length; index += 4)
                            if (bytes[index + 1] > 180 && bytes[index] < 80 && bytes[index + 2] < 80) count++;
                        return count;
                    }
                    async Task Save(byte[] bytes, string name)
                    {
                        int w = (int)Math.Round(240 * Root.XamlRoot.RasterizationScale), h = (int)Math.Round(120 * Root.XamlRoot.RasterizationScale);
                        using var bitmap = CanvasBitmap.CreateFromBytes(device, bytes, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
                        await bitmap.SaveAsync(Path.Combine(SessionStore.Folder, name));
                    }
                }
                DirectXRenderer.Detach(first); DirectXRenderer.Detach(second);
            }
            byte[] Read(Grid panel)
            {
                float scale = (float)Root.XamlRoot.RasterizationScale; var origin = panel.TransformToVisual(Root).TransformPoint(new(0, 0));
                var point = new DirectXDesktop.Point { X = (int)Math.Round(origin.X * scale), Y = (int)Math.Round(origin.Y * scale) }; DirectXDesktop.ClientToScreen(window, ref point);
                var center = new DirectXDesktop.Point { X = point.X + (int)(120 * scale), Y = point.Y + (int)(60 * scale) };
                if (DirectXDesktop.GetAncestor(DirectXDesktop.WindowFromPoint(center), 2) != window) throw new InvalidOperationException("Desktop resize pixels are occluded by another window.");
                nint dc = DirectXDesktop.GetDC(0); if (dc == 0) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                try { return DirectXDesktop.ReadMarkerPixels(dc, point.X, point.Y, (int)Math.Round(240 * scale), (int)Math.Round(120 * scale)); }
                finally { DirectXDesktop.ReleaseDC(0, dc); }
            }
        }
        finally
        {
            DirectXRenderer.Detach(first); DirectXRenderer.Detach(second); Root.Children.Remove(host); PaneHost.Visibility = visibility;
            if (!topmost) DirectXDesktop.SetWindowPos(window, -2, 0, 0, 0, 0, 0x13);
        }
    }
}
