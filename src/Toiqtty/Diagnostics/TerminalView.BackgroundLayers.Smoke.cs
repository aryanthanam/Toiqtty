using Toiqtty.Terminal;

namespace Toiqtty.Controls;

public sealed partial class TerminalView
{
    internal void CheckBackgroundLayersForCheck(Action<bool, string> check)
    {
        if (_snapshot is null || GridRows < 4 || GridColumns < 12) throw new InvalidOperationException("Background checks require an accepted visible grid.");
        var previousCells = _cells; var previousFrame = _frame; var previousSnapshot = _snapshot;
        bool previousHold = HoldSnapshotsForCheck, previousEager = _eagerBackgroundForCheck;
        var reference = new byte[6][];
        HoldSnapshotsForCheck = true;
        try
        {
            foreach (bool eager in new[] { true, false })
            {
                SetEagerBackgroundForCheck(eager); ResetLayers();
                _cells = new TerminalCell[GridColumns * GridRows]; _frame.CursorVisible = 0;
                for (int index = 0; index < _cells.Length; index++) _cells[index] = new() { Text = "x", Length = 1, Width = 1, Foreground = _frame.Foreground, Background = _frame.Background };
                _snapshot = new(_frame, [], []);
                for (int state = 0; state < 6; state++)
                {
                    switch (state)
                    {
                        case 1: Paint(1, 0xffc04060, false); Paint(2, _frame.Background, true); break;
                        case 2: Paint(1, _frame.Background, false); break;
                        case 3: Paint(2, _frame.Background, false); break;
                        case 4: Paint(1, 0xff307090, false); break;
                        case 5: Paint(1, _frame.Background, false); break;
                    }
                    using var target = CreateRenderTargetForCheck(); RenderFrameForCheck(target);
                    byte[] pixels = target.GetPixelBytes();
                    if (eager) reference[state] = pixels;
                    else
                    {
                        check(pixels.SequenceEqual(reference[state]), $"Lazy background layers preserve exact incremental color/selection/erase pixels, state {state}, {RenderDpi} DPI");
                        check(state == 0 ? !BackgroundLayerAllocatedForCheck : BackgroundLayerAllocatedForCheck,
                            state == 0 ? "A plain terminal allocates no background layer" : "A used background layer remains owned through later erases and recoloring");
                        check(_backgroundRowCount == (state is 1 ? 2 : state is 2 or 4 ? 1 : 0), "Background visibility tracks retained nonempty rows after partial updates");
                    }
                    void Paint(int row, uint background, bool selected)
                    {
                        for (int column = 2; column < 10; column++)
                        {
                            ref var cell = ref _cells[row * GridColumns + column]; cell.Background = background; cell.Flags = selected ? 64u : 0;
                        }
                        _redrawRows[row] = true;
                    }
                }
            }
        }
        finally
        {
            _cells = previousCells; _frame = previousFrame; _snapshot = previousSnapshot; HoldSnapshotsForCheck = previousHold;
            _eagerBackgroundForCheck = previousEager; ResetLayers(); _accessibleText = null; _canvas.Invalidate();
        }
    }
}
