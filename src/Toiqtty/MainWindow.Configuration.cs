using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toiqtty.Configuration;
using Toiqtty.Models;
using Windows.Storage;
using Windows.System;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private readonly bool _luaEnabled;
    private bool _luaInitialized;
    private AppSettings _userSettings = new();
    private LuaConfigResult _luaConfig = new();
    private LuaConfigWatcher? _luaWatcher;
    private CancellationTokenSource? _luaReload;
    private int _luaGeneration;
    private string? _luaError;
    private string _preferencesFingerprint = "";
    private bool LuaOwns(string property) => _luaConfig.Overrides.Contains(property);
    private async Task InitializeLua()
    {
        if (!_luaEnabled) return;
        if (_startupLua is not null) await _startupLua.Result;
        if (_closed) return;
        _userSettings = _startupLua?.Baseline ?? UserPreferences.Load(SessionStore.Folder, _saved.Settings);
        ConfigurationSettings.Apply(_userSettings, _saved.Settings);
        await ReloadLua(initial: true);
        _luaInitialized = true; SavePreferences();
        try { _luaWatcher = new(LuaConfigService.ConfigPath, () => DispatcherQueue.TryEnqueue(() => UiTasks.Observe(ReloadLua()))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ConfigError("Could not watch Lua configuration: " + ex.Message); }
    }
    private async Task ReloadLua(bool initial = false)
    {
        if (!_luaEnabled || _closed) return;
        _luaReload?.Cancel(); _luaReload?.Dispose();
        _luaReload = new(); var cancellation = _luaReload.Token; int generation = ++_luaGeneration;
        LuaRuntimeClient? nextRuntime=null;
        try
        {
            while (_dialogOpen) await Task.Delay(200, cancellation);
            LuaConfigResult result;
            if (initial && _startupLua is not null)
            {
                result = await _startupLua.Result;
                nextRuntime = _startupLua.TakeRuntime();
            }
            else if (File.Exists(LuaConfigService.ConfigPath))
            {
                nextRuntime=await LuaRuntimeClient.Start(new() { Path=LuaConfigService.ConfigPath,DataDirectory=SessionStore.Folder,Baseline=LuaConfigResult.Clone(_userSettings) },cancellation:cancellation);
                result=nextRuntime.Config;
            }
            else result=new() { Settings=LuaConfigResult.Clone(_userSettings) };
            if (_closed || generation != _luaGeneration) return;
            if (result.Error is not null) { ConfigError(result.Error); return; }
            var profiles = (_browserRuntime.Bridge?.Sources ?? []).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Id, StringComparer.OrdinalIgnoreCase);
            ConfigurationSettings.ResolveProfiles(result, profiles);
            _luaConfig = result; ConfigurationSettings.Apply(result.Settings, _saved.Settings);
            _bindingState.Reset();
            if (!initial) ApplyWindowSettings(initial: false);
            // Loaded applies appearance once after startup sessions and their
            // themes are installed. Live reload still updates existing views.
            if (!initial) ApplyAppearanceSettings();
            InstallLuaRuntime(result.Hooks.Length>0?nextRuntime:null);
            if(result.Hooks.Length>0) nextRuntime=null;
            foreach (var workspace in _saved.Workspaces) foreach (var pane in workspace.AllPanes()) pane.Session?.ApplySettings(_saved.Settings.WithTerminalTheme(workspace.Theme));
            foreach (var view in _views) view.ApplySettings();
            UpdateStatusBar();
            _luaError = null; LuaConfigBanner.IsOpen = false;
            if (!initial) { Save(); SetStatusMessage(File.Exists(LuaConfigService.ConfigPath) ? "Lua configuration reloaded" : "Lua configuration removed; normal settings restored"); }
        }
        catch (OperationCanceledException) { if(!cancellation.IsCancellationRequested) ConfigError("Lua configuration exceeded its 4 second execution limit."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { if (!_closed && generation == _luaGeneration) ConfigError(ex.Message); }
        finally { nextRuntime?.Dispose(); }
    }
    private void ConfigError(string error)
    {
        _luaError = error; LuaConfigBanner.Message = error.Length > 220 ? error[..220] + "…" : error;
        LuaConfigBanner.IsOpen = true;
        SetStatusMessage("Lua config error · current settings retained");
    }
    private void SavePreferences()
    {
        if (!_luaEnabled || !_luaInitialized) return;
        ConfigurationSettings.CapturePreferences(_saved.Settings, _userSettings, _luaConfig.Overrides);
        string serialized = JsonSerializer.Serialize(_userSettings, ConfigurationJsonContext.Default.AppSettings);
        if (serialized == _preferencesFingerprint) return;
        UserPreferences.Save(SessionStore.Folder, _userSettings); _preferencesFingerprint = serialized;
    }
    private List<Workspace> ConfiguredStartupSessions()
    {
        var sessions = LuaConfigResult.Clone(_luaConfig.StartupSessions);
        foreach (var session in sessions)
        {
            session.Id = Guid.NewGuid().ToString("N");
            foreach (var tab in session.Tabs)
            {
                tab.Id = Guid.NewGuid().ToString("N"); Reset(tab.Root);
                tab.FocusedPaneId = tab.Root.Leaves().First().Id;
            }
            session.ActiveTabId = session.Tabs[0].Id;
        }
        return sessions;
        static void Reset(PaneNode node)
        {
            node.Id = Guid.NewGuid().ToString("N");
            if (!node.IsLeaf) { Reset(node.First!); Reset(node.Second!); }
        }
    }
    private void ApplyStartupConfiguration()
    {
        if (_reattached || _luaConfig.StartupSessions.Count == 0) return;
        _saved.Workspaces = ConfiguredStartupSessions(); _saved.SelectedWorkspace = _saved.Workspaces[0].Id;
    }
    private void CreateConfiguredSessions()
    {
        var sessions = ConfiguredStartupSessions();
        if (sessions.Count == 0) { SetStatusMessage("No startup sessions are configured in Lua.", problem: true); return; }
        if (_saved.Workspaces.Count + sessions.Count > 64 || _saved.Workspaces.SelectMany(w => w.AllPanes()).Count() + sessions.SelectMany(w => w.AllPanes()).Count() > 256)
        { SetStatusMessage("Configured sessions exceed the session/pane limits.", problem: true); return; }
        _saved.Workspaces.AddRange(sessions);
        // Fresh pane ids are never on the host, so these create (one shared layout commit), visible tab first.
        EnsureShells(sessions, sessions[0], "Could not start configured shell: ");
        SelectWorkspace(sessions[0]); Save();
    }
    private async Task OpenLuaConfiguration()
    {
        try
        {
            string path = LuaConfigService.ConfigPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                await writer.WriteAsync("-- toiqtty Lua configuration. See Configuration/toiqtty.example.lua beside toiqtty.exe.\nlocal toiqtty = require('toiqtty')\nlocal config = toiqtty.config_builder()\n\n-- config.font_size = 15\n\nreturn config\n");
            }
            await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { ConfigError(ex.Message); }
    }
    private void LuaConfigStatusClick(object _1, RoutedEventArgs _2) => UiTasks.Observe(ShowLuaStatus());
    private async Task ShowLuaStatus()
    {
        var text = new TextBlock { Text = "Config: " + LuaConfigService.ConfigPath + "\n\n" + (_luaError ??
            (File.Exists(LuaConfigService.ConfigPath) ? "Loaded. Lua controls: " + string.Join(", ", _luaConfig.Overrides) : "No Lua file. Normal settings are active.")) +
            "\n\nStartup layouts run on a fresh start according to their mode, and never on reconnect or live reload.",
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 520 };
        await ShowDialog(new ContentDialog { Title = "Lua configuration", Content = new ScrollViewer { Content = text, MaxHeight = 450 }, CloseButtonText = "Done" });
    }
    private StackPanel LuaSettingsControls()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Lua config: " + LuaConfigService.ConfigPath + "\nSettings controlled by Lua are read-only here. Edit the file to change them.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var edit = new Button { Content = "Edit Lua configuration" }; edit.Click += (_, _) => UiTasks.Observe(OpenLuaConfiguration()); panel.Children.Add(edit);
        return panel;
    }
}
