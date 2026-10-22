namespace Toiqtty.Terminal;

/// <summary>A terminal owned locally by a test, or remotely by the session host.</summary>
/// <remarks>Members the host answers return a task; void members only queue. Nothing here
/// may block the caller on IPC (ArchitectureChecks enforces this). In-process sessions
/// complete their tasks synchronously.</remarks>
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
    /// <summary>Completes once the shell exists; faults when it could not be started.</summary>
    Task Ready { get; }
    event Action? Attention;
    event Action<TerminalNotice>? Notification;
    // Reads the cached frame only; never waits for the parser or the host.
    bool TryReadSnapshot(TerminalCell[] cells, out TerminalSnapshot? snapshot);
    void ApplySettings(Models.AppSettings settings);
    /// <summary>Resizes and clears the selection; remote sessions coalesce to the latest size.</summary>
    void QueueResize(ushort columns, ushort rows, uint width, uint height);
    void Key(uint key, uint modifiers, string text = "", TerminalKeyAction action = TerminalKeyAction.Press, uint? unshiftedCodepoint = null, uint consumedModifiers = 0);
    void Paste(string text, bool clipboard = false);
    void PasteClipboard();
    void Scroll(int delta);
    void ScrollTo(ulong row);
    void Focus(bool focused);
    void Select(int start, int end);
    void QueueSelectionGesture(int action, float x, float y, bool rectangle = false);
    // Answers from the published MouseTracking state, then queues the report.
    bool Mouse(int action, int button, uint modifiers, float x, float y);
    Task SendTextAsync(string text);
    Task PasteAsync(string text);
    Task<string> CopySelectionAsync();
    /// <summary>Ctrl+C: copies the selection, or sends the key when nothing is selected. A copied
    /// key's release is suppressed. Returns the copied text, empty when the key was sent.</summary>
    Task<string> CopyOrKeyAsync(uint key, uint modifiers, string text, TerminalKeyAction action, uint? unshiftedCodepoint, uint consumedModifiers);
    /// <summary>The OSC 8 target at a viewport cell, empty when none. A hover lookup is
    /// latest-wins: remote sessions keep one in flight and one pending, and a newer hover
    /// cancels the pending one. Cancellation never undoes a lookup already sent.</summary>
    Task<string> HyperlinkAsync(ushort column, ushort row, bool hover = false, CancellationToken cancellation = default);
    /// <summary>Direction 0 searches anew and supersedes any find not yet sent, which is then
    /// cancelled; steps (+1 older, -1 newer) queue in order. Cancellation withdraws an unsent
    /// find only; one already sent still runs on the host.</summary>
    Task<(int Count, int Index)> FindAsync(string text, int direction, CancellationToken cancellation = default);
    Task<int> QueryDropAsync(int action, int x = 0, int y = 0, IReadOnlyDictionary<string, byte[]>? items = null, uint operations = 1);
    Task<int> DropAsync(int x, int y, IReadOnlyDictionary<string, byte[]> items, uint operations = 1, TimeSpan? timeout = null);
    ValueTask CloseAsync();
    // Presentation detachment; local sessions keep their ownership.
    ValueTask DetachAsync();
}
