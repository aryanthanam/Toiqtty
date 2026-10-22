using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

/// <summary>Protocol-3 request factories: one per operation, each setting only that
/// operation's typed payload. Pointer coordinates are sent as x1000 fixed point.</summary>
internal static class HostRequests
{
    private static HostRequest Pane(string operation, string pane, string text = "") => new() { Operation = operation, Pane = pane, Text = text };
    internal static HostRequest Ping() => new();
    internal static HostRequest Shutdown() => new() { Operation = "shutdown" };
    internal static HostRequest Detach() => new() { Operation = "detach" };
    internal static HostRequest States(Dictionary<string, int> versions) => new() { Operation = "states", StateVersions = versions };
    internal static HostRequest Watch(Dictionary<string, long> panes) => new() { Operation = "watch", Watch = new(panes) };
    internal static HostRequest Attach(AppSettings[] settings, AttachPane[] panes) => new() { Operation = "attach", Attach = new(settings, panes) };
    internal static HostRequest Sync(SavedSession layout, string? epoch = null, long revision = 0) => new() { Operation = "sync", Layout = layout, LayoutEpoch = epoch, LayoutRevision = revision };
    internal static HostRequest Navigate(HostNavigation navigation) => new() { Operation = "navigate", Navigation = navigation };
    internal static HostRequest Create(string pane, string command, string directory, AppSettings? settings, SavedSession? layout = null,
        string? epoch = null, long revision = 0, int columns = 0, int rows = 0)
    {
        var request = Pane("create", pane); request.Create = new(command, directory, columns, rows);
        request.Settings = settings; request.Layout = layout; request.LayoutEpoch = epoch; request.LayoutRevision = revision; return request;
    }
    internal static HostRequest Close(string pane) => Pane("close", pane);
    internal static HostRequest Capture(string pane, int knownVersion = -1, long knownFrameRevision = -1, long knownStatusRevision = -1, ulong[]? images = null)
    { var request = Pane("capture", pane); request.Capture = new(knownVersion, knownFrameRevision, knownStatusRevision, images); return request; }
    internal static HostRequest Resize(string pane, int columns, int rows, int cellWidth, int cellHeight)
    { var request = Pane("resize", pane); request.Resize = new(columns, rows, cellWidth, cellHeight); return request; }
    internal static HostRequest Key(string pane, uint key, uint modifiers, string text = "", TerminalKeyAction action = TerminalKeyAction.Press, uint? unshifted = null, uint consumed = 0)
    { var request = Pane("key", pane, text); request.Key = new(key, modifiers, action, unshifted, consumed); return request; }
    internal static HostRequest CopyOrKey(string pane, uint key, uint modifiers, string text, TerminalKeyAction action, uint? unshifted, uint consumed)
    { var request = Pane("copy-or-key", pane, text); request.Key = new(key, modifiers, action, unshifted, consumed); return request; }
    internal static HostRequest Paste(string pane, string text, bool clipboard = false)
    { var request = Pane("paste", pane, text); request.Paste = new(clipboard); return request; }
    internal static HostRequest PasteChecked(string pane, string text) => Pane("paste-checked", pane, text);
    internal static HostRequest SendText(string pane, string text) => Pane("send-text", pane, text);
    internal static HostRequest Clipboard(string pane) => Pane("clipboard", pane);
    internal static HostRequest Scroll(string pane, int delta) { var request = Pane("scroll", pane); request.Scroll = new(delta); return request; }
    internal static HostRequest ScrollTo(string pane, ulong row) { var request = Pane("scroll-to", pane); request.ScrollTo = new(row); return request; }
    internal static HostRequest Focus(string pane, bool focused) { var request = Pane("focus", pane); request.Focus = new(focused); return request; }
    internal static HostRequest Select(string pane, int start, int end) { var request = Pane("select", pane); request.Select = new(start, end); return request; }
    internal static HostRequest Gesture(string pane, int action, float x, float y, bool rectangle)
    { var request = Pane("gesture", pane); request.Gesture = new(action, (int)(x * 1000), (int)(y * 1000), rectangle); return request; }
    internal static HostRequest Mouse(string pane, int action, int button, uint modifiers, float x, float y)
    { var request = Pane("mouse", pane); request.Mouse = new(action, button, modifiers, (int)(x * 1000), (int)(y * 1000)); return request; }
    internal static HostRequest DropQuery(string pane, int action, int x, int y, uint operations, IReadOnlyDictionary<string, byte[]>? items)
    { var request = Pane("drop", pane); request.DropQuery = new(action, x, y, operations, items?.ToDictionary()); return request; }
    internal static HostRequest DropCommit(string pane, int x, int y, uint operations, TimeSpan timeout, IReadOnlyDictionary<string, byte[]> items)
    { var request = Pane("drop-async", pane); request.DropCommit = new(x, y, operations, (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue), items.ToDictionary()); return request; }
    internal static HostRequest Copy(string pane) => Pane("copy", pane);
    internal static HostRequest Hyperlink(string pane, ushort column, ushort row) { var request = Pane("hyperlink", pane); request.Cell = new(column, row); return request; }
    internal static HostRequest Find(string pane, string text, int direction) { var request = Pane("find", pane, text); request.Find = new(direction); return request; }
    internal static HostRequest Settings(string pane, AppSettings settings) { var request = Pane("settings", pane); request.Settings = settings; return request; }
}
