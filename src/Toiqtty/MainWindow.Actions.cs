using Microsoft.UI.Windowing;
using Toiqtty.Models;
using Windows.System;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private readonly ActionBindingState _bindingState = new();
    private Task _actionTail = Task.CompletedTask;
    private int _pendingActions;
    private bool TryHandleActionKey(uint key, uint modifiers)
    {
        // Pure modifier presses must not consume the next leader/modal key.
        if (key is 16 or 17 or 18 or 91 or 92) return false;
        if (!_bindingState.Match(_saved.Settings, key, modifiers, Environment.TickCount64, out var action)) return false;
        if (action is { Kind: "action", Value: "copy" or "paste" } && _focusedBrowser is not null) return false;
        if (action is not null) _ = ObserveAction(QueueAction(action));
        QueueLuaPresentation(); return true;
    }
    private async Task ObserveAction(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException or OperationCanceledException)
        { if (!_closed) SetStatusMessage(ex.Message, problem: true); }
    }
    private Task QueueAction(ActionSpec action, CancellationToken cancellation = default)
    {
        action.Validate();
        if (_pendingActions >= 64) return Task.FromException(new InvalidOperationException("Action queue is full."));
        _pendingActions++; var previous = _actionTail;
        return _actionTail = Run();
        async Task Run()
        {
            try { try { await previous; } catch { } cancellation.ThrowIfCancellationRequested(); await ExecuteAction(action, new ActionBudget { Cancellation = cancellation, ExpectedPane = ActionPaneId }, 0); }
            finally { _pendingActions--; }
        }
    }
    private sealed class ActionBudget { internal int Remaining = 128; internal CancellationToken Cancellation; internal string? ExpectedPane; }
    private async Task ExecuteAction(ActionSpec action, ActionBudget budget, int depth)
    {
        if (_closed || _closing) throw new InvalidOperationException("The window is closing.");
        budget.Cancellation.ThrowIfCancellationRequested();
        if (budget.ExpectedPane != ActionPaneId) throw new InvalidOperationException("The action target changed; remaining steps were cancelled.");
        if (--budget.Remaining < 0 || depth > 8) throw new ArgumentException("Action execution exceeds its step or recursion limit.");
        action.Validate();
        switch (action.Kind)
        {
            case "sequence": foreach (var step in action.Steps) await ExecuteAction(step, budget, depth + 1); return;
            case "send_text": await RequireTerminal().SendTextAsync(action.Value); if (budget.ExpectedPane != ActionPaneId) throw new InvalidOperationException("The action target changed; remaining steps were cancelled."); return;
            case "paste": await RequireTerminal().PasteAsync(action.Value); if (budget.ExpectedPane != ActionPaneId) throw new InvalidOperationException("The action target changed; remaining steps were cancelled."); return;
            case "activate_key_table": _bindingState.Push(_saved.Settings, action.Value, Environment.TickCount64); QueueLuaPresentation(); return;
            case "pop_key_table": _bindingState.Pop(); QueueLuaPresentation(); return;
            case "lua":
                var runtime = _luaRuntime ?? throw new InvalidOperationException("No Lua action worker is configured.");
                string? pane = ActionPaneId;
                var context = StatusValues().ToDictionary(p => p.Key, p => (object?)p.Value);
                context["pane_id"] = ActionPaneId; context["session_id"] = _selected?.Id; context["tab_id"] = _selected?.ActiveTab.Id;
                context["key_table"] = _bindingState.ActiveTable(Environment.TickCount64); context["leader_active"] = _bindingState.LeaderActive(Environment.TickCount64);
                var result = await runtime.Invoke(new() { Action = action.Value, Status = context }, budget.Cancellation);
                if (!ReferenceEquals(runtime, _luaRuntime) || pane != ActionPaneId) throw new InvalidOperationException("The Lua action target changed while evaluating.");
                if (result.Error is not null) throw new ArgumentException("Lua action failed: " + result.Error);
                if (result.Action is { } next) await ExecuteAction(next, budget, depth + 1);
                return;
        }
        switch (action.Value)
        {
            case "newWorkspace": NewWorkspace(); break;
            case "newTab": NewTab(); break;
            case "newBrowser": NewBrowser(); break;
            case "openDirectory": await OpenDirectory(); break;
            case "nextWallpaper": CycleWallpaper(1); break;
            case "previousWallpaper": CycleWallpaper(-1); break;
            case "randomWallpaper": CycleWallpaper(1, true); break;
            case "toggleWallpaper": ToggleWallpaper(); break;
            case "chooseWallpaper": await ChooseWallpaper(); break;
            case "switchSession": await SwitchSession(); break;
            case "nextTab": CycleTab(1); break;
            case "previousTab": CycleTab(-1); break;
            case "splitRight": Split(true); break;
            case "splitDown": Split(false); break;
            case "closePane": if (ActionPaneId is { } paneId) ClosePane(paneId); break;
            case "palette": await ShowCommandPalette(); break;
            case "sidebar": ToggleSidebar(); break;
            case "find": await ShowFind(); break;
            case "zoom": ZoomClick(this, new()); break;
            case "notifications": ActivityClick(); break;
            case "copy": if (_focused is { } copying) await copying.CopySelectionAsync(); break;
            case "paste": if (_focused is { } focused) await focused.PasteClipboard(); break;
            case "focusLeft": FocusDirection(VirtualKey.Left); break;
            case "focusRight": FocusDirection(VirtualKey.Right); break;
            case "focusUp": FocusDirection(VirtualKey.Up); break;
            case "focusDown": FocusDirection(VirtualKey.Down); break;
            case "scrollUp": RequireTerminal().Scroll(-Math.Max(1, (int)(_focused?.FrameForCheck.Rows ?? 30) * 3 / 4)); break;
            case "scrollDown": RequireTerminal().Scroll(Math.Max(1, (int)(_focused?.FrameForCheck.Rows ?? 30) * 3 / 4)); break;
            case "movePane": if (ActionPaneId is { } movingPane) await MovePaneDialog(movingPane); break;
            case "toggleFullscreen": ToggleFullscreen(); break;
            case "toggleMaximized": if (AppWindow.Presenter is OverlappedPresenter presenter) { if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore(); else presenter.Maximize(); } break;
            case "toggleAlwaysOnTop": if (AppWindow.Presenter is OverlappedPresenter top) top.IsAlwaysOnTop = !top.IsAlwaysOnTop; break;
            case "increaseFontSize": ChangeActionFontSize(1); break;
            case "decreaseFontSize": ChangeActionFontSize(-1); break;
            case "resetFontSize": _saved.Settings.FontSize = _luaConfig.Settings.FontSize; foreach (var view in _views) view.ApplySettings(); break;
            case "resizeLeft": ResizeActionPane(true, -.025); break;
            case "resizeRight": ResizeActionPane(true, .025); break;
            case "resizeUp": ResizeActionPane(false, -.025); break;
            case "resizeDown": ResizeActionPane(false, .025); break;
            case "nextWorkspace": case "previousWorkspace":
                if (_saved.Workspaces.Count == 0) return;
                int index = _saved.Workspaces.IndexOf(_selected!);
                SelectWorkspace(_saved.Workspaces[(index + (action.Value == "previousWorkspace" ? -1 : 1) + _saved.Workspaces.Count) % _saved.Workspaces.Count]); break;
            default:
                if (action.Value.StartsWith("workspace", StringComparison.Ordinal))
                { int target = action.Value[^1] - '1'; if (target >= 0 && target < _saved.Workspaces.Count) SelectWorkspace(_saved.Workspaces[target]); }
                break;
        }
        budget.ExpectedPane = ActionPaneId;
    }
    private string? ActionPaneId => ActivePaneId ?? _selected?.FocusedPaneId;
    private Toiqtty.Terminal.ITerminalSession RequireTerminal() => _selected?.Root.Leaves().FirstOrDefault(p => p.Id == ActionPaneId)?.Session
        ?? throw new InvalidOperationException("Focus a terminal pane first.");
    private void ChangeActionFontSize(int delta)
    { _saved.Settings.FontSize = Math.Clamp(_saved.Settings.FontSize + delta, 9, 32); foreach (var view in _views) view.ApplySettings(); UpdateStatusBar(); Save(); }
    private void ResizeActionPane(bool vertical, double delta)
    {
        if (_selected is null || ActionPaneId is not { } id) return;
        PaneNode? Find(PaneNode node)
        {
            if (node.IsLeaf || !node.Leaves().Any(p => p.Id == id)) return null;
            return Find(node.First!) ?? Find(node.Second!) ?? (node.Vertical == vertical ? node : null);
        }
        if (Find(_selected.Root) is not { } split) return;
        split.Ratio = Math.Clamp(split.Ratio + delta, .1, .9); RebuildPanes(id); Save();
    }
}
