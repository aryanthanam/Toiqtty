using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Toiqtty.Models;
using Toiqtty.Terminal;
using Windows.Foundation;

namespace Toiqtty.Controls;

public sealed partial class TerminalView
{
    private readonly ScrollBar _scrollbar = new() { Orientation = Orientation.Vertical, Width = 14,
        Minimum = 0, Maximum = 0, IsEnabled = false, IsTabStop = false, IndicatorMode = ScrollingIndicatorMode.MouseIndicator };
    private readonly Canvas _scrollAnchor = CreateScrollAnchor();
    private readonly MiddleClickScroll _middleScroll = new();
    private readonly WheelScroll _wheelScroll = new(), _scrollbarWheel = new();
    private bool _updatingScrollbar, _scrollbarDragging;
    private ulong? _pendingScrollRow;
    private long _lastScrollbarSend;
    private Point _middleScrollOrigin;
    internal ScrollBar ScrollbarForCheck => _scrollbar;
    internal bool AutoScrollActive => _middleScroll.Active;
    internal TerminalScrollState? ScrollStateForCheck => _snapshot?.Scrollbar;
    internal MiddleClickScroll MiddleScrollForCheck => _middleScroll;

    private static Canvas CreateScrollAnchor()
    {
        // All marks share the same (11, 11) center. Avoid font baselines and
        // fallback glyph metrics in this small, DPI-scaled overlay.
        var anchor = new Canvas { Width = 22, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        anchor.Children.Add(new Ellipse { Width = 22, Height = 22,
            Fill = Chrome.Brush(Chrome.Selected), Stroke = Chrome.Brush(Chrome.Accent), StrokeThickness = 1 });
        var up = new Polygon { Points = new PointCollection { new(0, 4), new(3.5, 0), new(7, 4) }, Fill = Chrome.Brush(Chrome.Text) };
        var down = new Polygon { Points = new PointCollection { new(0, 0), new(7, 0), new(3.5, 4) }, Fill = Chrome.Brush(Chrome.Text) };
        var dot = new Ellipse { Width = 3, Height = 3, Fill = Chrome.Brush(Chrome.Text) };
        Canvas.SetLeft(up, 7.5); Canvas.SetTop(up, 4);
        Canvas.SetLeft(down, 7.5); Canvas.SetTop(down, 14);
        Canvas.SetLeft(dot, 9.5); Canvas.SetTop(dot, 9.5);
        anchor.Children.Add(up); anchor.Children.Add(down); anchor.Children.Add(dot);
        return anchor;
    }

    private void InitializeScrolling(Grid surface)
    {
        // Keep a constant gutter so history appearing does not reflow the shell.
        surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        Grid.SetColumn(_scrollbar, 1);
        surface.Children.Add(_scrollbar); surface.Children.Add(_scrollAnchor);
        AutomationProperties.SetName(_scrollbar, "Terminal scrollback");
        _scrollbar.ValueChanged += (_, e) =>
        {
            if (_disposed || _updatingScrollbar || !_scrollbar.IsEnabled) return;
            StopAutoScroll();
            _pendingScrollRow = (ulong)Math.Clamp(Math.Round(e.NewValue), 0, _scrollbar.Maximum);
            FlushScrollbar(Environment.TickCount64);
        };
        _scrollbar.Scroll += (_, e) =>
        {
            if (_disposed || _updatingScrollbar || !_scrollbar.IsEnabled) return;
            StopAutoScroll();
            _scrollbarDragging = e.ScrollEventType == ScrollEventType.ThumbTrack;
            FlushScrollbar(Environment.TickCount64, !_scrollbarDragging);
            if (e.ScrollEventType == ScrollEventType.EndScroll) FocusTerminal();
        };
        _scrollbar.PointerCaptureLost += (_, _) => { _scrollbarDragging = false; FlushScrollbar(Environment.TickCount64, true); };
        _scrollbar.PointerWheelChanged += (_, e) =>
        {
            StopAutoScroll();
            var point = e.GetCurrentPoint(_scrollbar);
            if (point.Properties.IsHorizontalMouseWheel) return;
            int notches = _scrollbarWheel.Consume(point.Properties.MouseWheelDelta);
            ScrollWheelHistory(notches);
            e.Handled = true;
        };
    }
    internal void RouteWheel(int delta, Point point, bool forceHistory = false, uint modifiers = 0)
    {
        int notches = _wheelScroll.Consume(delta);
        if (notches == 0) return; // Scroll(0) means jump to bottom.
        var settings = _settings.Scrolling;
        int direction = Math.Sign(notches) * (settings.Reverse ? -1 : 1);
        int button = direction > 0 ? 4 : 5;
        if (!forceHistory && ReportMouseAt(point, 0, button, modifiers))
        {
            // The first report also determines whether the app owns the wheel.
            long events = Math.Abs((long)notches) * settings.AppEventsPerNotch;
            for (long i = 1; i < events; i++) ReportMouseAt(point, 0, button, modifiers);
        }
        else ScrollWheelHistory(notches);
    }
    private void ScrollWheelHistory(int notches)
    {
        if (notches == 0) return;
        var settings = _settings.Scrolling;
        _session?.Scroll(notches * settings.LinesPerNotch * (settings.Reverse ? 1 : -1));
        ClearSelection(); _canvas.Invalidate();
    }
    private void UpdateScrollbar(TerminalScrollState? state)
    {
        _updatingScrollbar = true;
        try
        {
            _scrollbar.IsEnabled = state is { Maximum: > 0 };
            _scrollbar.Opacity = _scrollbar.IsEnabled ? 1 : .25;
            _scrollbar.Maximum = state?.Maximum ?? 0;
            _scrollbar.ViewportSize = state?.Visible ?? _rows;
            _scrollbar.SmallChange = 1;
            _scrollbar.LargeChange = Math.Max(1, _scrollbar.ViewportSize - 1);
            if (!_scrollbarDragging && _pendingScrollRow is null)
                _scrollbar.Value = Math.Min(state?.Offset ?? 0, _scrollbar.Maximum);
        }
        finally { _updatingScrollbar = false; }
    }
    private void FlushScrollbar(long now, bool force = false)
    {
        if (_pendingScrollRow is not { } row || (!force && now - _lastScrollbarSend < 33)) return;
        _pendingScrollRow = null; _lastScrollbarSend = now;
        _session?.ScrollTo(row); ClearSelection(); _canvas.Invalidate();
    }
    internal void BeginMiddleScroll(Point point)
    {
        _pendingScrollRow = null;
        _selecting = false;
        _middleScrollOrigin = point;
        _middleScroll.Begin(point.Y, Environment.TickCount64);
        ValidationObserved?.Invoke("autoscroll-start", point.ToString());
        _scrollAnchor.Margin = new Thickness(Math.Max(0, point.X - 11), Math.Max(0, point.Y - 11), 0, 0);
        _scrollAnchor.Visibility = Visibility.Visible;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        ToolTipService.SetToolTip(_canvas, null);
    }
    private bool ReleaseMiddleScroll(PointerRoutedEventArgs e)
    {
        if (!_middleScroll.Active) return false;
        ReleaseMiddleScrollAt(e.GetCurrentPoint(_canvas).Position);
        // A click latches autoscroll within this pane. A held drag ends on release.
        if (!_middleScroll.Latched) _canvas.ReleasePointerCapture(e.Pointer);
        e.Handled = true; return true;
    }
    internal void ReleaseMiddleScrollAt(Point point)
    {
        _middleScroll.Move(point.Y); _middleScroll.Release();
        if (!_middleScroll.Active) StopAutoScroll("middle-release");
    }
    internal void MiddleScrollCaptureLost(bool middlePressed)
    {
        if (!_middleScroll.Active || _middleScroll.Latched) return;
        // WinUI can revoke capture on button-up before PointerReleased arrives.
        // Process that as a release, rather than cancelling a stationary click.
        if (!middlePressed)
        {
            _middleScroll.Release();
            if (!_middleScroll.Active) StopAutoScroll("drag-release-capture");
        }
        else StopAutoScroll("capture-lost-while-held");
    }
    internal void MoveMiddleScroll(Point point)
    {
        if (!_middleScroll.Active) return;
        MiddleScrollPointerExited(point);
        if (_middleScroll.Active) _middleScroll.Move(point.Y);
    }
    internal void MiddleScrollPointerExited(Point point)
    {
        // Capture transitions can raise PointerExited inside the canvas.
        if (_middleScroll.Latched && (point.X < 0 || point.Y < 0 || point.X >= _canvas.ActualWidth || point.Y >= _canvas.ActualHeight))
            StopAutoScroll("pointer-outside-pane");
    }
    internal bool MiddleScrollKeyDown(uint key)
    {
        if (!_middleScroll.Active || MiddleClickScroll.IsModifierKey(key)) return false;
        StopAutoScroll("key-down:" + key);
        return key == 27;
    }
    private void CheckMiddleScrollFocus()
    {
        if (!_middleScroll.Active) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_middleScroll.Active || !IsLoaded || XamlRoot is null) return;
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (focused is null) { FocusTerminal(); return; }
            for (var ancestor = focused; ancestor is not null; ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor))
                if (ReferenceEquals(ancestor, this)) return;
            StopAutoScroll("focus-left-pane");
        });
    }
    internal void StopAutoScroll(string reason = "explicit")
    {
        bool active = _middleScroll.Active;
        if (!active && _scrollAnchor.Visibility != Visibility.Visible) return;
        ValidationObserved?.Invoke("autoscroll-stop", reason);
        _middleScroll.Stop();
        _scrollAnchor.Visibility = Visibility.Collapsed;
        if (active) _canvas.ReleasePointerCaptures();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }
    internal void TickScrolling(long now)
    {
        FlushScrollbar(now);
        int delta = _middleScroll.Tick(now, _cellHeight);
        // Scroll(0) means jump to the bottom, so never send a stationary tick.
        if (delta == 0) return;
        RouteAutoscroll(delta);
        _canvas.Invalidate();
    }
    internal void RouteAutoscroll(int delta)
    {
        if (delta == 0) return;
        int direction = Math.Sign(delta), button = direction < 0 ? 4 : 5;
        // Let Ghostty encode exactly the same wheel reports as a physical wheel.
        // Shift starts the local gesture; it is not forwarded as an app modifier.
        // Keep events at the starting point so the application's scroll region
        // does not change as the pointer moves away from the autoscroll anchor.
        for (long remaining = Math.Abs((long)delta); remaining > 0; remaining--)
        {
            if (ReportMouseAt(_middleScrollOrigin, 0, button, 0)) continue;
            _session?.Scroll((int)(remaining * direction));
            ClearSelection(); return;
        }
    }
}
