using System.Diagnostics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Oroterm.Controls;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private static async Task CheckDirectXConcurrentReadback(Action<bool,string> check)
    {
        var device=CanvasDevice.GetSharedDevice();
        using var gpu=new DirectXRenderer(device);
        using var reference=new CanvasRenderTarget(device,128,64,96);
        using(var draw=reference.CreateDrawingSession())draw.Clear(Microsoft.UI.Colors.Orange);
        using var target=new CanvasRenderTarget(device,512,128,96);
        using var format=new CanvasTextFormat {FontFamily=TerminalFont.Resolve(TerminalFont.BundledFamily,0),FontSize=16};
        using var stop=new CancellationTokenSource();long reads=0;int frames=0;
        var readers=Enumerable.Range(0,2).Select(_=>Task.Run(()=>
        {
            while(!stop.IsCancellationRequested)
            {
                var pixels=reference.GetPixelBytes();
                if(pixels[0]!=0 || pixels[1]!=165 || pixels[2]!=255 || pixels[3]!=255)
                    throw new InvalidOperationException("A parallel Win2D staging readback lost its fixed orange pixels.");
                Interlocked.Increment(ref reads);Thread.Sleep(1);
            }
        })).ToArray();
        var clock=Stopwatch.StartNew();
        try
        {
            while(clock.Elapsed.TotalSeconds<2)
            {
                using var layout=new CanvasTextLayout(device,"parallel_"+frames+" 中🙂",format,512,40);
                using(var draw=gpu.CreateDrawingSession(target))
                {
                    draw.Clear(Microsoft.UI.Colors.Transparent);draw.DrawTextLayout(layout,0,0,Microsoft.UI.Colors.White);
                }
                gpu.ForgetTextLayout(layout);frames++;await Task.Delay(1);
            }
        }
        finally {stop.Cancel();await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));}
        var stats=gpu.GetStats();
        File.WriteAllText(Path.Combine(SessionStore.Folder,"directx-concurrent-readback.json"),JsonSerializer.Serialize(new
        {frames,reads,seconds=clock.Elapsed.TotalSeconds,stats.DebugLayer,stats.DebugErrors,stats.DebugWarnings,stats.InteropWarnings,stats.BackendApi,
            meaning="Two background Win2D staging readers run alongside native D3D12 command execution and 11-on-12 color-atlas rasterization. Diagnostic target copies serialize access to the original Win2D provider."}));
        check(frames>=10 && reads>=10,"Native drawing and parallel Win2D GPU readback complete concurrently on one shared device");
        check(stats.DebugErrors==0 && stats.DebugWarnings==stats.InteropWarnings,"Shared-context concurrency produces no unexpected Direct3D debug warnings or errors");
    }
}
