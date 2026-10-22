using System.Text.Json.Serialization;
using Toiqtty.Models;
using Toiqtty.Terminal;

namespace Toiqtty.Hosting;

// Protocol-3 per-operation payloads. A request sets only the payload its operation
// names; the host decodes and range-checks it after the protocol and identity checks,
// so a payload from another version never stops a mismatch reply. Numbers the host
// bounds are ints: an out-of-range value reaches that check and gets InvalidRequest.
// Pointer coordinates stay x1000 fixed point (exact, cheap to format on input paths).

/// <summary>key and copy-or-key. The typed text travels in HostRequest.Text.</summary>
public sealed record KeyArgs(uint Key, uint Modifiers, TerminalKeyAction Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Unshifted, uint Consumed);
public sealed record ResizeArgs(int Columns, int Rows, int CellWidth, int CellHeight);
public sealed record GestureArgs(int Action, int XMilli, int YMilli, bool Rectangle);
public sealed record MouseArgs(int Action, int Button, uint Modifiers, int XMilli, int YMilli);
public sealed record ScrollArgs(int Delta);
public sealed record ScrollToArgs(ulong Row);
public sealed record SelectArgs(int Start, int End);
public sealed record FocusArgs(bool Focused);
/// <summary>paste; the text travels in HostRequest.Text.</summary>
public sealed record PasteArgs(bool Clipboard);
/// <summary>find; the needle travels in HostRequest.Text.</summary>
public sealed record FindArgs(int Direction);
/// <summary>hyperlink lookup at one cell.</summary>
public sealed record CellArgs(ushort Column, ushort Row);
/// <summary>drop: a synchronous enter/move/leave/drop query.</summary>
public sealed record DropQueryArgs(int Action, int X, int Y, uint Operations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, byte[]>? Items);
/// <summary>drop-async: commit a drop and wait for the application's conclusion.</summary>
public sealed record DropCommitArgs(int X, int Y, uint Operations, int TimeoutMilliseconds, Dictionary<string, byte[]> Items);
/// <summary>capture. KnownVersion -1 asks for a full frame; KnownStatusRevision -1 means no status baseline.</summary>
public sealed record CaptureArgs(int KnownVersion, long KnownFrameRevision, long KnownStatusRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong[]? Images);
/// <summary>watch: every interested pane with the last output revision seen (-1 none).</summary>
public sealed record WatchArgs(Dictionary<string, long> Panes);
/// <summary>create. Columns/Rows 0 keep the default 100x30 until the first resize.</summary>
public sealed record CreateArgs(string Command, string Directory, int Columns, int Rows);
/// <summary>One pane to reattach; SettingsIndex selects AttachArgs.Settings, -1 keeps the pane's settings.</summary>
public sealed record AttachPane(string Id, int SettingsIndex);
/// <summary>attach: batched reattach. Settings are deduplicated and shared by index.</summary>
public sealed record AttachArgs(AppSettings[] Settings, AttachPane[] Panes);
public sealed record FindResult(int Count, int Index);
