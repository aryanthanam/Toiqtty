using Microsoft.Graphics.Canvas;
using Oroterm.Controls;

namespace Oroterm;

public sealed partial class MainWindow
{
    private static void CheckDirectX12Ownership(Action<bool,string> check)
    {
        var provider=CanvasDevice.GetSharedDevice();
        using var producer=new DirectXRenderer(provider);
        using var consumer=new DirectXRenderer(provider);
        consumer.Resize(64,64,96,false);
        // These targets never pass through the CanvasRenderTarget diagnostic
        // bridge. Disposing their managed owners must not retire descriptors
        // or resources that an already-submitted D3D12 list can still read.
        for(int iteration=0;iteration<48;iteration++)
        {
            using(var layer=producer.CreateRenderTarget(64,64,96))
            using(var image=producer.CreateBitmap([0,255,0,255],1,1))
            {
                using(var draw=layer.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.Red);
                    draw.DrawImage(image,new(0,0,64,64),new(0,0,1,1),1,CanvasImageInterpolation.NearestNeighbor);
                }
                using(var draw=consumer.CreateDrawingSession(64,64))
                {
                    draw.Clear(Microsoft.UI.Colors.Blue);
                    draw.DrawImage(layer);
                }
            }
        }
        var pixels=new byte[64*64*4];consumer.ReadBackbufferForCheck(pixels);
        check(Enumerable.Range(0,64*64).All(i=>pixels[i*4]==0 && pixels[i*4+1]==255 && pixels[i*4+2]==0 && pixels[i*4+3]==255),
            "D3D12 targets and image descriptors remain valid after disposal while submitted producer/consumer draws complete");
        var stats=consumer.GetStats();
        check(stats.BackendApi==12 && stats.Submissions>=96 && stats.DebugErrors==0 && stats.DebugWarnings==stats.InteropWarnings,
            "Two renderers share an ordered native D3D12 queue with independently fenced allocator slots and no validation errors");
        using var retained=producer.CreateBitmap([0,0,255,255],1,1);
        producer.Dispose();
        using(var draw=consumer.CreateDrawingSession(64,64))
        {
            draw.Clear(Microsoft.UI.Colors.Blue);
            draw.DrawImage(retained,new(0,0,64,64),new(0,0,1,1),1,CanvasImageInterpolation.NearestNeighbor);
        }
        consumer.ReadBackbufferForCheck(pixels);
        check(pixels[0]==0 && pixels[1]==0 && pixels[2]==255 && pixels[3]==255,
            "A shared D3D12 texture keeps its device alive after its creating renderer is disposed");
    }
    private static void CheckDirectX12FrameBatch(Action<bool,string> check)
    {
        using var gpu=new DirectXRenderer(CanvasDevice.GetSharedDevice());
        gpu.Resize(64,64,96,false);
        using var background=gpu.CreateRenderTarget(64,64,96);
        using var text=gpu.CreateRenderTarget(64,64,96);
        ulong before=gpu.GetProfile().Lists;
        gpu.BeginFrame();
        using(var draw=background.CreateDrawingSession())draw.Clear(Microsoft.UI.Colors.Red);
        using(var draw=text.CreateDrawingSession())
        {
            draw.Clear(Microsoft.UI.Colors.Transparent);
            draw.FillRectangle(0,0,32,64,Microsoft.UI.Colors.Lime);
        }
        using(var draw=gpu.CreateDrawingSession(64,64))
        {
            draw.Clear(Microsoft.UI.Colors.Blue);draw.DrawImage(background);draw.DrawImage(text);
        }
        check(gpu.GetProfile().Lists==before,"A native frame records row targets and composition without submitting at drawing-session switches");
        var pixels=new byte[64*64*4];gpu.ReadBackbufferForCheck(pixels);
        check(pixels[1]==255 && pixels[0]==0 && pixels[2]==0 && pixels[(63*4)+2]==255 && pixels[(63*4)+1]==0,
            "Batched row targets become visible to the final composite in command-list order");
        gpu.Present();
    }
    private static void CheckDirectX12DescriptorPages(Action<bool,string> check)
    {
        using var gpu=new DirectXRenderer(CanvasDevice.GetSharedDevice());gpu.Resize(64,64,96,false);
        using var green=gpu.CreateBitmap([0,255,0,255],1,1);
        using var blue=gpu.CreateBitmap([255,0,0,255],1,1);
        using var red=gpu.CreateBitmap([0,0,255,255],1,1);
        var images=new List<DirectXBitmap>();
        try
        {
            // Keep more than one page live, then dispose owners while the
            // batched composite still references descriptors from both pages.
            for(int i=0;i<1025;i++)images.Add(gpu.CreateAliasForCheck(i==0?green:blue));
            check(gpu.GetProfile().DescriptorHeaps>=3,"Live sampled textures span multiple pooled descriptor pages");
            gpu.BeginFrame();
            using(var draw=gpu.CreateDrawingSession(64,64))
            {
                draw.Clear(Microsoft.UI.Colors.Red);
                draw.DrawImage(images[0],new(0,0,32,64),new(0,0,1,1),1,CanvasImageInterpolation.NearestNeighbor);
                draw.DrawImage(images[^1],new(32,0,32,64),new(0,0,1,1),1,CanvasImageInterpolation.NearestNeighbor);
            }
            foreach(var image in images)image.Dispose();images.Clear();
            // Aliases allocate no textures and submit no work: descriptor reuse
            // happens before the pending frame executes, making early recycling
            // fail deterministically rather than racing a fast GPU.
            for(int i=0;i<1025;i++)images.Add(gpu.CreateAliasForCheck(red));
            var pixels=new byte[64*64*4];gpu.ReadBackbufferForCheck(pixels);
            check(pixels[0]==0 && pixels[1]==255 && pixels[2]==0 && pixels[63*4]==255 && pixels[63*4+1]==0 && pixels[63*4+2]==0,
                "Descriptor slot reuse after owner disposal preserves in-flight pixels across heap-page switches");
            gpu.Present();gpu.GetStats();
        }
        finally{foreach(var image in images)image.Dispose();gpu.GetStats();}
    }
    private static void CheckDirectX12Lifetimes(Action<bool,string> check)
    {
        var device=CanvasDevice.GetSharedDevice();
        using var gpu=new DirectXRenderer(device);using var second=new DirectXRenderer(device);
        check(gpu.GetLifetimeStats().DevicePipelineBuilds==1 && second.GetLifetimeStats().DevicePipelineBuilds==1,
            "Immutable native pipelines have device lifetime and are initialized once for shared renderers");
        using(var target=new CanvasRenderTarget(device,64,64,96))
        {
            using(var draw=gpu.CreateDrawingSession(target))
            {
                draw.Clear(Microsoft.UI.Colors.Blue);draw.Target!.Dispose();
                var source=gpu.CreateBitmap([0,255,0,255],1,1);
                draw.DrawImage(source,new(0,0,32,64),new(0,0,1,1),1,CanvasImageInterpolation.NearestNeighbor);
                source.Dispose();
                draw.FillRectangle(32,0,32,64,Microsoft.UI.Colors.Red);
            }
            var pixels=target.GetPixelBytes();
            check(pixels[1]==255 && pixels[0]==0 && pixels[2]==0 && pixels[63*4+2]==255 && pixels[63*4]==0,
                "Active target and pending image lifetimes survive owner disposal before flush and drawing-session completion");
        }
        gpu.Resize(64,64,96,false);
        using var image=gpu.CreateBitmap([0,255,0,255],1,1);
        var before=gpu.GetLifetimeStats();gpu.BeginFrame();
        using(var draw=gpu.CreateDrawingSession(64,64))
        {
            draw.Clear(Microsoft.UI.Colors.Blue);
            for(int i=0;i<15000;i++)
            {
                draw.DrawImage(image,new(0,0,32,64),new(0,0,1,1),1,CanvasImageInterpolation.NearestNeighbor);
                draw.FillRectangle(32,0,32,64,Microsoft.UI.Colors.Red);
            }
        }
        var recorded=gpu.GetLifetimeStats();
        image.Dispose();
        check(recorded.PinRequests-before.PinRequests>=60000 && recorded.UniquePins-before.UniquePins<=8 && recorded.UploadBytes<=12*1024*1024UL,
            "Repeated draws retain each texture once per submission across bounded upload-arena rollover");
        var buffer=new byte[64*64*4];gpu.ReadBackbufferForCheck(buffer);gpu.Present();gpu.ReadBackbufferForCheck(buffer);gpu.GetStats();
        var retired=gpu.GetLifetimeStats();
        check(retired.LivePins==0 && retired.SubmissionArenaBytes<=3*16384UL,
            "Completed submission fences release all resource pins and recycle small arena pages");
        System.IO.File.WriteAllText(System.IO.Path.Combine(Oroterm.Models.SessionStore.Folder,"directx-lifetimes.json"),
            System.Text.Json.JsonSerializer.Serialize(new {before,recorded,retired},new System.Text.Json.JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
    }
}
