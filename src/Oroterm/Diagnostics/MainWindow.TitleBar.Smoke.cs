using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task CheckCaptionInsetRecovery(Action<bool, string> check)
    {
        bool rejected = false;
        try { _ = new GridLength(-1); }
        catch (ArgumentException) { rejected = true; }
        check(rejected, "WinUI reproduces the dumped invalid caption-size exception");
        UpdateTitleRegions();
        double original = CaptionInsetColumn.Width.Value;
        foreach (var (scale, inset) in new[] {
            (1d, -1d), (0d, 138d), (-1d, 138d), (double.NaN, 138d),
            (double.PositiveInfinity, 138d), (1d, double.NaN),
            (1d, double.PositiveInfinity), (double.Epsilon, 138d) })
        {
            var done = new TaskCompletionSource<bool>();
            if (!DispatcherQueue.TryEnqueue(() => done.SetResult(UpdateCaptionInset(scale, inset))))
                throw new InvalidOperationException("Title-bar regression callback could not be queued.");
            check(!await done.Task && CaptionInsetColumn.Width.Value == original,
                $"Queued invalid caption geometry ({scale}, {inset}) preserves the last valid layout");
        }
        check(UpdateCaptionInset(1, 138) && CaptionInsetColumn.Width.Value == 138
            && UpdateCaptionInset(1.75, 138) && Math.Abs(CaptionInsetColumn.Width.Value - 138 / 1.75) < .001
            && UpdateCaptionInset(1, 0) && CaptionInsetColumn.Width.Value == 0,
            "Caption layout recovers at 96/168 DPI and accepts a valid zero inset");
        UpdateTitleRegions();
    }

    private void CheckUnifiedTitleBar(Action<bool, string> check, string context)
    {
        UpdateTitleRegions();
        check(Math.Abs(TitleBar.ActualHeight - 32) < .01
            && PaneHost.TransformToVisual(Root).TransformPoint(new()).Y <= 33,
            context + ": tabs occupy one 32-DIP row directly above the terminal");
        check(Math.Abs(TitleSidebarColumn.Width.Value - SidebarColumn.Width.Value) < .01,
            context + ": sidebar header and session list stay aligned");
        check(TitleHitTest(NewTabButton) == 1 && TitleHitTest(ShellButton) == 1,
            context + ": title-bar actions are registered as interactive Windows regions");
        var activeTab = TabStrip.Items.OfType<Microsoft.UI.Xaml.Controls.ListViewItem>().FirstOrDefault();
        if (activeTab is not null)
            check(TitleHitTest(activeTab) == 1, context + ": tabs are registered as interactive Windows regions");
        check(TitleHitTest(TitleDragArea) == 2, context + ": reserved space remains a native window drag region");
        check(CaptionInsetColumn.Width.Value > 0
            && TerminalActions.TransformToVisual(Root).TransformPoint(new()).X + TerminalActions.ActualWidth
                <= Root.ActualWidth - CaptionInsetColumn.Width.Value,
            context + ": toolbar controls do not overlap Windows caption buttons");
        if (SidebarRestoreButton.Visibility == Visibility.Visible)
            check(TitleHitTest(SidebarRestoreButton) == 1, context + ": sidebar restore remains clickable in the title bar");
        else
            check(TitleHitTest(NewSessionButton) == 1 && TitleHitTest(SidebarToggleButton) == 1,
                context + ": session actions remain clickable in the title bar");
    }

    private int TitleHitTest(FrameworkElement element)
    {
        var logical = element.TransformToVisual(Root).TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        double scale = Root.XamlRoot.RasterizationScale;
        double x = logical.X * scale, y = logical.Y * scale;
        if (Contains(NonClientRegionKind.Passthrough)) return 1;
        if (Contains(NonClientRegionKind.Caption)) return 2;
        return 0;
        bool Contains(NonClientRegionKind kind)
        {
            // Query the real non-client input source, including WinUI's caption
            // rectangles. The parent HWND hit-test does not model input islands.
            IReadOnlyList<Windows.Graphics.RectInt32> regions = _titleInput!.GetRegionRects(kind);
            for (int i = 0; i < regions.Count; i++)
            {
                var rect = regions[i];
                if (x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height) return true;
            }
            return false;
        }
    }
}
