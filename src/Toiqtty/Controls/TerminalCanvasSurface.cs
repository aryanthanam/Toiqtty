using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Toiqtty.Controls;

/// <summary>Event-driven native Direct3D presentation independent of XAML's redraw timer.</summary>
internal sealed class TerminalCanvasSurface : Grid, IDisposable
{
    internal readonly record struct PresentationStats(long DrawAttempts, long SubmittedFrames, long AcquireMisses, long PresentMisses, long ReadyWaits, long ReadyTimeouts);
    private long _drawAttempts, _submittedFrames, _acquireMisses, _presentMisses, _readyWaits, _readyTimeouts;
    private bool _waitingForFrame, _waitingInteractive;
    internal PresentationStats PresentationForCheck => new(_drawAttempts, _submittedFrames, _acquireMisses, _presentMisses, _readyWaits, _readyTimeouts);
    internal bool CompositionRetryForCheck { get; set; }
    private readonly Grid _panel = new();
    private readonly DispatcherQueue _dispatcher;
    private DirectXRenderer? _renderer;
    private CanvasDevice? _device, _customDevice;
    private float _bufferWidth, _bufferHeight, _bufferDpi, _dpiScale = 1;
    private bool _queued, _suspended, _disposed, _resourcesNeeded = true, _compositionPending;
    private bool _queuedBulk;
    private int _drawGeneration, _drawFailures;
    internal long ImmediatePresentsForCheck, BulkPresentsForCheck;
    /// <summary>Output-driven redraws wait for the compositor tick (at most one per display
    /// refresh). Input-driven redraws are never delayed. Off by default: unpaced output
    /// redraws as soon as each frame is ready, at a higher CPU cost.</summary>
    internal bool PaceBulkFrames { get; set; }
    internal bool ReserveResizeForCheck { get; set; } = true;
    internal uint FrameLatencyForCheck = 2, PresentIntervalForCheck;
    internal bool CompositionPendingForCheck => _compositionPending;
    internal event Action<DirectXDrawingSession>? Draw;
    internal event Action? CreateResources;
    internal event Action? Presented;
    internal Color ClearColor { get; set; }
    private bool _transparent;
    internal bool Transparent
    {
        get => _transparent;
        set { if (_transparent == value) return; _transparent = value; Invalidate(); }
    }
    internal CanvasDevice Device => _device ?? throw new InvalidOperationException("The terminal graphics device is not ready.");
    internal bool ReadyToDraw => !_disposed && _device is not null;
    internal DirectXRenderer Renderer => _renderer ?? throw new InvalidOperationException("The terminal renderer is not ready.");
    internal long RetainedGpuBytes
    {
        get
        {
            if (_renderer is null) return 0; var stats = _renderer.GetStats();
            return checked((long)(stats.TextureBytes + stats.VertexBufferBytes));
        }
    }
    internal float Dpi => (float)(96 * (XamlRoot?.RasterizationScale ?? 1)) * _dpiScale;
    internal float DpiScale
    {
        get => _dpiScale;
        set { if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); if (value == _dpiScale) return; _dpiScale = value; _resourcesNeeded = true; Invalidate(); }
    }
    internal CanvasDevice? CustomDevice
    {
        get => _customDevice;
        set { if (ReferenceEquals(value, _customDevice)) return; _customDevice = value; ResetDevice(); Invalidate(); }
    }
    internal TerminalCanvasSurface()
    {
        _dispatcher = DispatcherQueue;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(_panel);
        // The owning view controls logical suspension. Routed child unload
        // events can arrive after a same-root reparent and must not override a
        // subsequent owner Resume. IsLoaded still guards drawing while detached.
        Loaded += (_, _) => Invalidate();
        SizeChanged += (_, _) => Invalidate();
    }
    internal void Suspend() { _suspended = true; CancelComposition(); _drawGeneration++; _queued = false; _queuedBulk = false; }
    internal void Resume() { if (_disposed) return; _suspended = false; Invalidate(); }
    internal void Invalidate(bool immediate = true)
    {
        if (_disposed || _suspended) return;
        if (_waitingForFrame) { _waitingInteractive |= immediate; return; }
        if (_queued)
        {
            if (immediate && _queuedBulk) QueueDraw(false, DispatcherQueuePriority.Normal);
            return;
        }
        if (!immediate)
        {
            // Opaque presentation already has a DXGI readiness event. Do not
            // add an unrelated XAML display tick before admitting its work.
            // Drawing surfaces have no DXGI present and retain their cadence.
            if (!PaceBulkFrames && !_transparent && Opacity == 1)
            { CancelComposition(); QueueDraw(true, DispatcherQueuePriority.Low); return; }
            QueueComposition();
            return;
        }
        CancelComposition();
        QueueDraw(false, DispatcherQueuePriority.Normal);
    }
    private void QueueDraw(bool bulk, DispatcherQueuePriority priority)
    {
        int generation = ++_drawGeneration;
        _queued = true; _queuedBulk = bulk;
        if (!_dispatcher.TryEnqueue(priority, () =>
        {
            if (generation != _drawGeneration) return;
            _queued = false; _queuedBulk = false; DrawFrame(bulk);
        })) { _queued = false; _queuedBulk = false; }
    }
    private void CancelComposition()
    {
        if (!_compositionPending) return;
        _compositionPending = false; CompositionTarget.Rendering -= CompositionRendering;
    }
    private void QueueComposition()
    {
        if (!_compositionPending) { _compositionPending = true; CompositionTarget.Rendering += CompositionRendering; }
    }
    private void CompositionRendering(object? sender, object args)
    {
        if (!_compositionPending) return;
        CancelComposition();
        // Yield between panes so interactive work can run ahead of a full
        // window's bulk redraws rather than waiting through every render handler.
        if (!_queued) QueueDraw(true, DispatcherQueuePriority.Low);
    }
    private void DrawFrame(bool bulk)
    {
        if (_waitingForFrame) { _waitingInteractive |= !bulk; return; }
        if (_disposed || _suspended || !IsLoaded || !double.IsFinite(ActualWidth) || !double.IsFinite(ActualHeight)
            || ActualWidth < 1 || ActualHeight < 1) return;
        try
        {
            if (_device is null)
            {
                Models.StartupTrace.Mark("graphics-device-begin");
                _device = _customDevice ?? CanvasDevice.GetSharedDevice();
                _device.DeviceLost += DeviceLost;
                _resourcesNeeded = true;
                Models.StartupTrace.Mark("graphics-device-end");
            }
            float width = (float)ActualWidth, height = (float)ActualHeight, dpi = Dpi;
            if (_bufferDpi != dpi) _resourcesNeeded = true;
            if (_renderer is null)
            {
                Models.StartupTrace.Mark("graphics-renderer-begin");
                try { _renderer = new DirectXRenderer(_device); }
                catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x887a0004))
                {
                    // A DX12-capable adapter is required. Unsupported hardware
                    // uses the shared WARP provider instead of losing the pane.
                    _device.DeviceLost -= DeviceLost;
                    _device = CanvasDevice.GetSharedDevice(true);
                    _device.DeviceLost += DeviceLost;
                    _renderer = new DirectXRenderer(_device);
                    _resourcesNeeded = true;
                }
                Models.StartupTrace.Mark("graphics-renderer-end");
            }
            _renderer.ReserveResizeForCheck = ReserveResizeForCheck;
            _renderer.SetPresentationPolicyForCheck(FrameLatencyForCheck, PresentIntervalForCheck);
            if (_resourcesNeeded)
            {
                Models.StartupTrace.Mark("graphics-resources-begin");
                _resourcesNeeded = false; CreateResources?.Invoke();
                Models.StartupTrace.Mark("graphics-resources-end");
            }
            _renderer.Resize(checked((uint)Math.Max(1, Math.Round((double)width * dpi / 96))), checked((uint)Math.Max(1, Math.Round((double)height * dpi / 96))), dpi, _transparent);
            _renderer.Attach(_panel, Opacity < 1);
            _bufferWidth = width; _bufferHeight = height; _bufferDpi = dpi;
            // Never wait for another pane or a display interval on the XAML
            // thread. Wait asynchronously for DXGI queue capacity when full.
            if (!_renderer.TryAcquireFrame())
            {
                _acquireMisses++;
                if (CompositionRetryForCheck) { QueueComposition(); return; }
                _readyWaits++;
                _waitingForFrame = true; _waitingInteractive = !bulk;
                var renderer = _renderer;
                renderer.WaitForFrame(_dispatcher, timedOut =>
                {
                    if (!ReferenceEquals(renderer, _renderer)) return;
                    _waitingForFrame = false;
                    if (timedOut) _readyTimeouts++;
                    bool interactive = _waitingInteractive; _waitingInteractive = false;
                    if (!_disposed && !_suspended) QueueDraw(!interactive, interactive ? DispatcherQueuePriority.Normal : DispatcherQueuePriority.Low);
                });
                return;
            }
            _drawAttempts++;
            using (var drawing = _renderer.CreateDrawingSession(width, height)) { drawing.Clear(ClearColor); Draw?.Invoke(drawing); }
            // Let the compositor use the newest completed buffer without waiting
            // for a vsync on the WinUI input/layout thread.
            if (!_renderer.Present()) { _presentMisses++; Invalidate(false); return; }
            _submittedFrames++; _drawFailures = 0;
            if (bulk) BulkPresentsForCheck++; else ImmediatePresentsForCheck++;
            Presented?.Invoke();
        }
        catch (Exception ex) when (_device is { } device && device.IsDeviceLost(ex.HResult))
        {
            _device.RaiseDeviceLost();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or OverflowException or ArgumentException)
        {
            // One failing pane must not end the process and every session in it.
            // Rebuild native presentation state and retry a bounded number of times.
            if (_drawFailures++ == 0) System.Diagnostics.Debug.WriteLine(ex);
            if (ex is System.Runtime.InteropServices.COMException) ResetDevice();
            if (_drawFailures <= 3) Invalidate(false);
        }
    }
    private void DeviceLost(CanvasDevice sender, object args)
        => _dispatcher.TryEnqueue(() => { if (_disposed || !ReferenceEquals(sender, _device)) return; ResetDevice(); Invalidate(); });
    internal void InjectDeviceLostForCheck()
    {
        if (_device is { } device) DeviceLost(device, null!);
    }
    private void ResetDevice()
    {
        if (_device is { } device) device.DeviceLost -= DeviceLost;
        DirectXRenderer.Detach(_panel);
        _renderer?.Dispose(); _renderer = null; _device = null;
        _waitingForFrame = false; _waitingInteractive = false;
        _bufferWidth = _bufferHeight = _bufferDpi = 0; _resourcesNeeded = true;
        // Shared devices and explicit test devices are owned by their providers.
    }
    internal void RemoveFromVisualTree() => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; CancelComposition(); ResetDevice(); Children.Clear();
    }
}
