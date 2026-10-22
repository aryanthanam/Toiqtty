using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Toiqtty.Models;
using Toiqtty.Terminal;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Toiqtty.Controls;

/// <summary>WinUI input surface with a native Direct3D renderer over libghostty state.</summary>
public sealed partial class TerminalView : Grid, IDisposable
{
    private readonly TerminalCanvasSurface _canvas = new() { ClearColor = Color(0xff131519) };
    private readonly TerminalInputBox _input = new()
    {
        Width = 2,
        Height = 2,
        Opacity = .01,
        Padding = new Thickness(0),
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        AcceptsReturn = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly Border _border;
    private readonly Grid _surface;
    private SessionTheme? _terminalTheme;
    private Chrome.Palette _paneColors = Chrome.For(null);
    private uint _selectionForeground = Chrome.Text;
    private uint? _cursorForeground;
    private WallpaperImage? _wallpaper;
    private WallpaperSettings _wallpaperSettings = new();
    internal void SetWallpaper(WallpaperImage? image, WallpaperSettings settings) { _wallpaper = image; _wallpaperSettings = settings; _canvas.Invalidate(); }
    internal void RefreshWallpaper() => _canvas.Invalidate();
    internal ushort FontWeightForCheck => Format(0).FontWeight.Weight;
    internal float FontSizeForCheck => Format(0).FontSize;
    internal float CellHeightForCheck => CellHeight;
    internal double ContentOpacityForCheck => _canvas.Opacity;
    internal string FontFaceForCheck => Format(0).FontFamily;
    internal TerminalFrame FrameForCheck => _frame;
    internal void SetSessionTheme(SessionTheme? theme)
    {
        if (_terminalTheme?.Key == theme?.Key) return;
        _terminalTheme = theme; _paneColors = Chrome.For(theme); SessionAccent = _paneColors.Accent;
        _selectionForeground = SessionTheme.TryColor(theme?.SelectionForeground, out uint selected) ? selected : _paneColors.Text;
        _cursorForeground = SessionTheme.TryColor(theme?.CursorForeground, out uint cursor) ? cursor : null;
        ResetLayers(); _canvas.Invalidate();
    }
    private readonly Func<uint, CanvasTextFormat> _formatForDrawing;
    private readonly Action<DirectXDrawingSession, float, float, float, uint, Windows.UI.Color> _underlineForDrawing;
    private bool _showFocusMarker;
    private uint _sessionAccent = Chrome.Accent;
    internal uint SessionAccent { get => _sessionAccent; set { if (_sessionAccent == value) return; _sessionAccent = value; UpdateBorder(); ((Microsoft.UI.Xaml.Shapes.Ellipse)_scrollAnchor.Children[0]).Stroke = Chrome.Brush(value); } }
    internal bool ShowFocusMarker { get => _showFocusMarker; set { if (_showFocusMarker == value) return; _showFocusMarker = value; UpdateBorder(); } }
    internal string ViewportLabel => $"{GridColumns} × {GridRows}";
    private bool _syntheticInputOnlyForCheck;
    internal void UseSyntheticInputOnlyForCheck()
    {
        _syntheticInputOnlyForCheck = true; _input.IsReadOnly = true; _canvas.IsHitTestVisible = false; AllowDrop = false;
    }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private PaneNode _pane;
    private readonly string _paneId;
    private readonly ITerminalSession? _session;
    private readonly Hosting.RemoteTerminalSession? _remote;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _viewDispatcher;
    private bool _warmQueued;
    internal long WarmedGlyphsForCheck => _textRenderer.WarmedGlyphsForCheck;
    private bool _viewAttached;
    private volatile bool _renderSuspended;
    private readonly bool _traceLifecycle = Environment.GetEnvironmentVariable("TOIQTTY_VIEW_LIFECYCLE_TRACE") == "1";
    private void TraceLifecycle(string action)
    {
        if (!_traceLifecycle) return;
        File.AppendAllText(Path.Combine(SessionStore.Folder, "view-lifecycle.jsonl"), System.Text.Json.JsonSerializer.Serialize(new
        {
            pane = _paneId,
            action,
            loaded = IsLoaded,
            parent = Parent?.GetType().Name,
            suspended = _renderSuspended,
            stack = action == "suspend" ? Environment.StackTrace : "",
            ticks = System.Diagnostics.Stopwatch.GetTimestamp()
        }) + Environment.NewLine);
    }
    private bool _inputClosing;
    private bool _inputSuspended;
    private bool _windowSuspended, _recreateOnResume;
    private AppSettings _settings;
    private (string Font, double Size, bool Ligatures, ushort Weight, VisualSettings Appearance) _renderSettings;
    internal ITerminalSession? Session => _session;
    internal bool IsDisposedForCheck => _disposed;
    internal bool IsSuspendedForCheck => _renderSuspended;
    internal object SuspensionForCheck => new { _renderSuspended, _inputSuspended, _windowSuspended, _inputClosing, IsLoaded, parent = Parent?.GetType().Name };
    internal object DebugGeometryForCheck => new
    {
        requestedColumns = _requestedColumns,
        requestedRows = _requestedRows,
        columns = GridColumns,
        rows = GridRows,
        resizeCells = _resizeCells?.Length,
        cells = _cells.Length,
        _hasSized,
        _resizePending,
        _capturePending,
        _capturedVersion,
        _version,
        held = _frame.RenderHeld
    };
    internal long RetainedGpuBytes => _canvas.RetainedGpuBytes;
    private TerminalCell[] _cells = new TerminalCell[100 * 30];
    private TerminalFrame _frame;
    private TerminalSnapshot? _snapshot;
    private readonly Dictionary<uint, CanvasTextFormat> _formats = [];
    private readonly Dictionary<(string Text, uint Style, uint Width), CanvasTextLayout> _glyphs = [];
    private DirectXRenderer? _glyphRenderer;
    private PowerlineRenderer? _powerline;
    private readonly TerminalTextRenderer _textRenderer = new()
    { UseNativeLayouts = true, UseAsciiShapes = DirectXRenderer.AsciiShapesAvailable, UseStreamedAscii = true };
    private DirectXRenderTarget? _backgroundLayer, _textLayer;
    private bool[] _blinkingRows = [];
    private bool[] _backgroundRows = [];
    private int _backgroundRowCount;
    private bool _eagerBackgroundForCheck;
    private bool[] _redrawRows = [];
    private bool _forceRows = true, _capturePending;
    private int _capturedVersion = -1;
    private long _readCacheRevision = -1;
    private bool _renderedBlink = true;
    private Point _dragPosition;
    private long _lastAutoscroll;
    internal long RowsPainted { get; private set; }
    internal long FramesPainted { get; private set; }
    internal TerminalCanvasSurface.PresentationStats PresentationForCheck => _canvas.PresentationForCheck;
    internal int ShapedRuns => _textRenderer.ShapedRuns;
    private long _presentedRows;
    internal long TextPresentationsForCheck { get; private set; }
    internal long CharacterSpacingCalls => _textRenderer.SpacingCalls;
    internal int ImageTextureCount => _images.TextureCount;
    internal int CachedTextLayouts => _textRenderer.CachedLayouts;
    internal DirectXRenderer.Stats NativeRenderStatsForCheck => _canvas.Renderer.GetStats();
    internal DirectXRenderer.ColorGlyphStats NativeColorGlyphsForCheck => _canvas.Renderer.ColorGlyphsForCheck;
    internal ulong NativePreparedLayoutsForCheck => _canvas.Renderer.PreparedLayoutsForCheck;
    internal DirectXRenderer.Profile NativeRenderProfileForCheck => _canvas.Renderer.GetProfile();
    internal string NativeAdapterNameForCheck => _canvas.Renderer.AdapterNameForCheck;
    internal void SetAtlasHashForCheck(bool legacy) => _canvas.Renderer.SetAtlasHashModeForCheck(legacy);
    internal void SetAsciiShapesForCheck(bool value) => _textRenderer.UseAsciiShapes = value;
    internal void SetStreamedAsciiForCheck(bool value) => _textRenderer.UseStreamedAscii = value;
    internal void SetEagerBackgroundForCheck(bool value)
    { if (_eagerBackgroundForCheck == value) return; _eagerBackgroundForCheck = value; ResetLayers(); _canvas.Invalidate(); }
    internal bool BackgroundLayerAllocatedForCheck => _backgroundLayer is not null;
    internal void SetReserveResizeForCheck(bool value) { _canvas.ReserveResizeForCheck = value; _canvas.Invalidate(); }
    internal DirectXRenderer.ResizeStats ResizeStatsForCheck => _canvas.Renderer.ResizeStatsForCheck;
    internal string SwapchainAddressForCheck => _canvas.Renderer.SwapchainAddressForCheck;
    internal DirectXRenderer.PresentResults PresentResultsForCheck => _canvas.Renderer.PresentResultsForCheck;
    internal void SetPresentationPolicyForCheck(uint latency, uint interval) { _canvas.FrameLatencyForCheck = latency; _canvas.PresentIntervalForCheck = interval; _canvas.Invalidate(); }
    internal DirectXRenderer.AtlasBuckets NativeAtlasBucketsForCheck => _canvas.Renderer.AtlasBucketsForCheck;
    internal void SetPresentationModesForCheck(bool expanded, bool compositionRetry, bool nativeLayouts = false, bool compositionBulk = false)
    { _canvas.Renderer.SetQuadModeForCheck(expanded); _canvas.CompositionRetryForCheck = compositionRetry; _textRenderer.UseNativeLayouts = nativeLayouts; _canvas.PaceBulkFrames = compositionBulk || _settings.FramePacing; }
    internal bool InputHasFocus => _input.FocusState != FocusState.Unfocused;
    internal bool IsComposing => _composing;
    internal event Action<string, string>? ValidationObserved;
    internal bool PhysicalCellsAreIntegral => Math.Abs(CellWidth * (XamlRoot?.RasterizationScale ?? 1) - Math.Round(CellWidth * (XamlRoot?.RasterizationScale ?? 1))) < .001
        && Math.Abs(CellHeight * (XamlRoot?.RasterizationScale ?? 1) - Math.Round(CellHeight * (XamlRoot?.RasterizationScale ?? 1))) < .001;
    internal int ResourceGenerations { get; private set; }
    internal bool HasGraphicsDeviceForCheck => _canvas.ReadyToDraw;
    internal float RenderDpi => _canvas.Dpi;
    internal (uint Width, uint Height) PhysicalCellSize => (_frame.CellWidth, _frame.CellHeight);
    internal bool PointerGeometryIsAligned => _frame.CellWidth == TerminalCellMetrics.PhysicalSize(CellWidth, XamlRoot?.RasterizationScale ?? 1)
        && _frame.CellHeight == TerminalCellMetrics.PhysicalSize(CellHeight, XamlRoot?.RasterizationScale ?? 1);
    internal bool InputCaretIsAligned => Math.Abs(_input.Margin.Left - Math.Min(_frame.CursorX, (uint)Math.Max(0, GridColumns - 1)) * CellWidth) < .01
        && Math.Abs(_input.Margin.Top - Math.Min(_frame.CursorY, (uint)Math.Max(0, GridRows - 1)) * CellHeight) < .01;
    internal void RecreateRenderResources()
    {
        if (!_canvas.ReadyToDraw)
        {
            _recreateOnResume = true; _capturedVersion = -1; _capturePending = true; _canvas.Invalidate(); return;
        }
        _recreateOnResume = false;
        _images.Dispose(); _textRenderer.Reset(); _powerline?.Dispose(); _powerline = null;
        _capturedVersion = -1; _capturePending = true; ResetLayers();
        MeasureCells(); _canvas.Invalidate();
    }
    private CanvasDevice? _testSoftwareDevice;
    // A software/hardware device transition exercises CreateResources(NewDevice)
    // without resetting the GPU used by the user's other applications.
    internal void ReplaceRenderDeviceForCheck(bool software) => _canvas.CustomDevice = software ? _testSoftwareDevice ??= new CanvasDevice(true) : null;
    internal void InjectRenderDeviceLostForCheck() => _canvas.InjectDeviceLostForCheck();
    internal void SetRenderDpiScale(float scale) => _canvas.DpiScale = scale;
    internal void ResumeRendering() { if (_disposed) return; _renderSuspended = false; _canvas.Resume(); SetCaptureVisible(IsLoaded); _hasSized = false; RecreateRenderResources(); _timer.Start(); }
    internal void SuspendRendering() { TraceLifecycle("suspend"); _renderSuspended = true; _canvas.Suspend(); SetCaptureVisible(false); StopAutoScroll(); _timer.Stop(); }
    private TerminalAutomationPeer? _textPeer;
    private AccessibleTerminalText? _accessibleText;
    internal TerminalAutomationPeer TextPeer => _textPeer ??= new TerminalAutomationPeer(this);
    protected override AutomationPeer OnCreateAutomationPeer() => TextPeer;
    private readonly TerminalImageRenderer _images = new();
    private long _blinkEpoch = Environment.TickCount64;
    private bool _blinkVisible = true, _hasBlinkingText;
    private long _animationDue = long.MaxValue;
    private readonly Dictionary<uint, PendingKey> _pressedKeys = [];
    private PendingKey? _pendingTextKey;
    private readonly record struct PendingKey(uint Key, uint Modifiers, TerminalKeyAction Action, uint Unshifted, uint Consumed, bool AltGr = false);
    private TerminalGridGeometry _geometry = new(100, 30, 8.4f, 21, 8, 21, 1);
    private TerminalGridGeometry? _pendingGeometry;
    private ushort GridColumns => _geometry.Columns;
    private ushort GridRows => _geometry.Rows;
    private ushort _requestedColumns = 100, _requestedRows = 30;
    private uint _requestedCellWidth, _requestedCellHeight;
    private TerminalCell[]? _resizeCells;
    private bool _resizePending;
    internal long ResizeCommits { get; private set; }
    internal bool ResizePendingForCheck => _resizePending || _resizeCells is not null;
    internal bool HoldSnapshotsForCheck { get; set; }
    private int _frameQueued;
    internal long FrameNotifications { get; private set; }
    internal event Action? FramePaintedForCheck;
    internal event Action<string, long>? LatencyStageForCheck;
    private void TraceLatency(string stage) => LatencyStageForCheck?.Invoke(stage, System.Diagnostics.Stopwatch.GetTimestamp());
    private int _hoverCell = -1;
    private int _hoverVersion = -1;
    private bool _hoverPending;
    private int _version = -1;
    private bool _resettingText, _selecting, _focused, _composing;
    private volatile bool _disposed;
    private int _compositionGeneration;
    private float CellWidth => _geometry.CellWidth;
    private float CellHeight => _geometry.CellHeight;
    private float _measuredCellWidth = 8.4f, _measuredCellHeight = 21;
    private bool _hasSized;
    private bool _lastAttention;
    private XamlRoot? _observedRoot;
    private double _rasterizationScale;
    public event Action<TerminalView>? Activated;
    public event Action<Uri>? HyperlinkRequested;
    public string PaneId => _paneId;
    internal string RenderedText => string.Concat(_cells.Select(c => c.GetText()));
    internal string RenderedRowForCheck(int row)
    {
        if (row < 0 || row >= GridRows) return "";
        var text = new System.Text.StringBuilder(GridColumns);
        for (int column = 0; column < GridColumns; column++) text.Append(_cells[row * GridColumns + column].GetText());
        return text.ToString();
    }
    internal bool HasColoredCells => _cells.Any(c => c.Background != 0 && c.Background != _frame.Background);
    public bool IsActive
    {
        get => _focused;
        set
        {
            // Attention can clear on the already focused pane. Refresh its rule,
            // but unchanged focus must not redraw or disturb autoscroll.
            // WinUI stores opacity as a float even though its projection is a
            // double. Compare at that precision so .65 does not redraw forever.
            double opacity = value ? 1 : (float)_settings.Appearance.InactivePaneOpacity;
            bool focusChanged = _focused != value;
            if (!focusChanged && _canvas.Opacity == opacity) { UpdateBorder(); return; }
            _focused = value;
            if (_canvas.Opacity != opacity) _canvas.Opacity = opacity;
            if (!value && focusChanged) StopAutoScroll();
            UpdateBorder(); _canvas.Invalidate();
        }
    }
    public TerminalView(PaneNode pane, AppSettings settings)
    {
        _formatForDrawing = Format; _underlineForDrawing = DrawUnderline;
        _pane = pane; _paneId = pane.Id; _settings = settings;
        _session = pane.Session;
        _viewDispatcher = DispatcherQueue;
        _remote = _session as Hosting.RemoteTerminalSession;
        if (_remote is { } remote) { remote.FrameAvailable += OnRemoteFrame; SetCaptureVisible(true); }
        _input.Terminal = this;
        AutomationProperties.SetAutomationId(this, "Terminal-" + pane.Id);
        AutomationProperties.SetName(_input, "Terminal input");
        AutomationProperties.SetName(_canvas, "Terminal output");
        var padding = settings.Appearance.Padding;
        var surface = _surface = new Grid { Background = Brush(0xff131519), Padding = new Thickness(padding.Left, padding.Top, padding.Right, padding.Bottom) };
        _canvas.Transparent = settings.Window.Backdrop != "none" && settings.Window.BackgroundOpacity < 1;
        _canvas.Opacity = settings.Appearance.InactivePaneOpacity;
        surface.Children.Add(_canvas); surface.Children.Add(_input);
        InitializeScrolling(surface);
        _border = new Border { BorderThickness = new Thickness(0), BorderBrush = Brush(0xff292e38), Child = surface };
        Children.Add(_border);
        _canvas.Draw += Render;
        _canvas.Presented += () =>
        {
            if (_presentedRows != RowsPainted) { _presentedRows = RowsPainted; TextPresentationsForCheck++; }
            TraceLatency("present-return"); FramePaintedForCheck?.Invoke();
            if (_textRenderer.WarmPending && !_warmQueued)
            {
                // After the frame is visible, never ahead of input or output frames.
                _warmQueued = _viewDispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    _warmQueued = false;
                    if (_disposed || !_canvas.ReadyToDraw) return;
                    try { _textRenderer.WarmGlyphs(_canvas.Dpi); }
                    catch (System.Runtime.InteropServices.COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }
                });
            }
        };
        _canvas.CreateResources += () => { if (_disposed) return; ResourceGenerations++; _wallpaper?.ForgetDevice(_canvas.Device); RecreateRenderResources(); };
        _canvas.SizeChanged += (_, _) => ResizeTerminal();
        _canvas.PointerPressed += PointerPressedOnCanvas;
        _canvas.PointerMoved += PointerMovedOnCanvas;
        _canvas.PointerReleased += (_, e) =>
        {
            if (ReleaseMiddleScroll(e)) return;
            if (!_selecting && ReportMouse(e, 1)) { _canvas.ReleasePointerCapture(e.Pointer); e.Handled = true; return; }
            if (_selecting) Gesture(1, e.GetCurrentPoint(_canvas).Position);
            _selecting = false;
            _canvas.ReleasePointerCapture(e.Pointer); _canvas.Invalidate();
        };
        _canvas.PointerCaptureLost += (_, e) => { _selecting = false; MiddleScrollCaptureLost(e.GetCurrentPoint(_canvas).Properties.IsMiddleButtonPressed); };
        _canvas.PointerCanceled += (_, _) => StopAutoScroll("pointer-cancelled");
        _canvas.PointerExited += (_, e) => { _hoverCell = -1; MiddleScrollPointerExited(e.GetCurrentPoint(_canvas).Position); };
        InitializeDrop();
        _canvas.PointerWheelChanged += (_, e) =>
        {
            StopAutoScroll();
            var point = e.GetCurrentPoint(_canvas);
            if (point.Properties.IsHorizontalMouseWheel || point.Properties.MouseWheelDelta == 0) return;
            var delta = point.Properties.MouseWheelDelta;
            if (Down(VirtualKey.Control)) { _wheelScroll.Reset(); ChangeFontSize(delta > 0 ? 1 : -1); }
            else RouteWheel(delta, point.Position, Down(VirtualKey.Shift), Down(VirtualKey.Menu) ? 4u : 0);
            _canvas.Invalidate(); e.Handled = true;
        };
        _input.PreviewKeyDown += KeyDownOnInput;
        _input.PreviewKeyUp += KeyUpOnInput;
        _input.TextCompositionStarted += (_, _) => BeginComposition();
        _input.TextCompositionEnded += (_, _) => EndComposition();
        _input.TextChanged += (_, _) => { ValidationObserved?.Invoke("input-text-changed", $"composing={_composing}; text={_input.Text}"); SendInputText(); };
        _input.GotFocus += (_, _) => { if (_disposed || _inputClosing || _inputSuspended || _windowSuspended) return; _session?.Focus(true); Activated?.Invoke(this); _canvas.Invalidate(); };
        _input.LostFocus += (_, _) =>
        {
            if (_disposed || _inputClosing || _inputSuspended || _windowSuspended) return;
            CheckMiddleScrollFocus();
            if (_composing) { _resettingText = true; _input.Text = ""; _resettingText = false; _composing = false; PositionInput(); }
            foreach (var key in _pressedKeys.Values) _session?.Key(key.Key, 0, action: TerminalKeyAction.Release, unshiftedCodepoint: key.Unshifted);
            _pressedKeys.Clear(); _pendingTextKey = null;
            _session?.Focus(false); _canvas.Invalidate();
        };
        _timer.Tick += (_, _) =>
        {
            if (_disposed || _renderSuspended || _session is not { } session) return;
            FlushResize();
            TickScrolling(Environment.TickCount64);
            if (_lastAttention != _pane.Attention) { _lastAttention = _pane.Attention; UpdateBorder(); }
            if (_capturePending) _canvas.Invalidate();
            if (_version != session.Version) { _version = session.Version; _blinkEpoch = Environment.TickCount64; _canvas.Invalidate(); }
            bool blink = (Environment.TickCount64 - _blinkEpoch) % (_settings.Cursor.BlinkInterval * 2) < _settings.Cursor.BlinkInterval;
            if (_blinkVisible != blink && ((_focused && _frame.CursorBlinking != 0) || _hasBlinkingText)) _canvas.Invalidate();
            _blinkVisible = blink;
            // A stalled synchronized-output writer must not freeze this pane forever.
            if (_frame.RenderHeld != 0) _canvas.Invalidate();
            if (Environment.TickCount64 >= _animationDue) _canvas.Invalidate();
            if (_selecting && Environment.TickCount64 - _lastAutoscroll >= 50 && (_dragPosition.Y < 0 || _dragPosition.Y >= GridRows * CellHeight))
            {
                _lastAutoscroll = Environment.TickCount64;
                Gesture(3, _dragPosition); _canvas.Invalidate();
            }
        };
        Loaded += (_, _) =>
        {
            if (_disposed) return;
            TraceLifecycle("loaded");
            ResumeAttachedView();
        };
        Unloaded += (_, _) =>
        {
            TraceLifecycle("unloaded"); SuspendRendering(); _pendingScrollRow = null; StopObservingRoot();
            // Routed unload notifications can arrive after a same-turn reparent.
            // Reconcile against current attachment/input ownership after routing.
            _viewDispatcher.TryEnqueue(ResumeAttachedView);
        };
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }
    private static SolidColorBrush Brush(uint value) => new(Color(value));
    private void SetCaptureVisible(bool visible)
    {
        if (_remote is null || _viewAttached == visible) return;
        _viewAttached = visible;
        if (visible) _remote.AttachView(); else _remote.DetachView();
    }
    private void OnRemoteFrame()
    {
        if (_disposed || _renderSuspended) return;
        if (Interlocked.Exchange(ref _frameQueued, 1) != 0) return;
        TraceLatency("frame-dispatch-post");
        if (!_viewDispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _frameQueued, 0);
            if (_disposed || _renderSuspended || !IsLoaded) return;
            FrameNotifications++; _version = _remote?.Version ?? _version; _blinkEpoch = Environment.TickCount64;
            TraceLatency("frame-invalidate");
            _canvas.Invalidate(_remote?.IsInteractiveForPresentation ?? true);
        })) Interlocked.Exchange(ref _frameQueued, 0);
    }
    private static Windows.UI.Color Color(uint value) => Windows.UI.Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    private void ResumeAttachedView()
    {
        if (_disposed || _inputClosing || _inputSuspended || _windowSuspended || !IsLoaded || XamlRoot is null) return;
        _renderSuspended = false; _canvas.Resume(); SetCaptureVisible(true); _timer.Start(); ObserveRoot();
        if (_recreateOnResume) { _recreateOnResume = false; RecreateRenderResources(); }
        else if (_canvas.ReadyToDraw && _renderSettings != (_settings.FontFamily, (double)_settings.FontSize, _settings.Ligatures, _settings.FontWeight, _settings.Appearance)) MeasureCells();
        ResizeTerminal();
    }
    internal void SetWindowRenderingActive(bool active, bool recreate = false)
    {
        if (_disposed) return;
        _recreateOnResume |= recreate;
        if (_windowSuspended == !active && !recreate) return;
        _windowSuspended = !active;
        if (!active)
        {
            SuspendRendering();
            if (!_inputSuspended) ReleaseInputFocus();
        }
        else
        {
            _input.IsEnabled = !_inputSuspended && !_inputClosing;
            ResumeAttachedView();
        }
    }
    public void FocusTerminal() { if (!_disposed && !_inputClosing && !_inputSuspended && !_windowSuspended) { _input.Focus(FocusState.Programmatic); Activated?.Invoke(this); } }
    internal void Bind(PaneNode pane, AppSettings settings)
    {
        if (pane.Id != _paneId || !ReferenceEquals(pane.Session, _session)) throw new InvalidOperationException("A terminal view can only bind to its original shell identity.");
        _pane = pane; _settings = settings; _inputSuspended = false; _input.IsEnabled = !_windowSuspended;
        _focused = false; _accessibleText = null;
        // Reparenting within the same XamlRoot can keep IsLoaded true and omit
        // Loaded/Unloaded notifications. Resume explicitly rather than depending
        // on a new Loaded event to restart capture, drawing and resize commits.
        if (_windowSuspended) SuspendRendering();
        else { _renderSuspended = false; _canvas.Resume(); SetCaptureVisible(IsLoaded); _timer.Start(); }
        TraceLifecycle("bind-resumed");
        if (IsLoaded && _canvas.ReadyToDraw && _renderSettings != (_settings.FontFamily, (double)_settings.FontSize, _settings.Ligatures, _settings.FontWeight, _settings.Appearance)) MeasureCells();
        ResizeTerminal();
    }
    internal void SuspendForReuse()
    {
        SuspendRendering();
        if (_inputSuspended) return;
        _inputSuspended = true;
        ReleaseInputFocus();
    }
    private void ReleaseInputFocus()
    {
        foreach (var key in _pressedKeys.Values)
            _session?.Key(key.Key, 0, action: TerminalKeyAction.Release, unshiftedCodepoint: key.Unshifted);
        _pressedKeys.Clear(); _pendingTextKey = null; _compositionGeneration++;
        _resettingText = true; _input.Text = ""; _resettingText = false; _composing = false;
        _session?.Focus(false); _focused = false; _input.IsEnabled = false;
    }
    internal void ReleaseInputForClose()
    {
        if (_inputClosing) return;
        _inputClosing = true;
        SuspendForReuse();
    }
    internal void HoldKeyForCheck(uint key, string text)
    {
        _pressedKeys[key] = new(key, 0, TerminalKeyAction.Press, text.Length == 0 ? 0u : text[0], 0);
        _session?.Key(key, 0, text);
    }
    private void UpdateBorder()
    {
        // Reserve the same one-pixel rule for each split, so focus changes
        // never resize the terminal. A single pane has no surrounding frame.
        var thickness = new Thickness(0, _showFocusMarker ? 1 : 0, 0, 0);
        if (!_border.BorderThickness.Equals(thickness)) _border.BorderThickness = thickness;
        var color = Color(_pane.Attention || (_focused && _showFocusMarker) ? _sessionAccent : Chrome.Background);
        if (_border.BorderBrush is SolidColorBrush brush) { if (brush.Color != color) brush.Color = color; }
        else _border.BorderBrush = new SolidColorBrush(color);
    }
    private void MeasureCells()
    {
        if (_disposed || !_canvas.ReadyToDraw) return;
        try { MeasureCellsCore(); }
        catch (Exception ex) when (_canvas.Device.IsDeviceLost(ex.HResult))
        {
            // Settings, resume and XamlRoot changes call this outside Draw,
            // where the presentation surface cannot catch device-loss errors.
            _capturedVersion = -1; _capturePending = true;
            _canvas.Device.RaiseDeviceLost();
        }
    }
    private void MeasureCellsCore()
    {
        var padding = _settings.Appearance.Padding;
        _surface.Padding = new Thickness(padding.Left, padding.Top, padding.Right, padding.Bottom);
        _canvas.Transparent = _settings.Window.Backdrop != "none" && _settings.Window.BackgroundOpacity < 1;
        _canvas.PaceBulkFrames = _settings.FramePacing;
        _canvas.Opacity = _focused ? 1 : _settings.Appearance.InactivePaneOpacity;
        foreach (var format in _formats.Values) format.Dispose();
        ClearCursorGlyphs();
        _formats.Clear();
        _textRenderer.Reset(); _textRenderer.ConfigureFeatures(_settings.Appearance.FontFeatures); ResetLayers();
        using var layout = new CanvasTextLayout(_canvas.Device, "M", Format(0), 100, 100);
        var scale = (float)(XamlRoot?.RasterizationScale ?? 1);
        // Integer physical pixels prevent antialiased background seams at fractional DPI.
        _measuredCellWidth = Math.Max(1, MathF.Ceiling((float)layout.LayoutBounds.Width * (float)_settings.Appearance.CellWidth * scale)) / scale;
        _measuredCellHeight = MathF.Ceiling(_settings.FontSize * (float)_settings.Appearance.LineHeight * scale) / scale;
        _renderSettings = (_settings.FontFamily, (double)_settings.FontSize, _settings.Ligatures, _settings.FontWeight, _settings.Appearance);
        _powerline?.Dispose();
        _powerline = null;
        _hasSized = false;
        ResizeTerminal(); _canvas.Invalidate();
    }
    private CanvasTextFormat Format(uint flags)
    {
        flags &= 3;
        if (_formats.TryGetValue(flags, out var existing)) return existing;
        return _formats[flags] = new CanvasTextFormat
        {
            FontFamily = TerminalFont.Resolve(_settings.FontFamily, flags, _settings.FontWeight),
            FontSize = _settings.FontSize,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = (flags & 1) != 0 ? (ushort)Math.Max(700, (int)_settings.FontWeight) : _settings.FontWeight },
            FontStyle = (flags & 2) != 0 ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
    }
    public void ApplySettings()
    {
        _wheelScroll.Reset(); _scrollbarWheel.Reset();
        var padding = _settings.Appearance.Padding;
        _surface.Padding = new Thickness(padding.Left, padding.Top, padding.Right, padding.Bottom);
        _canvas.Transparent = _settings.Window.Backdrop != "none" && _settings.Window.BackgroundOpacity < 1;
        _canvas.Opacity = _focused ? 1 : _settings.Appearance.InactivePaneOpacity;
        _session?.ApplySettings(_settings.WithTerminalTheme(_terminalTheme)); MeasureCells();
    }
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_disposed) return;
        if (sender.RasterizationScale == _rasterizationScale) return;
        _rasterizationScale = sender.RasterizationScale; _hasSized = false;
        _geometry = _geometry with { RasterizationScale = sender.RasterizationScale };
        _accessibleText = null; _capturedVersion = -1; MeasureCells();
    }
    private void ObserveRoot()
    {
        if (ReferenceEquals(_observedRoot, XamlRoot)) return;
        StopObservingRoot();
        _observedRoot = XamlRoot; _rasterizationScale = XamlRoot.RasterizationScale;
        _geometry = _geometry with { RasterizationScale = _rasterizationScale };
        _observedRoot.Changed += OnRootChanged;
    }
    private void StopObservingRoot()
    {
        _observedRoot?.Changed -= OnRootChanged;
        _observedRoot = null;
    }
    private void ResizeTerminal()
    {
        if (_disposed) return;
        // SizeChanged is raised during XAML layout. Coalesce the latest size
        // on the queued draw, outside that callback and before new input focus.
        _resizePending = true;
        _canvas.Invalidate();
    }
    private void FlushResize()
    {
        if (!_resizePending || _disposed) return;
        _resizePending = false;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0 || !float.IsFinite(_measuredCellWidth) || _measuredCellWidth <= 0
            || !float.IsFinite(_measuredCellHeight) || _measuredCellHeight <= 0
            || !double.IsFinite(_canvas.ActualWidth) || !double.IsFinite(_canvas.ActualHeight)
            || _canvas.ActualWidth < 8 || _canvas.ActualHeight < 8) return;
        var columns = (ushort)Math.Clamp((int)(_canvas.ActualWidth / _measuredCellWidth), 2, 1000);
        var rows = (ushort)Math.Clamp((int)(_canvas.ActualHeight / _measuredCellHeight), 1, 500);
        uint cellWidth = TerminalCellMetrics.PhysicalSize(_measuredCellWidth, scale), cellHeight = TerminalCellMetrics.PhysicalSize(_measuredCellHeight, scale);
        if (_hasSized && columns == _requestedColumns && rows == _requestedRows
            && cellWidth == _requestedCellWidth && cellHeight == _requestedCellHeight) return;
        _hasSized = true;
        _requestedColumns = columns; _requestedRows = rows;
        _requestedCellWidth = cellWidth; _requestedCellHeight = cellHeight;
        _pendingGeometry = new(columns, rows, _measuredCellWidth, _measuredCellHeight, cellWidth, cellHeight, scale);
        if (_remote is not null && _resizeCells is { } obsolete) TerminalCellPool.Shared.Return(obsolete);
        _resizeCells = _remote is not null ? TerminalCellPool.Shared.Rent(columns * rows) : new TerminalCell[columns * rows];
        _readCacheRevision = -1;
        _capturedVersion = -1; _capturePending = true; _selecting = false;
        _session?.QueueResize(columns, rows, cellWidth, cellHeight);
        ResizeCommits++; _canvas.Invalidate();
    }
    private void Render(DirectXDrawingSession drawing)
    {
        TraceLatency("draw-begin");
        if (_disposed || _session is null) return;
        // Submit geometry on the queued draw rather than waiting for the 33ms
        // maintenance timer. The ordered input queue receives this resize before
        // a newly focused pane can send keys. Retain the last frame until ready.
        FlushResize();
        var session = _session;
        bool capture = _snapshot is null || _capturedVersion != session.Version || _frame.RenderHeld != 0 || Environment.TickCount64 >= _animationDue;
        IReadOnlyList<int> changed = [];
        if (capture && !HoldSnapshotsForCheck)
        {
            int version = session.Version;
            var captureCells = _resizeCells ?? _cells;
            bool ready = _remote is { } remote
                ? remote.TryReadSnapshot(captureCells, ref _readCacheRevision, out TerminalSnapshot? fresh)
                : session.TryReadSnapshot(captureCells, out fresh);
            if (ready)
            {
                if (_resizeCells is not null)
                {
                    var previous = _cells;
                    _cells = captureCells; _resizeCells = null;
                    if (_remote is not null) TerminalCellPool.Shared.Return(previous);
                    if (_pendingGeometry is { } geometry) { _geometry = geometry; _pendingGeometry = null; }
                    ResetLayers();
                }
                _snapshot = fresh; _frame = fresh!.Frame; changed = fresh.ChangedRows;
                UpdateScrollbar(fresh.Scrollbar);
                _accessibleText = null;
                if (_textPeer is not null && changed.Count > 0)
                    _textPeer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
                _capturedVersion = version; _capturePending = false;
                _animationDue = _frame.AnimationDelay == uint.MaxValue ? long.MaxValue : Environment.TickCount64 + _frame.AnimationDelay;
                _images.Update(_canvas.Renderer, fresh.Graphics);
            }
            else _capturePending = true;
        }
        if (_snapshot is not { } snapshot) { drawing.Clear(_canvas.ClearColor); return; }
        _powerline ??= new PowerlineRenderer(_canvas.Device, CellWidth, CellHeight);
        PositionInput();
        if (_textLayer is null)
        {
            _textLayer = _canvas.Renderer.CreateRenderTarget(GridColumns * CellWidth, GridRows * CellHeight, _canvas.Dpi);
            using (var clear = _textLayer.CreateDrawingSession()) clear.Clear(Colors.Transparent);
            _blinkingRows = new bool[GridRows]; _backgroundRows = new bool[GridRows]; _backgroundRowCount = 0;
            _redrawRows = new bool[GridRows]; _forceRows = true;
        }
        if (_forceRows) Array.Fill(_redrawRows, true);
        else foreach (int row in changed) _redrawRows[row] = true;
        if (_renderedBlink != _blinkVisible)
            for (int row = 0; row < _blinkingRows.Length; row++) if (_blinkingRows[row]) _redrawRows[row] = true;
        _renderedBlink = _blinkVisible;
        if (_redrawRows.AsSpan().Contains(true))
        {
            if (_backgroundLayer is null)
            {
                // Default cell backgrounds are already supplied by the pane's
                // clear. Avoid allocating or sampling an empty full-size layer.
                // Once needed, retain it until geometry/device reset so a
                // selection or colored prompt does not churn GPU allocations.
                bool needed = _eagerBackgroundForCheck;
                for (int y = 0; y < _redrawRows.Length; y++) if (_redrawRows[y])
                {
                    _blinkingRows[y] = false;
                    for (int x = 0; x < GridColumns; x++)
                    {
                        ref var cell = ref _cells[y * GridColumns + x];
                        _blinkingRows[y] |= (cell.Flags & 256) != 0;
                        needed |= cell.Background != _frame.Background || (cell.Flags & (64 | 512)) != 0;
                    }
                }
                if (needed)
                {
                    _backgroundLayer = _canvas.Renderer.CreateRenderTarget(GridColumns * CellWidth, GridRows * CellHeight, _canvas.Dpi);
                    using var clear = _backgroundLayer.CreateDrawingSession(); clear.Clear(Colors.Transparent);
                }
            }
            if (_backgroundLayer is not null) using (var backgrounds = _backgroundLayer.CreateDrawingSession())
            {
                backgrounds.Blend = CanvasBlend.Copy;
                for (int y = 0; y < _redrawRows.Length; y++) if (_redrawRows[y])
                    backgrounds.FillRectangle(0, y * CellHeight, GridColumns * CellWidth, CellHeight, Colors.Transparent);
                backgrounds.Blend = CanvasBlend.SourceOver;
                uint selectionBackground = _terminalTheme is null ? 0xff3b4962 : _paneColors.Selected;
                for (int y = 0; y < _redrawRows.Length; y++)
                {
                    if (!_redrawRows[y]) continue;
                    _blinkingRows[y] = false;
                    if (_backgroundRows[y]) _backgroundRowCount--;
                    _backgroundRows[y] = false;
                    for (int x = 0; x < _frame.Columns;)
                    {
                        ref var cell = ref _cells[y * (int)_frame.Columns + x];
                        bool selected = (cell.Flags & 64) != 0;
                        _blinkingRows[y] |= (cell.Flags & 256) != 0;
                        if (cell.Background == _frame.Background && !selected && (cell.Flags & 512) == 0) { x++; continue; }
                        uint backgroundColor = selected ? selectionBackground : cell.Background;
                        if (!_backgroundRows[y]) { _backgroundRows[y] = true; _backgroundRowCount++; }
                        int start = x++;
                        while (x < _frame.Columns)
                        {
                            ref var next = ref _cells[y * (int)_frame.Columns + x]; bool nextSelected = (next.Flags & 64) != 0;
                            if (next.Background == _frame.Background && !nextSelected && (next.Flags & 512) == 0) break;
                            if ((nextSelected ? selectionBackground : next.Background) != backgroundColor) break;
                            _blinkingRows[y] |= (next.Flags & 256) != 0; x++;
                        }
                        backgrounds.FillRectangle(start * CellWidth, y * CellHeight, (x - start) * CellWidth, CellHeight, Color(backgroundColor));
                    }
                }
            }
            using var text = _textLayer.CreateDrawingSession();
            text.Blend = CanvasBlend.Copy;
            for (int y = 0; y < _redrawRows.Length; y++) if (_redrawRows[y])
                text.FillRectangle(0, y * CellHeight, GridColumns * CellWidth, CellHeight, Colors.Transparent);
            text.Blend = CanvasBlend.SourceOver;
            for (int y = 0; y < _redrawRows.Length; y++)
            {
                if (!_redrawRows[y]) continue;
                _redrawRows[y] = false;
                text.SetClip(new Rect(0, y * CellHeight, GridColumns * CellWidth, CellHeight));
                _textRenderer.DrawRow(_canvas.Device, text, _cells, y, GridColumns, CellWidth, CellHeight, _formatForDrawing,
                    _settings.Ligatures, _blinkVisible, _powerline, _underlineForDrawing, _terminalTheme is null ? null : _selectionForeground);
                RowsPainted++;
            }
        }
        _hasBlinkingText = _blinkingRows.AsSpan().Contains(true); _forceRows = false; FramesPainted++;
        if (StartupTrace.Enabled)
        {
            StartupTrace.Mark("first-frame");
            if (_cells.Any(cell => cell.Length > 0)) StartupTrace.Mark("first-output");
        }
        var background = Color(_frame.Background);
        bool translucent = _settings.Window.Backdrop != "none" && _settings.Window.BackgroundOpacity < 1;
        if (translucent) background.A = (byte)Math.Round(_settings.Window.BackgroundOpacity * 255);
        var surfaceColor = translucent ? Color(0) : background;
        if (_surface.Background is SolidColorBrush surfaceBrush) { if (surfaceBrush.Color != surfaceColor) surfaceBrush.Color = surfaceColor; }
        else _surface.Background = new SolidColorBrush(surfaceColor);
        if (_canvas.ClearColor != background) _canvas.ClearColor = background;
        drawing.Clear(background);
        _wallpaper?.Draw(_canvas.Renderer, _canvas.Device, drawing, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight, _wallpaperSettings.Fit, (float)_wallpaperSettings.Opacity);
        using var clip = drawing.CreateLayer(1, new Rect(0, 0, GridColumns * CellWidth, GridRows * CellHeight));
        _images.Draw(drawing, snapshot, CellWidth, CellHeight, 0);
        if (_backgroundLayer is not null && (_backgroundRowCount > 0 || _eagerBackgroundForCheck)) drawing.DrawImage(_backgroundLayer);
        _images.Draw(drawing, snapshot, CellWidth, CellHeight, 1);
        drawing.DrawImage(_textLayer);
        _images.Draw(drawing, snapshot, CellWidth, CellHeight, 2);
        if (_frame.CursorVisible != 0 && !_session.HasExited && (!_focused || _frame.CursorBlinking == 0 || _blinkVisible))
        {
            uint cursorX = _frame.CursorWideTail != 0 && _frame.CursorX > 0 ? _frame.CursorX - 1 : _frame.CursorX;
            var px = cursorX * CellWidth; var py = _frame.CursorY * CellHeight;
            float cursorWidth = Math.Max(1, _cells[_frame.CursorY * _frame.Columns + cursorX].Width) * CellWidth;
            var color = Color(_focused || _terminalTheme is not null ? _frame.CursorColor : 0xff626c7c);
            if (_focused && _frame.CursorStyle == 0) drawing.FillRectangle(px, py + 1, 2, CellHeight - 2, color);
            else if (_focused && _frame.CursorStyle == 2) drawing.FillRectangle(px, py + CellHeight - 3, cursorWidth, 2, color);
            else if (_focused && _frame.CursorStyle == 1)
            {
                ref var cell = ref _cells[_frame.CursorY * _frame.Columns + cursorX];
                float width = Math.Max(1, cell.Width) * CellWidth;
                drawing.FillRectangle(px, py, width, CellHeight, color);
                if (cell.Length > 0 && (cell.Flags & 32) == 0)
                {
                    var key = (cell.GetText(), cell.Flags & 3, cell.Width);
                    if (!ReferenceEquals(_glyphRenderer, _canvas.Renderer)) { ClearCursorGlyphs(); _glyphRenderer = _canvas.Renderer; }
                    if (!_glyphs.TryGetValue(key, out var glyph))
                    {
                        if (_glyphs.Count >= 256) { var old = _glyphs.First(); _canvas.Renderer.ForgetTextLayout(old.Value); old.Value.Dispose(); _glyphs.Remove(old.Key); }
                        glyph = new CanvasTextLayout(_canvas.Device, key.Item1, Format(cell.Flags), width + CellWidth, CellHeight);
                        _textRenderer.SetLineMetrics(glyph, _canvas.Device, Format(cell.Flags), CellHeight, cell.Flags); _glyphs.Add(key, glyph);
                    }
                    drawing.DrawTextLayout(glyph, px, py + 1, Color(_cursorForeground ?? cell.Background));
                }
            }
            else drawing.DrawRectangle(px + .5f, py + 1, cursorWidth - 1, CellHeight - 2, color);
        }
        TraceLatency("draw-end");
    }
    private void ClearCursorGlyphs()
    {
        foreach (var glyph in _glyphs.Values) { _glyphRenderer?.ForgetTextLayout(glyph); glyph.Dispose(); }
        _glyphs.Clear(); _glyphRenderer = null;
    }
    private void ResetLayers()
    {
        _backgroundLayer?.Dispose(); _textLayer?.Dispose(); _backgroundLayer = _textLayer = null;
        _forceRows = true;
    }
    private void PositionInput()
    {
        // The real text control anchors the IME candidate window at the terminal
        // caret. During composition it also shows preedit without sending it to PTY.
        double left = Math.Min(_frame.CursorX, (uint)Math.Max(0, GridColumns - 1)) * CellWidth;
        double top = Math.Min(_frame.CursorY, (uint)Math.Max(0, GridRows - 1)) * CellHeight;
        var margin = _input.Margin;
        if (margin.Left != left || margin.Top != top) _input.Margin = new Thickness(left, top, 0, 0);
        double width = _composing ? Math.Max(CellWidth * 2, Math.Max(0, (int)GridColumns - (int)_frame.CursorX) * CellWidth) : 2;
        double height = _composing ? CellHeight + 8 : 2;
        double opacity = _composing ? 1 : .01;
        if (_input.Width != width) _input.Width = width;
        if (_input.Height != height) _input.Height = height;
        if (_input.FontSize != _settings.FontSize) _input.FontSize = _settings.FontSize;
        if (_input.Opacity != opacity) _input.Opacity = opacity;
    }
    internal AccessibleTerminalText AccessibleText()
    {
        if (_accessibleText is not null) return _accessibleText;
        var text = new System.Text.StringBuilder(); var offsets = new int[_cells.Length + 1];
        int selectionStart = -1, selectionEnd = -1;
        for (int index = 0; index < _cells.Length; index++)
        {
            if (index > 0 && index % GridColumns == 0) text.Append('\n');
            var cell = _cells[index];
            offsets[index] = cell.Width == 0 && index % GridColumns > 0 ? offsets[index - 1] : text.Length;
            if (cell.Width > 0) text.Append(cell.Length == 0 ? " " : cell.GetText());
            if ((cell.Flags & 64) != 0) { if (selectionStart < 0) selectionStart = offsets[index]; selectionEnd = text.Length; }
        }
        offsets[^1] = text.Length;
        int cursor = offsets[Math.Clamp((int)(_frame.CursorY * GridColumns + _frame.CursorX), 0, offsets.Length - 1)];
        return _accessibleText = new(text.ToString(), offsets, GridColumns, GridRows, cursor, selectionStart, selectionEnd);
    }
    private Rect AccessibleGridBounds()
    {
        var bounds = TextPeer.GetBoundingRectangle();
        var position = _canvas.TransformToVisual(this).TransformPoint(new());
        double scale = XamlRoot?.RasterizationScale ?? 1;
        return new(bounds.X + position.X * scale, bounds.Y + position.Y * scale, GridColumns * CellWidth * scale, GridRows * CellHeight * scale);
    }
    internal int AccessibleCellAt(Point screenPoint)
    {
        var bounds = AccessibleGridBounds();
        var (X, Y) = _geometry.FromScreen(screenPoint.X, screenPoint.Y, bounds.X, bounds.Y);
        return _geometry.CellAt(X, Y);
    }
    internal double[] AccessibleRectangles(AccessibleTerminalText document, int start, int end)
    {
        if (start == end) return [];
        var result = new List<double>(); var bounds = AccessibleGridBounds(); double scale = XamlRoot?.RasterizationScale ?? 1;
        for (int row = 0; row < document.Rows; row++)
        {
            int first = -1, last = -1;
            for (int col = 0; col < document.Columns; col++)
            {
                int index = row * document.Columns + col;
                int offset = document.CellOffsets[index], next = index + 1;
                while (next < document.CellOffsets.Length - 1 && document.CellOffsets[next] == offset) next++;
                // A FindText range can start inside a combining cluster. Its
                // visible rectangle still covers the cluster's complete cell.
                if (offset >= end || document.CellOffsets[next] <= start) continue;
                if (first < 0) first = col; last = col;
            }
            if (first >= 0) result.AddRange([bounds.X + first * CellWidth * scale, bounds.Y + row * CellHeight * scale, (last - first + 1) * CellWidth * scale, CellHeight * scale]);
        }
        return result.ToArray();
    }
    internal void AccessibleSelect(AccessibleTerminalText document, int start, int end)
    {
        if (start == end) ClearSelection();
        else
        {
            int first = Array.FindLastIndex(document.CellOffsets, value => value <= start);
            while (first > 0 && document.CellOffsets[first - 1] == document.CellOffsets[first]) first--;
            int last = Array.FindLastIndex(document.CellOffsets, value => value < end);
            first = Math.Clamp(first, 0, _cells.Length - 1); last = Math.Clamp(Math.Max(first, last), first, _cells.Length - 1);
            _session?.Select(first, last);
            start = document.CellOffsets[first];
            int next = last + 1;
            while (next < document.CellOffsets.Length - 1 && document.CellOffsets[next] == document.CellOffsets[last]) next++;
            end = document.CellOffsets[next];
        }
        // UIA clients may ask GetSelection immediately, before the next render
        // captures the engine's dirty rows. Publish the accepted range now.
        _accessibleText = document with { SelectionStart = start == end ? -1 : start, SelectionEnd = start == end ? -1 : end };
        _textPeer?.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged); _canvas.Invalidate();
    }
    private void DrawUnderline(DirectXDrawingSession drawing, float x, float y, float width, uint style, Windows.UI.Color color)
    {
        if (style == 0 || width == 0) return;
        float baseline = y + CellHeight - 2;
        if (style is 1 or 2)
        {
            drawing.DrawLine(x, baseline, x + width, baseline, color);
            if (style == 2) drawing.DrawLine(x, baseline - 2, x + width, baseline - 2, color);
        }
        else if (style == 3)
        {
            // Phase depends on absolute x so adjacent cells form one continuous wave.
            float period = Math.Max(4, CellWidth / 2);
            for (float offset = 0; offset < width; offset += .5f)
            {
                float end = Math.Min(width, offset + .5f);
                float Wave(float position) => baseline - 1 + MathF.Sin(position * 2 * MathF.PI / period);
                drawing.DrawLine(x + offset, Wave(x + offset), x + end, Wave(x + end), color);
            }
        }
        else
        {
            float dash = style == 4 ? 1 : 3, step = dash + 2;
            for (float offset = 0; offset < width; offset += step)
                drawing.DrawLine(x + offset, baseline, x + Math.Min(width, offset + dash), baseline, color);
        }
    }
    internal async Task<bool> CheckRenderedColorJoins(string imagePath)
    {
        using var target = new CanvasRenderTarget(_canvas.Device, GridColumns * CellWidth, GridRows * CellHeight, _canvas.Dpi);
        using (var drawing = _canvas.Renderer.CreateDrawingSession(target)) Render(drawing);
        await target.SaveAsync(imagePath);
        var pixels = target.GetPixelColors();
        int stride = (int)target.SizeInPixels.Width;
        float scale = target.Dpi / 96;
        int checkedJoins = 0;
        for (int y = 0; y < _frame.Rows; y++)
            for (int x = 1; x < _frame.Columns; x++)
            {
                var cell = _cells[y * (int)_frame.Columns + x];
                var previous = _cells[y * (int)_frame.Columns + x - 1];
                if (previous.Background == _frame.Background || previous.Background == 0) continue;
                var text = cell.GetText();
                bool separator = text.Length == 1 && text[0] is '\ue0b0' or '\ue0b4';
                bool flatJoin = separator && cell.Foreground == previous.Background;
                bool sameBackground = cell.Background == previous.Background && !separator;
                if (!flatJoin && !sameBackground) continue;
                int boundary = (int)MathF.Round(x * CellWidth * scale);
                int top = (int)MathF.Round(y * CellHeight * scale);
                int height = (int)MathF.Round(CellHeight * scale);
                var expected = Color(previous.Background);
                var rows = flatJoin ? Enumerable.Range(2, Math.Max(0, height - 4)) : [0, height - 1];
                foreach (int py in rows)
                    if (pixels[(top + py) * stride + boundary - 1] != expected || pixels[(top + py) * stride + boundary] != expected)
                        return false;
                checkedJoins++;
            }
        return checkedJoins > 0;
    }
    internal CanvasRenderTarget CreateRenderTargetForCheck() => new(_canvas.Device, GridColumns * CellWidth, GridRows * CellHeight, _canvas.Dpi);
    internal Point ContentOriginForCheck(UIElement relativeTo) => _canvas.TransformToVisual(relativeTo).TransformPoint(new());
    internal void RenderFrameForCheck(CanvasRenderTarget target)
    {
        using var drawing = _canvas.Renderer.CreateDrawingSession(target); Render(drawing);
    }
    internal bool HasImage(uint imageId) => _snapshot?.Graphics.Any(g => g.ImageId == imageId) == true;
    internal bool ImageIsBlue(uint imageId) => _snapshot?.Graphics.Any(g => g.ImageId == imageId && g.Image.Pixels.Length >= 3
        && g.Image.Pixels[0] == 0 && g.Image.Pixels[1] == 0 && g.Image.Pixels[2] == 255) == true;
    internal async Task<bool> CheckRenderedImage(uint imageId, Windows.UI.Color expected, string imagePath)
    {
        using var target = new CanvasRenderTarget(_canvas.Device, GridColumns * CellWidth, GridRows * CellHeight, _canvas.Dpi);
        using (var drawing = _canvas.Renderer.CreateDrawingSession(target)) Render(drawing);
        await target.SaveAsync(imagePath);
        var graphic = _snapshot?.Graphics.FirstOrDefault(g => g.ImageId == imageId);
        if (graphic is null) return false;
        var scale = target.Dpi / 96;
        float x = graphic.Column * CellWidth + (graphic.OffsetX + graphic.PixelWidth / 2f) * CellWidth / _frame.CellWidth;
        float y = graphic.Row * CellHeight + (graphic.OffsetY + graphic.PixelHeight / 2f) * CellHeight / _frame.CellHeight;
        int px = (int)(x * scale), py = (int)(y * scale);
        if (px < 0 || py < 0 || px >= target.SizeInPixels.Width || py >= target.SizeInPixels.Height) return false;
        var actual = target.GetPixelColors()[py * (int)target.SizeInPixels.Width + px];
        return Math.Abs(actual.R - expected.R) <= 2 && Math.Abs(actual.G - expected.G) <= 2 && Math.Abs(actual.B - expected.B) <= 2;
    }
    private int CellAt(Point point)
        => _geometry.CellAt(point.X, point.Y);
    private void PointerPressedOnCanvas(object sender, PointerRoutedEventArgs e)
    {
        if (_resizePending || _pendingGeometry is not null) { _canvas.Invalidate(); e.Handled = true; return; }
        FocusTerminal();
        var point = e.GetCurrentPoint(_canvas);
        ValidationObserved?.Invoke("pointer-press", $"middle={point.Properties.IsMiddleButtonPressed}; position={point.Position}");
        if (_middleScroll.Active) { StopAutoScroll(); e.Handled = true; return; }
        if (Down(VirtualKey.Control) && point.Properties.IsLeftButtonPressed && OpenHyperlink(point.Position)) { e.Handled = true; return; }
        if (ReportMouse(e, 0)) { _canvas.CapturePointer(e.Pointer); e.Handled = true; return; }
        if (point.Properties.IsMiddleButtonPressed)
        {
            if (_canvas.CapturePointer(e.Pointer)) BeginMiddleScroll(point.Position);
            e.Handled = true; return;
        }
        if (point.Properties.IsRightButtonPressed) { UiTasks.Observe(PasteClipboard()); e.Handled = true; return; }
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragPosition = point.Position;
        Gesture(0, point.Position);
        _selecting = true; _canvas.CapturePointer(e.Pointer); _canvas.Invalidate(); e.Handled = true;
    }
    private bool OpenHyperlink(Point point)
    {
        if (_session is not { } session) return false;
        int cell = CellAt(point);
        _ = OpenHyperlink(session, (ushort)(cell % GridColumns), (ushort)(cell / GridColumns)); return true;
    }
    private async Task OpenHyperlink(ITerminalSession session, ushort column, ushort row)
    {
        string target = await session.HyperlinkAsync(column, row);
        if (_disposed || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) return;
        if (uri.Scheme is "http" or "https" && HyperlinkRequested is not null) HyperlinkRequested(uri);
        else await Launcher.LaunchUriAsync(uri);
    }
    private void PointerMovedOnCanvas(object sender, PointerRoutedEventArgs e)
    {
        if (_middleScroll.Active) { MoveMiddleScroll(e.GetCurrentPoint(_canvas).Position); e.Handled = true; return; }
        if (!_selecting)
        {
            if (ReportMouse(e, 2)) return;
            int cell = CellAt(e.GetCurrentPoint(_canvas).Position);
            if (_session is not { } session || (_hoverCell == cell && _hoverVersion == session.Version)) return;
            _hoverCell = cell;
            if (!_hoverPending) _ = UpdateHover(session);
            return;
        }
        _dragPosition = e.GetCurrentPoint(_canvas).Position;
        Gesture(2, _dragPosition); _canvas.Invalidate();
    }
    private void Gesture(int action, Point point)
    {
        if (_pendingGeometry is not null) return;
        var (X, Y) = _geometry.ToNative(point.X, point.Y);
        _session?.QueueSelectionGesture(action, X, Y, Down(VirtualKey.Menu));
    }
    internal void DragSelectionForCheck(int row, int firstColumn, int lastColumn)
    {
        ClearSelection();
        var start = new Point((firstColumn + .1) * CellWidth, (row + .5) * CellHeight);
        var end = new Point((lastColumn + .9) * CellWidth, (row + .5) * CellHeight);
        Gesture(0, start); Gesture(2, end); Gesture(1, end);
    }
    private async Task UpdateHover(ITerminalSession session)
    {
        _hoverPending = true;
        try
        {
            while (!_disposed)
            {
                int cell = _hoverCell;
                if (cell < 0) return;
                int version = session.Version;
                var link = await session.HyperlinkAsync((ushort)(cell % GridColumns), (ushort)(cell / GridColumns));
                if (_disposed || _selecting || _middleScroll.Active) return;
                if (cell != _hoverCell) continue;
                _hoverVersion = version;
                ProtectedCursor = InputSystemCursor.Create(string.IsNullOrEmpty(link) ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Hand);
                ToolTipService.SetToolTip(_canvas, string.IsNullOrEmpty(link) ? null : "Ctrl+click · " + link);
                return;
            }
        }
        finally { _hoverPending = false; }
    }
    private bool ReportMouse(PointerRoutedEventArgs e, int action, int? wheel = null)
    {
        if (Down(VirtualKey.Shift)) return false;
        var point = e.GetCurrentPoint(_canvas); var props = point.Properties;
        int button = wheel ?? (props.IsLeftButtonPressed ? 1 : props.IsRightButtonPressed ? 2 : props.IsMiddleButtonPressed ? 3 : 0);
        if (action == 1) button = props.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => 1,
            PointerUpdateKind.RightButtonReleased => 2,
            PointerUpdateKind.MiddleButtonReleased => 3,
            _ => 1
        };
        return ReportMouseAt(point.Position, action, button, (Down(VirtualKey.Control) ? 2u : 0) | (Down(VirtualKey.Menu) ? 4u : 0));
    }
    private bool ReportMouseAt(Point point, int action, int button, uint modifiers)
    {
        if (_pendingGeometry is not null) return false;
        var (X, Y) = _geometry.ToNative(point.X, point.Y);
        return _session?.Mouse(action, button, modifiers, X, Y) ?? false;
    }
    private void ClearSelection() { _selecting = false; _session?.Select(-1, -1); }
    private void SendInputText()
    {
        if (_syntheticInputOnlyForCheck) return;
        if (_disposed || _inputClosing || _inputSuspended || _windowSuspended) return;
        if (_resettingText || _composing || _input.Text.Length == 0) return;
        var pending = _pendingTextKey;
        _pendingTextKey = null;
        if (pending is { } key && !_composing)
        {
            _session?.Key(key.Key, key.Modifiers, _input.Text, key.Action, key.Unshifted, key.Consumed);
            _pressedKeys[key.Key] = key;
        }
        else _session?.Key(0, 0, _input.Text);
        _resettingText = true; _input.Text = ""; _resettingText = false; ClearSelection();
    }
    private void BeginComposition() { if (_disposed || _inputClosing || _inputSuspended || _windowSuspended) return; _compositionGeneration++; _composing = true; _pendingTextKey = null; PositionInput(); ValidationObserved?.Invoke("composition-start", ""); }
    private void EndComposition()
    {
        ValidationObserved?.Invoke("composition-end", _input.Text);
        int generation = _compositionGeneration;
        // RichEdit can raise composition-ended before TextBox.Text reflects
        // cancellation. Keep suppressing TextChanged until its notification
        // stack unwinds, then send only the final committed text.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_composing || generation != _compositionGeneration) return;
            ValidationObserved?.Invoke("composition-settled", _input.Text);
            _composing = false; SendInputText(); PositionInput();
        });
    }
    internal void SetPreeditForCheck(string text) { if (!_composing) BeginComposition(); _input.Text = text; }
    internal void CompletePreeditForCheck(string text, bool endBeforeTextChange = false)
    {
        if (endBeforeTextChange) EndComposition();
        _input.Text = text;
        if (!endBeforeTextChange) EndComposition();
    }
    public void CopySelection()
        => UiTasks.Observe(CopySelectionAsync());
    internal async Task CopySelectionAsync()
    { if (_session is not null) SetClipboardText(await _session.CopySelectionAsync()); }
    private static void SetClipboardText(string text)
    {
        if (text.Length == 0) return;
        try { var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { System.Diagnostics.Debug.WriteLine(ex); }
    }
    private async Task CopyOrInterrupt(ITerminalSession session, PendingKey pending, string text)
    { SetClipboardText(await session.CopyOrKeyAsync(pending.Key, pending.Modifiers, text, pending.Action, pending.Unshifted, pending.Consumed)); }
    public Task PasteClipboard()
    {
        try
        {
            _session?.PasteClipboard();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { System.Diagnostics.Debug.WriteLine(ex); }
        FocusTerminal();
        return Task.CompletedTask;
    }
    public void ChangeFontSize(float delta)
    {
        _settings.FontSize = Math.Clamp(_settings.FontSize + delta, 9, 32);
        FontSizeChanged?.Invoke();
    }
    public event Action? FontSizeChanged;
    public static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    private void KeyDownOnInput(object sender, KeyRoutedEventArgs e)
    {
        if (_syntheticInputOnlyForCheck || _disposed || _inputClosing || _inputSuspended || _windowSuspended) { e.Handled = true; return; }
        if (MiddleScrollKeyDown((uint)e.Key)) { e.Handled = true; return; }
        ValidationObserved?.Invoke("input-key", $"key={e.Key}; original={e.OriginalKey}; scan={e.KeyStatus.ScanCode}; composing={_composing}; modifiers={CurrentModifiers()}");
        uint virtualKey = (uint)e.Key;
        if (WindowsKeyboard.IsImeKey(virtualKey) || WindowsKeyboard.IsImeKey((uint)e.OriginalKey))
        { _pendingTextKey = null; return; }
        uint key = WindowsKeyboard.PhysicalKey(virtualKey, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
        _pendingTextKey = null;
        if (_composing) return;
        bool ctrl = Down(VirtualKey.Control), alt = Down(VirtualKey.Menu), shift = Down(VirtualKey.Shift);
        bool altGr = ctrl && alt && Down(VirtualKey.RightMenu);
        uint shortcutModifiers = (shift ? 1u : 0) | (ctrl ? 2u : 0) | (alt ? 4u : 0) | (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? 8u : 0);
        var binding = altGr ? null : Keybindings.Match(_settings, virtualKey, shortcutModifiers);
        if (binding == "copy") { CopySelection(); e.Handled = true; return; }
        if (binding == "paste" || (shift && e.Key == VirtualKey.Insert)) { UiTasks.Observe(PasteClipboard()); e.Handled = true; return; }
        if (ctrl && !alt && e.Key == VirtualKey.C && _session is { } session)
        {
            var (Text, Unshifted) = WindowsKeyboard.Translate(virtualKey, e.KeyStatus.ScanCode);
            var copyKey = new PendingKey(key, CurrentModifiers(), e.KeyStatus.WasKeyDown ? TerminalKeyAction.Repeat : TerminalKeyAction.Press, Unshifted, shift ? 1u : 0);
            _pressedKeys[key] = copyKey; UiTasks.Observe(CopyOrInterrupt(session, copyKey, Text)); e.Handled = true; return;
        }
        if (binding is "scrollUp" or "scrollDown") { _session?.Scroll(binding == "scrollUp" ? -GridRows : GridRows); e.Handled = true; return; }
        bool modifierKey = virtualKey is 16 or 17 or 18 or 91 or 92;
        bool special = modifierKey || virtualKey is 8 or 9 or 13 or 19 or 20 or 27 or >= 33 and <= 46 or >= 112 and <= 145;
        bool shortcut = ctrl || alt;
        // AltGr produces text through TextChanged instead of Ctrl+Alt shortcuts.
        uint modifiers = CurrentModifiers();
        var translated = WindowsKeyboard.Translate(virtualKey, e.KeyStatus.ScanCode);
        var pending = new PendingKey(key, modifiers, e.KeyStatus.WasKeyDown ? TerminalKeyAction.Repeat : TerminalKeyAction.Press,
            translated.Unshifted, shift ? 1u : 0);
        if ((!special && !shortcut) || altGr) { _pendingTextKey = pending with { Modifiers = altGr ? modifiers & ~6u : modifiers, AltGr = altGr }; return; }
        _pressedKeys[key] = pending;
        _session?.Key(key, modifiers, translated.Text, pending.Action, pending.Unshifted, pending.Consumed);
        if (!modifierKey) { ClearSelection(); e.Handled = true; }
    }
    private void KeyUpOnInput(object sender, KeyRoutedEventArgs e)
    {
        if (_syntheticInputOnlyForCheck || _disposed || _inputClosing || _inputSuspended || _windowSuspended) { e.Handled = true; return; }
        uint physical = WindowsKeyboard.PhysicalKey((uint)e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
        if (_pendingTextKey?.Key == physical) _pendingTextKey = null;
        if (!_pressedKeys.Remove(physical, out var key)) return;
        uint modifiers = CurrentModifiers();
        if (key.AltGr) modifiers &= ~6u;
        _session?.Key(key.Key, modifiers, action: TerminalKeyAction.Release, unshiftedCodepoint: key.Unshifted, consumedModifiers: key.Consumed);
    }
    private static uint CurrentModifiers() => (Down(VirtualKey.Shift) ? 1u : 0) | (Down(VirtualKey.Control) ? 2u : 0) | (Down(VirtualKey.Menu) ? 4u : 0)
        | (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? 8u : 0)
        | ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.CapitalLock) & CoreVirtualKeyStates.Locked) != 0 ? 16u : 0)
        | ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.NumberKeyLock) & CoreVirtualKeyStates.Locked) != 0 ? 32u : 0);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseInputForClose(); _pendingScrollRow = null;
        if (_remote is { } remote) { remote.FrameAvailable -= OnRemoteFrame; SetCaptureVisible(false); }
        _timer.Stop();
        _powerline?.Dispose();
        _images.Dispose(); _textRenderer.Dispose(); ResetLayers();
        StopObservingRoot();
        foreach (var format in _formats.Values) format.Dispose();
        ClearCursorGlyphs();
        _formats.Clear(); _canvas.RemoveFromVisualTree();
        _testSoftwareDevice?.Dispose();
        if (_remote is not null)
        {
            TerminalCellPool.Shared.Return(_cells);
            if (_resizeCells is { } pending && !ReferenceEquals(pending, _cells)) TerminalCellPool.Shared.Return(pending);
            _cells = []; _resizeCells = null;
        }
    }
}
