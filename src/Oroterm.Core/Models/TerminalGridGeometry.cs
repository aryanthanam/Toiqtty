namespace Oroterm.Models;

/// <summary>The displayed grid and its native coordinate system, published together with a frame.</summary>
internal readonly record struct TerminalGridGeometry(
    ushort Columns, ushort Rows, float CellWidth, float CellHeight,
    uint NativeCellWidth, uint NativeCellHeight, double RasterizationScale)
{
    internal int CellAt(double x, double y)
        => Math.Clamp((int)Math.Floor(y / CellHeight), 0, Rows - 1) * Columns
            + Math.Clamp((int)Math.Floor(x / CellWidth), 0, Columns - 1);

    internal (float X, float Y) ToNative(double x, double y)
        => ((float)(x / CellWidth * NativeCellWidth), (float)(y / CellHeight * NativeCellHeight));

    internal (double X, double Y) FromScreen(double x, double y, double originX, double originY)
        => ((x - originX) / RasterizationScale, (y - originY) / RasterizationScale);
}
