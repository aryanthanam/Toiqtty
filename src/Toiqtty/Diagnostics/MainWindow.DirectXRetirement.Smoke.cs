using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private static async Task CheckFinalizedRendererTeardown(Action<bool,string> check)
    {
        int posted=DirectXRenderer.RendererHandle.PostedDestroysForCheck, owned=DirectXRenderer.RendererHandle.OwnerDestroysForCheck;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Abandon() { var leaked=new DirectXRenderer(CanvasDevice.GetSharedDevice()); leaked.GetStats(); }
        for(int i=0;i<4;i++) Abandon();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        for(int i=0;i<100 && DirectXRenderer.RendererHandle.OwnerDestroysForCheck-owned<4;i++) await Task.Delay(20);
        check(DirectXRenderer.RendererHandle.PostedDestroysForCheck-posted>=4 && DirectXRenderer.RendererHandle.OwnerDestroysForCheck-owned>=4,
            "Renderers released by the finalizer are destroyed on their UI thread, never beside it");
        using var after=new DirectXRenderer(CanvasDevice.GetSharedDevice());
        check(after.GetStats().DeviceRemovedReason==0,"The shared device remains usable after finalizer-posted renderer teardown");
    }
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
