using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Toiqtty.Controls;
using Windows.Graphics;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private string _windowGeometry = "", _windowBackdrop = "";
    private void ApplyWindowSettings(bool initial)
    {
        var settings = _saved.Settings.Window;
        string geometry = $"{settings.Width}:{settings.Height}:{settings.State}";
        if (initial || _windowGeometry.Length > 0 && geometry != _windowGeometry)
        {
            if (AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped) AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            double scale = Root.XamlRoot?.RasterizationScale ?? 1;
            AppWindow.Resize(new SizeInt32(Math.Min((int)(settings.Width * scale), Math.Max(400, area.Width - 40)),
                Math.Min((int)(settings.Height * scale), Math.Max(300, area.Height - 40))));
            if (settings.State == "fullscreen") AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            else if (settings.State == "maximized" && AppWindow.Presenter is OverlappedPresenter maximized) maximized.Maximize();
        }
        _windowGeometry = geometry;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = settings.AlwaysOnTop;
        if (_windowBackdrop != settings.Backdrop)
        {
            SystemBackdrop = settings.Backdrop switch {
                "mica" => new MicaBackdrop(),
                "mica_alt" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                "acrylic" => new DesktopAcrylicBackdrop(),
                _ => null
            };
            _windowBackdrop = settings.Backdrop;
        }
        ApplyWindowBackground();
    }
    private void ApplyWindowBackground()
    {
        var colors = Chrome.For(ThemeFor(_selected));
        bool translucent = _saved.Settings.Window.Backdrop != "none" && _saved.Settings.Window.BackgroundOpacity < 1;
        Root.Background = translucent ? Chrome.Brush(0) : Chrome.Brush(colors.Background);
        var color = Chrome.Brush(colors.Background).Color;
        color.A = translucent ? (byte)Math.Round(_saved.Settings.Window.BackgroundOpacity * 255) : (byte)255;
        TitleBar.Background = new SolidColorBrush(color);
        var sidebar = Chrome.Brush(ThemeFor(_selected) is null ? Chrome.Sidebar : colors.Background).Color;
        sidebar.A = color.A;
        Sidebar.Background = SidebarHeader.Background = new SolidColorBrush(sidebar);
    }
    private void ToggleFullscreen()
    {
        AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = _saved.Settings.Window.AlwaysOnTop;
        QueueTitleRegions();
    }
}
