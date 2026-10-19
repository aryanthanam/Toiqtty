using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Imaging;
using Oroterm.Models;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Oroterm;

public sealed partial class MainWindow
{
    // Move this test window itself; never inject desktop input or alter display settings.
    private async Task RunDpiSmoke()
    {
        _metadataTimer.Stop();
        var checks = new List<string>();
        var visits = new List<object>();
        var scales = new List<double>();
        string? error = null;
        DisplayArea[] displays = [];
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        async Task Wait(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(12);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("DPI fixture did not settle.");
                await Task.Delay(50);
            }
        }
        try
        {
            File.WriteAllText(Path.Combine(SessionStore.Folder, "dpi-started.txt"), "Enumerating physical displays.");
            var displayList = DisplayArea.FindAll();
            // The WinRT vector supports indexed access; its IEnumerable projection
            // can fail QueryInterface on this Windows App SDK build.
            displays = Enumerable.Range(0, displayList.Count).Select(i => displayList[i]).OrderBy(d => d.WorkArea.X).ToArray();
            Check(displays.Length >= 2, "At least two physical displays are connected");
            var initial = _selected!;
            string client = Environment.GetEnvironmentVariable("OROTERM_SMOKE_EXE") ?? throw new InvalidOperationException("DPI fixture executable is required.");
            NewWorkspace($"\"{client}\" --dpi-client", "Physical DPI check");
            RemoveWorkspace(initial);
            if (SidebarColumn.Width.Value > 0) ToggleSidebar();
            Title = "Oroterm — isolated physical DPI check";
            var view = _views.Single();
            var session = _selected!.Root.Leaves().Single().Session;
            await Wait(() => view.RenderedText.Contains("DPI_CHECK") && view.HasImage(901));
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            foreach (int displayIndex in Enumerable.Range(0, displays.Length).Append(0))
            {
                var display = displays[displayIndex];
                var area = display.WorkArea;
                long frames = view.FramesPainted;
                AppWindow.MoveAndResize(new RectInt32(area.X + 30, area.Y + 30,
                    Math.Min(1100, area.Width - 60), Math.Min(800, area.Height - 60)));
                await Wait(() =>
                {
                    var actual = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                    return actual.X == area.X && actual.Y == area.Y && view.FramesPainted > frames
                        && Math.Abs(view.RenderDpi - GetDpiForWindow(handle)) < .01
                        && view.RenderedText.Contains("DPI_CHECK");
                });
                await Task.Delay(500);
                double scale = Root.XamlRoot.RasterizationScale;
                uint windowsDpi = GetDpiForWindow(handle);
                string name = $"visit-{visits.Count + 1}-display-{displayIndex + 1}-{scale * 100:0}pct";
                CheckUnifiedTitleBar(Check, name);
                Check(Math.Abs(scale * 96 - windowsDpi) < .01, name + ": XAML scaling matches actual Windows DPI");
                Check(Math.Abs(view.RenderDpi - windowsDpi) < .01, name + ": Win2D rendering DPI follows the physical display");
                Check(view.PhysicalCellsAreIntegral && view.PhysicalCellSize.Width > 0 && view.PhysicalCellSize.Height > 0,
                    name + ": terminal cells remain aligned to physical pixels");
                Check(view.InputCaretIsAligned, name + ": input control remains anchored to the terminal caret");
                Check(view.PointerGeometryIsAligned, name + ": selection and mouse reporting use the rendered physical cell size");
                Check(await view.CheckRenderedImage(901, Windows.UI.Color.FromArgb(255, 255, 0, 0), Path.Combine(SessionStore.Folder, name + "-image.png")),
                    name + ": retained Kitty image renders the correct red pixels");
                Check(await view.CheckRenderedColorJoins(Path.Combine(SessionStore.Folder, name + "-joins.png")),
                    name + ": colored prompt joins have no seams");
                var document = view.AccessibleText();
                int offset = document.Text.IndexOf("DPI_CHECK", StringComparison.Ordinal);
                Check(offset >= 0, name + ": accessible text retains Unicode fixture output");
                var rectangles = view.AccessibleRectangles(document, offset, offset + 9);
                File.WriteAllText(Path.Combine(SessionStore.Folder, name + "-geometry.json"), JsonSerializer.Serialize(new {
                    scale, windowsDpi, offset, rectangles, document.Columns, document.Rows,
                    windowX=AppWindow.Position.X, windowY=AppWindow.Position.Y, windowWidth=AppWindow.Size.Width, windowHeight=AppWindow.Size.Height
                }, new JsonSerializerOptions { WriteIndented=true }));
                await Capture(name + "-window.png", scale);
                string uiaPath = Path.Combine(SessionStore.Folder, name + "-external-uia.json");
                var uiaStart = new System.Diagnostics.ProcessStartInfo(client) { UseShellExecute=false, CreateNoWindow=true };
                foreach (string argument in new[] { "--dpi-uia", Environment.ProcessId.ToString(), uiaPath,
                    view.PhysicalCellSize.Width.ToString(), view.PhysicalCellSize.Height.ToString() }) uiaStart.ArgumentList.Add(argument);
                using (var uia = System.Diagnostics.Process.Start(uiaStart)!)
                {
                    try { await uia.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (TimeoutException) { if (!uia.HasExited) uia.Kill(); throw; }
                }
                using var uiaReport = JsonDocument.Parse(File.ReadAllText(uiaPath));
                Check(uiaReport.RootElement.GetProperty("passed").GetBoolean(), name + ": external UIA text bounds and screen-point hit testing follow the physical display");
                Check(rectangles.Length == 4 && rectangles[0] >= 0 && rectangles[1] >= 0
                    && rectangles[0] + rectangles[2] <= Root.ActualWidth * scale,
                    name + ": in-process accessibility bounds remain inside the XAML root");
                var cellPoint = new Windows.Foundation.Point(rectangles[0] + view.PhysicalCellSize.Width / 2.0,
                    rectangles[1] + view.PhysicalCellSize.Height / 2.0);
                int cell = view.AccessibleCellAt(cellPoint);
                Check(document.CellOffsets[cell] == offset, name + ": XAML accessibility coordinates map to the correct terminal cell");
                Check(ReferenceEquals(session, _selected.Root.Leaves().Single().Session), name + ": display transition preserves the same shell");
                scales.Add(scale);
                visits.Add(new { name, displayIndex, scale, windowsDpi, canvasDpi = view.RenderDpi,
                    window = new { x = AppWindow.Position.X, y = AppWindow.Position.Y, width = AppWindow.Size.Width, height = AppWindow.Size.Height },
                    viewport = view.ViewportLabel, cellWidth = view.PhysicalCellSize.Width, cellHeight = view.PhysicalCellSize.Height,
                    framesPainted = view.FramesPainted, resourceGenerations = view.ResourceGenerations, rectangles });
            }
            Check(scales.Distinct().Count() >= 2, "Physical display transitions exercised at least two different scaling factors");
            Check(scales[0] == scales[^1], "Returning to the first display restores its original scaling factor");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(SessionStore.Folder, "dpi-smoke.json"), JsonSerializer.Serialize(new {
                version = BuildInfo.Version, passed = error is null, checks, visits, error,
                displays = displays.Select(d => new { workArea = new { x=d.WorkArea.X, y=d.WorkArea.Y, width=d.WorkArea.Width, height=d.WorkArea.Height } }),
                note = "Real monitor moves and Windows DPI; no synthetic DPI override, display-setting changes, desktop input injection, GPU reset, or production-session access. IME candidate selection and physical mouse input are not established by this geometry check."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }

        async Task Capture(string name, double scale)
        {
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(Root);
            var buffer = await bitmap.GetPixelsAsync(); byte[] pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            string path = Path.Combine(SessionStore.Folder, name);
            File.WriteAllBytes(path, []);
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync();
        }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
