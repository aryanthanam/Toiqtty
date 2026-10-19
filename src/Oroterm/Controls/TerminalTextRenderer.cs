using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Oroterm.Terminal;
using System.Text;

namespace Oroterm.Controls;

/// <summary>Shapes contiguous style runs with DirectWrite, then aligns clusters to Ghostty's cell advances.</summary>
internal sealed class TerminalTextRenderer : IDisposable
{
    private readonly Dictionary<(string, uint, string), CanvasTextLayout> _layouts = [];
    private readonly Queue<(string, uint, string)> _layoutOrder = [];
    private readonly Dictionary<uint, float> _baselines = [];
    private readonly StringBuilder _rowBuilder=new(256),_widthBuilder=new(256);
    private CanvasTypography? _ligaturesOn,_ligaturesOff;
    private DirectXRenderer? _gpu;
    private Dictionary<string,uint> _features = new();
    internal void ConfigureFeatures(Dictionary<string,uint> features) => _features = new(features);
    private readonly TerminalClusterMetrics _clusterMetrics=new();
    internal bool UseProjectedClusterMetricsForCheck { get=>_clusterMetrics.UseProjectedMetricsForCheck; set=>_clusterMetrics.UseProjectedMetricsForCheck=value; }
    internal int NativeClusterReadsForCheck=>_clusterMetrics.NativeReads;
    private CanvasTypography Typography(bool ligatures)
    {
        var existing=ligatures?_ligaturesOn:_ligaturesOff;
        if(existing is not null) return existing;
        var typography=new CanvasTypography();
        try
        {
            typography.AddFeature(CanvasTypographyFeatureName.StandardLigatures,ligatures?1u:0u);
            typography.AddFeature(CanvasTypographyFeatureName.ContextualLigatures,ligatures?1u:0u);
            typography.AddFeature(CanvasTypographyFeatureName.ContextualAlternates,ligatures?1u:0u);
            foreach (var (tag, value) in _features)
                typography.AddFeature((CanvasTypographyFeatureName)((uint)tag[0] | (uint)tag[1] << 8 | (uint)tag[2] << 16 | (uint)tag[3] << 24), value);
        }
        catch { typography.Dispose(); throw; }
        if(ligatures) _ligaturesOn=typography; else _ligaturesOff=typography;
        return typography;
    }
    private static void ResetBuilder(StringBuilder builder) { builder.Clear(); if(builder.Capacity>4096) builder.Capacity=4096; }
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
    internal int CachedLayouts => _layouts.Count;
    public float MaxAdvanceError { get; private set; }
    public void DrawRow(ICanvasResourceCreator resource, DirectXDrawingSession drawing, TerminalCell[] cells, int row, int columns,
        float cellWidth, float cellHeight, Func<uint, CanvasTextFormat> format, bool ligatures, bool blinkVisible,
        PowerlineRenderer? powerline, Action<DirectXDrawingSession, float, float, float, uint, Windows.UI.Color> underline, uint? selectionForeground = null)
    {
        if(!ReferenceEquals(_gpu,drawing.Renderer))
        {
            foreach(var old in _layouts.Values)_gpu?.ForgetTextLayout(old);
            _gpu=drawing.Renderer;
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
            var builder=_rowBuilder; builder.Clear(); builder.EnsureCapacity(lastColumn+2); builder.Append('\u202d');
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
                    widths=_widthBuilder; widths.Clear();
                    for (int i = 0; i < characters; i++) widths.Append('\u0001').Append('\0');
                }
                if (widths is not null)
                {
                    widths.Append('\0', (value.Length - 1) * 2);
                    widths.Append((char)(cell.Width & 0xffff)).Append((char)(cell.Width >> 16));
                }
                characters += value.Length; runAdvance += cellWidth * cell.Width;
                if(cell.UnderlineStyle!=0 || (cell.Flags&(8|128))!=0) Decorate(in cell, x * cellWidth, py);
                x++;
            }
            if (x == start) { x++; continue; }
            // Ghostty already assigns visual cells. DirectWrite's paragraph bidi
            // algorithm must not reorder those cells when shaping RTL scripts.
            builder.Append('\u202c');
            // Widths distinguish ordinary text from the same graphemes in different width modes.
            var key = (builder.ToString(), first.Flags & 3, widths?.ToString() ?? "");
            ResetBuilder(builder); if(widths is not null) ResetBuilder(widths);
            if (!_layouts.TryGetValue(key, out var layout))
            {
                if (_layouts.Count >= 2048 && _layouts.Remove(_layoutOrder.Dequeue(), out var evicted))
                { _gpu.ForgetTextLayout(evicted);evicted.Dispose(); }
                layout = new CanvasTextLayout(resource, key.Item1, format(first.Flags), 100000, cellHeight);
                SetLineMetrics(layout, resource, format(first.Flags), cellHeight, first.Flags);
                layout.SetTypography(0,key.Item1.Length,Typography(ligatures));
                int offset = 0, pendingStart = 0, pendingCount = 0; float pendingSpacing = 0;
                foreach (var cluster in _clusterMetrics.Read(layout,key.Item1.Length))
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
    public void Dispose()
    {
        foreach(var layout in _layouts.Values) { _gpu?.ForgetTextLayout(layout);layout.Dispose(); }
        _gpu=null;_layouts.Clear(); _layoutOrder.Clear(); _baselines.Clear();
        _ligaturesOn?.Dispose(); _ligaturesOff?.Dispose(); _ligaturesOn=_ligaturesOff=null;
        ResetBuilder(_rowBuilder); ResetBuilder(_widthBuilder);
    }
}
