using Toiqtty.Controls;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task CheckSurfaceScheduling(Action<bool, string> check)
    {
        using var surface = new TerminalCanvasSurface { Width = 64, Height = 64 };
        surface.Draw += draw => draw.Clear(Microsoft.UI.Colors.Blue);
        try
        {
            await Present(() => { Root.Children.Add(surface); Root.UpdateLayout(); });
            await Task.Delay(50);
            long before = surface.PresentationForCheck.SubmittedFrames, immediate = surface.ImmediatePresentsForCheck, bulk = surface.BulkPresentsForCheck;
            await Present(() =>
            {
                surface.Invalidate(false);
                check(!surface.CompositionPendingForCheck, "Opaque bulk work queues directly without subscribing to a XAML composition tick");
                surface.Invalidate();
            });
            await Task.Delay(50);
            check(surface.PresentationForCheck.SubmittedFrames == before + 1 && surface.ImmediatePresentsForCheck == immediate + 1
                && surface.BulkPresentsForCheck == bulk, "Interactive work promotes a queued bulk draw exactly once and invalidates the stale callback");
            before = surface.PresentationForCheck.SubmittedFrames; surface.Invalidate(false); surface.Suspend();
            await Task.Delay(50);
            check(surface.PresentationForCheck.SubmittedFrames == before, "Suspension cancels an already queued bulk draw");
            await Present(surface.Resume);
            check(surface.PresentationForCheck.SubmittedFrames == before + 1, "Resuming a surface admits one fresh frame after suspension");
            await Present(() => surface.Transparent = true);
            before = surface.PresentationForCheck.SubmittedFrames; surface.Invalidate(false);
            check(surface.CompositionPendingForCheck, "Translucent bulk work retains composition cadence because drawing surfaces have no DXGI readiness event");
            surface.Suspend();
            check(!surface.CompositionPendingForCheck, "Suspending a translucent surface removes its pending composition subscription");
            await Task.Delay(50);
            check(surface.PresentationForCheck.SubmittedFrames == before, "A cancelled translucent callback cannot present after suspension");
        }
        finally { surface.Dispose(); Root.Children.Remove(surface); }
        async Task Present(Action request)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Presented() => done.TrySetResult();
            surface.Presented += Presented;
            try { request(); await done.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { surface.Presented -= Presented; }
        }
    }
}
