using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Toiqtty.Controls;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task CheckDirectXReadiness(Action<bool, string> check)
    {
        var panel = new Grid { IsHitTestVisible = false };
        Root.Children.Add(panel);
        try
        {
            using (var gpu = new DirectXRenderer(CanvasDevice.GetSharedDevice()))
            {
                gpu.Resize(64, 64, 96, false); gpu.Attach(panel);
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int calls = 0;
                gpu.WaitForFrame(DispatcherQueue, timedOut => { calls++; ready.TrySetResult(!timedOut); });
                gpu.WaitForFrame(DispatcherQueue, _ => throw new InvalidOperationException("A duplicate readiness wait was admitted."));
                check(await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)), "A one-shot OS wait delivers real DXGI readiness without a composition-timer retry");
                check(calls == 1 && gpu.TryAcquireFrame(), "External readiness is acknowledged exactly once before beginning the next native frame");
                using (var draw = gpu.CreateDrawingSession(64, 64)) draw.Clear(Microsoft.UI.Colors.Blue);
                check(gpu.Present(), "A frame admitted by the readiness callback submits successfully");
            }
            bool stale = false;
            var retired = new DirectXRenderer(CanvasDevice.GetSharedDevice()); retired.Resize(64, 64, 96, false);
            retired.WaitForFrame(DispatcherQueue, _ => stale = true);
            retired.Dispose();
            await Task.Delay(100);
            check(!stale, "Disposal cancels readiness delivery and queued callbacks cannot access a retired renderer");
        }
        finally { DirectXRenderer.Detach(panel); Root.Children.Remove(panel); }
    }
}
