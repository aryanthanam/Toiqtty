using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Toiqtty.Terminal;
using System.Text;

namespace Toiqtty.Controls;

/// <summary>Shapes contiguous style runs with DirectWrite, then aligns clusters to Ghostty's cell advances.</summary>
internal sealed class TerminalTextRenderer : IDisposable
{
    // A bounded process-lifetime table avoids a new three-character key for
    // every isolated ASCII cell in heavily styled terminal output.
    private static readonly string[] DirectionalAscii = Enumerable.Range(0, 128)
        .Select(value => string.Concat("\u202d", TerminalCell.Ascii((byte)value), "\u202c")).ToArray();
    private readonly Dictionary<(string, uint, string), CanvasTextLayout> _layouts = [];
    private readonly Queue<(string, uint, string)> _layoutOrder = [];
    private readonly Dictionary<(string, uint, string), DirectXRenderer.TextLayoutHandle> _nativeLayouts = [];
    private readonly Queue<(string, uint, string)> _nativeLayoutOrder = [];
    private readonly Dictionary<uint, DirectXRenderer.TextFormatHandle> _nativeFormats = [];
    private DirectXRenderer.FontFeature[]? _nativeFeaturesOn, _nativeFeaturesOff;
    private bool _nativeLayoutMode;
    private bool _asciiShapeMode;
    private bool _streamedAscii;
    internal bool UseStreamedAscii
    { get => _streamedAscii; set { if (value == _streamedAscii) return; Reset(); _streamedAscii = value; } }
    private byte[]? _asciiText;
    private readonly Dictionary<(string, uint, string), DirectXRenderer.TextShapeHandle> _asciiShapes = [];
    private readonly Queue<(string, uint, string)> _asciiShapeOrder = [];
    private long _asciiShapeBytes;
    internal long AsciiShapeBytesForCheck => _asciiShapeBytes;
    internal int AsciiShapesCreatedForCheck { get; private set; }
    internal bool UseAsciiShapes { get => _asciiShapeMode; set { if (value == _asciiShapeMode) return; Reset(); _asciiShapeMode = value; } }
    private bool _disposed;
    internal bool UseNativeLayouts
    { get => _nativeLayoutMode; set { if (value == _nativeLayoutMode) return; Reset(); _nativeLayoutMode = value; } }
    private readonly Dictionary<uint, float> _baselines = [];
    private readonly StringBuilder _rowBuilder = new(256), _widthBuilder = new(256);
    private CanvasTypography? _ligaturesOn, _ligaturesOff;
    private DirectXRenderer? _gpu;
    private Dictionary<string, uint> _features = new();
    internal void ConfigureFeatures(Dictionary<string, uint> features)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_features.Count == features.Count && features.All(pair => _features.TryGetValue(pair.Key, out uint value) && value == pair.Value)) return;
        Reset(); _features = new(features);
    }
    private readonly TerminalClusterMetrics _clusterMetrics = new();
    internal bool UseProjectedClusterMetricsForCheck { get => _clusterMetrics.UseProjectedMetricsForCheck; set => _clusterMetrics.UseProjectedMetricsForCheck = value; }
    internal int NativeClusterReadsForCheck => _clusterMetrics.NativeReads;
    private CanvasTypography Typography(bool ligatures)
    {
        var existing = ligatures ? _ligaturesOn : _ligaturesOff;
        if (existing is not null) return existing;
        var typography = new CanvasTypography();
        try
        {
            typography.AddFeature(CanvasTypographyFeatureName.StandardLigatures, ligatures ? 1u : 0u);
            typography.AddFeature(CanvasTypographyFeatureName.ContextualLigatures, ligatures ? 1u : 0u);
            typography.AddFeature(CanvasTypographyFeatureName.ContextualAlternates, ligatures ? 1u : 0u);
            foreach (var (tag, value) in _features)
                typography.AddFeature((CanvasTypographyFeatureName)((uint)tag[0] | (uint)tag[1] << 8 | (uint)tag[2] << 16 | (uint)tag[3] << 24), value);
        }
        catch { typography.Dispose(); throw; }
        if (ligatures) _ligaturesOn = typography; else _ligaturesOff = typography;
        return typography;
    }
    private static void ResetBuilder(StringBuilder builder) { builder.Clear(); if (builder.Capacity > 4096) builder.Capacity = 4096; }
    internal void SetLineMetrics(CanvasTextLayout layout, ICanvasResourceCreator resource, CanvasTextFormat format, float cellHeight, uint style)
    {
        style &= 3;
        if (!_baselines.TryGetValue(style, out float baseline))
        {
            using var probe = new CanvasTextLayout(resource, "M", format, 1000, cellHeight);
            _baselines[style] = baseline = probe.LineMetrics[0].Baseline;
        }
        // A fallback font must not move neighboring primary-font glyphs vertically.
        layout.LineSpacingMode = CanvasLineSpacingMode.Uniform;
        layout.LineSpacing = cellHeight;
        layout.LineSpacingBaseline = baseline;
    }
    public int ShapedRuns { get; private set; }
    internal long SpacingCalls { get; private set; }
    internal bool DisableSpacingBatchForCheck { get; set; }
    internal int CachedLayouts => _layouts.Count + _nativeLayouts.Count + _asciiShapes.Count;
    public float MaxAdvanceError { get; private set; }
    public void DrawRow(ICanvasResourceCreator resource, DirectXDrawingSession drawing, TerminalCell[] cells, int row, int columns,
        float cellWidth, float cellHeight, Func<uint, CanvasTextFormat> format, bool ligatures, bool blinkVisible,
        PowerlineRenderer? powerline, Action<DirectXDrawingSession, float, float, float, uint, Windows.UI.Color> underline, uint? selectionForeground = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_gpu, drawing.Renderer))
        {
            foreach (var old in _layouts.Values) _gpu?.ForgetTextLayout(old);
            foreach (var old in _nativeLayouts.Values) _gpu?.ForgetTextLayout(old);
            _gpu = drawing.Renderer;
        }
        int lastColumn = columns;
        while (lastColumn > 0)
        {
            ref var last = ref cells[row * columns + lastColumn - 1];
            if (last.Length != 0 || last.UnderlineStyle != 0 || (last.Flags & (8 | 128)) != 0) break;
            lastColumn--;
        }
        for (int x = 0; x < lastColumn;)
        {
            ref var first = ref cells[row * columns + x];
            if (first.Width == 0 || (first.Flags & 32) != 0 || ((first.Flags & 256) != 0 && !blinkVisible)) { x++; continue; }
            var color = Color((first.Flags & 64) != 0 && selectionForeground is { } selected ? selected : first.Foreground); if ((first.Flags & 16) != 0) color.A = 140;
            float px = x * cellWidth, py = row * cellHeight;
            var text = first.GetText();
            if (text.Length == 1 && text[0] is '\ue0b0' or '\ue0b2' or '\ue0b4' or '\ue0b6' && powerline?.TryDraw(drawing, text, px, py, color) == true)
            { Decorate(first, px, py); x++; continue; }
            if (_asciiShapeMode && UseStreamedAscii)
            {
                _asciiText ??= new byte[4096]; int end = x; bool eligible = true;
                while (end < lastColumn)
                {
                    ref var cell = ref cells[row * columns + end]; var value = cell.GetText();
                    if (cell.Flags != first.Flags || cell.Foreground != first.Foreground || value.Length == 1 && value[0] is '\ue0b0' or '\ue0b2' or '\ue0b4' or '\ue0b6') break;
                    if (end - x == _asciiText.Length || cell.Width != 1 || value.Length > 1 || value.Length == 1 && (value[0] < 32 || value[0] > 126)) { eligible = false; break; }
                    _asciiText[end - x] = value.Length == 0 ? (byte)32 : (byte)value[0]; end++;
                }
                // Tiny repeated style runs are cheaper to draw from the shape
                // cache than to reshape through HarfBuzz on every row update.
                if (eligible && end - x >= 8)
                {
                    var nativeFormat = NativeFormat(first.Flags & 3);
                    if (_gpu.TryPrepareAsciiText(nativeFormat, _asciiText.AsSpan(0, end - x), NativeFeatures(ligatures)))
                    {
                        for (int column = x; column < end; column++)
                        {
                            ref var cell = ref cells[row * columns + column];
                            if (cell.UnderlineStyle != 0 || (cell.Flags & (8 | 128)) != 0) Decorate(in cell, column * cellWidth, py);
                        }
                        drawing.DrawPreparedAscii(nativeFormat, cellWidth, px, py + 1, color);
                        ShapedRuns++; AsciiShapesCreatedForCheck++; x = end; continue;
                    }
                }
            }
            var builder = _rowBuilder; builder.Clear(); builder.EnsureCapacity(lastColumn + 2); builder.Append('\u202d');
            StringBuilder? widths = null; int characters = 0; float runAdvance = 0;
            int start = x;
            while (x < lastColumn)
            {
                ref var cell = ref cells[row * columns + x];
                if (cell.Width == 0) { x++; continue; }
                var value = cell.GetText();
                if (cell.Flags != first.Flags || cell.Foreground != first.Foreground ||
                    (value.Length == 1 && value[0] is '\ue0b0' or '\ue0b2' or '\ue0b4' or '\ue0b6')) break;
                if (value.Length == 0) value = " ";
                builder.Append(value);
                if (widths is null && (value.Length != 1 || cell.Width != 1))
                {
                    widths = _widthBuilder; widths.Clear();
                    for (int i = 0; i < characters; i++) widths.Append('\u0001').Append('\0');
                }
                if (widths is not null)
                {
                    widths.Append('\0', (value.Length - 1) * 2);
                    widths.Append((char)(cell.Width & 0xffff)).Append((char)(cell.Width >> 16));
                }
                characters += value.Length; runAdvance += cellWidth * cell.Width;
                if (cell.UnderlineStyle != 0 || (cell.Flags & (8 | 128)) != 0) Decorate(in cell, x * cellWidth, py);
                x++;
            }
            if (x == start) { x++; continue; }
            // Ghostty already assigns visual cells. DirectWrite's paragraph bidi
            // algorithm must not reorder those cells when shaping RTL scripts.
            builder.Append('\u202c');
            // Widths distinguish ordinary text from the same graphemes in different width modes.
            string runText = characters == 1 && widths is null && builder[1] < 128 ? DirectionalAscii[builder[1]] : builder.ToString();
            var key = (runText, first.Flags & 3, widths?.ToString() ?? "");
            ResetBuilder(builder); if (widths is not null) ResetBuilder(widths);
            if (_asciiShapeMode && key.Item3.Length == 0)
            {
                if (!_asciiShapes.TryGetValue(key, out var ascii) && !_nativeLayouts.ContainsKey(key) && !_layouts.ContainsKey(key))
                {
                    ascii = _gpu.TryCreateAsciiShape(NativeFormat(key.Item2), key.Item1, NativeFeatures(ligatures), cellWidth);
                    if (ascii is not null)
                    {
                        while (_asciiShapes.Count > 0 && (_asciiShapes.Count >= 256 || _asciiShapeBytes + ascii.NativeBytes > 2 * 1024 * 1024))
                            if (_asciiShapes.Remove(_asciiShapeOrder.Dequeue(), out var evicted)) { _asciiShapeBytes -= evicted.NativeBytes; evicted.Dispose(); }
                        _asciiShapes.Add(key, ascii); _asciiShapeBytes += ascii.NativeBytes; _asciiShapeOrder.Enqueue(key); AsciiShapesCreatedForCheck++; ShapedRuns++;
                    }
                }
                if (ascii is not null) { drawing.DrawTextShape(ascii, px, py + 1, color); continue; }
            }
            if (_nativeLayoutMode)
            {
                if (!_nativeLayouts.TryGetValue(key, out var native))
                {
                    if (_nativeLayouts.Count >= 2048 && _nativeLayouts.Remove(_nativeLayoutOrder.Dequeue(), out var evicted))
                    { _gpu.ForgetTextLayout(evicted); evicted.Dispose(); }
                    var nativeFormat = NativeFormat(key.Item2);
                    native = _gpu.CreateTerminalLayout(nativeFormat, key.Item1, key.Item3, NativeFeatures(ligatures), cellWidth, cellHeight, runAdvance,
                        !DisableSpacingBatchForCheck, out var metrics);
                    SpacingCalls += metrics.SpacingCalls; MaxAdvanceError = Math.Max(MaxAdvanceError, metrics.AdvanceError);
                    _nativeLayouts.Add(key, native); _nativeLayoutOrder.Enqueue(key); ShapedRuns++;
                }
                drawing.DrawTextLayout(native, px, py + 1, color); continue;
            }
            if (!_layouts.TryGetValue(key, out var layout))
            {
                if (_layouts.Count >= 2048 && _layouts.Remove(_layoutOrder.Dequeue(), out var evicted))
                { _gpu.ForgetTextLayout(evicted); evicted.Dispose(); }
                layout = new CanvasTextLayout(resource, key.Item1, format(first.Flags), 100000, cellHeight);
                SetLineMetrics(layout, resource, format(first.Flags), cellHeight, first.Flags);
                layout.SetTypography(0, key.Item1.Length, Typography(ligatures));
                int offset = 0, pendingStart = 0, pendingCount = 0; float pendingSpacing = 0;
                foreach (var cluster in _clusterMetrics.Read(layout, key.Item1.Length))
                {
                    float advance = 0;
                    for (int i = offset; i < offset + cluster.CharacterCount; i++)
                    {
                        int character = i - 1;
                        if (character < 0 || character >= characters) continue;
                        uint width = key.Item3.Length == 0 ? 1u : key.Item3[character * 2] | (uint)key.Item3[character * 2 + 1] << 16;
                        advance += cellWidth * width;
                    }
                    float spacing = advance - cluster.Width;
                    if (!DisableSpacingBatchForCheck && cluster.CharacterCount == 1)
                    {
                        if (pendingCount > 0 && spacing != pendingSpacing)
                        { layout.SetCharacterSpacing(pendingStart, pendingCount, 0, pendingSpacing, 0); SpacingCalls++; pendingCount = 0; }
                        if (pendingCount == 0) { pendingStart = offset; pendingSpacing = spacing; }
                        pendingCount++;
                    }
                    else
                    {
                        if (pendingCount > 0)
                        { layout.SetCharacterSpacing(pendingStart, pendingCount, 0, pendingSpacing, 0); SpacingCalls++; pendingCount = 0; }
                        layout.SetCharacterSpacing(offset, cluster.CharacterCount, 0, spacing, 0); SpacingCalls++;
                    }
                    offset += cluster.CharacterCount;
                }
                if (pendingCount > 0) { layout.SetCharacterSpacing(pendingStart, pendingCount, 0, pendingSpacing, 0); SpacingCalls++; }
                MaxAdvanceError = Math.Max(MaxAdvanceError, Math.Abs((float)layout.LayoutBoundsIncludingTrailingWhitespace.Width - runAdvance));
                _layouts.Add(key, layout); _layoutOrder.Enqueue(key); ShapedRuns++;
            }
            drawing.DrawTextLayout(layout, px, py + 1, color);
        }
        DirectXRenderer.TextFormatHandle NativeFormat(uint style)
        {
            if (_nativeFormats.TryGetValue(style, out var existing)) return existing;
            var font = format(style); using var probe = new CanvasTextLayout(resource, "M", font, 1000, cellHeight);
            SetLineMetrics(probe, resource, font, cellHeight, style);
            var created = _gpu!.CreateTerminalFormat(probe); _nativeFormats.Add(style, created); return created;
        }
        void Decorate(in TerminalCell cell, float x, float y)
        {
            float width = cellWidth * cell.Width;
            var color = Color(cell.Foreground);
            underline(drawing, x, y, width, cell.UnderlineStyle, Color(cell.UnderlineColor));
            if ((cell.Flags & 8) != 0) drawing.DrawLine(x, y + cellHeight / 2, x + width, y + cellHeight / 2, color);
            if ((cell.Flags & 128) != 0) drawing.DrawLine(x, y + 1, x + width, y + 1, color);
        }
    }
    private static Windows.UI.Color Color(uint value) => Windows.UI.Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    private DirectXRenderer.FontFeature[] NativeFeatures(bool ligatures)
    {
        var cached = ligatures ? _nativeFeaturesOn : _nativeFeaturesOff; if (cached is not null) return cached;
        static uint Tag(string text) => (uint)text[0] | (uint)text[1] << 8 | (uint)text[2] << 16 | (uint)text[3] << 24;
        var features = new Dictionary<uint, uint> { { Tag("liga"), ligatures ? 1u : 0u }, { Tag("clig"), ligatures ? 1u : 0u }, { Tag("calt"), ligatures ? 1u : 0u } };
        foreach (var feature in _features) features[Tag(feature.Key)] = feature.Value;
        cached = features.Select(pair => new DirectXRenderer.FontFeature(pair.Key, pair.Value)).ToArray();
        if (ligatures) _nativeFeaturesOn = cached; else _nativeFeaturesOff = cached; return cached;
    }
    internal void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ReleaseLayouts();
    }
    public void Dispose()
    {
        if (_disposed) return; ReleaseLayouts(); _disposed = true;
    }
    private void ReleaseLayouts()
    {
        foreach (var layout in _layouts.Values) { _gpu?.ForgetTextLayout(layout); layout.Dispose(); }
        foreach (var layout in _nativeLayouts.Values) { _gpu?.ForgetTextLayout(layout); layout.Dispose(); }
        foreach (var shape in _asciiShapes.Values) shape.Dispose(); _asciiShapes.Clear(); _asciiShapeOrder.Clear(); _asciiShapeBytes = 0;
        foreach (var probe in _nativeFormats.Values) probe.Dispose();
        _nativeLayouts.Clear(); _nativeLayoutOrder.Clear(); _nativeFormats.Clear(); _nativeFeaturesOn = _nativeFeaturesOff = null;
        _gpu = null; _layouts.Clear(); _layoutOrder.Clear(); _baselines.Clear();
        _asciiText = null;
        _ligaturesOn?.Dispose(); _ligaturesOff?.Dispose(); _ligaturesOn = _ligaturesOff = null;
        ResetBuilder(_rowBuilder); ResetBuilder(_widthBuilder);
    }
}
