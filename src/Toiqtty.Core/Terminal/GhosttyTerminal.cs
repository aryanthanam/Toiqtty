using System.Runtime.InteropServices;
using System.Text;

namespace Toiqtty.Terminal;

// Managed snapshots own text strings. The native ABI's inline UTF-8 storage
// stays in Native.Cell and never travels through the frontend's cell buffers.
public struct TerminalCell
{
    public uint Foreground, Background, Flags, Length;
    public uint UnderlineColor, UnderlineStyle, Width;
    public string? Text;
    private static readonly string[] AsciiText = Enumerable.Range(0, 128).Select(value => ((char)value).ToString()).ToArray();
    internal static string Ascii(byte value) => AsciiText[value];
    public readonly string GetText() => Text ?? "";
    internal static string DecodeText(ReadOnlySpan<byte> utf8, string? previous = null)
    {
        if (utf8.Length == 0) return "";
        if (utf8.Length == 1 && utf8[0] < 128) return AsciiText[utf8[0]];
        return DecodeUnicode(utf8, previous);
    }
    private static string DecodeUnicode(ReadOnlySpan<byte> utf8, string? previous)
    {
        if (previous is not null && utf8.Length <= 128)
        {
            Span<char> text = stackalloc char[utf8.Length];
            int length = Encoding.UTF8.GetChars(utf8, text);
            var decoded = text[..length];
            return decoded.SequenceEqual(previous.AsSpan()) ? previous : new string(decoded);
        }
        return Encoding.UTF8.GetString(utf8);
    }
    internal readonly bool SameAppearance(in TerminalCell other)
    {
        if (Foreground != other.Foreground || Background != other.Background || Flags != other.Flags || Width != other.Width
            || UnderlineColor != other.UnderlineColor || UnderlineStyle != other.UnderlineStyle || Length != other.Length) return false;
        return GetText() == other.GetText();
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct TerminalFrame
{
    public uint Columns, Rows, CursorX, CursorY, CursorVisible, CursorStyle, Foreground, Background;
    public uint CursorColor, CursorBlinking, RenderHeld, CellWidth, CellHeight;
    public uint AnimationDelay;
    public uint CursorWideTail, ChangedRows;
}

public enum TerminalKeyAction { Release = 0, Press = 1, Repeat = 2 }
public sealed record TerminalImage(ulong Generation, uint Width, uint Height, uint Channels, byte[] Pixels);
public sealed record TerminalGraphic(TerminalImage Image, uint ImageId, uint PlacementId, int Z, int Column, int Row,
    uint OffsetX, uint OffsetY, uint PixelWidth, uint PixelHeight, uint SourceX, uint SourceY, uint SourceWidth, uint SourceHeight);
public sealed record TerminalScrollState(ulong Total, ulong Offset, ulong Visible)
{
    public ulong Maximum => Total > Visible ? Total - Visible : 0;
}
public sealed record TerminalSnapshot(TerminalFrame Frame, IReadOnlyList<TerminalGraphic> Graphics, IReadOnlyList<int> ChangedRows)
{
    public TerminalScrollState? Scrollbar { get; init; }
}

/// <summary>Owns one libghostty-vt instance. The session serializes all access.</summary>
public sealed class GhosttyTerminal : IDisposable
{
    private nint _handle;
    private readonly Native.Effect _effect;
    private readonly Dictionary<ulong, TerminalImage> _images = [];
    internal ulong CachedImageBytes { get { ThrowIfDisposed(); return Native.ot_graphics_cache_bytes(_handle); } }
    internal ulong CaptureTextBytesForCheck { get { ThrowIfDisposed(); return Native.ot_capture_text_bytes(_handle); } }
    private Native.Cell[] _nativeCells = [];
    internal int NativeCellBufferAllocationsForCheck { get; private set; }
    internal int NativeCellBufferCapacityForCheck => _nativeCells.Length;
    private object? _lastCells;
    private bool _incremental;
    private readonly List<int> _changedRows = [];
    private byte[]? _metadataBuffer;
    private readonly byte[][] _metadataBytes = [[], []];
    private readonly string[] _metadataText = ["", ""];
    public event Action<byte[]>? Response;
    public event Action? Attention;
    public event Action<TerminalEvent>? HostEvent;
    private TaskCompletionSource<int>? _dropCompletion;
    private uint _dropOperations;
    public ITerminalClipboard? Clipboard { get; set; }
    public bool AllowClipboardRead { get; set; }
    public bool AllowClipboardWrite { get; set; } = true;

    public GhosttyTerminal(ushort columns = 100, ushort rows = 30)
    {
        _effect = (_, kind, data, length) =>
        {
            // Never let a managed exception cross the native callback boundary.
            try
            {
                if (kind == 1) Attention?.Invoke();
                else if (kind is 6 or 7) HandleClipboard(data);
                else if (kind == 8) { FinishDrop(0); HostEvent?.Invoke(new(TerminalEventKind.Reset)); }
                else if (kind == 9)
                {
                    int state = Marshal.ReadInt32(data);
                    if (state == 0 || state >= 2) FinishDrop(state switch { 3 => 1, 4 => 2, _ => 0 });
                }
                else if (kind is >= 2 and <= 5)
                {
                    var item = Marshal.PtrToStructure<Native.HostEvent>(data);
                    HostEvent?.Invoke(new((TerminalEventKind)kind, item.State, item.Detail, item.Progress,
                        item.HasExitCode != 0 ? item.ExitCode : null, item.Id.Text(), item.App.Text(), item.Title.Text(), item.Body.Text()));
                }
                else if (kind == 0 && length > 0)
                {
                    var bytes = new byte[checked((int)length)];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    Response?.Invoke(bytes);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        };
        _handle = Native.ot_create(columns, rows, _effect, 0);
        if (_handle == 0) throw new InvalidOperationException("libghostty could not allocate a terminal.");
        byte[] identity = Encoding.UTF8.GetBytes("toiqtty " + Models.BuildInfo.Version);
        Native.ot_identity(_handle, identity, (nuint)identity.Length);
    }

    public void Write(byte[] data, int length) { ThrowIfDisposed(); Native.ot_write(_handle, data, (nuint)length); }
    private string? _themeKey;
    public void SetTheme(Models.SessionTheme? theme)
    {
        ThrowIfDisposed();
        if (theme is { IsValid: false }) throw new ArgumentException("Invalid terminal theme.", nameof(theme));
        if (_themeKey == theme?.Key) return;
        uint fg = 0, bg = 0, cursor = 0; uint[]? palette = null;
        if (theme is not null)
        {
            Models.SessionTheme.TryColor(theme.Foreground, out fg); Models.SessionTheme.TryColor(theme.Background, out bg);
            Models.SessionTheme.TryColor(theme.Cursor.Length == 0 ? theme.Foreground : theme.Cursor, out cursor);
            if (theme.Palette.Length > 0) palette = theme.PaletteColors.Select(color => { Models.SessionTheme.TryColor(color, out uint value); return value; }).ToArray();
        }
        if (Native.ot_theme(_handle, fg, bg, cursor, palette, (nuint)(palette?.Length ?? 0)) != 0) throw new InvalidOperationException("libghostty could not apply terminal colors.");
        if (theme is { Indexed.Length: > 0 })
        {
            var entries = theme.Indexed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('=')).ToArray();
            var indices = entries.Select(entry => uint.Parse(entry[0], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var colors = entries.Select(entry => { Models.SessionTheme.TryColor(entry[1], out uint color); return color; }).ToArray();
            if (Native.ot_palette_entries(_handle, indices, colors, (nuint)indices.Length) != 0) throw new InvalidOperationException("libghostty could not apply indexed colors.");
        }
        _themeKey = theme?.Key;
    }
    public void SetScrollbackLimits(long? bytes, long? lines)
    {
        ThrowIfDisposed();
        if (bytes is < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (lines is < 0) throw new ArgumentOutOfRangeException(nameof(lines));
        if (Native.ot_scrollback_limits(_handle, bytes ?? -1, lines ?? -1) != 0)
            throw new InvalidOperationException("libghostty could not apply the scrollback limits.");
    }
    public void SetCursorDefaults(Models.CursorSettings cursor)
    {
        ThrowIfDisposed(); cursor.Validate();
        if (Native.ot_cursor_defaults(_handle, cursor.NativeStyle, cursor.Blink) != 0) throw new InvalidOperationException("libghostty could not apply cursor defaults.");
    }
    public void Resize(ushort columns, ushort rows, uint cellWidth, uint cellHeight)
    {
        ThrowIfDisposed();
        if (Native.ot_resize(_handle, columns, rows, cellWidth, cellHeight) != 0)
            throw new InvalidOperationException("libghostty could not resize the terminal.");
        _lastCells = null; // A recycled exact-sized destination still needs a full capture.
    }
    public TerminalFrame ReadFrame(TerminalCell[] cells)
    {
        ThrowIfDisposed();
        if (_nativeCells.Length < cells.Length || (_nativeCells.Length > 16384 && cells.Length < _nativeCells.Length / 4))
        {
            _nativeCells = new Native.Cell[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, cells.Length))];
            NativeCellBufferAllocationsForCheck++;
        }
        var frame = CaptureNative(_nativeCells, cells.Length, cells);
        for (int rowIndex = 0; rowIndex < _changedRows.Count; rowIndex++)
        {
            int row = _changedRows[rowIndex];
            for (int index = row * (int)frame.Columns; index < (row + 1) * frame.Columns; index++) cells[index] = _nativeCells[index].ToCell(cells[index].Text);
        }
        return frame;
    }
    private unsafe TerminalFrame CaptureNative(Native.Cell[] storage, int length, object owner)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(_lastCells, owner))
        {
            _lastCells = owner;
            Native.ot_incremental(_handle, _incremental ? 1 : 0);
        }
        TerminalFrame frame;
        // The array marshaller copies the entire retained capacity in/out.
        // Pin this unmanaged struct array for the call instead; the bridge
        // receives only the active cell count and never retains this pointer.
        fixed (Native.Cell* nativeCells = storage)
            if (Native.ot_frame(_handle, out frame, nativeCells, (nuint)length) < 0)
            {
                _lastCells = null;
                throw new InvalidOperationException("libghostty could not capture the terminal frame.");
            }
        _changedRows.Clear();
        for (int row = 0; row < frame.Rows; row++)
        {
            if (Native.ot_row_changed(_handle, (uint)row) == 0) continue;
            _changedRows.Add(row);
        }
        return frame;
    }
    public void EnableIncrementalRendering(bool enabled = true) { _incremental = enabled; Native.ot_incremental(_handle, enabled ? 1 : 0); }
    /// <summary>Kitty graphics file transfers let a program, including a remote one, display any local file this user can read.</summary>
    public void AllowImageFiles(bool enabled) => Native.ot_image_files(_handle, enabled ? 1 : 0);
    public TerminalSnapshot ReadSnapshot(TerminalCell[] cells)
        => ReadSnapshot(ReadFrame(cells));
    internal TerminalSnapshot ReadSnapshot(Utf8TerminalBuffer cells)
    {
        var frame = CaptureNative(cells.Cells, cells.Length, cells);
        try { cells.OwnOverflow(_changedRows, (int)frame.Columns); }
        catch { _lastCells = null; throw; }
        return ReadSnapshot(frame);
    }
    private TerminalSnapshot ReadSnapshot(TerminalFrame frame)
    {
        var scrollbar = ReadScrollState();
        int count = Native.ot_graphics(_handle, out var pointer);
        if (count == 0) { _images.Clear(); return new(frame, [], _changedRows.ToArray()) { Scrollbar = scrollbar }; }
        var graphics = new List<TerminalGraphic>(count);
        var used = new HashSet<ulong>();
        for (int index = 0; index < count; index++)
        {
            var item = Marshal.PtrToStructure<Native.Graphic>(pointer + index * Marshal.SizeOf<Native.Graphic>());
            if (!_images.TryGetValue(item.Generation, out var image))
            {
                var pixels = new byte[checked((int)item.Length)];
                Marshal.Copy(item.Pixels, pixels, 0, pixels.Length);
                image = new(item.Generation, item.Width, item.Height, item.Channels, pixels);
                _images.Add(item.Generation, image);
            }
            used.Add(item.Generation);
            graphics.Add(new(image, item.ImageId, item.PlacementId, item.Z, item.Column, item.Row, item.OffsetX, item.OffsetY,
                item.PixelWidth, item.PixelHeight, item.SourceX, item.SourceY, item.SourceWidth, item.SourceHeight));
        }
        foreach (var generation in _images.Keys.Where(key => !used.Contains(key)).ToArray()) _images.Remove(generation);
        graphics.Sort((a, b) =>
        {
            int order = a.Z.CompareTo(b.Z); if (order != 0) return order;
            order = a.ImageId.CompareTo(b.ImageId); return order != 0 ? order : a.PlacementId.CompareTo(b.PlacementId);
        });
        return new(frame, graphics, _changedRows.ToArray()) { Scrollbar = scrollbar };
    }
    public string Hyperlink(ushort column, ushort row)
    {
        ThrowIfDisposed();
        var bytes = new byte[2048];
        int length = Native.ot_hyperlink(_handle, column, row, bytes, (nuint)bytes.Length);
        if (length > bytes.Length) { bytes = new byte[length]; length = Native.ot_hyperlink(_handle, column, row, bytes, (nuint)bytes.Length); }
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
    public void Scroll(int delta) { ThrowIfDisposed(); Native.ot_scroll(_handle, delta); }
    public TerminalScrollState ReadScrollState()
    {
        ThrowIfDisposed();
        if (Native.ot_scrollbar(_handle, out var total, out var offset, out var visible) != 0)
            throw new InvalidOperationException("libghostty could not read the scrollbar state.");
        return new(total, offset, visible);
    }
    public void ScrollTo(ulong row) { ThrowIfDisposed(); Native.ot_scroll_to(_handle, row); }
    public byte[] EncodeKey(uint key, uint modifiers, string text = "", TerminalKeyAction action = TerminalKeyAction.Press,
        uint? unshiftedCodepoint = null, uint consumedModifiers = 0)
    {
        ThrowIfDisposed();
        var input = Encoding.UTF8.GetBytes(text);
        var output = new byte[Math.Max(1024, input.Length * 8 + 64)];
        var unshifted = unshiftedCodepoint ?? (key is >= 65 and <= 90 ? key + 32 : key is >= 48 and <= 57 or 32 ? key : 0);
        var length = Native.ot_key(_handle, key, modifiers, (int)action, unshifted, consumedModifiers, input, (nuint)input.Length, output, (nuint)output.Length);
        if (length < 0) throw new InvalidOperationException("libghostty could not encode the key.");
        return output[..length];
    }
    public byte[] EncodePaste(string text)
    {
        ThrowIfDisposed();
        var input = Encoding.UTF8.GetBytes(text);
        var output = new byte[input.Length + 32];
        var length = Native.ot_paste(_handle, input, (nuint)input.Length, output, (nuint)output.Length);
        if (length < 0) throw new InvalidOperationException("libghostty could not encode the paste.");
        return output[..length];
    }
    public void Paste(string text, bool clipboard = true)
    {
        ThrowIfDisposed(); var bytes = Encoding.UTF8.GetBytes(text);
        if (Native.ot_clipboard_paste(_handle, bytes, (nuint)bytes.Length, clipboard ? 1 : 0) != 0)
            throw new InvalidOperationException("libghostty could not paste the content.");
    }
    public unsafe void PasteClipboard()
    {
        ThrowIfDisposed();
        if (Clipboard is not { } clipboard) return;
        var listing = clipboard.Read([], true);
        if (listing.Result != 0) throw new IOException("The clipboard is currently unavailable.");
        var allocations = new List<nint>();
        Native.MimeReader reader = (_, mime, writer) =>
        {
            try
            {
                string type = mime.Text(); var result = clipboard.Read([type], false);
                if (result.Result != 0 || !result.Contents.TryGetValue(type, out var bytes) || bytes.Length > TerminalClipboardLimits.MaxBytes) return false;
                var write = Marshal.GetDelegateForFunctionPointer<Native.WriterCallback>(writer.Write);
                fixed (byte* data = bytes) return write(writer.Context, (nint)data, (nuint)bytes.Length);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return false; }
        };
        try
        {
            var mimes = listing.Available.Where(mime => mime.Length is > 0 and <= 200).Distinct().Take(256).Select(mime =>
            {
                var bytes = Encoding.UTF8.GetBytes(mime); var pointer = Marshal.AllocHGlobal(bytes.Length); allocations.Add(pointer);
                Marshal.Copy(bytes, 0, pointer, bytes.Length); return new Native.String { Pointer = pointer, Length = (nuint)bytes.Length };
            }).ToArray();
            if (Native.ot_clipboard_paste_mimes(_handle, mimes, (nuint)mimes.Length, reader, 0) != 0)
                throw new IOException("The clipboard representation could not be read.");
        }
        finally { foreach (var allocation in allocations) Marshal.FreeHGlobal(allocation); GC.KeepAlive(reader); }
    }
    public int Drop(int action, int x = 0, int y = 0, IReadOnlyDictionary<string, byte[]>? items = null, uint operations = 1)
    {
        ThrowIfDisposed();
        if (action == 1 && _dropCompletion is not null) CancelDrop();
        if (items is null) return Native.ot_drop(_handle, action, x, y, operations, [], 0);
        if (items.Count > 16 || items.Sum(item => (long)item.Value.Length) > TerminalClipboardLimits.MaxBytes) throw new ArgumentException("Drop exceeds the size limit.");
        var allocations = new List<nint>();
        Native.String Allocate(byte[] bytes)
        {
            var pointer = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)); allocations.Add(pointer);
            if (bytes.Length > 0) Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return new() { Pointer = pointer, Length = (nuint)bytes.Length };
        }
        try
        {
            var contents = items.Select(item => new Native.ClipboardContent { Mime = Allocate(Encoding.UTF8.GetBytes(item.Key)), Data = Allocate(item.Value) }).ToArray();
            return Native.ot_drop(_handle, action, x, y, operations, contents, (nuint)contents.Length);
        }
        finally { foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer); }
    }
    // Caller serializes this with VT writes. Completion runs on the reader's
    // callback; asynchronous continuations never execute while holding its gate.
    public Task<int> BeginDrop(int x, int y, IReadOnlyDictionary<string, byte[]> items, uint operations = 1)
    {
        ThrowIfDisposed();
        CancelDrop();
        if (Drop(0) != 1 || Drop(4, operations: operations) == 0) return Task.FromResult(0);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dropCompletion = completion;
        _dropOperations = operations & 3;
        try { if (Drop(3, x, y, items, operations) != 1) CancelDrop(); }
        catch { CancelDrop(); throw; }
        return completion.Task;
    }
    public void CancelDrop(Task<int>? expected = null)
    {
        if (_dropCompletion is null) return;
        if (expected is not null && _dropCompletion.Task != expected) return;
        FinishDrop(0);
        if (_handle != 0) Native.ot_drop(_handle, 5, 0, 0, 0, [], 0);
    }
    private void FinishDrop(int operation)
    {
        var completion = _dropCompletion; _dropCompletion = null;
        completion?.TrySetResult((_dropOperations & (uint)operation) != 0 ? operation : 0);
    }
    public string Metadata(int kind)
    {
        ThrowIfDisposed();
        var buffer = _metadataBuffer ??= new byte[8192];
        var length = Native.ot_metadata(_handle, kind, buffer, (nuint)buffer.Length);
        // The native bridge maps zero to title and every other value to cwd.
        int index = kind == 0 ? 0 : 1;
        var bytes = buffer.AsSpan(0, length);
        if (bytes.SequenceEqual(_metadataBytes[index])) return _metadataText[index];
        _metadataBytes[index] = bytes.ToArray();
        return _metadataText[index] = Encoding.UTF8.GetString(bytes);
    }
    public bool MouseTracking { get { ThrowIfDisposed(); return Native.ot_mouse_tracking(_handle) != 0; } }
    public byte[]? EncodeMouse(int action, int button, uint modifiers, float x, float y)
    {
        ThrowIfDisposed(); var output = new byte[128];
        int length = Native.ot_mouse(_handle, action, button, modifiers, x, y, output, (nuint)output.Length);
        return length < 0 ? null : output[..length];
    }
    public void Focus(bool focused) { ThrowIfDisposed(); Native.ot_focus(_handle, focused ? 1 : 0); }
    public void Select(int start, int end) { ThrowIfDisposed(); Native.ot_select(_handle, start, end); }
    public int SelectionGesture(int action, float x, float y, bool rectangle = false) { ThrowIfDisposed(); return Native.ot_gesture(_handle, action, x, y, rectangle ? 1 : 0); }
    public string CopySelection()
    {
        ThrowIfDisposed(); var bytes = new byte[65536];
        int length = Native.ot_copy(_handle, bytes, (nuint)bytes.Length);
        if (length > bytes.Length) { bytes = new byte[length]; length = Native.ot_copy(_handle, bytes, (nuint)bytes.Length); }
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
    public (int Count, int Index) Find(string text, int direction)
    {
        ThrowIfDisposed(); var bytes = Encoding.UTF8.GetBytes(text);
        if (Native.ot_find(_handle, bytes, (nuint)bytes.Length, direction, out var count, out var index) != 0)
            throw new InvalidOperationException("libghostty search failed.");
        return (checked((int)count), checked((int)index));
    }
    private void HandleClipboard(nint pointer)
    {
        var request = Marshal.PtrToStructure<Native.ClipboardRequest>(pointer);
        var allocations = new List<nint>();
        Native.String Allocate(byte[] value)
        {
            if (value.Length == 0) return default;
            var memory = Marshal.AllocHGlobal(value.Length); allocations.Add(memory);
            Marshal.Copy(value, 0, memory, value.Length);
            return new() { Pointer = memory, Length = (nuint)value.Length };
        }
        try
        {
            int result = 1;
            Native.ClipboardContent[] contents = [];
            Native.String[] available = [];
            if (request.Location != 0 || Clipboard is null) result = 2;
            else if (request.Write != 0 && (AllowClipboardWrite || request.Granted != 0))
            {
                if (request.ContentsLength > 256) result = 4;
                else
                {
                    var representations = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    long total = 0;
                    for (int i = 0; i < (int)request.ContentsLength; i++)
                    {
                        var item = Marshal.PtrToStructure<Native.ClipboardContent>(request.Contents + i * Marshal.SizeOf<Native.ClipboardContent>());
                        total += checked((long)item.Data.Length);
                        if (total > TerminalClipboardLimits.MaxBytes) throw new InvalidDataException("Clipboard request exceeds the limit.");
                        var bytes = new byte[checked((int)item.Data.Length)];
                        if (bytes.Length > 0) Marshal.Copy(item.Data.Pointer, bytes, 0, bytes.Length);
                        representations.Add(item.Mime.Text(), bytes);
                    }
                    result = Clipboard.Write(representations);
                }
            }
            else if (request.Write == 0 && (AllowClipboardRead || request.Granted != 0 || (request.List != 0 && request.MimesLength == 0)))
            {
                if (request.MimesLength > 256) result = 2;
                else
                {
                    var mimes = new List<string>();
                    for (int i = 0; i < (int)request.MimesLength; i++)
                        mimes.Add(Marshal.PtrToStructure<Native.String>(request.Mimes + i * Marshal.SizeOf<Native.String>()).Text());
                    var read = Clipboard.Read(mimes, request.List != 0);
                    result = read.Result;
                    contents = read.Contents.Select(item => new Native.ClipboardContent { Mime = Allocate(Encoding.UTF8.GetBytes(item.Key)), Data = Allocate(item.Value) }).ToArray();
                    available = read.Available.Select(mime => Allocate(Encoding.UTF8.GetBytes(mime))).ToArray();
                }
            }
            Native.ot_clipboard_reply(pointer, result, contents, (nuint)contents.Length, available, (nuint)available.Length, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            Native.ot_clipboard_reply(pointer, 4, [], 0, [], 0, 0);
        }
        finally { foreach (var allocation in allocations) Marshal.FreeHGlobal(allocation); }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle == 0, this);
    public void Dispose()
    {
        FinishDrop(0);
        if (_handle == 0) return;
        Native.ot_destroy(_handle);
        _handle = 0;
        _images.Clear();
        _lastCells = null; _nativeCells = [];
        GC.KeepAlive(_effect);
    }
    internal static class Native
    {
        private const string Library = "toiqtty-native";
        internal const int CellTextBytes = 32;
        static Native()
        {
            uint expected = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<Cell>()
                | ((uint)Marshal.OffsetOf<Cell>(nameof(Cell.Text)).ToInt32() << 8) | (CellTextBytes << 16);
            if (CellLayout() != expected) throw new BadImageFormatException("Native terminal cell layout mismatch. Rebuild the native bridge and publish the matching application assemblies.");
        }
        [DllImport(Library, EntryPoint = "ot_cell_layout", CallingConvention = CallingConvention.Cdecl)] private static extern uint CellLayout();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong ot_capture_text_bytes(nint terminal);
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct Cell
        {
            public uint Foreground, Background, Flags, Length, UnderlineColor;
            public byte UnderlineStyle, Width;
            public nint ExtendedText;
            public CellText Text;
            public TerminalCell ToCell(string? previousText)
            {
                var result = new TerminalCell
                {
                    Foreground = Foreground,
                    Background = Background,
                    Flags = Flags,
                    Length = Length,
                    UnderlineColor = UnderlineColor,
                    UnderlineStyle = UnderlineStyle,
                    Width = Width
                };
                if (ExtendedText != 0 && Length > 0) result.Text = TerminalCell.DecodeText(new ReadOnlySpan<byte>((byte*)ExtendedText, checked((int)Length)), previousText);
                else result.Text = TerminalCell.DecodeText(((ReadOnlySpan<byte>)Text)[..(int)Math.Min(Length, (uint)CellTextBytes)], previousText);
                return result;
            }
        }
        [System.Runtime.CompilerServices.InlineArray(CellTextBytes)]
        internal struct CellText { private byte _first; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct String
        {
            public nint Pointer; public nuint Length;
            public readonly string Text() => Length == 0 ? "" : Marshal.PtrToStringUTF8(Pointer, checked((int)Length))!;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct HostEvent
        {
            public int State, Detail, Progress, HasExitCode, ExitCode;
            public String Id, App, Title, Body;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ClipboardContent { public String Mime, Data; }
        [StructLayout(LayoutKind.Sequential)] internal struct Writer { public nint Write, Context; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool MimeReader(nint context, String mime, Writer writer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool WriterCallback(nint context, nint bytes, nuint length);
        [StructLayout(LayoutKind.Sequential)]
        internal struct ClipboardRequest
        {
            public nint Request;
            public int Write, Location, List, Granted, CanRemember;
            public nint Contents; public nuint ContentsLength;
            public nint Mimes; public nuint MimesLength;
            public String Name;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Graphic
        {
            public ulong Generation;
            public uint ImageId, PlacementId, Width, Height, Channels;
            public int Z, Column, Row;
            public uint OffsetX, OffsetY, PixelWidth, PixelHeight, SourceX, SourceY, SourceWidth, SourceHeight;
            public nint Pixels;
            public nuint Length;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Effect(nint context, int kind, nint data, nuint length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint ot_create(ushort columns, ushort rows, Effect effect, nint context);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_destroy(nint terminal);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_identity(nint terminal, byte[] identity, nuint length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_scrollback_limits(nint terminal, long bytes, long lines);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_theme(nint terminal, uint foreground, uint background, uint cursor, uint[]? palette, nuint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_write(nint terminal, byte[] data, nuint length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_resize(nint terminal, ushort columns, ushort rows, uint width, uint height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static unsafe extern int ot_frame(nint terminal, out TerminalFrame frame, Cell* cells, nuint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_incremental(nint terminal, int enabled);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_image_files(nint terminal, int enabled);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_row_changed(nint terminal, uint row);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_clipboard_reply(nint request, int result, ClipboardContent[] contents, nuint count, String[] available, nuint availableCount, int remember);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_clipboard_paste(nint terminal, byte[] text, nuint length, int clipboard);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_clipboard_paste_mimes(nint terminal, String[] mimes, nuint count, MimeReader reader, nint context);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_drop(nint terminal, int action, int x, int y, uint operations, ClipboardContent[] contents, nuint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_graphics(nint terminal, out nint graphics);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong ot_graphics_cache_bytes(nint terminal);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_hyperlink(nint terminal, ushort column, ushort row, byte[] output, nuint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_palette_entries(nint terminal, uint[] indices, uint[] colors, nuint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_cursor_defaults(nint terminal, uint style, [MarshalAs(UnmanagedType.I1)] bool blink);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_scroll(nint terminal, int delta);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_scrollbar(nint terminal, out ulong total, out ulong offset, out ulong visible);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_scroll_to(nint terminal, ulong row);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_key(nint terminal, uint key, uint modifiers, int action, uint unshifted, uint consumedModifiers, byte[] text, nuint length, byte[] output, nuint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_paste(nint terminal, byte[] text, nuint length, byte[] output, nuint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_metadata(nint terminal, int kind, byte[] output, nuint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_mouse(nint terminal, int action, int button, uint modifiers, float x, float y, byte[] output, nuint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_mouse_tracking(nint terminal);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_focus(nint terminal, int focused);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ot_select(nint terminal, int start, int end);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_gesture(nint terminal, int action, float x, float y, int rectangle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_copy(nint terminal, byte[] output, nuint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ot_find(nint terminal, byte[] text, nuint length, int direction, out nuint count, out nuint index);
    }
}
