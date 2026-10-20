namespace Toiqtty.Models;

/// <summary>Browser-style vertical autoscroll; distances are in logical pixels.</summary>
public sealed class MiddleClickScroll
{
    public static bool IsModifierKey(uint key) => key is 16 or 17 or 18 or 91 or 92 or >= 160 and <= 165;
    public const double DeadZone = 8;
    public bool Active { get; private set; }
    public bool Latched { get; private set; }
    public bool Dragged { get; private set; }
    private double _anchor, _position, _fraction;
    private long _lastTick;

    public void Begin(double y, long now)
    {
        Active = true; Latched = Dragged = false;
        _anchor = _position = y; _fraction = 0; _lastTick = now;
    }
    public void Move(double y)
    {
        _position = y;
        if (Math.Abs(y - _anchor) > DeadZone) Dragged = true;
    }
    public void Release()
    {
        if (!Active) return;
        if (Dragged) Stop(); else Latched = true;
    }
    public int Tick(long now, double lineHeight)
    {
        if (!Active) return 0;
        double seconds = Math.Clamp((now - _lastTick) / 1000.0, 0, .1);
        _lastTick = now;
        double distance = _position - _anchor;
        if (Math.Abs(distance) <= DeadZone) { _fraction = 0; return 0; }
        double speed = Math.Sign(distance) * Math.Clamp(Math.Pow((Math.Abs(distance) - DeadZone) / Math.Max(1, lineHeight), 1.4) * 12, 1, 180);
        // A direction change must not carry the previous direction's remainder.
        if (Math.Sign(_fraction) != Math.Sign(speed)) _fraction = 0;
        _fraction += speed * seconds;
        int rows = (int)_fraction;
        _fraction -= rows;
        return rows;
    }
    public void Stop() { Active = Latched = Dragged = false; _fraction = 0; }
}
