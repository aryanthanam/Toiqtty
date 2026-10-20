namespace Toiqtty.Models;

public sealed class ScrollingSettings
{
    public int LinesPerNotch { get; set; } = 3;
    public int AppEventsPerNotch { get; set; } = 1;
    public bool Reverse { get; set; }

    public void Validate()
    {
        if (LinesPerNotch is < 1 or > 100 || AppEventsPerNotch is < 1 or > 100)
            throw new ArgumentException("scrolling speeds must be integers between 1 and 100.");
    }
}

// Pane-owned input remainder; no timer, allocation, or frame-rate dependence.
public sealed class WheelScroll
{
    private long _remainder;
    public int Consume(int delta)
    {
        if (delta == 0) return 0;
        if (Math.Sign(delta) != Math.Sign(_remainder)) _remainder = 0;
        _remainder += delta;
        int notches = (int)(_remainder / 120);
        _remainder %= 120;
        return notches;
    }
    public void Reset() => _remainder = 0;
}
