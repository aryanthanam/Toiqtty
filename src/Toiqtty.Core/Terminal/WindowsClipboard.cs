using System.Runtime.InteropServices;
using System.Text;

namespace Toiqtty.Terminal;

/// <summary>Synchronous, thread-local Win32 access for Ghostty's synchronous clipboard callbacks.</summary>
public sealed class WindowsClipboard : ITerminalClipboard
{
    private const uint UnicodeText = 13;
    private const string ExactPrefix = "Toiqtty MIME length: ";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public int Write(IReadOnlyDictionary<string, byte[]> contents)
    {
        var handles = new Dictionary<uint, nint>();
        nint owner = 0;
        bool opened = false;
        try
        {
            long total = 0;
            foreach (var (mime, data) in contents)
            {
                total += data.Length;
                if (!ValidMime(mime)) return 2;
                if (total > TerminalClipboardLimits.MaxBytes) return 4;
                if (mime is "text/plain" or "text/plain;charset=utf-8")
                {
                    var text = Utf8.GetString(data);
                    if (text.Contains('\0')) return 4;
                    Add(UnicodeText, Encoding.Unicode.GetBytes(text + "\0"));
                }
                else
                {
                    Add(RegisterClipboardFormatW(mime), data);
                    if (mime == "image/png") Add(RegisterClipboardFormatW("PNG"), data);
                    if (mime == "text/html") Add(RegisterClipboardFormatW("HTML Format"), EncodeHtml(Utf8.GetString(data)));
                }
                // Windows can round HGLOBAL allocations; preserve exact binary lengths.
                var exact = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(exact, data.Length);
                data.CopyTo(exact, 4); Add(RegisterClipboardFormatW(ExactPrefix + mime), exact);
            }
            owner = CreateWindowExW(0, "STATIC", "Toiqtty clipboard", 0, 0, 0, 0, 0, -3, 0, 0, 0);
            if (owner == 0) return 5;
            if (!OpenClipboard(owner)) return 3;
            opened = true;
            if (!EmptyClipboard()) return 5;
            foreach (var format in handles.Keys.ToArray())
            {
                if (SetClipboardData(format, handles[format]) == 0) return 5;
                handles[format] = 0; // The system now owns this HGLOBAL.
            }
            return 0;
        }
        catch (DecoderFallbackException) { return 4; }
        finally
        {
            if (opened) CloseClipboard();
            if (owner != 0) DestroyWindow(owner);
            foreach (var handle in handles.Values) if (handle != 0) GlobalFree(handle);
        }
        void Add(uint format, byte[] bytes)
        {
            if (format == 0) throw new InvalidOperationException("Cannot register clipboard format.");
            var memory = GlobalAlloc(2, (nuint)Math.Max(1, bytes.Length));
            if (memory == 0) throw new OutOfMemoryException();
            var pointer = GlobalLock(memory);
            if (pointer == 0) { GlobalFree(memory); throw new InvalidOperationException("Cannot lock clipboard storage."); }
            try { if (bytes.Length > 0) Marshal.Copy(bytes, 0, pointer, bytes.Length); else Marshal.WriteByte(pointer, 0); }
            finally { GlobalUnlock(memory); }
            if (handles.Remove(format, out var old)) GlobalFree(old);
            handles.Add(format, memory);
        }
    }
    public ClipboardReadResult Read(IReadOnlyList<string> mimes, bool list)
    {
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var available = new HashSet<string>(StringComparer.Ordinal);
        if (!OpenClipboard(0)) return new(3, contents, []);
        try
        {
            uint format = 0;
            for (int i = 0; i < 512 && (format = EnumClipboardFormats(format)) != 0; i++)
            {
                if (format == UnicodeText) { available.Add("text/plain"); continue; }
                var name = new StringBuilder(512);
                if (GetClipboardFormatNameW(format, name, name.Capacity) == 0) continue;
                var mime = name.ToString();
                if (mime.StartsWith(ExactPrefix, StringComparison.Ordinal)) mime = mime[ExactPrefix.Length..];
                if (ValidMime(mime)) available.Add(mime);
                if (mime == "PNG") available.Add("image/png");
                if (mime == "HTML Format") available.Add("text/html");
            }
            long total = 0;
            foreach (var mime in mimes.Distinct())
            {
                if (!ValidMime(mime)) continue;
                uint exactFormat = RegisterClipboardFormatW(ExactPrefix + mime);
                byte[]? data = Get(exactFormat, true);
                if (data is null && mime is "text/plain" or "text/plain;charset=utf-8")
                {
                    var raw = Get(UnicodeText, false);
                    if (raw is not null)
                    {
                        var text = Encoding.Unicode.GetString(raw);
                        int end = text.IndexOf('\0');
                        data = Utf8.GetBytes(end < 0 ? text : text[..end]);
                    }
                }
                else data ??= Get(RegisterClipboardFormatW(mime), false);
                if (data is null && mime == "image/png") data = Get(RegisterClipboardFormatW("PNG"), false);
                if (data is null && mime == "text/html")
                {
                    var html = Get(RegisterClipboardFormatW("HTML Format"), false);
                    if (html is not null) data = DecodeHtml(html);
                }
                if (data is null) continue;
                total += data.Length;
                if (total > TerminalClipboardLimits.MaxBytes) return new(4, new Dictionary<string, byte[]>(), []);
                contents[mime] = data;
            }
            return new(0, contents, list ? available.Take(256).ToArray() : []);
        }
        finally { CloseClipboard(); }
    }
    private static byte[]? Get(uint format, bool exact)
    {
        var handle = GetClipboardData(format);
        if (handle == 0) return null;
        nuint size = GlobalSize(handle);
        if (size > TerminalClipboardLimits.MaxBytes * 2u + 16 || (exact && size < 4)) return null;
        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;
        try
        {
            int length = exact ? Marshal.ReadInt32(pointer) : checked((int)size);
            if (length < 0 || (nuint)length > size - (exact ? 4u : 0u) || length > TerminalClipboardLimits.MaxBytes * 2) return null;
            var bytes = new byte[length];
            if (length > 0) Marshal.Copy(pointer + (exact ? 4 : 0), bytes, 0, length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }
    private static bool ValidMime(string mime) => mime.Length is > 2 and <= 200 && mime.Contains('/') && mime.All(c => c is >= '!' and <= '~');
    internal static byte[] EncodeHtml(string fragment)
    {
        const string template = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html><body><!--StartFragment-->", after = "<!--EndFragment--></body></html>";
        int headerLength = Encoding.ASCII.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 0, 0, 0, 0));
        int start = headerLength + Encoding.UTF8.GetByteCount(before), end = start + Encoding.UTF8.GetByteCount(fragment);
        return Encoding.UTF8.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, headerLength,
            end + Encoding.UTF8.GetByteCount(after), start, end) + before + fragment + after + "\0");
    }
    internal static byte[]? DecodeHtml(byte[] html)
    {
        var header = Encoding.ASCII.GetString(html, 0, Math.Min(html.Length, 1024));
        int Offset(string key)
        {
            int index = header.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;
            int start = index + key.Length, end = header.IndexOfAny(['\r', '\n'], start);
            return int.TryParse(header.AsSpan(start, (end < 0 ? header.Length : end) - start), out var value) ? value : -1;
        }
        int first = Offset("StartFragment:"), last = Offset("EndFragment:");
        if (first < 0 || last < first || last > html.Length) return null;
        return html[first..last];
    }
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string format);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatNameW(uint format, StringBuilder name, int count);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("kernel32")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32")] private static extern nint GlobalFree(nint memory);
}
