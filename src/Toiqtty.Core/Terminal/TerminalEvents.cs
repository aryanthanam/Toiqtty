namespace Toiqtty.Terminal;

public enum TerminalEventKind { Notification = 2, Progress = 3, ProgramStatus = 4, SemanticPrompt = 5, Reset = 8 }
public sealed record TerminalEvent(TerminalEventKind Kind, int State = 0, int Detail = 0, int Progress = -1,
    int? ExitCode = null, string Id = "", string App = "", string Title = "", string Body = "");
public sealed record TerminalNotice(DateTimeOffset Time, string Title, string Body);
public sealed record TerminalCommand(string Text, DateTimeOffset Started, DateTimeOffset? Finished = null, int? ExitCode = null, string Error = "", TimeSpan? Elapsed = null)
{
    public TimeSpan? Duration => Elapsed ?? (Finished is { } end ? end - Started : null);
}
public sealed record TerminalStatusSnapshot(int ProgressState, int Progress, bool CommandRunning, int? LastExitCode,
    IReadOnlyList<TerminalEvent> Programs, IReadOnlyList<TerminalNotice> Notices, IReadOnlyList<TerminalCommand> Commands)
{
    [System.Text.Json.Serialization.JsonIgnore] internal long Revision { get; init; }
}

/// <summary>Only called under the session gate. Terminal output cannot grow histories without bounds.</summary>
internal sealed class TerminalStatus
{
    private readonly Dictionary<string, TerminalEvent> _programs = new(StringComparer.Ordinal);
    private readonly Queue<TerminalNotice> _notices = new();
    private readonly List<TerminalCommand> _commands = [];
    private int _progressState, _progress = -1;
    private bool _running;
    private int? _exitCode;
    private long _commandStarted;
    private TerminalStatusSnapshot? _snapshot;
    private long _snapshotRevision;
    public TerminalNotice? LatestNotice => _notices.LastOrDefault();
    private static string Limit(string text, int limit = 4096) => text.Length <= limit ? text : text[..limit];
    public void Apply(TerminalEvent item)
    {
        _snapshot = null;
        switch (item.Kind)
        {
            case TerminalEventKind.Reset:
                _programs.Clear(); _commands.Clear(); _notices.Clear(); _progressState = 0; _progress = -1; _running = false; _exitCode = null; break;
            case TerminalEventKind.Notification:
                while (_notices.Count >= 100) _notices.Dequeue();
                _notices.Enqueue(new(DateTimeOffset.Now, Limit(item.Title), Limit(item.Body))); break;
            case TerminalEventKind.Progress:
                _progressState = item.State; _progress = item.Progress; break;
            case TerminalEventKind.ProgramStatus:
                if (item.State == 5)
                {
                    foreach (var id in _programs.Keys.Where(id => item.Id.Length == 0 || id == item.Id || id.StartsWith(item.Id + "/", StringComparison.Ordinal)).ToArray()) _programs.Remove(id);
                }
                else
                {
                    if (!_programs.ContainsKey(item.Id) && _programs.Count >= 128) _programs.Remove(_programs.Keys.First());
                    _programs[item.Id] = item with { Id = Limit(item.Id, 1024), App = Limit(item.App), Title = Limit(item.Title), Body = Limit(item.Body) };
                }
                break;
            case TerminalEventKind.SemanticPrompt:
                if (item.State == 1 && item.Detail == 0)
                {
                    Finish(null, "Prompt returned without a completion marker");
                    _programs.Clear(); _progressState = 0; _progress = -1;
                }
                if (item.State == 3)
                {
                    Finish(null, "Command tracking restarted");
                    _running = true;
                    _commandStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (_commands.Count >= 100) _commands.RemoveAt(0);
                    _commands.Add(new(Limit(item.Title, 8192), DateTimeOffset.Now));
                }
                else if (item.State == 4)
                {
                    _running = false; _exitCode = item.ExitCode;
                    if (_commands.Count > 0 && _commands[^1].Finished is null)
                        _commands[^1] = _commands[^1] with
                        {
                            Finished = DateTimeOffset.Now,
                            ExitCode = item.ExitCode,
                            Error = Limit(item.Body),
                            Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_commandStarted)
                        };
                }
                break;
        }
    }
    public void Finish(int? exitCode, string error)
    {
        if (_running) Apply(new(TerminalEventKind.SemanticPrompt, State: 4, ExitCode: exitCode, Body: error));
    }
    public TerminalStatusSnapshot Snapshot() => _snapshot ??= new(_progressState, _progress, _running, _exitCode,
        Freeze(_programs.Values.ToArray()), Freeze(_notices.ToArray()), Freeze(_commands.ToArray()))
    { Revision = ++_snapshotRevision };
    private static IReadOnlyList<T> Freeze<T>(T[] values) => values.Length == 0 ? values : Array.AsReadOnly(values);
    public string Summary
    {
        get
        {
            var program = _programs.Values.LastOrDefault(item => item.State is 1 or 3 or 4) ?? _programs.Values.LastOrDefault();
            if (program is not null) return (program.State switch { 1 => "Working", 2 => "Done", 3 => "Needs attention", 4 => "Error", _ => "Idle" })
                + (program.Progress >= 0 ? $" {program.Progress}%" : "") + (program.Title.Length > 0 ? " · " + program.Title : "");
            if (_progressState != 0) return (_progressState switch { 2 => "Failed", 4 => "Paused", _ => "Progress" }) + (_progress >= 0 ? $" {_progress}%" : "…");
            return _running ? "Running" : _exitCode is { } code ? $"exit {code}" : "";
        }
    }
}

public static class TerminalClipboardLimits { public const int MaxBytes = 8 * 1024 * 1024; }
public sealed record ClipboardReadResult(int Result, IReadOnlyDictionary<string, byte[]> Contents, IReadOnlyList<string> Available);
public interface ITerminalClipboard
{
    int Write(IReadOnlyDictionary<string, byte[]> contents);
    ClipboardReadResult Read(IReadOnlyList<string> mimes, bool list);
}
