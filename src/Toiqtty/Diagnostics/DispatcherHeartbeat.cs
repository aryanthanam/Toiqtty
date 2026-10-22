using Microsoft.UI.Dispatching;

namespace Toiqtty;

/// <summary>Measures the largest gap between ticks of a fast dispatcher timer: how long the
/// UI thread went without running queued work. Create, use and stop on the UI thread.</summary>
internal sealed class DispatcherHeartbeat
{
    private readonly DispatcherQueueTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _last, _maxGap;
    internal DispatcherHeartbeat(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(5);
        _timer.Tick += (_, _) => Beat();
        _timer.Start();
    }
    private void Beat()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        _maxGap = Math.Max(_maxGap, now - _last); _last = now;
    }
    /// <summary>Stops the timer and returns the largest gap in milliseconds, including the tail.</summary>
    internal double Stop() { Beat(); _timer.Stop(); return _maxGap; }
}
