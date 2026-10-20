using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Toiqtty.Models;
using Windows.Foundation;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private void CheckRetainedPresentationResize(Action<bool, string> check)
    {
        var panel = new Grid { IsHitTestVisible = false };
        Root.Children.Add(panel);
        try
        {
            using var gpu = new Controls.DirectXRenderer(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice());
            using var presentation = new Controls.DirectXPresentation(gpu.Handle);
            gpu.Resize(64, 64, 96, true); presentation.Resize(64, 64); presentation.Attach(panel, true);
            var surface = presentation.ActiveDrawingSurface!;
            gpu.Resize(128, 96, 96, false); presentation.Resize(128, 96); presentation.Attach(panel, false);
            check(presentation.ActiveDrawingSurface is null && surface.Size.Width == 64 && surface.Size.Height == 64
                && presentation.TextureBytes == 64 * 64 * 8UL,
                "Opaque resizing retains an inactive transparency surface at its actual previous size and memory estimate");
            presentation.Attach(panel, true);
            check(ReferenceEquals(surface, presentation.ActiveDrawingSurface) && surface.Size.Width == 128 && surface.Size.Height == 96
                && presentation.SurfaceCreations == 2 && presentation.TextureBytes == 128 * 96 * 8UL,
                "Transparency reactivation resizes the retained surface before use without creating another surface");
            gpu.Resize(80, 72, 96, true); presentation.Resize(80, 72); presentation.Attach(panel, true);
            check(surface.Size.Width == 80 && surface.Size.Height == 72, "Size-only updates resize an already active transparency surface");
            gpu.BeginFrame();
            using (var draw = gpu.CreateDrawingSession(80, 72)) draw.Clear(Microsoft.UI.Colors.Blue);
            Controls.DirectXRenderer.Check(Controls.DirectXRenderer.Native.PresentSurface(gpu.Handle, ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr));
            check(gpu.GetStats().DeviceRemovedReason == 0, "The lazily resized composition surface accepts a real GPU frame");
        }
        finally { Controls.DirectXRenderer.Detach(panel); Root.Children.Remove(panel); }
    }
    private async Task CheckDirectXCompositor(Grid panel, float width, float height, float dpi, bool transparent, List<object> evidence, Action<bool, string> check)
    {
        bool offscreen = Environment.GetEnvironmentVariable("TOIQTTY_DIRECTX_OFFSCREEN") == "1";
        if (_power.DisplayState == 0 || offscreen)
        {
            evidence.Add(new { dpi, transparent, panel.Opacity, displayState = _power.DisplayState, skipped = true, reason = offscreen ? "Explicit offscreen diagnostic mode; native GPU pixels remain checked, desktop composition is unverified." : "Windows reports the session display is powered off; GPU backbuffer checks continue." });
            File.WriteAllText(Path.Combine(SessionStore.Folder, "directx-compositor.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        bool topmost = (DirectXDesktop.GetWindowLongPtr(window, -20).ToInt64() & 8) != 0;
        if (!DirectXDesktop.SetWindowPos(window, -1, 0, 0, 0, 0, 0x13)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        nint dc = 0;
        try
        {
            Root.UpdateLayout(); await Task.Delay(80); Marshal.ThrowExceptionForHR(DirectXDesktop.DwmFlush());
            dc = DirectXDesktop.GetDC(0); if (dc == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var records = new List<object>(); bool correct = false; var deadline = DateTime.UtcNow.AddSeconds(1);
            do
            {
                records.Clear(); correct = true;
                foreach (var marker in new (string Name, float X, float Y, int R, int G, int B)[] { ("white", 2, 2, 255, 255, 255), ("cyan", width - 2, 2, 0, 255, 255), ("yellow", 2, height - 2, 255, 255, 0), ("red", width - 2, height - 2, 255, 0, 0), ("alpha", width / 2, 2, transparent ? 127 : 0, transparent ? 127 : 0, 255) })
                {
                    Point local = panel.TransformToVisual(Root).TransformPoint(new(marker.X, marker.Y));
                    var screen = new DirectXDesktop.Point { X = (int)Math.Round(local.X * Root.XamlRoot.RasterizationScale), Y = (int)Math.Round(local.Y * Root.XamlRoot.RasterizationScale) };
                    if (!DirectXDesktop.ClientToScreen(window, ref screen)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    bool owned = DirectXDesktop.GetAncestor(DirectXDesktop.WindowFromPoint(screen), 2) == window;
                    // Read only explicit marker pixels inside this owned fixture.
                    uint pixel = owned ? DirectXDesktop.GetPixel(dc, screen.X, screen.Y) : uint.MaxValue;
                    int r = (int)(pixel & 255), g = (int)(pixel >> 8 & 255), b = (int)(pixel >> 16 & 255);
                    int expectedR = (int)Math.Round(255 - (255 - marker.R) * panel.Opacity), expectedG = (int)Math.Round(255 - (255 - marker.G) * panel.Opacity), expectedB = (int)Math.Round(255 - (255 - marker.B) * panel.Opacity);
                    bool equal = owned && Math.Abs(r - expectedR) <= 4 && Math.Abs(g - expectedG) <= 4 && Math.Abs(b - expectedB) <= 4; correct &= equal;
                    records.Add(new { marker.Name, screen.X, screen.Y, owned, r, g, b, expected = new { R = expectedR, G = expectedG, B = expectedB }, passed = equal });
                }
                if (!correct) { await Task.Delay(50); Marshal.ThrowExceptionForHR(DirectXDesktop.DwmFlush()); }
            } while (!correct && DateTime.UtcNow < deadline);
            var found = new Dictionary<string, (int MinX, int MinY, int MaxX, int MaxY, int Count)>();
            if (!correct)
            {
                var origin = panel.TransformToVisual(Root).TransformPoint(new(0, 0)); float scale = (float)Root.XamlRoot.RasterizationScale;
                var start = new DirectXDesktop.Point { X = (int)Math.Round(origin.X * scale), Y = (int)Math.Round(origin.Y * scale) };
                DirectXDesktop.ClientToScreen(window, ref start);
                int pixelWidth = (int)(width * scale), pixelHeight = (int)(height * scale);
                var pixels = DirectXDesktop.ReadMarkerPixels(dc, start.X, start.Y, pixelWidth, pixelHeight);
                using (var bitmap = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), pixels, pixelWidth, pixelHeight,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore))
                    await bitmap.SaveAsync(Path.Combine(SessionStore.Folder, "compositor-failure-" + evidence.Count + ".png"));
                for (int y = 0; y < pixelHeight; y += 2) for (int x = 0; x < pixelWidth; x += 2)
                {
                    int offset = (y * pixelWidth + x) * 4, b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                    string? name = r < 16 && g > 239 && b > 239 ? "cyan" : r > 239 && g < 16 && b < 16 ? "red" : r > 239 && g > 239 && b < 16 ? "yellow" : null;
                    if (name is null) continue;
                    if (found.TryGetValue(name, out var area)) found[name] = (Math.Min(area.MinX, x), Math.Min(area.MinY, y), Math.Max(area.MaxX, x), Math.Max(area.MaxY, y), area.Count + 1);
                    else found[name] = (x, y, x, y, 1);
                }
            }
            evidence.Add(new
            {
                dpi,
                transparent,
                panel.Opacity,
                displayState = _power.DisplayState,
                rootScale = Root.XamlRoot.RasterizationScale,
                width,
                height,
                panel.ActualWidth,
                panel.ActualHeight,
                markers = records,
                foundMarkers = found.ToDictionary(pair => pair.Key, pair => new { pair.Value.MinX, pair.Value.MinY, pair.Value.MaxX, pair.Value.MaxY, pair.Value.Count })
            });
            File.WriteAllText(Path.Combine(SessionStore.Folder, "directx-compositor.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            check(correct, $"Actual Windows composition preserves native corner placement and premultiplied alpha at {dpi} rendering DPI, opacity {panel.Opacity}");
        }
        finally
        {
            if (dc != 0) DirectXDesktop.ReleaseDC(0, dc);
            if (!topmost) DirectXDesktop.SetWindowPos(window, -2, 0, 0, 0, 0, 0x13);
        }
    }
    private static class DirectXDesktop
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XResolution, YResolution; public uint ColorsUsed, ColorsImportant; }
        internal static byte[] ReadMarkerPixels(nint screen, int x, int y, int width, int height)
        {
            nint memory = CreateCompatibleDC(screen), bitmap = 0, previous = 0;
            if (memory == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                bitmap = CreateDIBSection(screen, ref info, 0, out var data, 0, 0);
                if (bitmap == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                previous = SelectObject(memory, bitmap);
                if (previous == 0 || previous == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00cc0020)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (!GdiFlush()) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var bytes = new byte[checked(width * height * 4)]; Marshal.Copy(data, bytes, 0, bytes.Length); return bytes;
            }
            finally { if (previous != 0 && previous != -1) SelectObject(memory, previous); if (bitmap != 0) DeleteObject(bitmap); DeleteDC(memory); }
        }
        [DllImport("gdi32", SetLastError = true)] private static extern nint CreateCompatibleDC(nint screen);
        [DllImport("gdi32", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32", SetLastError = true)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32")] private static extern bool DeleteObject(nint value);
        [DllImport("gdi32")] private static extern bool DeleteDC(nint dc);
        [DllImport("gdi32", SetLastError = true)] private static extern bool GdiFlush();
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [DllImport("user32", SetLastError = true)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32", SetLastError = true)] internal static extern bool ClientToScreen(nint window, ref Point point);
        [DllImport("user32")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32")] internal static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32", SetLastError = true)] internal static extern nint GetDC(nint window);
        [DllImport("user32")] internal static extern int ReleaseDC(nint window, nint dc);
        [DllImport("gdi32")] internal static extern uint GetPixel(nint dc, int x, int y);
        [DllImport("dwmapi")] internal static extern int DwmFlush();
    }
}
