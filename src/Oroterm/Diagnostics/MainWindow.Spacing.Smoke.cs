using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Oroterm.Controls;
using Oroterm.Models;
using Oroterm.Terminal;

namespace Oroterm;

public sealed partial class MainWindow
{
    private static async Task CheckBatchedSpacing(Action<bool, string> check)
    {
        using var gpu=new DirectXRenderer(CanvasDevice.GetSharedDevice());
        using var terminal = new GhosttyTerminal(100, 4);
        var bytes = System.Text.Encoding.UTF8.GetBytes("\x1b[?2027h" + new string('a', 90) + "\r\n"
            + "office ffi => === != <= >= <-> abcXYZ012345\r\n"
            + "café 中🙂 é אבג abc 日本語\r\n" + "a" + new string('\u0301', 70));
        terminal.Write(bytes, bytes.Length); var cells = new TerminalCell[400]; terminal.ReadSnapshot(cells);
        long calls = 0, legacyCalls = 0;
        foreach (bool ligatures in new[] { false, true })
        foreach (uint style in new uint[] { 0, 1, 2, 3 })
        {
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1000, 96, 96);
            using var referenceTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1000, 96, 96);
            using var renderer = new TerminalTextRenderer();
            using var reference = new TerminalTextRenderer { DisableSpacingBatchForCheck = true,UseProjectedClusterMetricsForCheck=true };
            using var format = new CanvasTextFormat { FontFamily = TerminalFont.Resolve(TerminalFont.BundledFamily, style), FontSize = 14,
                FontWeight = (style & 1) != 0 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = (style & 2) != 0 ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal, WordWrapping = CanvasWordWrapping.NoWrap };
            for (int i = 0; i < cells.Length; i++) cells[i].Flags = (cells[i].Flags & ~3u) | style;
            using (var draw = gpu.CreateDrawingSession(target))
            {
                draw.Clear(Microsoft.UI.Colors.Transparent);
                for (int row = 0; row < 4; row++) renderer.DrawRow(target, draw, cells, row, 100, 10, 24, _ => format, ligatures, true, null, (_, _, _, _, _, _) => { });
            }
            using (var draw = gpu.CreateDrawingSession(referenceTarget))
            {
                draw.Clear(Microsoft.UI.Colors.Transparent);
                for (int row = 0; row < 4; row++) reference.DrawRow(referenceTarget, draw, cells, row, 100, 10, 24, _ => format, ligatures, true, null, (_, _, _, _, _, _) => { });
            }
            bool equal = target.GetPixelColors().SequenceEqual(referenceTarget.GetPixelColors());
            if (!equal)
            {
                await target.SaveAsync(Path.Combine(SessionStore.Folder, $"spacing-batch-{style}-{ligatures}.png"));
                await referenceTarget.SaveAsync(Path.Combine(SessionStore.Folder, $"spacing-reference-{style}-{ligatures}.png"));
            }
            check(equal, $"batched spacing preserves exact ASCII, ligature, fallback, RTL and long-grapheme pixels for style {style}, ligatures {ligatures}");
            check(renderer.NativeClusterReadsForCheck>0,"Reusable native cluster metrics were exercised for style "+style+", ligatures "+ligatures);
            calls += renderer.SpacingCalls; legacyCalls += reference.SpacingCalls;
        }
        check(calls < legacyCalls, "batched spacing reduces DirectWrite interop calls without changing rendered pixels");
        File.WriteAllText(Path.Combine(SessionStore.Folder, "spacing-batch.json"), System.Text.Json.JsonSerializer.Serialize(new { calls, legacyCalls }));
        using var metricsFormat=new CanvasTextFormat { FontFamily=TerminalFont.Resolve(TerminalFont.BundledFamily,0),FontSize=14 };
        string metricsText="office ffi => 中🙂 é אבג "+new string('a',240);
        using var metricsLayout=new CanvasTextLayout(CanvasDevice.GetSharedDevice(),metricsText,metricsFormat,10000,30);
        var metrics=new TerminalClusterMetrics();
        var actual=metrics.Read(metricsLayout,metricsText.Length).ToArray();
        var expected=metricsLayout.ClusterMetrics;
        check(actual.Length==expected.Length && actual.Select((cluster,index)=>cluster.CharacterCount==expected[index].CharacterCount && cluster.Width==expected[index].Width).All(value=>value),
            "Native cluster count, UTF-16 spans and advances exactly match Win2D projected metrics");
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++) metrics.Read(metricsLayout,metricsText.Length);
        long nativeAllocated=GC.GetAllocatedBytesForCurrentThread()-before;
        before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++) _=metricsLayout.ClusterMetrics;
        long projectedAllocated=GC.GetAllocatedBytesForCurrentThread()-before;
        check(nativeAllocated<projectedAllocated/10,"Reusable cluster reads allocate at least 90% fewer managed bytes than the projected property");
        File.WriteAllText(Path.Combine(SessionStore.Folder,"cluster-metrics.json"),System.Text.Json.JsonSerializer.Serialize(new { iterations=1000,nativeAllocated,projectedAllocated }));
        using var largeLayout=new CanvasTextLayout(CanvasDevice.GetSharedDevice(),new string('W',9000),metricsFormat,100000,30);
        check(metrics.Read(largeLayout,9000).Length==9000 && metrics.RetainedCapacityForCheck<=8192,
            "Unusually long text preserves every cluster without retaining an oversized metrics buffer");
    }
}
