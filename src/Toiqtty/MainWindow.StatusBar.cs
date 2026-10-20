using System.Globalization;
using Microsoft.UI.Xaml;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private string _statusMessage = "";
    private long _statusMessageUntil;
    private (uint Background, uint Foreground, uint Border)? _statusColors;

    private void UpdateStatusBar()
    {
        var settings = _saved.Settings.StatusBar;
        StatusBar.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Height = settings.Height;
        StatusText.FontSize = SessionStatus.FontSize = settings.FontSize;
        var theme = Chrome.For(ThemeFor(_selected));
        uint background = SessionTheme.TryColor(settings.Background, out uint bg) ? bg : theme.Background;
        uint foreground = SessionTheme.TryColor(settings.Foreground, out uint fg) ? fg : theme.Secondary;
        var colors = (background, foreground, theme.Border);
        if (_statusColors != colors)
        {
            _statusColors = colors;
            StatusBar.Background = Chrome.Brush(background);
            StatusText.Foreground = SessionStatus.Foreground = Chrome.Brush(foreground);
            StatusBar.BorderBrush = Chrome.Brush(theme.Border);
        }
        if (!settings.Enabled) return;
        var values = StatusValues();
        StatusText.Inlines.Clear(); SessionStatus.Inlines.Clear();
        StatusText.Text = Environment.TickCount64 < _statusMessageUntil ? _statusMessage : settings.Format(settings.Left, values);
        SessionStatus.Text = settings.Format(settings.Right, values);
        if (Environment.TickCount64 >= _statusMessageUntil && _luaPresentation.Left is { } left) ApplyLuaText(StatusText,left);
        if (_luaPresentation.Right is { } right) ApplyLuaText(SessionStatus,right);
    }
    private Dictionary<string,string> StatusValues()
    {
        var pane = _selected?.Root.Leaves().FirstOrDefault(p => p.Id == ActivePaneId);
        var session = pane?.Session;
        return new Dictionary<string, string>
        {
            ["session"] = _selected?.Name ?? "",
            ["tab"] = _selected?.ActiveTab.Name ?? "",
            ["directory"] = session is null ? "" : DisplayDirectory(session.Directory),
            ["branch"] = pane is { IsBrowser: false } ? _selected?.Branch ?? "" : "",
            ["status"] = session is { HasExited: true } ? $"Shell exited with code {session.ExitCode}" : session?.StatusSummary ?? "",
            ["exit_code"] = session is { HasExited: true } ? session.ExitCode.ToString(CultureInfo.InvariantCulture) : "",
            ["shells"] = _saved.Workspaces.SelectMany(w => w.AllPanes()).Count(p => !p.IsBrowser && p.Session is { HasExited: false }).ToString(CultureInfo.InvariantCulture),
            ["panes"] = (_selected?.Root.Leaves().Count() ?? 0).ToString(CultureInfo.InvariantCulture),
            ["tabs"] = (_selected?.Tabs.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            ["sessions"] = _saved.Workspaces.Count.ToString(CultureInfo.InvariantCulture),
            ["font_size"] = _saved.Settings.FontSize.ToString("0.#", CultureInfo.InvariantCulture),
            ["viewport"] = _focused?.ViewportLabel ?? "",
            ["url"] = pane is { IsBrowser: true } ? pane.BrowserUrl : "",
            ["version"] = BuildInfo.Version,
            ["key_table"] = _bindingState.ActiveTable(Environment.TickCount64),
            ["leader"] = _bindingState.LeaderActive(Environment.TickCount64) ? "LEADER" : ""
        };
    }

    private void SetStatusMessage(string message, bool problem = false)
    {
        _statusMessage = message;
        _statusMessageUntil = Environment.TickCount64 + 5000;
        if (problem)
        {
            StatusNotice.Message = message.Length > 2048 ? message[..2048] + "…" : message;
            StatusNotice.IsOpen = true;
        }
        UpdateStatusBar();
    }
}
