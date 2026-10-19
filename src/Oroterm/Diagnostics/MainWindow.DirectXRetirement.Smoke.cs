using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Oroterm.Controls;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private static void CheckDirectXLayoutRetirement(Action<bool,string> check)
    {
        var device=CanvasDevice.GetSharedDevice();
        using var gpu=new DirectXRenderer(device);
        using var target=new CanvasRenderTarget(device,160,40,96);
        using var format=new CanvasTextFormat {FontFamily=TerminalFont.Resolve(TerminalFont.BundledFamily,0),FontSize=16};
        using var layout=new CanvasTextLayout(device,"🙂 retire",format,160,40);
        void Draw()
        {
            using var draw=gpu.CreateDrawingSession(target);
            draw.Clear(Microsoft.UI.Colors.Transparent);draw.DrawTextLayout(layout,0,0,Microsoft.UI.Colors.White);
        }
        Draw();var before=target.GetPixelBytes();var stats=gpu.GetStats();
        check(stats.ShapeCacheBytes>0 && stats.AtlasEntries>0,"A color layout populates native shape and whole-layout atlas ownership");
        gpu.ForgetTextLayout(layout);stats=gpu.GetStats();
        check(stats.ShapeCacheBytes==0 && stats.AtlasEntries==0,"Retiring a managed layout releases its native shape and color-atlas owner immediately");
        Draw();
        check(before.SequenceEqual(target.GetPixelBytes()),"A retired layout can be cached again without changing native pixels");
        gpu.ForgetTextLayout(layout);
        using var ascii=new CanvasTextLayout(device,"retire_ascii",format,160,40);
        void DrawAscii()
        {
            using var draw=gpu.CreateDrawingSession(target);
            draw.Clear(Microsoft.UI.Colors.Transparent);draw.DrawTextLayout(ascii,0,0,Microsoft.UI.Colors.White);
        }
        DrawAscii();before=target.GetPixelBytes();uint glyphEntries=gpu.GetStats().AtlasEntries;
        gpu.ForgetTextLayout(ascii);stats=gpu.GetStats();
        check(stats.ShapeCacheBytes==0 && stats.AtlasEntries==glyphEntries && glyphEntries>0,
            "Retiring a monochrome layout preserves reusable glyph masks while releasing its shaped-run owner");
        DrawAscii();check(before.SequenceEqual(target.GetPixelBytes()),"Glyph-mask reuse remains pixel-identical after layout retirement");
        gpu.SetTextModeForCheck(true);DrawAscii();gpu.SetTextModeForCheck(false);gpu.ForgetTextLayout(ascii);
        check(gpu.GetStats().ShapeCacheBytes==0 && gpu.GetStats().AtlasEntries==glyphEntries,
            "Retirement removes forced whole-layout diagnostic entries after returning to glyph mode");
    }
}
