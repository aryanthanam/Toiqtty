using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.Win32.SafeHandles;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Dispatching;

namespace Toiqtty.Controls;

/// <summary>Owns native D3D12 composition; Win2D supplies DirectWrite layouts.</summary>
internal sealed unsafe class DirectXRenderer : IDisposable
{
    private readonly RendererHandle _handle;
    private DirectXDrawingSession? _active;
    private bool _disposed;
    private WaitHandle? _frameEvent;
    private RegisteredWaitHandle? _frameWait;
    private int _waitGeneration;
    private readonly DirectXPresentation _presentation;
    private uint _width, _height;
    private float _dpi;
    private bool _transparent;
    internal RendererHandle Handle => _handle;
    internal static void ShutdownDevices() => Native.ShutdownDevices();
    internal sealed class PreparedDeviceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal PreparedDeviceHandle(nint pointer) : base(true) => SetHandle(pointer);
        protected override bool ReleaseHandle() { Native.ReleasePreparedDevice(handle); return true; }
    }
    internal static PreparedDeviceHandle PrepareDevice(CanvasDevice device)
    {
        Check(Native.PrepareDevice(((WinRT.IWinRTObject)device).NativeObject.ThisPtr, out var pointer));
        GC.KeepAlive(device);
        return new(pointer);
    }
    internal static void InstallDevice(PreparedDeviceHandle device) => Check(Native.InstallDevice(device));
    internal DirectXRenderer(CanvasDevice device)
    {
        Check(Native.Create(((WinRT.IWinRTObject)device).NativeObject.ThisPtr, out var pointer));
        _handle = new(pointer); _presentation = new(_handle); GC.KeepAlive(device);
    }
    internal void Resize(uint width, uint height, float dpi, bool transparent)
    {
        if (_width != 0 && width == _width && height == _height && dpi == _dpi && transparent == _transparent) return;
        // A failed resize may have released the buffers; never skip the retry.
        _width = 0;
        Check(Native.Resize(_handle, width, height, dpi, transparent ? 1 : 0));
        _presentation.Resize(width, height);
        _width = width; _height = height; _dpi = dpi; _transparent = transparent;
    }
    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private bool _reserveResize = true;
    internal bool ReserveResizeForCheck { get => _reserveResize; set { if (value == _reserveResize) return; Native.ResizeCapacityForCheck(_handle, value ? 1 : 0); _reserveResize = value; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ResizeStats { public ulong BufferResizes, LogicalResizes; public uint Width, Height, CapacityWidth, CapacityHeight; }
    internal ResizeStats ResizeStatsForCheck { get { Native.ResizeStats(_handle, out var value); return value; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorGlyphStats { public ulong Hits, Misses; public uint Available, Enabled; }
    internal ColorGlyphStats ColorGlyphsForCheck { get { Native.ColorGlyphs(_handle, out var value); return value; } }
    internal ulong PreparedLayoutsForCheck => Native.PreparedLayouts(_handle);
    [StructLayout(LayoutKind.Sequential)]
    internal struct PresentResults { public ulong Calls, Ok, Occluded, Busy, OtherSuccess; public int LastResult; public uint Reserved; }
    internal PresentResults PresentResultsForCheck { get { Native.PresentResultsForCheck(_handle, out var value); return value; } }
    private uint _frameLatency = 2, _presentInterval;
    internal void SetPresentationPolicyForCheck(uint latency, uint interval)
    {
        if (latency == _frameLatency && interval == _presentInterval) return;
        Check(Native.PresentationPolicyForCheck(_handle, latency, interval)); _frameLatency = latency; _presentInterval = interval;
    }
    internal void Attach(FrameworkElement panel, bool composited = false)
        => _presentation.Attach(panel, _transparent || composited || panel.Opacity < 1);
    internal static void Detach(FrameworkElement panel) => DirectXPresentation.Detach(panel);
    internal uint PresentationSurfaceCreations => _presentation.SurfaceCreations;
    internal string SwapchainAddressForCheck
    {
        get
        {
            Check(Native.Swapchain(_handle, out var pointer));
            try { return "0x" + pointer.ToString("X"); }
            finally { Marshal.Release(pointer); }
        }
    }
    private string? _adapterName;
    internal string AdapterNameForCheck
    {
        get
        {
            if (_adapterName is not null) return _adapterName;
            Span<byte> name = stackalloc byte[512]; uint length;
            fixed (byte* bytes = name) length = Native.AdapterName(_handle, bytes, (uint)name.Length);
            return _adapterName = System.Text.Encoding.UTF8.GetString(name[..checked((int)Math.Min(length, (uint)name.Length))]);
        }
    }
    internal void BeginFrame() => Check(Native.BeginFrame(_handle));
    internal bool TryAcquireFrame()
    {
        if (_presentation.ActiveDrawingSurface is null) { int result = Native.AcquireFrame(_handle); Check(result); if (result != 0) return false; }
        BeginFrame(); return true;
    }
    internal bool Present()
    {
        if (_active is not null) throw new InvalidOperationException("A drawing session is still active.");
        if (_presentation.ActiveDrawingSurface is { } surface) { Check(Native.PresentSurface(_handle, ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr)); return true; }
        int result = Native.Present(_handle); Check(result); return result == 0;
    }
    internal void WaitForFrame(DispatcherQueue dispatcher, Action<bool> available)
    {
        if (_frameWait is not null) return;
        if (_frameEvent is null)
        {
            Check(Native.FrameEvent(_handle, out var handle));
            _frameEvent = new FrameEvent(handle);
        }
        int generation = ++_waitGeneration;
        // The OS waits without reserving a managed worker. The callback only
        // posts; all renderer state and readiness acknowledgement stay on XAML.
        _frameWait = ThreadPool.RegisterWaitForSingleObject(_frameEvent, (_, timedOut) => dispatcher.TryEnqueue(() =>
        {
            if (_disposed || generation != _waitGeneration) return;
            _frameWait?.Unregister(null); _frameWait = null;
            if (!timedOut) Native.FrameAvailable(_handle);
            available(timedOut);
        }), null, 1000, true);
    }
    private sealed class FrameEvent : WaitHandle
    {
        internal FrameEvent(SafeWaitHandle handle) { SafeWaitHandle = handle; }
    }
    internal DirectXBitmap CreateBitmap(byte[] pixels, int width, int height)
    {
        if (pixels.Length != checked(width * height * 4)) throw new ArgumentException("Expected premultiplied BGRA pixels.", nameof(pixels));
        fixed (byte* data = pixels) { Check(Native.TextureCreate(_handle, checked((uint)width), checked((uint)height), 96, data, 0, out var texture)); return new(this, texture, width, height, 96); }
    }
    internal DirectXBitmap CreateAliasForCheck(DirectXBitmap image)
    {
        Check(Native.TextureAliasForCheck(_handle, image.Handle, out var texture));
        return new(this, texture, (int)image.SizeInPixels.Width, (int)image.SizeInPixels.Height, image.Dpi);
    }
    internal DirectXRenderTarget CreateRenderTarget(float width, float height, float dpi)
    {
        int w = checked((int)Math.Max(1, Math.Round((double)width * dpi / 96))), h = checked((int)Math.Max(1, Math.Round((double)height * dpi / 96)));
        Check(Native.TextureCreate(_handle, checked((uint)w), checked((uint)h), dpi, null, 1, out var texture));
        return new(this, texture, w, h, dpi);
    }
    internal DirectXDrawingSession CreateDrawingSession(float width, float height) => new(this, null, new(0, 0, width, height));
    internal DirectXDrawingSession CreateDrawingSession(CanvasRenderTarget target)
    {
        Check(Native.TextureFromBitmap(_handle, ((WinRT.IWinRTObject)target).NativeObject.ThisPtr, target.Dpi, out var pointer));
        var wrapped = new DirectXRenderTarget(this, pointer, (int)target.SizeInPixels.Width, (int)target.SizeInPixels.Height, target.Dpi);
        return new(this, wrapped, new(0, 0, target.Size.Width, target.Size.Height), ownsTarget: true);
    }
    internal void Activate(DirectXDrawingSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_active, session)) return;
        if (_active is not null) { Check(Native.End(_handle)); _active = null; }
        Check(Native.Begin(_handle, session.Target?.Handle.DangerousGetHandle() ?? 0)); _active = session;
        Check(Native.Clip(_handle, new(session.Clip)));
    }
    internal void Finish(DirectXDrawingSession session)
    {
        if (!ReferenceEquals(_active, session)) return;
        try { Check(Native.End(_handle)); } finally { _active = null; }
    }
    internal Stats GetStats() { Native.GetStats(_handle, out var result); result.TextureBytes += _presentation.TextureBytes; return result; }
    internal Profile GetProfile() { Native.GetProfile(_handle, out var result); return result; }
    internal LifetimeStats GetLifetimeStats() { Native.GetLifetimeStats(_handle, out var result); return result; }
    internal void ReadBackbufferForCheck(byte[] destination)
    { fixed (byte* pixels = destination) Check(Native.ReadPixelsForCheck(_handle, pixels, (ulong)destination.Length)); }
    internal void SetTextModeForCheck(bool layout) => Native.TextModeForCheck(_handle, layout ? 1 : 0);
    internal void SetQuadModeForCheck(bool expanded) => Native.QuadModeForCheck(_handle, expanded ? 1 : 0);
    internal uint LayoutScratchCapacityForCheck => Native.LayoutScratchCapacity(_handle);
    internal void SetAtlasHashModeForCheck(bool legacy) => Check(Native.AtlasHashModeForCheck(_handle, legacy ? 1 : 0));
    [StructLayout(LayoutKind.Sequential)]
    internal struct AtlasBuckets { public uint Entries, Buckets, Occupied, MaxChain; }
    internal AtlasBuckets AtlasBucketsForCheck { get { Native.AtlasBuckets(_handle, out var value); return value; } }
    internal void ForgetTextLayout(CanvasTextLayout layout)
    {
        if (_disposed) return;
        Check(Native.ForgetText(_handle, ((WinRT.IWinRTObject)layout).NativeObject.ThisPtr)); GC.KeepAlive(layout);
    }
    internal void ForgetTextLayout(TextLayoutHandle layout)
    { if (!_disposed) Check(Native.ForgetLayout(_handle, layout)); }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FontFeature(uint tag, uint value) { internal readonly uint Tag = tag, Value = value; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct LayoutMetrics { internal float AdvanceError; internal uint SpacingCalls; }
    internal TextFormatHandle CreateTerminalFormat(CanvasTextLayout probe)
    {
        int result = Native.TerminalFormat(((WinRT.IWinRTObject)probe).NativeObject.ThisPtr, out var format);
        GC.KeepAlive(probe); Check(result); return new(format);
    }
    internal static bool AsciiShapesAvailable => Native.AsciiShapeAvailable() != 0;
    /// <summary>Rasterizes a format's printable ASCII into the atlas between frames.</summary>
    internal uint WarmAsciiGlyphs(TextFormatHandle format, float dpi)
    {
        int result = Native.AsciiWarm(_handle, format, dpi, out uint warmed);
        Check(result); return warmed;
    }
    internal bool TryPrepareAsciiText(TextFormatHandle format, ReadOnlySpan<byte> text, FontFeature[] features)
    {
        fixed (byte* textData = text)
        fixed (FontFeature* featureData = features)
        {
            int result = Native.AsciiPrepare(format, textData, (uint)text.Length, featureData, (uint)features.Length);
            Check(result); return result == 0;
        }
    }
    internal TextShapeHandle? TryCreateAsciiShape(TextFormatHandle format, string text, FontFeature[] features, float cellWidth)
    {
        fixed (char* textData = text)
        fixed (FontFeature* featureData = features)
        {
            int result = Native.AsciiShape(_handle, format, textData, (uint)text.Length, featureData, (uint)features.Length, cellWidth, out var shape, out ulong bytes);
            Check(result); return result == 1 ? null : new(shape, checked((long)bytes));
        }
    }
    internal TextLayoutHandle CreateTerminalLayout(TextFormatHandle format, string text, string widths, FontFeature[] features,
        float cellWidth, float cellHeight, float advance, bool batchSpacing, out LayoutMetrics metrics)
    {
        fixed (char* textData = text)
        fixed (char* widthData = widths)
        fixed (FontFeature* featureData = features)
        {
            int result = Native.TerminalLayout(_handle, format, textData, (uint)text.Length,
                widthData, (uint)widths.Length, featureData, (uint)features.Length, cellWidth, cellHeight, advance, batchSpacing ? 1 : 0, out var layout, out metrics);
            Check(result); return new(layout);
        }
    }
    internal string DebugMessagesForCheck()
    {
        uint length = Native.DebugMessages(_handle, null, 0);
        if (length == 0) return "";
        var buffer = new byte[Math.Min(length, 1024 * 1024)];
        fixed (byte* data = buffer) Native.DebugMessages(_handle, data, (uint)buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _waitGeneration++; _frameWait?.Unregister(null); _frameWait = null;
        _frameEvent?.Dispose(); _frameEvent = null;
        try { _presentation.Dispose(); } finally { _handle.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public ulong Frames, DrawCalls, Vertices, AtlasHits, AtlasMisses, AtlasEvictions, UploadedBytes, TextureBytes, AtlasBytes;
        public uint AtlasEntries, FeatureLevel;
        public int DeviceRemovedReason;
        public uint DebugLayer;
        public ulong DebugErrors, DebugWarnings;
        public ulong ShapeCacheBytes;
        public ulong VertexBufferBytes;
        public uint BackendApi, SubmissionSlots;
        public ulong Submissions;
        public ulong InteropWarnings;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Profile
    {
        public ulong Frequency, Lists, Resets, FenceWaits, FenceWaitTicks, ResetTicks, ExecuteTicks;
        public ulong UploadCreates, UploadCreateTicks, RasterTransfers, RasterTransferTicks, RasterDrawTicks, PresentTicks;
        public ulong DevicePipelineTicks, DevicePrivateBefore, DevicePrivateCreated, DevicePrivateBridge, DevicePrivateD2D;
        public ulong RendererPrivateBefore, RendererPrivateAfter, DescriptorHeaps, TextureBytes, UploadBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct LifetimeStats
    {
        public ulong SubmissionArenaBytes, PinRequests, UniquePins, LivePins;
        public ulong ShapeArenaBytes, DevicePipelineBuilds, DescriptorPages, UploadBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeRect(Rect value)
    {
        internal readonly float X = (float)value.X, Y = (float)value.Y, Width = (float)value.Width, Height = (float)value.Height;
    }
    internal sealed class RendererHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        // Renderer teardown waits on the shared device and uses the shared 11-on-12
        // context, which only the creating UI thread may touch. A handle released
        // by the finalizer posts destruction there; a stopped dispatcher means the
        // process is ending, and the renderer is left to process teardown.
        private readonly DispatcherQueue? _owner = DispatcherQueue.GetForCurrentThread();
        internal RendererHandle(nint value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle()
        {
            nint renderer = handle;
            if (_owner is null || _owner.HasThreadAccess) Native.Destroy(renderer);
            else if (_owner.TryEnqueue(DispatcherQueuePriority.Low, () => { Native.Destroy(renderer); Interlocked.Increment(ref OwnerDestroysForCheck); }))
                Interlocked.Increment(ref PostedDestroysForCheck);
            return true;
        }
        internal static int PostedDestroysForCheck, OwnerDestroysForCheck;
    }
    internal sealed class TextLayoutHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal TextLayoutHandle(nint value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() { Native.LayoutRelease(handle); return true; }
    }
    internal sealed class TextFormatHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal TextFormatHandle(nint value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() { Native.TerminalFormatRelease(handle); return true; }
    }
    internal sealed class TextShapeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal long NativeBytes { get; }
        internal TextShapeHandle(nint value, long bytes) : base(true) { SetHandle(value); NativeBytes = bytes; }
        protected override bool ReleaseHandle() { Native.AsciiShapeRelease(handle); return true; }
    }
    internal sealed class TextureHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal TextureHandle(nint value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() { Native.TextureDestroy(handle); return true; }
    }
    internal static class Native
    {
        private const string Dll = "toiqtty-renderer.dll";
        [DllImport(Dll, EntryPoint = "otr_prepare_device", CallingConvention = CallingConvention.Cdecl)] internal static extern int PrepareDevice(nint canvas, out nint result);
        [DllImport(Dll, EntryPoint = "otr_install_device", CallingConvention = CallingConvention.Cdecl)] internal static extern int InstallDevice(PreparedDeviceHandle device);
        [DllImport(Dll, EntryPoint = "otr_release_prepared_device", CallingConvention = CallingConvention.Cdecl)] internal static extern void ReleasePreparedDevice(nint device);
        [DllImport(Dll, EntryPoint = "otr_prepared_layouts_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern ulong PreparedLayouts(RendererHandle r);
        [DllImport(Dll, EntryPoint = "otr_color_glyph_stats", CallingConvention = CallingConvention.Cdecl)] internal static extern void ColorGlyphs(RendererHandle r, out ColorGlyphStats value);
        [DllImport(Dll, EntryPoint = "otr_resize_capacity_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern void ResizeCapacityForCheck(RendererHandle r, int enabled);
        [DllImport(Dll, EntryPoint = "otr_resize_stats", CallingConvention = CallingConvention.Cdecl)] internal static extern void ResizeStats(RendererHandle r, out DirectXRenderer.ResizeStats value);
        [DllImport(Dll, EntryPoint = "otr_present_results_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern void PresentResultsForCheck(RendererHandle r, out PresentResults value);
        [DllImport(Dll, EntryPoint = "otr_presentation_policy_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern int PresentationPolicyForCheck(RendererHandle r, uint latency, uint interval);
        [DllImport(Dll, EntryPoint = "otr_ascii_shape_available", CallingConvention = CallingConvention.Cdecl)] internal static extern int AsciiShapeAvailable();
        [DllImport(Dll, EntryPoint = "otr_ascii_prepare_utf8", CallingConvention = CallingConvention.Cdecl)] internal static extern int AsciiPrepare(TextFormatHandle format, byte* text, uint length, FontFeature* features, uint featureCount);
        [DllImport(Dll, EntryPoint = "otr_ascii_warm", CallingConvention = CallingConvention.Cdecl)] internal static extern int AsciiWarm(RendererHandle r, TextFormatHandle format, float dpi, out uint warmed);
        [DllImport(Dll, EntryPoint = "otr_ascii_draw_prepared", CallingConvention = CallingConvention.Cdecl)] internal static extern int AsciiDrawPrepared(RendererHandle r, TextFormatHandle format, float cellWidth, float x, float y, uint color);
        [DllImport(Dll, EntryPoint = "otr_ascii_shape", CallingConvention = CallingConvention.Cdecl)] internal static extern int AsciiShape(RendererHandle r, TextFormatHandle format, char* text, uint length, FontFeature* features, uint featureCount, float cellWidth, out nint shape, out ulong bytes);
        [DllImport(Dll, EntryPoint = "otr_ascii_shape_draw", CallingConvention = CallingConvention.Cdecl)] internal static extern int AsciiShapeDraw(RendererHandle r, TextShapeHandle shape, float x, float y, uint color);
        [DllImport(Dll, EntryPoint = "otr_ascii_shape_release", CallingConvention = CallingConvention.Cdecl)] internal static extern void AsciiShapeRelease(nint shape);
        [DllImport(Dll, EntryPoint = "otr_shutdown_devices", CallingConvention = CallingConvention.Cdecl)] internal static extern void ShutdownDevices();
        [DllImport(Dll, EntryPoint = "otr_adapter_name", CallingConvention = CallingConvention.Cdecl)] internal static extern uint AdapterName(RendererHandle r, byte* destination, uint capacity);
        [DllImport(Dll, EntryPoint = "otr_quad_mode_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern void QuadModeForCheck(RendererHandle r, int expanded);
        [DllImport(Dll, EntryPoint = "otr_atlas_hash_mode_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern int AtlasHashModeForCheck(RendererHandle r, int legacy);
        [DllImport(Dll, EntryPoint = "otr_atlas_buckets", CallingConvention = CallingConvention.Cdecl)] internal static extern void AtlasBuckets(RendererHandle r, out DirectXRenderer.AtlasBuckets value);
        [DllImport(Dll, EntryPoint = "otr_terminal_format", CallingConvention = CallingConvention.Cdecl)] internal static extern int TerminalFormat(nint probe, out nint format);
        [DllImport(Dll, EntryPoint = "otr_terminal_format_release", CallingConvention = CallingConvention.Cdecl)] internal static extern void TerminalFormatRelease(nint format);
        [DllImport(Dll, EntryPoint = "otr_terminal_layout", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TerminalLayout(RendererHandle r, TextFormatHandle format,
            char* text, uint length, char* widths, uint widthsLength, FontFeature* features, uint featureCount, float cellWidth, float cellHeight, float advance,
            int batchSpacing, out nint layout, out LayoutMetrics metrics);
        [DllImport(Dll, EntryPoint = "otr_layout_release", CallingConvention = CallingConvention.Cdecl)] internal static extern void LayoutRelease(nint layout);
        [DllImport(Dll, EntryPoint = "otr_layout_scratch_capacity", CallingConvention = CallingConvention.Cdecl)] internal static extern uint LayoutScratchCapacity(RendererHandle r);
        [DllImport(Dll, EntryPoint = "otr_text_layout", CallingConvention = CallingConvention.Cdecl)] internal static extern int TextLayout(RendererHandle r, TextLayoutHandle layout, float x, float y, uint color);
        [DllImport(Dll, EntryPoint = "otr_forget_layout", CallingConvention = CallingConvention.Cdecl)] internal static extern int ForgetLayout(RendererHandle r, TextLayoutHandle layout);
        [DllImport(Dll, EntryPoint = "otr_forget_text", CallingConvention = CallingConvention.Cdecl)] internal static extern int ForgetText(RendererHandle r, nint layout);
        [DllImport(Dll, EntryPoint = "otr_create", CallingConvention = CallingConvention.Cdecl)] internal static extern int Create(nint canvas, out nint result);
        [DllImport(Dll, EntryPoint = "otr_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void Destroy(nint renderer);
        [DllImport(Dll, EntryPoint = "otr_resize", CallingConvention = CallingConvention.Cdecl)] internal static extern int Resize(RendererHandle r, uint w, uint h, float dpi, int alpha);
        [DllImport(Dll, EntryPoint = "otr_swapchain", CallingConvention = CallingConvention.Cdecl)] internal static extern int Swapchain(RendererHandle r, out nint result);
        [DllImport(Dll, EntryPoint = "otr_present", CallingConvention = CallingConvention.Cdecl)] internal static extern int Present(RendererHandle r);
        [DllImport(Dll, EntryPoint = "otr_acquire_frame", CallingConvention = CallingConvention.Cdecl)] internal static extern int AcquireFrame(RendererHandle r);
        [DllImport(Dll, EntryPoint = "otr_frame_event", CallingConvention = CallingConvention.Cdecl)] internal static extern int FrameEvent(RendererHandle r, out SafeWaitHandle handle);
        [DllImport(Dll, EntryPoint = "otr_frame_available", CallingConvention = CallingConvention.Cdecl)] internal static extern void FrameAvailable(RendererHandle r);
        [DllImport(Dll, EntryPoint = "otr_begin_frame", CallingConvention = CallingConvention.Cdecl)] internal static extern int BeginFrame(RendererHandle r);
        [DllImport(Dll, EntryPoint = "otr_device", CallingConvention = CallingConvention.Cdecl)] internal static extern int Device(RendererHandle r, out nint device);
        [DllImport(Dll, EntryPoint = "otr_present_surface", CallingConvention = CallingConvention.Cdecl)] internal static extern int PresentSurface(RendererHandle r, nint surface);
        [DllImport(Dll, EntryPoint = "otr_resize_surface", CallingConvention = CallingConvention.Cdecl)] internal static extern int ResizeSurface(nint surface, uint width, uint height);
        [DllImport(Dll, EntryPoint = "otr_texture_create", CallingConvention = CallingConvention.Cdecl)] internal static extern int TextureCreate(RendererHandle r, uint w, uint h, float dpi, byte* pixels, int target, out nint result);
        [DllImport(Dll, EntryPoint = "otr_texture_from_bitmap", CallingConvention = CallingConvention.Cdecl)] internal static extern int TextureFromBitmap(RendererHandle r, nint bitmap, float dpi, out nint result);
        [DllImport(Dll, EntryPoint = "otr_texture_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void TextureDestroy(nint texture);
        [DllImport(Dll, EntryPoint = "otr_texture_alias_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern int TextureAliasForCheck(RendererHandle r, TextureHandle source, out nint result);
        [DllImport(Dll, EntryPoint = "otr_begin", CallingConvention = CallingConvention.Cdecl)] internal static extern int Begin(RendererHandle r, nint target);
        [DllImport(Dll, EntryPoint = "otr_end", CallingConvention = CallingConvention.Cdecl)] internal static extern int End(RendererHandle r);
        [DllImport(Dll, EntryPoint = "otr_clear", CallingConvention = CallingConvention.Cdecl)] internal static extern int Clear(RendererHandle r, uint color);
        [DllImport(Dll, EntryPoint = "otr_clip", CallingConvention = CallingConvention.Cdecl)] internal static extern int Clip(RendererHandle r, NativeRect rect);
        [DllImport(Dll, EntryPoint = "otr_fill", CallingConvention = CallingConvention.Cdecl)] internal static extern int Fill(RendererHandle r, NativeRect rect, uint color, int copy);
        [DllImport(Dll, EntryPoint = "otr_triangle", CallingConvention = CallingConvention.Cdecl)] internal static extern int Triangle(RendererHandle r, float x0, float y0, float x1, float y1, float x2, float y2, uint color);
        [DllImport(Dll, EntryPoint = "otr_powerline", CallingConvention = CallingConvention.Cdecl)] internal static extern int Powerline(RendererHandle r, NativeRect rect, uint color, int shape);
        [DllImport(Dll, EntryPoint = "otr_line", CallingConvention = CallingConvention.Cdecl)] internal static extern int Line(RendererHandle r, float x0, float y0, float x1, float y1, float width, uint color);
        [DllImport(Dll, EntryPoint = "otr_image", CallingConvention = CallingConvention.Cdecl)] internal static extern int Image(RendererHandle r, TextureHandle texture, NativeRect dst, NativeRect src, float opacity, int linear);
        [DllImport(Dll, EntryPoint = "otr_text", CallingConvention = CallingConvention.Cdecl)] internal static extern int Text(RendererHandle r, nint layout, float x, float y, uint color);
        [DllImport(Dll, EntryPoint = "otr_stats", CallingConvention = CallingConvention.Cdecl)] internal static extern void GetStats(RendererHandle r, out Stats result);
        [DllImport(Dll, EntryPoint = "otr_profile", CallingConvention = CallingConvention.Cdecl)] internal static extern void GetProfile(RendererHandle r, out Profile result);
        [DllImport(Dll, EntryPoint = "otr_lifetime_stats", CallingConvention = CallingConvention.Cdecl)] internal static extern void GetLifetimeStats(RendererHandle r, out LifetimeStats result);
        [DllImport(Dll, EntryPoint = "otr_text_mode_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern void TextModeForCheck(RendererHandle r, int mode);
        [DllImport(Dll, EntryPoint = "otr_debug_messages", CallingConvention = CallingConvention.Cdecl)] internal static extern uint DebugMessages(RendererHandle r, byte* data, uint capacity);
        [DllImport(Dll, EntryPoint = "otr_read_pixels_for_check", CallingConvention = CallingConvention.Cdecl)] internal static extern int ReadPixelsForCheck(RendererHandle r, byte* data, ulong capacity);
    }
}

internal class DirectXBitmap : IDisposable
{
    internal DirectXRenderer Renderer { get; }
    internal DirectXRenderer.TextureHandle Handle { get; }
    internal Size SizeInPixels { get; }
    internal Size Size => new(SizeInPixels.Width * 96 / Dpi, SizeInPixels.Height * 96 / Dpi);
    internal float Dpi { get; }
    internal DirectXBitmap(DirectXRenderer renderer, nint pointer, int width, int height, float dpi)
    { Renderer = renderer; Handle = new(pointer); SizeInPixels = new(width, height); Dpi = dpi; }
    public void Dispose() => Handle.Dispose();
}
internal sealed class DirectXRenderTarget(DirectXRenderer renderer, nint pointer, int width, int height, float dpi)
    : DirectXBitmap(renderer, pointer, width, height, dpi)
{
    internal DirectXDrawingSession CreateDrawingSession() => new(Renderer, this, new(0, 0, Size.Width, Size.Height));
}

/// <summary>Ordered native triangles and atlas quads, batched by texture and blend state.</summary>
internal sealed class DirectXDrawingSession : IDisposable
{
    internal DirectXRenderer Renderer => _renderer;
    private readonly DirectXRenderer _renderer;
    private readonly bool _ownsTarget;
    private readonly Rect _bounds;
    private bool _disposed;
    internal DirectXBitmap? Target { get; }
    internal Rect Clip { get; private set; }
    internal CanvasBlend Blend { get; set; } = CanvasBlend.SourceOver;
    internal CanvasAntialiasing Antialiasing { get; set; }
    internal DirectXDrawingSession(DirectXRenderer renderer, DirectXBitmap? target, Rect clip, bool ownsTarget = false)
    { _renderer = renderer; Target = target; Clip = _bounds = clip; _ownsTarget = ownsTarget; }
    private DirectXRenderer.RendererHandle Activate() { ObjectDisposedException.ThrowIf(_disposed, this); _renderer.Activate(this); return _renderer.Handle; }
    private static uint Argb(Color c) => (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
    internal void Clear(Color color) => DirectXRenderer.Check(DirectXRenderer.Native.Clear(Activate(), Argb(color)));
    internal void FillRectangle(float x, float y, float width, float height, Color color)
        => DirectXRenderer.Check(DirectXRenderer.Native.Fill(Activate(), new(new Rect(x, y, width, height)), Argb(color), Blend == CanvasBlend.Copy ? 1 : 0));
    internal void DrawLine(float x0, float y0, float x1, float y1, Color color, float width = 1)
        => DirectXRenderer.Check(DirectXRenderer.Native.Line(Activate(), x0, y0, x1, y1, width, Argb(color)));
    internal void DrawRectangle(float x, float y, float width, float height, Color color)
    { DrawLine(x, y, x + width, y, color); DrawLine(x + width, y, x + width, y + height, color); DrawLine(x + width, y + height, x, y + height, color); DrawLine(x, y + height, x, y, color); }
    internal void FillTriangle(float x0, float y0, float x1, float y1, float x2, float y2, Color color)
        => DirectXRenderer.Check(DirectXRenderer.Native.Triangle(Activate(), x0, y0, x1, y1, x2, y2, Argb(color)));
    internal void FillPowerline(float x, float y, float width, float height, Color color, int shape)
        => DirectXRenderer.Check(DirectXRenderer.Native.Powerline(Activate(), new(new Rect(x, y, width, height)), Argb(color), shape));
    internal void DrawTextLayout(CanvasTextLayout layout, float x, float y, Color color)
    {
        DirectXRenderer.Check(DirectXRenderer.Native.Text(Activate(), ((WinRT.IWinRTObject)layout).NativeObject.ThisPtr, x, y, Argb(color))); GC.KeepAlive(layout);
    }
    internal void DrawTextLayout(DirectXRenderer.TextLayoutHandle layout, float x, float y, Color color)
        => DirectXRenderer.Check(DirectXRenderer.Native.TextLayout(Activate(), layout, x, y, Argb(color)));
    internal void DrawTextShape(DirectXRenderer.TextShapeHandle shape, float x, float y, Color color)
        => DirectXRenderer.Check(DirectXRenderer.Native.AsciiShapeDraw(Activate(), shape, x, y, Argb(color)));
    internal void DrawPreparedAscii(DirectXRenderer.TextFormatHandle format, float cellWidth, float x, float y, Color color)
        => DirectXRenderer.Check(DirectXRenderer.Native.AsciiDrawPrepared(Activate(), format, cellWidth, x, y, Argb(color)));
    internal void DrawImage(DirectXBitmap image)
        => DrawImage(image, new(0, 0, image.Size.Width, image.Size.Height), new(0, 0, image.SizeInPixels.Width, image.SizeInPixels.Height), 1, CanvasImageInterpolation.NearestNeighbor);
    internal void DrawImage(DirectXBitmap image, Rect destination, Rect source, float opacity, CanvasImageInterpolation interpolation)
    {
        DirectXRenderer.Check(DirectXRenderer.Native.Image(Activate(), image.Handle, new(destination), new(source), opacity, interpolation == CanvasImageInterpolation.NearestNeighbor ? 0 : 1));
    }
    internal IDisposable CreateLayer(float opacity, Rect clip)
    {
        if (opacity != 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        var old = Clip; Clip = Intersect(old, clip); DirectXRenderer.Check(DirectXRenderer.Native.Clip(Activate(), new(Clip))); return new ClipScope(this, old);
    }
    internal void SetClip(Rect clip) { Clip = Intersect(_bounds, clip); DirectXRenderer.Check(DirectXRenderer.Native.Clip(Activate(), new(Clip))); }
    internal void ResetClip() => SetClip(_bounds);
    private static Rect Intersect(Rect a, Rect b)
    { double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y); return new(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y)); }
    private sealed class ClipScope(DirectXDrawingSession session, Rect old) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed || session._disposed) return; _disposed = true; session.Clip = old; DirectXRenderer.Check(DirectXRenderer.Native.Clip(session.Activate(), new(old))); }
    }
    public void Dispose() { if (_disposed) return; try { _renderer.Finish(this); } finally { _disposed = true; if (_ownsTarget) Target?.Dispose(); } }
}
