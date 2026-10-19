using System.Diagnostics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Oroterm.Controls;
using Oroterm.Models;
using Windows.UI;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task CheckDirectXStress(int seconds,Action<bool,string> check)
    {
        _power.ObserveDisplayState();await Task.Delay(200);
        var panel=new Grid {IsHitTestVisible=false};
        var host=new Grid {Width=480,Height=240,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new(20,150,0,0),Background=new SolidColorBrush(Microsoft.UI.Colors.White),IsHitTestVisible=false};
        var paneVisibility=PaneHost.Visibility;PaneHost.Visibility=Visibility.Collapsed;
        host.Children.Add(panel);Grid.SetRowSpan(host,4);Root.Children.Add(host);
        var compositorCases=new HashSet<(float Dpi,bool Software,bool Transparent)>();var compositorEvidence=new List<object>();
        using var process=Process.GetCurrentProcess();
        process.Refresh();long coldPrivate=process.PrivateMemorySize64;int coldHandles=process.HandleCount;
        long initialPrivate=coldPrivate;int initialHandles=coldHandles;
        long sampledPeakPrivate=coldPrivate;int sampledPeakHandles=coldHandles;
        CanvasDevice? device=null;DirectXRenderer? gpu=null;CanvasTextFormat? format=null;TerminalTextRenderer? metrics=null;
        DirectXBitmap? image=null;var pixels=new byte[840*420*4];
        int frame=0,resizes=0,devices=0,presented=0,blockedPresents=0;ulong evictions=0,maxGpu=0,maxShapes=0;long nextReport=0;
        uint width=0,height=0;float dpi=96;bool transparent=false;
        var clock=Stopwatch.StartNew();
        void DisposeDevice()
        {
            DirectXRenderer.Detach(panel);image?.Dispose();image=null;metrics?.Dispose();metrics=null;format?.Dispose();format=null;
            if(gpu is not null){evictions+=gpu.GetStats().AtlasEvictions;gpu.Dispose();gpu=null;}device?.Dispose();device=null;
        }
        void Save(bool finished)
        {
            process.Refresh();var stats=gpu?.GetStats()??default;
            sampledPeakPrivate=Math.Max(sampledPeakPrivate,process.PrivateMemorySize64);sampledPeakHandles=Math.Max(sampledPeakHandles,process.HandleCount);
            var record=new {timestamp=DateTimeOffset.Now,elapsedSeconds=clock.Elapsed.TotalSeconds,requestedSeconds=seconds,frame,presented,blockedPresents,resizes,deviceTransitions=devices,
                software=devices%2==0,evictions=evictions+stats.AtlasEvictions,maxGpuBytes=maxGpu,maxShapeCacheBytes=maxShapes,
                presentationSurfaceCreations=gpu?.PresentationSurfaceCreations??0,
                coldPrivateBytes=coldPrivate,coldHandles,warmedPrivateBytes=initialPrivate,warmedHandles=initialHandles,
                privateBytes=process.PrivateMemorySize64,privateGrowthBytes=process.PrivateMemorySize64-initialPrivate,handles=process.HandleCount,handleGrowth=process.HandleCount-initialHandles,
                sampledPeakPrivateBytes=sampledPeakPrivate,sampledPeakHandles,managedHeapBytes=GC.GetGCMemoryInfo().HeapSizeBytes,
                gen0Collections=GC.CollectionCount(0),gen1Collections=GC.CollectionCount(1),gen2Collections=GC.CollectionCount(2),
                debugErrors=stats.DebugErrors,debugWarnings=stats.DebugWarnings,interopWarnings=stats.InteropWarnings,backendApi=stats.BackendApi,displayState=_power.DisplayState,compositorCases=compositorCases.Count,finished,
                meaning="Real D3D12 swapchain resize/alpha/DPI/presentation and pre-Present backbuffer readback, native atlas churn, owned hardware/WARP devices. Device replacement is not physical driver removal; Present return is not scanout."};
            string path=Path.Combine(SessionStore.Folder,"directx-stress.json");
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);
            File.AppendAllText(Path.Combine(SessionStore.Folder,"directx-stress-samples.jsonl"),JsonSerializer.Serialize(record)+Environment.NewLine);
        }
        try
        {
            while(clock.Elapsed.TotalSeconds<seconds)
            {
                if(gpu is null || frame%300==0)
                {
                    DisposeDevice();devices++;device=new CanvasDevice(devices%2==0);gpu=new DirectXRenderer(device);
                    format=new CanvasTextFormat {FontFamily=TerminalFont.Resolve(TerminalFont.BundledFamily,0),FontSize=14,WordWrapping=CanvasWordWrapping.NoWrap};
                    metrics=new TerminalTextRenderer();image=gpu.CreateBitmap([0,0,128,128],1,1);width=0;
                }
                if(frame%5==0 || width==0)
                {
                    width=(uint)(frame%2==0?840:560);height=(uint)(frame%3==0?420:280);
                    dpi=new[]{96f,120f,144f,168f,192f}[frame/5%5];transparent=frame/5%2==0;
                    width=(uint)Math.Min(width,Math.Max(64,(Root.ActualWidth-40)*dpi/96));height=(uint)Math.Min(height,Math.Max(64,(Root.ActualHeight-170)*dpi/96));
                    host.Width=width*96/dpi;host.Height=height*96/dpi;Root.UpdateLayout();
                    gpu.Resize(width,height,dpi,transparent);gpu.Attach(panel);resizes++;
                }
                float w=width*96/dpi,h=height*96/dpi;
                using var layout=new CanvasTextLayout(device,"\u202dGPU_"+frame+" 中🙂 café é 👩🏽‍💻\u202c",format,4000,36);
                metrics!.SetLineMetrics(layout,device!,format!,36,0);
                void DrawScene()
                {
                    gpu.BeginFrame();
                    using(var draw=gpu.CreateDrawingSession(w,h))
                    {
                        draw.Clear(transparent?Color.FromArgb(128,0,0,255):Microsoft.UI.Colors.Blue);
                        draw.DrawTextLayout(layout,4,4,Color.FromArgb(255,(byte)frame,(byte)(frame>>2),220));
                        using(draw.CreateLayer(1,new(0,48,w,40)))draw.FillRectangle(0,0,w,h,Microsoft.UI.Colors.Lime);
                        draw.DrawImage(image!,new(w-24,h-24,16,16),new(0,0,1,1),1,CanvasImageInterpolation.NearestNeighbor);
                        draw.FillRectangle(0,0,8,8,Microsoft.UI.Colors.White);
                        draw.FillRectangle(w-8,0,8,8,Microsoft.UI.Colors.Cyan);
                        draw.FillRectangle(0,h-8,8,8,Microsoft.UI.Colors.Yellow);
                        draw.FillRectangle(w-8,h-8,8,8,Microsoft.UI.Colors.Red);
                    }
                }
                DrawScene();
                gpu.ReadBackbufferForCheck(pixels);
                int index=checked((int)((height-2)*width+width-2)*4);
                if(pixels[index]!=0 || pixels[index+1]!=0 || pixels[index+2]!=255 || pixels[index+3]!=255)throw new InvalidOperationException("Native swapchain backbuffer readback lost the corner marker after resizing.");
                int background=checked((int)(2*width+width/2)*4);
                byte expected=transparent?(byte)128:(byte)255;
                if(pixels[background]!=expected || pixels[background+1]!=0 || pixels[background+2]!=0 || pixels[background+3]!=(transparent?128:255))throw new InvalidOperationException("Native swapchain alpha/DPI resize did not preserve premultiplied background pixels.");
                if(gpu.Present())
                {
                    presented++;
                    if(compositorCases.Add((dpi,devices%2==0,transparent)))
                    {
                        await CheckDirectXCompositor(panel,w,h,dpi,transparent,compositorEvidence,check);
                        if(compositorCases.Count<=2)
                        {
                            panel.Opacity=.5;
                            try{gpu.Attach(panel);DrawScene();gpu.Present();await CheckDirectXCompositor(panel,w,h,dpi,transparent,compositorEvidence,check);}
                            finally{panel.Opacity=1;gpu.Attach(panel);DrawScene();gpu.Present();}
                        }
                    }
                }
                else blockedPresents++;
                if(gpu.PresentationSurfaceCreations>2)throw new InvalidOperationException("Pane presentation objects were recreated during alpha, opacity or DPI changes.");
                var stats=gpu.GetStats();maxGpu=Math.Max(maxGpu,stats.TextureBytes+stats.VertexBufferBytes);maxShapes=Math.Max(maxShapes,stats.ShapeCacheBytes);
                if(stats.AtlasEntries>2048 || stats.AtlasBytes>2UL*2048*2048*4 || stats.ShapeCacheBytes>8UL*1024*1024 || stats.DebugErrors>0 || stats.DebugWarnings!=stats.InteropWarnings)throw new InvalidOperationException("Native cache bounds or Direct3D debug validation failed during stress: "+gpu.DebugMessagesForCheck());
                if(frame==0){process.Refresh();initialPrivate=process.PrivateMemorySize64;initialHandles=process.HandleCount;}
                frame++;
                if(clock.ElapsedMilliseconds>=nextReport){Save(false);nextReport=clock.ElapsedMilliseconds+2000;}
                await Task.Delay(15);
            }
            Save(true);process.Refresh();
            check(clock.Elapsed.TotalSeconds>=seconds && frame>seconds*3,"Native swapchain stress completed its requested duration with real backbuffer pixel readbacks");
            check(resizes>seconds/3 && devices>=2,"Repeated native buffer, alpha and DPI changes preserve pixels across owned hardware and WARP device replacement");
            check(compositorCases.Count==20,"Native GPU matrix exercises opaque and translucent surfaces at all five rendering DPIs on hardware and WARP; directx-compositor.json distinguishes actual onscreen checks from explicit skips");
            check(process.PrivateMemorySize64-initialPrivate<256L*1024*1024 && process.HandleCount-initialHandles<150,"Native swapchain/atlas stress finishes within 256MiB private-memory and 150-handle growth guardrails; samples record transient peaks");
        }
        finally{DisposeDevice();Root.Children.Remove(host);PaneHost.Visibility=paneVisibility;}
    }
}
