using System.Text;
using Toiqtty.Terminal;
using Toiqtty.Models;

int passed = 0;
if (args.Contains("--status-snapshot-client"))
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.Write("\x1b]9;4;1;75\x07\x1b[?1000hSTATUS_READY\r\n");
    int textSequence = 0;
    while (Console.ReadLine() is { } line)
    {
        if (line == "next") Console.Write("\x1b]9;4;1;25\x07\x1b[?1000lSTATUS_NEXT\r\n");
        else if (line == "history") for (int i = 0; i < 100; i++) Console.Write($"\x1b]133;C;cmdline_url=STATUS_HISTORY_{i:D3}_" + new string('x', 128) + "\x07\x1b]133;D;0\x07");
        else if (line == "text") Console.WriteLine("STATUS_TEXT_" + (++textSequence));
        else if (line == "reset") Console.Write("\u001bcSTATUS_RESET\r\n");
        else if (line == "exit") return;
    }
    return;
}
if (args.Contains("--scroll-input-client")) { ScrollInputClient.Run(); return; }
if (args.Contains("--raw-input-client")) { RawInputClient.Run(); return; }
if (args.Contains("--performance-client")) { PerformanceClient.Run(); return; }
if (args.Length == 2 && args[0] == "--codec-benchmark") { CodecPerformanceChecks.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--row-copy-benchmark") { CodecPerformanceChecks.RunRows(args[1]); return; }
if (args.Length == 2 && args[0] == "--utf8-capture-benchmark") { Utf8CaptureChecks.Benchmark(args[1]); return; }
if (args.Length == 2 && args[0] == "--parser-benchmark") { ParserPerformanceChecks.Run(args[1]); return; }
if (args.Contains("--host-json")) { await HostJsonChecks.Run(Check); return; }
if (args.Length == 2 && args[0] == "--host-json-benchmark") { HostJsonChecks.Benchmark(args[1]); return; }
if (args.Length == 3 && args[0] == "--host-json-ipc-benchmark") { await HostJsonIpcChecks.Run(args[1], args[2]); return; }
if (args.Contains("--utf8-capture")) { Utf8CaptureChecks.Run(Check); return; }
if (args.Contains("--style-runs")) { StyleRunChecks.Run(Check); return; }
if (args.Length == 2 && args[0] == "--style-run-benchmark") { StyleRunChecks.Benchmark(args[1]); return; }
if (args.Length == 2 && args[0] == "--metadata-benchmark") { CodecPerformanceChecks.RunMetadata(args[1]); return; }
if (args.Length == 2 && args[0] == "--shutdown-replies") { await ShutdownReplyChecks.Run(args[1], Check); Console.WriteLine($"{passed} shutdown acknowledgement checks passed."); return; }
if (args.Length == 4 && args[0] == "--host-transport-soak") { await HostTransportSoakChecks.Run(args[1], int.Parse(args[2]), args[3], Check); return; }
if (args.Length == 4 && args[0] == "--idle-metadata-benchmark") { await IdleMetadataBenchmark.Run(args[1], int.Parse(args[2]), args[3], Check); return; }
if (args.Length == 2 && args[0] is "--stop-performance-host" or "--stop-endurance-host")
{
    string folder = Path.GetFullPath(args[1]);
    if (!folder.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(folder).StartsWith(args[0] == "--stop-endurance-host" ? "endurance-" : "performance-", StringComparison.Ordinal)) throw new ArgumentException("Only the requested isolated test hosts may be stopped.");
    (await Toiqtty.Hosting.SessionHostClient.TryConnectAsync(folder))?.ShutdownForTest(); return;
}
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description); passed++;
}
if (args.Contains("--process-waits")) { ProcessWaitChecks.Run(Check); Console.WriteLine($"{passed} process wait checks passed."); return; }
if (args.Length == 2 && args[0] == "--output-watch") { await OutputWatchChecks.Run(args[1], Check); Console.WriteLine($"{passed} output watch checks passed."); return; }
if (args.Contains("--flood-handshake")) { await FloodHandshakeChecks.Run(Check); Console.WriteLine($"{passed} flood handshake checks passed."); return; }
if (args.Length == 1 && args[0] == "--cell-buffers") { CellBufferChecks.Run(Check); Console.WriteLine($"{passed} cell-buffer checks passed."); return; }
if (args.Length == 1 && args[0] == "--selection-coordinates") { SelectionCoordinateChecks.Run(Check); Console.WriteLine($"{passed} selection coordinate checks passed."); return; }
if (args.Length == 1 && args[0] == "--architecture") { await ArchitectureChecks.Run(Check); Console.WriteLine($"{passed} architecture checks passed."); return; }
if (args.Length == 2 && args[0] == "--architecture-host") { await ArchitectureChecks.RunHost(args[1], Check); Console.WriteLine($"{passed} architecture host checks passed."); return; }
if (args.Length == 2 && args[0] == "--read-connections") { await ReadConnectionChecks.Run(args[1], Check); Console.WriteLine($"{passed} read connection checks passed."); return; }
if (args.Length == 2 && args[0] == "--status-revisions") { await StatusRevisionChecks.Run(args[1], Check); Console.WriteLine($"{passed} status revision checks passed."); return; }
if (args.Length == 2 && args[0] == "--row-handoff") { await FrameDeltaChecks.RunHost(args[1], Check); Console.WriteLine($"{passed} row handoff checks passed."); return; }
if (args.Length == 2 && args[0] == "--cold-start-ui") { await ColdStartupChecks.RunWindows(args[1], Check); Console.WriteLine($"{passed} cold-start WinUI checks passed."); return; }
if (args.Length == 1 && args[0] == "--snapshot-limits") { SnapshotLimitChecks.Run(Check); Console.WriteLine($"{passed} snapshot limit checks passed."); return; }
if (args.Length == 1 && args[0] == "--status-published") { await StatusSnapshotChecks.RunSession(Check); return; }
if (args.Length == 1 && args[0] == "--console-lifetime") { await ConsoleLifetimeChecks.Run(Check); return; }
if (args.Length == 2 && args[0] == "--metadata-batch") { await MetadataBatchChecks.Run(args[1], Check); Console.WriteLine($"{passed} metadata batch checks passed."); return; }
if (args.Length == 2 && args[0] == "--metadata-payload") { await MetadataPayloadChecks.Run(args[1], Check); return; }
if (args.Length == 3 && args[0] == "--snapshot-fuzz") { await SnapshotFuzzChecks.Run(int.Parse(args[1]), int.Parse(args[2]), Check); return; }
if (args.Length == 2 && args[0] == "--host-scheduling") { await HostSchedulingChecks.Run(args[1], Check); await HostSchedulingChecks.Creates(args[1], Check); Console.WriteLine($"{passed} host scheduling checks passed."); return; }
if (args.Length == 2 && args[0] == "--host-accept-storm") { await HostSchedulingChecks.AcceptStorm(args[1], Check); return; }
if (args.Contains("--interop-only")) { await InteropChecks.Run(Check); Console.WriteLine($"{passed} interoperability checks passed."); return; }
if (args.Contains("--navigation-layout")) { NavigationChecks.Run(Check); Console.WriteLine($"{passed} navigation layout checks passed."); return; }
if (args.Contains("--scrolling")) { ScrollingChecks.Run(Check); Console.WriteLine($"{passed} scrolling checks passed."); return; }
if (args.Contains("--input-dispatch")) { await InputDispatchChecks.Run(Check); Console.WriteLine($"{passed} input dispatch checks passed."); return; }
if (args.Contains("--latency-client"))
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.Write("\x1b[?25lLATENCY_READY\r\n");
    while (Console.ReadLine() is { } line)
        if (line.StartsWith("echo ", StringComparison.Ordinal)) Console.Write("\x1b[2J\x1b[HACK_" + line[5..] + "\r\n");
    return;
}
if (args.Contains("--delta-client"))
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.Write("\x1b[?25l\x1b[HDELTA_READY\x1b[8;1H\x1b_Ga=T,f=32,s=1,v=1,i=902,q=2,C=1;/wAA/w==\x1b\\\x1b[1;1H");
    while (Console.ReadLine() is { } line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[0] == "set" && int.TryParse(parts[1], out int row))
            Console.Write($"\x1b[{Math.Clamp(row, 3, Math.Max(3, Console.WindowHeight))};1H\x1b[2KDELTA_{parts[2]}\x1b[1;1H");
    }
    return;
}
if (args.Length == 2 && args[0] == "--scrolling-ui") { await ScrollingChecks.RunWindows(args[1], Check); Console.WriteLine($"{passed} scrolling WinUI checks passed."); return; }
if (args.Length == 2 && args[0] == "--resize-ui") { await ResizeChecks.RunWindows(args[1], Check); Console.WriteLine($"{passed} resize WinUI checks passed."); return; }
if (args.Length == 2 && args[0] == "--pane-reuse-ui") { await ResizeChecks.RunWindows(args[1], Check, "pane-reuse"); Console.WriteLine($"{passed} retained pane WinUI checks passed."); return; }
if (args.Length == 2 && args[0] == "--scale-ui") { await ResizeChecks.RunWindows(args[1], Check, "scale"); Console.WriteLine($"{passed} scale WinUI checks passed."); return; }
if (args.Length == 2 && args[0] == "--frame-delta-ui") { await ResizeChecks.RunWindows(args[1], Check, "frame-delta"); Console.WriteLine($"{passed} row-delta WinUI checks passed."); return; }
if (args.Length == 2 && args[0] == "--background-ui") { await ResizeChecks.RunWindows(args[1], Check, "background"); Console.WriteLine($"{passed} background WinUI checks passed."); return; }
if (args.Length == 2 && args[0] == "--input-ui") { await ResizeChecks.RunWindows(args[1], Check, "input"); Console.WriteLine($"{passed} input WinUI checks passed."); return; }
if (args.Length == 2 && args[0] == "--input-close-ui") { await ResizeChecks.RunWindows(args[1], Check, "input-close"); Console.WriteLine($"{passed} input close WinUI checks passed."); return; }
if (args.Length == 2 && args[0] == "--input-native-close-ui") { await ResizeChecks.RunWindows(args[1], Check, "input-close-native"); Console.WriteLine($"{passed} native input close WinUI checks passed."); return; }
if (args.Contains("--snapshot-optimizations")) { SnapshotOptimizationChecks.Run(Check); Console.WriteLine($"{passed} snapshot optimization checks passed."); return; }
if (args.Contains("--frame-deltas")) { FrameDeltaChecks.Run(Check); Console.WriteLine($"{passed} frame delta checks passed."); return; }
if (args.Length == 2 && args[0] == "--navigation-ui") { await NavigationChecks.RunWindows(args[1], Check); Console.WriteLine($"{passed} navigation WinUI checks passed."); return; }
if (args.Length is 3 or 4 && args[0] == "--startup-benchmark") { await StartupChecks.Run(args[1], int.Parse(args[2]), args.Length == 4 && args[3] == "profile", args.Length == 4 && args[3] == "immediate"); return; }
if (args.Contains("--browser-only")) { await BrowserChecks.Run(Check); Console.WriteLine($"{passed} browser checks passed."); return; }
if (args.Length == 2 && args[0] == "--lua-config") { await LuaConfigChecks.Run(args[1], Check); Console.WriteLine($"{passed} Lua configuration checks passed."); return; }
if (args.Length == 2 && args[0] == "--lua-ui") { await LuaConfigChecks.RunWindows(args[1], Check); Console.WriteLine($"{passed} Lua WinUI checks passed."); return; }
if (args.Contains("--session-themes")) { await SessionThemeChecks.Run(Check); Console.WriteLine($"{passed} session theme checks passed."); return; }
if (args.Length == 2 && args[0] == "--interactions") { InteractionChecks.Run(Check); await InteractionChecks.RunConfig(args[1], Check); Console.WriteLine($"{passed} interaction checks passed."); return; }
if (args.Contains("--appearance")) { AppearanceChecks.Run(Check); Console.WriteLine($"{passed} appearance checks passed."); return; }
if (args.Length == 2 && args[0] == "--lua-runtime") { await AppearanceChecks.RunRuntime(args[1], Check); Console.WriteLine($"{passed} Lua runtime checks passed."); return; }
if (args.Contains("--shell-only")) { await ShellIntegrationChecks.Run(Check); Console.WriteLine($"{passed} shell integration checks passed."); return; }
if (args.Length == 2 && args[0] == "--hardware-input") { HardwareInputClient.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--notification-activation") { await NotificationActivationChecks.Run(args[1], Check); return; }
if (args.Length == 2 && args[0] == "--session-host") { await PersistentSessionChecks.Run(args[1], Check); return; }
if (args.Length == 2 && args[0] == "--session-upgrade-window") { await PersistentSessionChecks.RunUpgradeWindow(args[1], Check); return; }
if (args.Length == 2 && args[0] == "--session-ui") { await PersistentSessionChecks.RunWindows(args[1], Check); return; }
if (args.Length == 2 && args[0] == "--session-ui-crash") { await PersistentSessionChecks.RunWindows(args[1], Check, crash: true); return; }
if (args.Contains("--persistent-shell-client")) { await PersistentSessionChecks.RunShellClient(); return; }
if (args.Contains("--dpi-client") || args.Contains("--resize-client"))
{
    Console.OutputEncoding = new UTF8Encoding(false);
    void Redraw() => Console.Write("\x1b[2J\x1b[HDPI_CHECK Unicode café 中🙂\r\n"
        + "\x1b[41;37m  terminal  \x1b[44;31m\ue0b0\x1b[37m  DPI  \x1b[0;34m\ue0b4\x1b[0m\r\n"
        + "Bold \x1b[1mtext\x1b[0m and \x1b[4munderline\x1b[0m\r\n"
        + "\x1b[5;2H\x1b_Ga=T,f=24,s=1,v=1,i=901,q=2,c=4,r=2,C=1;/wAA\x1b\\"
        + "\x1b[9;1HCARET>  \x1b[?12l\x1b[2 q");
    Redraw();
    while (true)
        if (Console.ReadKey(intercept: true).KeyChar == 'r' && args.Contains("--resize-client")) Redraw();
}
if (args.Length == 2 && args[0] == "--drop-client")
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.Write("\x1b]72;t=a;text/plain\x1b\\\x1b]72;t=m:o=2;text/plain\x1b\\");
    if (args[1] == "timeout") { await Task.Delay(10000); return; }
    var input = new StringBuilder();
    while (input.Length < 65536)
    {
        input.Append(Console.ReadKey(intercept: true).KeyChar);
        string text = input.ToString();
        int drop = text.IndexOf("t=M:", StringComparison.Ordinal);
        if (drop >= 0 && text.IndexOf("\x1b\\", drop, StringComparison.Ordinal) >= 0)
        {
            Console.Write("\x1b]72;t=r:o=2\x1b\\"); return;
        }
    }
    throw new IOException("The test drop client did not receive a drop.");
}
if (args.Length == 3 && args[0] == "--uia-window") { await AccessibilityChecks.Run(int.Parse(args[1]), args[2]); return; }
if (args.Length is 5 or 6 && args[0] is "--uia-observe" or "--uia-post-soak") { await UiaObserveChecks.Run(int.Parse(args[1]), args[2], int.Parse(args[3]), args[4], args.Length == 6 ? int.Parse(args[5]) : -1, args[0] == "--uia-post-soak"); return; }
if (args.Length == 5 && args[0] == "--dpi-uia") { await DpiAccessibilityChecks.Run(int.Parse(args[1]), args[2], int.Parse(args[3]), int.Parse(args[4])); return; }
if (args.Length >= 1 && args[0] == "--soak") { await SoakChecks.Run(args.Length > 1 ? int.Parse(args[1]) : 600, Check, args.Length > 2 ? args[2] : "artifacts/soak.json"); return; }
if (args.Length > 0)
{
    Console.Error.WriteLine("Unknown test arguments: " + string.Join(' ', args));
    Environment.ExitCode = 1;
    return;
}
void Write(GhosttyTerminal terminal, string text) { var bytes = Encoding.UTF8.GetBytes(text); terminal.Write(bytes, bytes.Length); }
string Row(TerminalCell[] cells, int columns, int row = 0) => string.Concat(cells.Skip(row * columns).Take(columns).Select(c => c.GetText().Length == 0 ? " " : c.GetText())).TrimEnd();
using (var terminal = new GhosttyTerminal(20, 5))
{
    var cells = new TerminalCell[100];
    Write(terminal, "Hello \x1b[31mred\x1b[0m");
    var frame = terminal.ReadFrame(cells);
    Check(Row(cells, 20) == "Hello red", "VT stream renders text without escape bytes");
    Check(cells[6].Foreground != cells[0].Foreground, "SGR foreground colors survive the native bridge");
    Write(terminal, "\r\n\x1b[1;4mbold\x1b[0m"); terminal.ReadFrame(cells);
    Check((cells[20].Flags & 5) == 5, "bold and underline attributes");
    Write(terminal, "\x1b[2J\x1b[H");
    var unicode = Encoding.UTF8.GetBytes("é中🙂");
    for (int i = 0; i < unicode.Length; i++) terminal.Write([unicode[i]], 1);
    terminal.ReadFrame(cells);
    Check(cells[0].GetText() == "é" && cells[1].GetText() == "中" && cells[3].GetText() == "🙂", "fragmented UTF-8 and wide character placement");
    Write(terminal, "\x1b[?1049hALT"); terminal.ReadFrame(cells);
    Check(string.Concat(cells.Select(c => c.GetText())).Contains("ALT"), "alternate screen entry");
    Write(terminal, "\x1b[?1049l"); terminal.ReadFrame(cells);
    Check(cells[0].GetText() == "é", "alternate screen restores primary buffer");
    Write(terminal, "\x1b]2;Build shell\x07");
    Check(terminal.Metadata(0) == "Build shell", "OSC title metadata");
    int bells = 0; terminal.Attention += () => bells++;
    Write(terminal, "\a"); Check(bells == 1, "BEL notification callback");
    string response = ""; terminal.Response += bytes => response += Encoding.UTF8.GetString(bytes);
    Write(terminal, "\x1b[6n"); Check(response.StartsWith("\x1b["), "device status queries receive a PTY response");
    Check(Encoding.UTF8.GetString(terminal.EncodeKey(38, 0)) == "\x1b[A", "normal cursor key encoding");
    Write(terminal, "\x1b[?1h");
    Check(Encoding.UTF8.GetString(terminal.EncodeKey(38, 0)) == "\x1bOA", "application cursor key encoding follows terminal mode");
    Check(terminal.EncodeKey(67, 2, "c").SequenceEqual(new byte[] { 3 }), "Ctrl+C is a terminal interrupt");
    Check(Encoding.UTF8.GetString(terminal.EncodeKey(0, 0, "λ")) == "λ", "Unicode text input encoding");
    Write(terminal, "\x1b[?2004h");
    Check(Encoding.UTF8.GetString(terminal.EncodePaste("a\nb")) == "\x1b[200~a\nb\x1b[201~", "bracketed paste framing");
    Write(terminal, "\x1b[?2004l");
    Check(Encoding.UTF8.GetString(terminal.EncodePaste("a\nb")) == "a\rb", "non-bracketed paste newline conversion");
    terminal.Resize(20, 5, 8, 20);
    Write(terminal, "\x1b[?1000h\x1b[?1006h");
    Check(Encoding.UTF8.GetString(terminal.EncodeMouse(0, 1, 0, 17, 21)!) == "\x1b[<0;3;2M", "SGR mouse coordinates and button encoding");
    Write(terminal, "\x1b[?1000l");
    Check(terminal.EncodeMouse(0, 1, 0, 17, 21) is null, "mouse events stay local when application tracking is disabled");
    Write(terminal, "\x1b[?1004h"); response = ""; terminal.Focus(true); terminal.Focus(false);
    Check(response == "\x1b[I\x1b[O", "focus reports respect terminal mode");
    terminal.Resize(40, 10, 8, 20); cells = new TerminalCell[400]; frame = terminal.ReadFrame(cells);
    Check(frame.Columns == 40 && frame.Rows == 10, "terminal resize and frame capacity");
}
using (var terminal = new GhosttyTerminal(20, 3))
{
    Write(terminal, "one\r\ntwo\r\nthree\r\nfour\r\nfive");
    var cells = new TerminalCell[60]; terminal.ReadFrame(cells);
    Check(Row(cells, 20) == "three", "scrollback retains lines beyond viewport");
    terminal.Scroll(-2); terminal.ReadFrame(cells);
    Check(Row(cells, 20) == "one", "scrollback viewport scrolling");
    terminal.Scroll(0); terminal.ReadFrame(cells);
    Check(Row(cells, 20) == "three", "scrollback returns to live output");
    var result = terminal.Find("one", 0);
    Check(result.Count == 1 && terminal.CopySelection() == "one", "history search finds and selects offscreen text");
    terminal.ReadFrame(cells);
    Check(Row(cells, 20) == "one" && (cells[0].Flags & 64) != 0, "find scrolls to and highlights the selected match");
    terminal.Find("", 0); terminal.Select(-1, -1);
    Check(terminal.CopySelection() == "", "clearing search releases the selection");
}
using (var terminal = new GhosttyTerminal(5, 3))
{
    Write(terminal, "ab中efgh");
    terminal.Select(0, 8);
    Check(terminal.CopySelection() == "ab中efgh", "native copy unwraps soft lines and omits wide-character spacers");
}
ScrollbackChecks.Run(Check);
ScrollingChecks.Run(Check);
await InputDispatchChecks.Run(Check);
NavigationChecks.Run(Check);
SnapshotOptimizationChecks.Run(Check);
Utf8CaptureChecks.Run(Check);
StyleRunChecks.Run(Check);
SnapshotLimitChecks.Run(Check);
CellBufferChecks.Run(Check);
SelectionCoordinateChecks.Run(Check);
await ArchitectureChecks.Run(Check);
ColdStartupChecks.Run(Check);
await SessionThemeChecks.Run(Check);
AppearanceChecks.Run(Check);
InteractionChecks.Run(Check);
StatusSnapshotChecks.Run(Check);
await StatusSnapshotChecks.RunSession(Check);
await ConsoleLifetimeChecks.Run(Check);
await MetadataBatchChecks.Protocol(Check);
FrameDeltaChecks.Run(Check);
ProtocolChecks.Run(Check);
ImageMediumChecks.Run(Check);
HostProtocolChecks.Run(Check);
HostV2Wire.Run(Check);
await HostV2Wire.RunClient(Check);
KeyboardLayoutChecks.Run(Check);
await ProtocolChecks.Transport(Check);
await ImageMediumChecks.Transport(Check);
await HostProtocolChecks.DropTransport(Check);
var tree = new PaneNode(); var original = tree.Id;
Check(new AppSettings().FontFamily == TerminalFont.BundledFamily, "bundled JetBrains Mono is the default terminal font");
Check(new AppSettings().Shell == ShellDefaults.Command && new PaneNode().Command == ShellDefaults.Command,
    "settings and new panes prefer PowerShell 7 when installed");
var originalDataFolder = Environment.GetEnvironmentVariable("TOIQTTY_DATA_DIR");
var settingsTestFolder = Path.Combine(Path.GetTempPath(), "toiqtty-settings-" + Guid.NewGuid().ToString("N"));
try
{
    Environment.SetEnvironmentVariable("TOIQTTY_DATA_DIR", settingsTestFolder);
    Directory.CreateDirectory(settingsTestFolder);
    File.WriteAllText(SessionStore.FilePath, "{\"Version\":1,\"Settings\":{\"FontFamily\":\"Cascadia Mono\"}}");
    var migrated = SessionStore.Load();
    Check(migrated.Settings.ScrollbackLimitBytes == 50_000_000 && migrated.Settings.ScrollbackLimitLines is null,
        "saved layouts without scrollback settings inherit Ghostty-compatible defaults");
    Check(migrated.Version == 5 && migrated.Settings.FontFamily == TerminalFont.BundledFamily, "legacy default font migrates to bundled JetBrains Mono");
    File.WriteAllText(SessionStore.FilePath, "{\"Version\":1,\"Settings\":{\"FontFamily\":\"Consolas\"}}");
    Check(SessionStore.Load().Settings.FontFamily == "Consolas", "font migration preserves custom choices");
    File.WriteAllText(SessionStore.FilePath, "{\"Version\":2,\"Settings\":{\"FontFamily\":\"Cascadia Mono\"}}");
    Check(SessionStore.Load().Settings.FontFamily == "Cascadia Mono", "font migration runs only once");
    File.WriteAllText(SessionStore.FilePath, "{\"Version\":2,\"Settings\":{\"Shell\":\"powershell.exe -NoLogo\"}}");
    Check(SessionStore.Load().Settings.Shell == ShellDefaults.Command, "old default shell migrates to PowerShell 7");
    File.WriteAllText(SessionStore.FilePath, "{\"Version\":2,\"Settings\":{\"Shell\":\"cmd.exe\"}}");
    Check(SessionStore.Load().Settings.Shell == "cmd.exe", "shell migration preserves custom commands");
    File.WriteAllText(SessionStore.FilePath, "{\"Version\":4,\"Settings\":{\"Shell\":\"powershell.exe -NoLogo\"}}");
    Check(SessionStore.Load().Settings.Shell == ShellDefaults.LegacyCommand, "shell migration runs only once");
    var savedLayout = new SavedSession { Version = 3, Settings = new AppSettings { Shell = ShellDefaults.LegacyCommand } };
    savedLayout.Workspaces.Add(new Workspace { Root = new PaneNode { Command = ShellDefaults.LegacyCommand } });
    savedLayout.Workspaces[0].Root.Split(savedLayout.Workspaces[0].Root.Id, true, "cmd.exe /Q /K");
    File.WriteAllText(SessionStore.FilePath, System.Text.Json.JsonSerializer.Serialize(savedLayout));
    var restoredShells = SessionStore.Load();
    Check(restoredShells.Workspaces[0].Root.Leaves().First().Command == ShellDefaults.Command,
        "restored panes using the old default launch PowerShell 7");
    Check(restoredShells.Workspaces[0].Root.Leaves().Last().Command == "cmd.exe /Q /K",
        "restored custom pane commands are preserved");
    var legacyRoot = new PaneNode { Command = "cmd.exe", Directory = Environment.CurrentDirectory };
    legacyRoot.Split(legacyRoot.Id, true, "pwsh.exe");
    string legacyFocus = legacyRoot.Leaves().Last().Id;
    File.WriteAllText(SessionStore.FilePath, System.Text.Json.JsonSerializer.Serialize(new
    {
        Version = 4,
        Workspaces = new[] { new { Id = "legacy-session", Name = "Project", Root = legacyRoot, FocusedPaneId = legacyFocus } }
    }));
    var sessionWithTabs = SessionStore.Load(); var migratedSession = sessionWithTabs.Workspaces.Single();
    Check(sessionWithTabs.Version == 5 && migratedSession.Tabs.Count == 1 && migratedSession.Root.Leaves().Select(p => p.Id).SequenceEqual(legacyRoot.Leaves().Select(p => p.Id))
        && migratedSession.FocusedPaneId == legacyFocus, "old workspace layouts migrate to a session with one tab without losing splits or focused pane");
    migratedSession.Tabs.Add(new TerminalTab { Name = "Logs", Root = new PaneNode { Command = "cmd.exe", Directory = Environment.CurrentDirectory } });
    migratedSession.ActiveTabId = migratedSession.Tabs[1].Id; SessionStore.Save(sessionWithTabs);
    var tabRoundTrip = SessionStore.Load().Workspaces.Single();
    Check(tabRoundTrip.Tabs.Count == 2 && tabRoundTrip.ActiveTab.Name == "Logs" && tabRoundTrip.Tabs[0].Root.Leaves().Count() == 2,
        "saved sessions preserve independent tab layouts and the active tab");
}
finally
{
    Environment.SetEnvironmentVariable("TOIQTTY_DATA_DIR", originalDataFolder);
    foreach (var file in Directory.EnumerateFiles(settingsTestFolder)) File.Delete(file);
    Directory.Delete(settingsTestFolder);
}
Check(tree.Split(original, true, "cmd.exe") && tree.Leaves().Count() == 2, "workspace split creates two independent leaves");
var second = tree.Leaves().Last().Id;
tree.Split(second, false, "cmd.exe");
Check(tree.Leaves().Count() == 3 && tree.Second is { Vertical: false }, "nested split orientations");
var restored = System.Text.Json.JsonSerializer.Deserialize<PaneNode>(System.Text.Json.JsonSerializer.Serialize(tree))!;
Check(restored.Leaves().Select(p => p.Id).SequenceEqual(tree.Leaves().Select(p => p.Id)), "layout JSON round trip preserves pane identity");
tree = PaneNode.Remove(tree, original)!;
Check(tree.Leaves().Count() == 2 && !tree.Vertical, "closing a pane collapses its split");

using (var session = new TerminalSession("cmd.exe /Q /K", Environment.CurrentDirectory))
{
    var cells = new TerminalCell[3000];
    await Task.Delay(400);
    session.Paste("echo TOIQTTY_SMOKE_7291"); session.Key(13, 0);
    var deadline = DateTime.UtcNow.AddSeconds(10); bool found = false;
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(100); session.ReadFrame(cells);
        if (Enumerable.Range(0, 30).Any(y => Row(cells, 100, y) == "TOIQTTY_SMOKE_7291")) { found = true; break; }
    }
    if (!found) Console.WriteLine("Captured screen: " + string.Join(" | ", Enumerable.Range(0, 30).Select(y => Row(cells, 100, y))));
    Check(found, "real ConPTY shell accepts input and returns command output through Ghostty");
    session.Resize(80, 25, 8, 20);
    var frame = session.ReadFrame(cells);
    Check(frame.Columns == 80 && frame.Rows == 25, "running ConPTY and Ghostty resize together");
    session.Paste("exit 7"); session.Key(13, 0);
    deadline = DateTime.UtcNow.AddSeconds(10);
    while (!session.HasExited && DateTime.UtcNow < deadline) await Task.Delay(100);
    Check(session.HasExited && session.ExitCode == 7, "shell exit is observed without hanging the reader");
}
for (int i = 0; i < 3; i++)
{
    var closing = new TerminalSession("cmd.exe /Q /K", Environment.CurrentDirectory);
    if (i == 1) await Task.Delay(250);
    closing.Dispose();
    var closeDeadline = DateTime.UtcNow.AddSeconds(10);
    while (!closing.HasExited && DateTime.UtcNow < closeDeadline) await Task.Delay(100);
    Check(closing.HasExited, $"pseudoconsole teardown handles startup timing {i + 1}");
}
Check(ShellIntegration.PrepareCommand("powershell.exe -NoLogo").Contains("-NoExit -Command"), "interactive PowerShell receives per-process directory integration");
Check(ShellIntegration.PrepareCommand("powershell.exe -Command echo hi") == "powershell.exe -Command echo hi", "explicit PowerShell commands are preserved");
var shellEnvironment = ShellIntegration.CreateEnvironmentBlock(new Dictionary<string, string>
{ ["TERM"] = "dumb", ["TERM_PROGRAM"] = "WezTerm", ["PATH"] = "kept", ["NO_COLOR"] = "1" });
Check(shellEnvironment.Contains("TERM=xterm-256color\0") && shellEnvironment.Contains("COLORTERM=truecolor\0")
    && shellEnvironment.Contains("TERM_PROGRAM=toiqtty\0"), "shell startup advertises toiqtty's VT and truecolor capabilities");
Check(shellEnvironment.Contains("PATH=kept\0") && shellEnvironment.Contains("NO_COLOR=1\0") && shellEnvironment.EndsWith("\0\0"),
    "Unicode child environment preserves user variables and color preferences");
using (var session = new TerminalSession("powershell.exe -NoLogo -NoProfile", Environment.CurrentDirectory))
{
    var cells = new TerminalCell[3000];
    // Wait for the integrated prompt; slow runners can still be starting PowerShell after its first output.
    var deadline = DateTime.UtcNow.AddSeconds(30);
    bool prompted = false;
    while (!prompted && DateTime.UtcNow < deadline)
    {
        await Task.Delay(100); session.ReadFrame(cells);
        prompted = Enumerable.Range(0, 30).Any(y => Row(cells, 100, y).StartsWith("PS ", StringComparison.Ordinal));
    }
    session.Paste("Set-Location $env:TEMP"); session.Key(13, 0);
    while (!string.Equals(Path.TrimEndingDirectorySeparator(session.Directory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && DateTime.UtcNow < deadline) await Task.Delay(100);
    if (!string.Equals(Path.TrimEndingDirectorySeparator(session.Directory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
    {
        session.ReadFrame(cells);
        Console.WriteLine($"Prompt seen: {prompted}; directory '{session.Directory}'; temp '{Path.GetTempPath()}'; screen: " + string.Join(" | ", Enumerable.Range(0, 30).Select(y => Row(cells, 100, y)).Where(row => row.Length > 0)));
    }
    Check(string.Equals(Path.TrimEndingDirectorySeparator(session.Directory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase),
        "PowerShell prompt reports live directory changes through OSC 7");
    Check(string.Equals(Path.TrimEndingDirectorySeparator(session.LocalDirectory ?? ""), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase),
        "the integration's authenticated OSC 7 survives Ghostty and sets the local directory");
    session.Paste("[Console]::Write([char]27 + ']7;file:///C:/Windows' + [char]7); Start-Sleep -Seconds 5"); session.Key(13, 0);
    deadline = DateTime.UtcNow.AddSeconds(15);
    while (!session.Directory.Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline) await Task.Delay(100);
    // Like a remote location, it clears local tracking: new panes use the configured start folder.
    Check(session.Directory.Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase) && session.LocalDirectory is null
        && !session.WorkingDirectory.Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase),
        "an OSC 7 written by a program in the shell is shown but never becomes the local directory");
}
await ShellIntegrationChecks.Run(Check);
await HardeningChecks.Run(Check);
await InteropChecks.Run(Check);
Console.WriteLine($"\n{passed} checks passed.");
