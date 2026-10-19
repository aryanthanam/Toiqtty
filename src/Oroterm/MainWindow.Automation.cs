using Oroterm.Automation;
using Oroterm.Models;

namespace Oroterm;

public sealed partial class MainWindow
{
    private ControlServer? _controlServer;
    private void InitializeControlServer()
    {
        _controlServer = new(SessionStore.Folder, (request, token) => {
            var completion = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () => {
                try { token.ThrowIfCancellationRequested(); completion.TrySetResult(await HandleControl(request, token)); }
                catch (Exception ex) { completion.TrySetResult(new() { Error = ex.Message }); }
            })) completion.TrySetResult(new() { Error = "The window is unavailable." });
            return completion.Task.WaitAsync(token);
        });
    }
    private async Task<ControlReply> HandleControl(ControlRequest request, CancellationToken cancellation = default)
    {
        if (request.Version != 1) throw new ArgumentException("Unsupported CLI protocol version.");
        if (_closed || _closing) throw new InvalidOperationException("The window is closing.");
        if (request.Command is null || request.Session is null || request.Pane is null || request.Name is null || request.Shell is null || request.Directory is null || request.Text is null || request.Action is null)
            throw new ArgumentException("Invalid CLI request.");
        if (request.Command == "sessions") return ControlReply.Success(_saved.Workspaces.Select(w => new {
            id = w.Id, name = w.Name, selected = w == _selected, theme = ThemeFor(w)?.Name,
            tabs = w.Tabs.Select(t => new { id = t.Id, name = t.Name, selected = t == w.ActiveTab,
                panes = t.Root.Leaves().Select(p => new { id = p.Id, kind = p.Kind, directory = p.Session?.Directory ?? p.Directory, shell = p.Command, exited = p.Session?.HasExited ?? false }) })
        }).ToArray());
        if (_dialogOpen || _draggedPaneId is not null || _draggingSessions || _draggingTabs) throw new InvalidOperationException("Finish the current dialog or drag before changing the workspace.");
        Workspace Resolve()
        {
            if (request.Session.Length == 0) return _selected ?? throw new ArgumentException("No session is selected.");
            var exact = _saved.Workspaces.FirstOrDefault(w => w.Id == request.Session);
            if (exact is not null) return exact;
            var matches = _saved.Workspaces.Where(w => w.Name.Equals(request.Session, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : throw new ArgumentException(matches.Length == 0 ? "Session not found." : "Session name is ambiguous; use its ID.");
        }
        void ValidateLaunch()
        {
            if (request.Name.Length > 60 || request.Name.Any(char.IsControl) || request.Shell.Length > 32768 || request.Shell.Contains('\0')) throw new ArgumentException("Invalid session/tab name or shell command.");
            if (request.Directory.Length > 0 && !Directory.Exists(request.Directory)) throw new ArgumentException("Launch directory does not exist.");
            if (_saved.Workspaces.SelectMany(w => w.AllPanes()).Count() >= 256) throw new ArgumentException("Pane limit reached.");
        }
        switch (request.Command)
        {
            case "focus":
                SelectWorkspace(Resolve());
                if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } minimized) minimized.Restore();
                Activate(); break;
            case "new-session":
                ValidateLaunch(); if (_saved.Workspaces.Count >= 64) throw new ArgumentException("Session limit reached.");
                NewWorkspace(Empty(request.Shell), Empty(request.Name), Empty(request.Directory)); break;
            case "rename-session":
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 60 || request.Name.Any(char.IsControl)) throw new ArgumentException("Use a nonempty session name of at most 60 characters.");
                Resolve().Name = request.Name; RefreshSidebar(); Save(); break;
            case "new-tab":
                ValidateLaunch(); var session = Resolve(); if (session.Tabs.Count >= 64) throw new ArgumentException("Tab limit reached.");
                SelectWorkspace(session); NewTab(Empty(request.Shell), Empty(request.Name), Empty(request.Directory)); break;
            case "split":
                if (request.Direction is not ("right" or "down")) throw new ArgumentException("Split direction must be right or down.");
                ValidateLaunch(); var target = Resolve(); if (target.Root.Leaves().Count() >= 16) throw new ArgumentException("Tab pane limit reached.");
                SelectWorkspace(target); Split(request.Direction == "right"); break;
            case "send-text": case "paste":
                if (System.Text.Encoding.UTF8.GetByteCount(request.Text) > 65536) throw new ArgumentException("Input exceeds 64 KiB.");
                var pane = request.Pane.Length == 0 ? Resolve().Root.Leaves().FirstOrDefault(p => p.Id == Resolve().FocusedPaneId)
                    : _saved.Workspaces.SelectMany(w => w.AllPanes()).FirstOrDefault(p => p.Id == request.Pane);
                var terminal = pane?.Session ?? throw new ArgumentException("Terminal pane not found.");
                if (request.Command == "paste") await terminal.PasteAsync(request.Text); else await terminal.SendTextAsync(request.Text); break;
            case "reload": await ReloadLua(); if (_luaError is not null) throw new ArgumentException(_luaError); break;
            case "action":
                if (request.Action is "palette" or "find" or "movePane" or "chooseWallpaper" or "switchSession") throw new ArgumentException("Interactive dialog actions are available in the window, not through the CLI.");
                if (request.Session.Length > 0) SelectWorkspace(Resolve());
                await QueueAction(request.Action.StartsWith("lua:",StringComparison.Ordinal) ? new() { Kind="lua",Value=request.Action[4..] } : new() { Value=request.Action }, cancellation); break;
            default: throw new ArgumentException("Unknown CLI command: " + request.Command);
        }
        return ControlReply.Success(new { session = _selected?.Id, tab = _selected?.ActiveTab.Id, pane = ActivePaneId ?? _selected?.FocusedPaneId });
        static string? Empty(string value) => value.Length == 0 ? null : value;
    }
}
