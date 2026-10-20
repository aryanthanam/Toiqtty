namespace Toiqtty.Terminal;

/// <summary>A terminal owned locally by a test, or remotely by the session host.</summary>
public interface ITerminalSession : IDisposable
{
    int Version { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    string Title { get; }
    string StatusSummary { get; }
    string Directory { get; }
    string WorkingDirectory { get; }
    string? LocalDirectory { get; }
    TerminalPerformance Performance { get; }
    TerminalStatusSnapshot Status { get; }
    event Action? Attention;
    event Action<TerminalNotice>? Notification;
    TerminalFrame ReadFrame(TerminalCell[] cells);
    TerminalSnapshot ReadSnapshot(TerminalCell[] cells);
    bool TryReadSnapshot(TerminalCell[] cells, out TerminalSnapshot? snapshot);
    void ApplySettings(Models.AppSettings settings);
    void Resize(ushort columns, ushort rows, uint width, uint height);
    void Key(uint key, uint modifiers, string text = "", TerminalKeyAction action = TerminalKeyAction.Press, uint? unshiftedCodepoint = null, uint consumedModifiers = 0);
    void Paste(string text, bool clipboard = false);
    void SendText(string text) => throw new NotSupportedException("Literal text sending needs an updated session host.");
    Task SendTextAsync(string text) { SendText(text); return Task.CompletedTask; }
    Task PasteAsync(string text) { Paste(text); return Task.CompletedTask; }
    void PasteClipboard();
    void Scroll(int delta);
    void ScrollTo(ulong row);
    void Focus(bool focused);
    void Select(int start, int end);
    int SelectionGesture(int action, float x, float y, bool rectangle = false);
    int Drop(int action, int x = 0, int y = 0, IReadOnlyDictionary<string, byte[]>? items = null, uint operations = 1);
    Task<int> DropAsync(int x, int y, IReadOnlyDictionary<string, byte[]> items, uint operations = 1, TimeSpan? timeout = null);
    string CopySelection();
    string Hyperlink(ushort column, ushort row);
    (int Count, int Index) Find(string text, int direction);
    bool Mouse(int action, int button, uint modifiers, float x, float y);
    void QueueSelectionGesture(int action,float x,float y,bool rectangle=false) => SelectionGesture(action,x,y,rectangle);
    Task<string> CopySelectionAsync() => Task.FromResult(CopySelection());
    Task<string> HyperlinkAsync(ushort column,ushort row) => Task.FromResult(Hyperlink(column,row));
    Task<(int Count,int Index)> FindAsync(string text,int direction) => Task.FromResult(Find(text,direction));
    Task<int> QueryDropAsync(int action,int x=0,int y=0,IReadOnlyDictionary<string,byte[]>? items=null,uint operations=1)
        => Task.FromResult(Drop(action,x,y,items,operations));
    ValueTask CloseAsync() { Dispose(); return ValueTask.CompletedTask; }
    // Presentation detachment does not transfer or terminate local ownership.
    ValueTask DetachAsync() => ValueTask.CompletedTask;
}
