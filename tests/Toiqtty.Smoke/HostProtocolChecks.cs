using Toiqtty.Terminal;
using Toiqtty.Models;
using System.Text;

internal static class HostProtocolChecks
{
    internal static async Task DropTransport(Action<bool, string> check)
    {
        static async Task WaitReady(TerminalSession session)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (session.Drop(0) == 1 && session.Drop(4, operations: 3) == 2) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("The test drop client did not register.");
        }
        var items = new Dictionary<string, byte[]> { ["text/plain"] = Encoding.UTF8.GetBytes("test drop") };
        await using (var session = new TerminalSession($"\"{Environment.ProcessPath}\" --drop-client move", Environment.CurrentDirectory))
        {
            await WaitReady(session);
            session.Drop(1, items: items, operations: 3);
            check(await session.DropAsync(0, 0, items, operations: 3) == 2, "real ConPTY client concludes Move through the session's asynchronous native drop completion");
        }
        await using (var session = new TerminalSession($"\"{Environment.ProcessPath}\" --drop-client timeout", Environment.CurrentDirectory))
        {
            await WaitReady(session);
            check(await session.DropAsync(0, 0, items, operations: 3, timeout: TimeSpan.FromMilliseconds(100)) == 0 && session.Drop(0) == 1,
                "unresponsive ConPTY drop client times out without reporting Move or losing registration");
        }
    }
    public static void Run(Action<bool, string> check)
    {
        using var terminal = new GhosttyTerminal(24, 6);
        void Write(string value) { var data = Encoding.UTF8.GetBytes(value); terminal.Write(data, data.Length); }
        var clipboard = new MemoryClipboard(); terminal.Clipboard = clipboard;
        string responses = ""; terminal.Response += data => responses += Encoding.UTF8.GetString(data);
        Write("\x1b]52;c;aGVsbG8=\a");
        check(Encoding.UTF8.GetString(clipboard.Contents["text/plain"]) == "hello", "OSC 52 writes decoded text through the clipboard host callback");
        Write("\x1b]52;c;?\a");
        check(clipboard.Reads == 0 && !responses.Contains("aGVsbG8="), "clipboard reads are denied by default without accessing platform data");
        terminal.AllowClipboardRead = true; responses = "";
        Write("\x1b]52;c;?\a");
        check(responses.Contains("aGVsbG8="), "OSC 52 clipboard reads return a protocol reply when explicitly enabled");
        Write("\x1b]5522;type=write:id=c1\x1b\\\x1b]5522;type=wdata:mime=dGV4dC9wbGFpbg==;R2hvc3Q=\x1b\\"
            + "\x1b]5522;type=wdata:mime=dGV4dC9wbGFpbg==;dHk=\x1b\\\x1b]5522;type=wdata:mime=dGV4dC9odG1s;PGI+aGk8L2I+\x1b\\\x1b]5522;type=wdata\x1b\\");
        check(responses.Contains("status=DONE:id=c1") && clipboard.Contents.Count == 2 && Encoding.UTF8.GetString(clipboard.Contents["text/plain"]) == "Ghostty"
            && Encoding.UTF8.GetString(clipboard.Contents["text/html"]) == "<b>hi</b>", "Kitty clipboard chunks commit all MIME representations together and acknowledge completion");
        responses = ""; Write("\x1b]5522;type=read:id=r1;dGV4dC9odG1s\x1b\\");
        check(responses.Contains("status=DATA:id=r1:mime=dGV4dC9odG1s;PGI+aGk8L2I+") && responses.Contains("status=DONE:id=r1"), "Kitty clipboard reads preserve requested MIME payloads and completion status");
        terminal.AllowClipboardWrite = false; responses = "";
        Write("\x1b]5522;type=write:id=denied\x1b\\\x1b]5522;type=wdata\x1b\\");
        check(responses.Contains("status=EPERM:id=denied") && clipboard.Contents.Count == 2, "disabled clipboard writes return EPERM and preserve the current clipboard");
        terminal.AllowClipboardRead = false; responses = "";
        Write("\x1b[?5522h"); terminal.Paste("ignored clipboard text");
        check(responses.Contains("\x1b]5522;type=read:status=OK:pw=") && !responses.Contains("ignored clipboard text"), "user clipboard paste emits Kitty paste events when mode 5522 is enabled");
        int password = responses.IndexOf("pw=", StringComparison.Ordinal);
        if (password >= 0)
        {
            var tail = responses[(password + 3)..]; int separator = tail.IndexOfAny([':', ';', '\x1b']);
            string pw = separator < 0 ? tail : tail[..separator]; responses = "";
            Write("\x1b]5522;type=read:id=granted:name=UGFzdGUgZXZlbnQ=:pw=" + pw + ";dGV4dC9wbGFpbg==\x1b\\");
            check(responses.Contains("status=DATA:id=granted") && responses.Contains("R2hvc3R0eQ=="), "Kitty user-paste password grants a one-time read without enabling unsolicited clipboard access");
        }
        else check(false, "Kitty paste events include a one-time password");
        responses = ""; terminal.Paste("literal", clipboard: false);
        check(responses == "literal", "text insertion remains text while Kitty clipboard paste events are enabled");
        responses = ""; int beforePaste = clipboard.Reads;
        terminal.PasteClipboard();
        check(clipboard.Reads == beforePaste + 1 && responses.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("text/plain text/html\n"))),
            "user paste advertises every clipboard MIME without reading payloads for a Kitty paste event");
        clipboard.Contents = new() { ["image/png"] = [0, 255, 0, 128] };
        responses = ""; terminal.PasteClipboard();
        check(responses.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("image/png\n"))) && responses.Contains("pw="), "an image-only clipboard produces a Kitty paste event without requiring plain text");
        var imagePasswordTail = responses[(responses.IndexOf("pw=", StringComparison.Ordinal) + 3)..];
        var imagePassword = imagePasswordTail[..imagePasswordTail.IndexOfAny([':', ';', '\x1b'])];
        responses = "";
        Write("\x1b]5522;type=read:id=image:name=UGFzdGUgZXZlbnQ=:pw=" + imagePassword + ";aW1hZ2UvcG5n\x1b\\");
        check(responses.Contains("status=DATA:id=image:mime=aW1hZ2UvcG5n;AP8AgA=="), "a user image-paste grant returns the requested binary MIME payload while unsolicited reads stay disabled");
        Write("\x1b[?5522l"); clipboard.Contents = new() { ["text/plain"] = Encoding.UTF8.GetBytes("legacy clipboard") };
        responses = ""; terminal.PasteClipboard();
        check(responses == "legacy clipboard", "legacy clipboard paste reads its text representation through the synchronous native MIME reader");
        responses = ""; clipboard.FailData = true; bool failedRead = false;
        try { terminal.PasteClipboard(); } catch (IOException) { failedRead = true; }
        check(failedRead && responses.Length == 0, "a failed clipboard MIME read emits no partial paste bytes");
        clipboard.FailData = false;

        var status = new TerminalStatus(); var events = new List<TerminalEvent>();
        terminal.HostEvent += item => { events.Add(item); status.Apply(item); };
        Write("\x1b]777;notify;Build complete;All tests passed\a\x1b]9;Short notice\a");
        check(status.Snapshot().Notices.Count == 2 && status.Snapshot().Notices[0].Title == "Build complete" && status.Snapshot().Notices[0].Body == "All tests passed",
            "OSC 9 and 777 notification titles and bodies survive the native callback lifetime");
        Write("\x1b]9;4;1;42\a");
        check(status.Snapshot().ProgressState == 1 && status.Snapshot().Progress == 42, "OSC 9;4 exposes determinate progress state and percentage");
        responses = ""; Write("\x1b]7501;?\x1b\\");
        check(responses.Contains("states=idle,working,done,blocked,error"), "program status capability query is answered only now that reports are consumed");
        Write("\x1b]7501;state=blocked:kind=permission:progress=40:id=a/b:app=terraform:title=UGxhbg==:msg=QXBwbHk/\a");
        var program = status.Snapshot().Programs.Single();
        check(program.State == 3 && program.Detail == 1 && program.Progress == 40 && program.Id == "a/b" && program.Title == "Plan" && program.Body == "Apply?",
            "program status reports preserve state, blocking kind, progress, hierarchy and decoded text");
        Write("\x1b]7501;state=clear:id=a\a");
        check(status.Snapshot().Programs.Count == 0, "clearing a program status parent removes its descendants");
        Write("\x1b]133;A\a\x1b]133;B\a\x1b]133;C;cmdline_url=ls%20-la\a");
        check(status.Snapshot().CommandRunning && status.Snapshot().Commands.Last().Text == "ls -la", "semantic shell events expose decoded command start metadata");
        Write("\x1b]133;D;-1;err=boom\a");
        check(!status.Snapshot().CommandRunning && status.Snapshot().LastExitCode == -1 && status.Snapshot().Commands.Last().Error == "boom",
            "semantic command completion preserves negative Windows exit codes and error metadata");
        Write("\x1b]133;C;cmdline_url=interrupted\a\x1b]133;A\a");
        check(!status.Snapshot().CommandRunning && status.Snapshot().Commands[^1].Finished is not null && status.Snapshot().Commands[^1].ExitCode is null,
            "a prompt without a completion marker ends interrupted tracking without inventing an exit code");
        for (int i = 0; i < 250; i++) status.Apply(new(TerminalEventKind.Notification, Body: new string('x', 10000)));
        for (int i = 0; i < 250; i++) status.Apply(new(TerminalEventKind.ProgramStatus, State: 1, Id: "task" + i));
        check(status.Snapshot().Notices.Count == 100 && status.Snapshot().Notices.All(n => n.Body.Length <= 4096) && status.Snapshot().Programs.Count <= 128,
            "notification content and program status histories have bounded memory");
        Write("\x1b" + "c");
        check(status.Snapshot().Notices.Count == 0 && status.Snapshot().Programs.Count == 0 && status.Snapshot().Commands.Count == 0 && !status.Snapshot().CommandRunning,
            "RIS clears host-side command, progress, status and notification state");

        var cells = new TerminalCell[24 * 6];
        terminal.EnableIncrementalRendering(); Write("hello world\r\nsecond row");
        check(terminal.ReadSnapshot(cells).ChangedRows.Count == 6, "first incremental snapshot captures every row");
        check(terminal.ReadSnapshot(cells).ChangedRows.Count == 0 && cells[0].GetText() == "h", "unchanged snapshots preserve the cell buffer and report zero dirty rows");
        Write("\x1b[2;1HX");
        var partial = terminal.ReadSnapshot(cells);
        check(partial.ChangedRows.SequenceEqual([1]) && cells[24].GetText() == "X" && cells[0].GetText() == "h", "a one-row edit only extracts that row and preserves every other row");
        var different = new TerminalCell[cells.Length];
        check(terminal.ReadSnapshot(different).ChangedRows.Count == 6 && different[0].GetText() == "h", "switching consumer buffers forces a complete snapshot");
        Write("\x1b" + "c\x1b[?2027h" + "a" + new string('\u0301', 64));
        terminal.ReadSnapshot(cells);
        check(cells[0].GetText() == "a" + new string('\u0301', 64), "graphemes larger than the old 128-byte bridge buffer are preserved in full");
        Write("\x1b" + "c中\x1b[1;2H");
        check(terminal.ReadSnapshot(cells).Frame.CursorWideTail == 1, "cursor snapshot marks the continuation half of a wide character");
        Write("\x1b" + "chello world\r\nsecond row");
        terminal.SelectionGesture(0, 8, 5); terminal.SelectionGesture(1, 8, 5);
        terminal.SelectionGesture(0, 8, 5); terminal.SelectionGesture(1, 8, 5);
        check(terminal.CopySelection() == "hello", "Ghostty double-click selects a whole word");
        terminal.SelectionGesture(0, 8, 5); terminal.SelectionGesture(1, 8, 5);
        check(terminal.CopySelection() == "hello world", "Ghostty triple-click selects a whole logical line");
        terminal.Select(-1, -1); terminal.SelectionGesture(0, 0, 0); terminal.SelectionGesture(2, 38, 22, rectangle: true);
        check(terminal.CopySelection().Contains("hello") && terminal.CopySelection().Contains("secon"), "Alt-drag uses Ghostty rectangular selection geometry");
        terminal.SelectionGesture(1, 38, 22); terminal.Select(-1, -1);
        Write("\x1b" + "c" + string.Concat(Enumerable.Range(0, 30).Select(i => $"line{i}\r\n")));
        terminal.SelectionGesture(0, 0, 5); terminal.SelectionGesture(2, 20, -20);
        for (int i = 0; i < 4; i++) terminal.SelectionGesture(3, 20, -20);
        check(terminal.CopySelection().Contains("line") && terminal.CopySelection().Split('\n').Length >= 4, "selection autoscroll retains a tracked anchor while extending into scrollback");

        Write("\x1b" + "c\x1b[>11u");
        check(Encoding.UTF8.GetString(terminal.EncodeKey(160, 1)).Contains("57441"), "Kitty keyboard supports modifier-only Shift key events");
        check(!terminal.EncodeKey(0x1000d, 0).SequenceEqual(terminal.EncodeKey(13, 0)), "Kitty keyboard distinguishes keypad Enter from main Enter");
        check(WindowsKeyboard.PhysicalKey(13, 0x1c, true) == 0x1000d && WindowsKeyboard.PhysicalKey(16, 0x36, false) == 161,
            "Windows extended/scancode mapping preserves keypad Enter and modifier sides");
        var settings = new AppSettings { Keybindings = new() { ["copy"] = "Alt+C", ["splitRight"] = "" } };
        check(Keybindings.Match(settings, 67, 4) == "copy" && Keybindings.Match(settings, 68, 3) is null, "shortcut overrides support remapping and explicit disabling");
        check(Keybindings.Match(new AppSettings(), 'O', 3) == "openDirectory", "Ctrl+Shift+O opens the current folder without conflicting with other default actions");
        var explorerBindings = new AppSettings { Keybindings = Keybindings.ParseOverrides("openDirectory = Ctrl+Alt+O") };
        check(Keybindings.Match(explorerBindings, 'O', 6) == "openDirectory" && Keybindings.Match(explorerBindings, 'O', 3) is null,
            "The Explorer shortcut can be remapped and releases its previous chord");
        explorerBindings.Keybindings = Keybindings.ParseOverrides("openDirectory =");
        check(Keybindings.Match(explorerBindings, 'O', 3) is null, "The Explorer shortcut can be explicitly disabled");
        bool duplicate = false;
        try { Keybindings.ParseOverrides("copy = Ctrl+Shift+V"); } catch (FormatException) { duplicate = true; }
        check(duplicate, "settings reject shortcut collisions with existing actions");
        check(ShellIntegration.QuoteDroppedPaths("pwsh.exe", ["C:\\it's here.txt"]) == "'C:\\it''s here.txt' ", "dropped PowerShell paths preserve spaces and apostrophes");
        check(ShellIntegration.QuoteDroppedPaths("pwsh.exe", ["C:\\x\u2019; calc #.txt"]) == "'C:\\x\u2019\u2019; calc #.txt' ", "dropped PowerShell paths keep typographic quotes literal");
        check(ShellIntegration.QuoteDroppedPaths("pwsh.exe -File C:\\bash\\wsl.ps1", ["C:\\it's"]) == "'C:\\it''s' ", "shell quoting follows the launched program, not substrings of its arguments");
        check(ShellIntegration.QuoteDroppedPaths("\"C:\\Program Files\\Git\\bin\\bash.exe\" -l", ["C:\\it's"]) == "'C:\\it'\\''s' ", "quoted POSIX shell paths use POSIX quoting");
        check(ShellIntegration.QuoteDroppedPaths("wsl.exe", ["D:\\some folder\\file.txt"]) == "'/mnt/d/some folder/file.txt' ", "WSL file drops translate drive paths before POSIX quoting");
        var html = WindowsClipboard.EncodeHtml("<b>café 中🙂</b>");
        check(Encoding.UTF8.GetString(WindowsClipboard.DecodeHtml(html)!) == "<b>café 中🙂</b>", "Windows HTML clipboard byte offsets preserve Unicode MIME fragments");

        responses = ""; Write("\x1b]72;t=a:i=7;text/plain text/uri-list\x1b\\");
        check(terminal.Drop(0) == 1, "Kitty drag/drop registration activates the upstream drop-target state");
        terminal.Drop(1, 16, 42, new Dictionary<string, byte[]> { ["text/plain"] = [] });
        check(responses.Contains("t=m:x=2:y=2:X=16:Y=42:o=1:i=7"), "native drag movement reports cell/pixel coordinates and multiplexer client ID");
        Write("\x1b]72;t=m:o=0;text/plain\x1b\\");
        check(terminal.Drop(4) == 0, "Kitty client rejection controls native drop feedback");
        Write("\x1b]72;t=m:o=1;text/plain\x1b\\");
        check(terminal.Drop(4) == 1, "Kitty client acceptance enables a copy operation");
        responses = ""; terminal.Drop(3, 16, 42, new Dictionary<string, byte[]> { ["text/plain"] = Encoding.UTF8.GetBytes("dropped café") });
        check(responses.Contains("t=M:"), "native drop advertises its retained MIME representations");
        Write("\x1b]72;t=r:x=1\x1b\\");
        check(responses.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("dropped café"))), "Kitty drop data requests are answered from upstream-owned binary-safe storage");
        Write("\x1b]72;t=r:o=1\x1b\\\x1b]72;t=A\x1b\\");
        check(terminal.Drop(0) == 0, "Kitty drop conclusion and unregister release protocol state");

        Write("\x1b]72;t=a;text/plain text/html\x1b\\");
        var dropped = new Dictionary<string, byte[]> { ["text/plain"] = Encoding.UTF8.GetBytes("move café"), ["text/html"] = Encoding.UTF8.GetBytes("<b>café</b>") };
        responses = ""; terminal.Drop(1, 8, 21, dropped, operations: 3);
        check(responses.Contains("o=3"), "Kitty drag movement advertises the source's Copy and Move operation mask");
        Write("\x1b]72;t=m:o=2;text/plain text/html\x1b\\");
        check(terminal.Drop(4, operations: 3) == 2 && terminal.Drop(4, operations: 1) == 0,
            "Move acceptance is intersected with the native source's allowed operations");
        var move = terminal.BeginDrop(8, 21, dropped, operations: 3);
        check(!move.IsCompleted, "native Move completion waits for the client's explicit conclusion");
        responses = ""; Write("\x1b]72;t=r:x=2\x1b\\");
        check(responses.Contains(Convert.ToBase64String(dropped["text/html"])), "Kitty drop retains HTML MIME data until the client reads it");
        Write("\x1b]72;t=r:o=2\x1b\\");
        check(move.IsCompletedSuccessfully && move.Result == 2, "upstream Move conclusion completes the native drop with Move");
        terminal.Drop(1, items: dropped); Write("\x1b]72;t=m:o=1;text/plain\x1b\\");
        var cancel = terminal.BeginDrop(0, 0, dropped);
        terminal.CancelDrop(cancel);
        check(cancel.IsCompletedSuccessfully && cancel.Result == 0 && terminal.Drop(0) == 1,
            "drop cancellation frees upstream retained data without unregistering the client");
        terminal.Drop(1, items: dropped); Write("\x1b]72;t=m:o=1;text/plain\x1b\\");
        var copy = terminal.BeginDrop(0, 0, dropped);
        Write("\x1b]72;t=r:o=1\x1b\\");
        check(copy.IsCompletedSuccessfully && copy.Result == 1, "upstream Copy conclusion completes a native Copy drop");
        terminal.Drop(1, items: dropped); Write("\x1b]72;t=m:o=1;text/plain\x1b\\");
        var unsupported = terminal.BeginDrop(0, 0, dropped);
        Write("\x1b]72;t=r:o=2\x1b\\");
        check(unsupported.IsCompletedSuccessfully && unsupported.Result == 0, "a client cannot conclude Move for a Copy-only drag source");
        terminal.Drop(1, items: dropped); Write("\x1b]72;t=m:o=1;text/plain\x1b\\");
        var replaced = terminal.BeginDrop(0, 0, dropped);
        terminal.Drop(1, items: dropped);
        check(replaced.IsCompletedSuccessfully && replaced.Result == 0, "a new drag cancels the previous pending native drop");
        var unregister = terminal.BeginDrop(0, 0, dropped);
        Write("\x1b]72;t=A\x1b\\");
        check(unregister.IsCompletedSuccessfully && unregister.Result == 0, "client unregistration cancels a pending native drop");
        Write("\x1b]72;t=a;text/plain\x1b\\"); terminal.Drop(1, items: dropped);
        var reset = terminal.BeginDrop(0, 0, dropped);
        Write("\x1b" + "c");
        check(reset.IsCompletedSuccessfully && reset.Result == 0, "terminal reset cancels a pending native drop");
        using var closing = new GhosttyTerminal();
        var registration = Encoding.UTF8.GetBytes("\x1b]72;t=a;text/plain\x1b\\"); closing.Write(registration, registration.Length);
        var disposed = closing.BeginDrop(0, 0, dropped); closing.Dispose();
        check(disposed.IsCompletedSuccessfully && disposed.Result == 0, "disposing a terminal completes a pending native drop as canceled");
    }
    private sealed class MemoryClipboard : ITerminalClipboard
    {
        public Dictionary<string, byte[]> Contents = [];
        public int Reads;
        public bool FailData;
        public int Write(IReadOnlyDictionary<string, byte[]> contents) { Contents = contents.ToDictionary(pair => pair.Key, pair => pair.Value); return 0; }
        public ClipboardReadResult Read(IReadOnlyList<string> mimes, bool list)
        {
            Reads++;
            if (FailData && mimes.Count > 0) return new(5, new Dictionary<string, byte[]>(), []);
            return new(0, Contents.Where(pair => mimes.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value), list ? Contents.Keys.ToArray() : []);
        }
    }
}
