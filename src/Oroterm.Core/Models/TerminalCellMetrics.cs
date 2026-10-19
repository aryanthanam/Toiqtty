namespace Oroterm.Models;

internal static class TerminalCellMetrics
{
    // Rendering already snaps each cell to an integral physical pixel size.
    // Recover that integer with Round: Ceil can turn a float round-trip such
    // as 37 / 1.75 * 1.75 = 37.000002 into 38 and shift pointer hit testing.
    internal static uint PhysicalSize(float logicalSize, double rasterizationScale)
        => (uint)Math.Max(1, Math.Round(logicalSize * rasterizationScale));
}
