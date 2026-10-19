using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Oroterm.Browser;
using Oroterm.Models;
using Windows.System;

namespace Oroterm.Controls;

internal sealed class BrowserView : Grid, IDisposable
{
    private PaneNode _pane;
    private readonly BrowserRuntime _runtime;
    private readonly WebView2 _web = new();
    private readonly TextBox _address = new() { PlaceholderText = "Search or enter address", MinWidth = 100, FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGray), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _outline = new() { BorderThickness = new Thickness(0,1,0,0), IsHitTestVisible = false };
    private readonly Button _back, _forward, _reload;
    private readonly Grid _bar;
    private readonly Border _footer;
    private Chrome.Palette _sessionColors = Chrome.For(null);
    private Task? _initializing;
    private bool _disposed, _active;
    private bool _processFailed;
    public event Action<BrowserView>? Activated;
    public event Action<string>? CloseRequested;
    public event Action<string>? NewWindowRequested;
    public event Action? LocationChanged;
    public event Action? ProfileRequested;
    public event Action? RecoveryRequested;
    public event Func<string, CoreWebView2PermissionKind, Task<bool>>? PermissionRequested;
    public string PaneId => _pane.Id;
    public string SourceId => _pane.BrowserSourceId;
    internal WebView2 Web => _web;
    public bool IsActive { get => _active; set { _active = value; _outline.BorderBrush = Chrome.Brush(value ? _sessionColors.Accent : _sessionColors.Border); } }
    internal void SetSessionTheme(SessionTheme? theme)
    {
        _sessionColors = Chrome.For(theme);
        Background = _bar.Background = _footer.Background = _address.Background = Chrome.Brush(_sessionColors.Background);
        _address.Foreground = Chrome.Brush(_sessionColors.Text); _address.BorderBrush = Chrome.Brush(_sessionColors.Border);
        _status.Foreground = Chrome.Brush(_sessionColors.Secondary);
        foreach (var button in _bar.Children.OfType<Button>()) button.Foreground = Chrome.Brush(_sessionColors.Text);
        IsActive = _active;
    }
    public BrowserView(PaneNode pane, BrowserRuntime runtime)
    {
        _pane = pane; _runtime = runtime;
        AutomationProperties.SetAutomationId(this, "Browser-" + pane.Id);
        AutomationProperties.SetName(this, "Browser pane");
        AutomationProperties.SetName(_address, "Browser address");
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        Background=Chrome.Brush(Chrome.Background);
        _address.MinWidth=50; _address.Height=30; _address.Padding=new Thickness(9,4,9,4); _address.CornerRadius=new CornerRadius(2);
        _address.Background=Chrome.Brush(Chrome.Background); _address.BorderBrush=Chrome.Brush(Chrome.Border); _address.BorderThickness=new Thickness(1);
        _status.Foreground=Chrome.Brush(Chrome.Muted);
        var bar = _bar = new Grid { Padding = new Thickness(6,5,6,5), ColumnSpacing = 5, Background = Chrome.Brush(Chrome.Toolbar) };
        bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var navigation = new StackPanel { Orientation = Orientation.Horizontal };
        _back = Button("\uE72B", "Back", () => { if (_web.CoreWebView2?.CanGoBack == true) _web.GoBack(); });
        _forward = Button("\uE72A", "Forward", () => { if (_web.CoreWebView2?.CanGoForward == true) _web.GoForward(); });
        _reload = Button("\uE72C", "Reload", () => { if (_processFailed) RecoveryRequested?.Invoke(); else if (_web.CoreWebView2 is not null) _web.Reload(); else _ = Initialize(); });
        _back.IsEnabled = _forward.IsEnabled = false;
        navigation.Children.Add(_back); navigation.Children.Add(_forward); navigation.Children.Add(_reload);
        bar.Children.Add(navigation); SetColumn(_address, 1); bar.Children.Add(_address);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var more = new Button { Content = "⋯", Padding = new Thickness(8, 2, 8, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(more, "Browser menu");
        var menu = new MenuFlyout();
        var forwardMenu=new MenuFlyoutItem { Text="Forward" }; forwardMenu.Click+=(_,_)=> { if (_web.CoreWebView2?.CanGoForward==true) _web.GoForward(); }; menu.Items.Add(forwardMenu);
        SizeChanged+=(_,_)=> { bool compact=ActualWidth<360; _forward.Visibility=compact?Visibility.Collapsed:Visibility.Visible; forwardMenu.Visibility=compact?Visibility.Visible:Visibility.Collapsed; };
        var profile = new MenuFlyoutItem { Text = "Change browser profile…" }; profile.Click += (_, _) => ProfileRequested?.Invoke(); menu.Items.Add(profile);
        var external = new MenuFlyoutItem { Text = "Open in default browser" }; external.Click += async (_, _) => { if (BrowserAddress.IsAllowed(_pane.BrowserUrl) && _pane.BrowserUrl != "about:blank") await Launcher.LaunchUriAsync(new(_pane.BrowserUrl)); }; menu.Items.Add(external);
        var devtools = new MenuFlyoutItem { Text = "Developer tools" }; devtools.Click += (_, _) => _web.CoreWebView2?.OpenDevToolsWindow(); menu.Items.Add(devtools);
        var clear = new MenuFlyoutItem { Text = "Clear this profile’s cookies" }; clear.Click += (_, _) => { _web.CoreWebView2?.CookieManager.DeleteAllCookies(); _status.Text = "Cookies cleared. Connected sources may sync them again."; }; menu.Items.Add(clear);
        more.Flyout = menu; tools.Children.Add(more);
        tools.Children.Add(Button("\uE8BB", "Close browser pane", () => CloseRequested?.Invoke(PaneId)));
        SetColumn(tools, 2); bar.Children.Add(tools);
        Children.Add(bar); SetRow(_web, 1); Children.Add(_web);
        var footer = _footer = new Border { Padding = new Thickness(9, 4, 9, 4),Background=Chrome.Brush(Chrome.Toolbar), Child = _status }; SetRow(footer, 2); Children.Add(footer);
        SetRowSpan(_outline, 3); Children.Add(_outline);
        _address.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Navigate(_address.Text); e.Handled = true; } };
        GotFocus += (_, _) => Activated?.Invoke(this);
        _web.GotFocus += (_, _) => Activated?.Invoke(this);
        Loaded += async (_, _) => await Initialize();
        IsActive = false;
    }
    private static Button Button(string glyph, string name, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Width = 28,MinWidth=28, Height = 28, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),CornerRadius=new CornerRadius(2),Foreground=Chrome.Brush(Chrome.Secondary) };
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); button.Click += (_, _) => click(); return button;
    }
    // Splitting clones the leaf model while the running WebView remains alive.
    public void Bind(PaneNode pane) => _pane = pane;
    internal Task Initialize() => _initializing ??= InitializeCore();
    private async Task InitializeCore()
    {
        try
        {
            _status.Text = "Starting browser…";
            var environment = await _runtime.Environment();
            if (_disposed) return;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = BrowserAddress.ProfileName(SourceId);
            await _web.EnsureCoreWebView2Async(environment, options);
            if (_disposed) return;
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                if (!BrowserAddress.IsAllowed(args.Uri)) { args.Cancel = true; _status.Text = "Only HTTP and HTTPS pages can open here."; }
                else _status.Text = "Loading…";
            };
            core.SourceChanged += (_, _) =>
            {
                if (BrowserAddress.IsAllowed(core.Source)) { _pane.BrowserUrl = core.Source; _address.Text = core.Source; LocationChanged?.Invoke(); }
            };
            core.HistoryChanged += (_, _) => { _back.IsEnabled = core.CanGoBack; _forward.IsEnabled = core.CanGoForward; };
            core.NavigationCompleted += (_, args) => _status.Text = args.IsSuccess ? ProfileLabel() : "Could not load page: " + args.WebErrorStatus;
            core.NewWindowRequested += (_, args) => { args.Handled = true; if (BrowserAddress.IsAllowed(args.Uri)) NewWindowRequested?.Invoke(args.Uri); };
            core.PermissionRequested += async (_, args) =>
            {
                using var deferral = args.GetDeferral();
                try
                {
                    bool allow = !_disposed && PermissionRequested is not null && await PermissionRequested(args.Uri, args.PermissionKind);
                    args.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                }
                finally { deferral.Complete(); }
            };
            core.ProcessFailed += (_, args) =>
            {
                if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) _processFailed = true;
                _status.Text = "Browser process stopped: " + args.ProcessFailedKind + ". Press Reload to reopen this pane.";
            };
            await _runtime.Register(SourceId, core);
            if (_disposed) return;
            Navigate(_pane.BrowserUrl);
            _status.Text = ProfileLabel();
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            _status.Text = "Browser unavailable: " + ex.Message + " · Install or repair Microsoft Edge WebView2 Runtime.";
            _initializing = null;
        }
    }
    private string ProfileLabel() => SourceId.Length == 0 ? "Oroterm browser profile" :
        "Cookies from " + (_runtime.Bridge?.Sources.FirstOrDefault(s => s.Id == SourceId)?.Name ?? "disconnected browser profile");
    public void Navigate(string address)
    {
        try
        {
            string resolved = BrowserAddress.Resolve(address);
            _pane.BrowserUrl = resolved; _address.Text = resolved;
            if (_web.CoreWebView2 is not null) _web.CoreWebView2.Navigate(resolved);
            LocationChanged?.Invoke();
        }
        catch (ArgumentException ex) { _status.Text = ex.Message; }
    }
    public void FocusBrowser() { if (!_disposed) { Activated?.Invoke(this); _web.Focus(FocusState.Programmatic); } }
    public void FocusAddress() { Activated?.Invoke(this); _address.Focus(FocusState.Programmatic); _address.SelectAll(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _runtime.Unregister(SourceId, _web.CoreWebView2);
        try { _web.Close(); } catch (System.Runtime.InteropServices.COMException) { }
    }
}
