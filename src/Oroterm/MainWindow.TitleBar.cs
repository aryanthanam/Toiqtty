using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace Oroterm;

public sealed partial class MainWindow
{
    private InputNonClientPointerSource? _titleInput;
    private XamlRoot? _titleRoot;
    private bool _titleRegionsPending;

    private void InitializeTitleBar()
    {
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        _titleInput = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        TitleBar.Loaded += (_, _) =>
        {
            if (!ReferenceEquals(_titleRoot, TitleBar.XamlRoot))
            {
                if (_titleRoot is not null) _titleRoot.Changed -= TitleRootChanged;
                _titleRoot = TitleBar.XamlRoot;
                _titleRoot.Changed += TitleRootChanged;
            }
            QueueTitleRegions();
        };
        TitleBar.SizeChanged += (_, _) => QueueTitleRegions();
        TabStrip.SizeChanged += (_, _) => QueueTitleRegions();
        TabScroller.SizeChanged += (_, _) => UpdateTabOverflow();
        TabScroller.ViewChanged += (_, _) => UpdateTabOverflow();
        Root.SizeChanged+=(_,_)=>UpdateSidebarWidth();
        AppWindow.Changed += TitleWindowChanged;
        Closed += (_, _) =>
        {
            AppWindow.Changed -= TitleWindowChanged;
            if (_titleRoot is not null) _titleRoot.Changed -= TitleRootChanged;
        };
    }

    private void TitleRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueTitleRegions();
    private void TitleWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => QueueTitleRegions();
    private void QueueTitleRegions()
    {
        if (_closed || _closing || _titleRegionsPending) return;
        _titleRegionsPending = DispatcherQueue.TryEnqueue(() =>
        {
            _titleRegionsPending = false;
            if (!_closed && !_closing) UpdateTitleRegions();
        });
    }

    private void UpdateTitleRegions()
    {
        if (_closed || _closing || _titleRoot is null || _titleInput is null
            || !double.IsFinite(TitleBar.ActualWidth) || TitleBar.ActualWidth <= 0) return;
        double scale = _titleRoot.RasterizationScale;
        if (!UpdateCaptionInset(scale, AppWindow.TitleBar.RightInset)) return;
        var regions = new List<RectInt32>();
        var titleBounds = Bounds(TitleBar);
        Add(TabStrip, Clip(Bounds(TabScroller), titleBounds));
        Add(TerminalActions, titleBounds);
        Add(TabOverflowButton,titleBounds);
        Add(SidebarRestoreButton, titleBounds);
        if (SidebarHeader.Visibility == Visibility.Visible)
        {
            Add(NewSessionButton, titleBounds);
            Add(SidebarToggleButton, titleBounds);
        }
        // WinUI owns caption dragging; only controls receive client input.
        // https://learn.microsoft.com/windows/apps/develop/title-bar
        _titleInput.SetRegionRects(NonClientRegionKind.Passthrough, regions.ToArray());

        Rect Bounds(FrameworkElement element) => element.TransformToVisual(Root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        void Add(FrameworkElement element, Rect clip)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            Rect bounds = Clip(Bounds(element), clip);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            int left = (int)Math.Floor(bounds.X * scale), top = (int)Math.Floor(bounds.Y * scale);
            int right = (int)Math.Ceiling(bounds.Right * scale), bottom = (int)Math.Ceiling(bounds.Bottom * scale);
            regions.Add(new RectInt32(left, top, right - left, bottom - top));
        }
        static Rect Clip(Rect bounds, Rect clip)
        {
            double left = Math.Max(bounds.Left, clip.Left), top = Math.Max(bounds.Top, clip.Top);
            return new Rect(left, top, Math.Max(0, Math.Min(bounds.Right, clip.Right) - left), Math.Max(0, Math.Min(bounds.Bottom, clip.Bottom) - top));
        }
    }

    private bool UpdateCaptionInset(double scale, double physicalInset)
    {
        // Native title-bar geometry can be unavailable during window/display
        // transitions. Keep the last valid layout until the next change event;
        // invalid GridLength values escape the dispatcher as a fatal HRESULT.
        if (!double.IsFinite(scale) || scale <= 0
            || !double.IsFinite(physicalInset) || physicalInset < 0) return false;
        double inset = physicalInset / scale;
        if (!double.IsFinite(inset)) return false;
        if (Math.Abs(CaptionInsetColumn.Width.Value - inset) > .01)
        {
            CaptionInsetColumn.Width = new GridLength(inset);
            QueueTitleRegions();
        }
        return true;
    }
}
