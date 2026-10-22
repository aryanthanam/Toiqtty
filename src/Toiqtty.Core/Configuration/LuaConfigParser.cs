using Lua;
using Lua.Standard;
using Toiqtty.Models;

namespace Toiqtty.Configuration;

public static partial class LuaConfigParser
{
    public const int MaxSourceBytes = 256 * 1024;
    public static async Task<LuaConfigResult> Evaluate(LuaConfigRequest request, CancellationToken cancellation = default)
        => (await EvaluateRuntime(request, cancellation)).Result;
    internal static async Task<LuaConfigEvaluation> EvaluateRuntime(LuaConfigRequest request, CancellationToken cancellation = default)
    {
        var result = new LuaConfigResult { Settings = LuaConfigResult.Clone(request.Baseline) };
        var state = LuaState.Create();
        state.OpenBasicLibrary(); state.OpenStringLibrary(); state.OpenTableLibrary(); state.OpenMathLibrary();
        state.OpenBitwiseLibrary(); state.OpenCoroutineLibrary(); state.OpenModuleLibrary();
        foreach (string name in new[] { "dofile", "loadfile", "collectgarbage", "print" }) state.Environment[name] = LuaValue.Nil;
        var package = state.Environment["package"].Read<LuaTable>();
        package["searchers"] = new LuaTable(); package["searchpath"] = LuaValue.Nil; package["path"] = "";
        string folder = Path.GetDirectoryName(Path.GetFullPath(request.Path))!;
        var loader = new ConfigModuleLoader(folder); state.ModuleLoader = loader;
        var api = new LuaTable();
        api["home_dir"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        api["config_dir"] = folder; api["data_dir"] = request.DataDirectory;
        api["version"] = BuildInfo.Version; api["default_shell"] = ShellDefaults.Command;
        api["getenv"] = new LuaFunction((context, _) => new(context.Return(Environment.GetEnvironmentVariable(context.GetArgument<string>(0)) is { } value ? new LuaValue(value) : LuaValue.Nil)));
        api["file_exists"] = new LuaFunction((context, _) => new(context.Return(File.Exists(context.GetArgument<string>(0)))));
        api["directory_exists"] = new LuaFunction((context, _) => new(context.Return(Directory.Exists(context.GetArgument<string>(0)))));
        api["path_join"] = new LuaFunction((context, _) => new(context.Return(Path.Combine(context.GetArgument<string>(0), context.GetArgument<string>(1)))));
        api["config_builder"] = new LuaFunction((context, _) => new(context.Return(new LuaTable())));
        api["strftime"] = new LuaFunction((context, _) => new(context.Return(LuaConfigEvaluation.Strftime(context.GetArgument<string>(0)))));
        package["loaded"].Read<LuaTable>()["toiqtty"] = api;
        state.Environment["toiqtty"] = api;
        var os = new LuaTable(); os["getenv"] = api["getenv"]; state.Environment["os"] = os;
        string source = loader.Read(request.Path);
        var values = await state.DoStringAsync(source, "@" + request.Path, cancellation);
        if (values.Length != 1 || values[0].Type != LuaValueType.Table) throw new ArgumentException("The Lua config must return exactly one settings table.");
        var table = values[0].Read<LuaTable>();
        Keys(table, "config", "shell", "start_directory", "font", "font_size", "font_weight", "restore_sessions", "allow_clipboard_read", "allow_clipboard_write", "allow_image_files", "ligatures", "desktop_notifications", "keybindings", "browser", "startup", "scrollback_limit_bytes", "scrollback_limit_lines", "scrolling", "status_bar", "color_schemes", "color_scheme", "cursor", "wallpaper", "events", "bindings", "leader", "key_tables", "appearance", "window", "actions");
        var overrides = new List<string>(); var settings = result.Settings;
        void String(string key, string property, Action<string> set) { if (Present(table, key)) { set(Text(table[key], key)); overrides.Add(property); } }
        void Bool(string key, string property, Action<bool> set) { if (Present(table, key)) { set(Boolean(table[key], key)); overrides.Add(property); } }
        String("shell", "Shell", value => settings.Shell = value);
        String("start_directory", "StartDirectory", value => settings.StartDirectory = Environment.ExpandEnvironmentVariables(value));
        String("font", "FontFamily", value => settings.FontFamily = value);
        if (Present(table, "font_size")) { settings.FontSize = (float)Number(table["font_size"], "font_size", 9, 32); overrides.Add("FontSize"); }
        ParseAppearance(table, settings, overrides, folder);
        ParseInteractions(table, settings, overrides);
        Bool("restore_sessions", "RestoreWorkspaces", value => settings.RestoreWorkspaces = value);
        Bool("allow_clipboard_read", "AllowClipboardRead", value => settings.AllowClipboardRead = value);
        Bool("allow_clipboard_write", "AllowClipboardWrite", value => settings.AllowClipboardWrite = value);
        Bool("allow_image_files", "AllowImageFiles", value => settings.AllowImageFiles = value);
        Bool("ligatures", "Ligatures", value => settings.Ligatures = value);
        Bool("desktop_notifications", "DesktopNotifications", value => settings.DesktopNotifications = value);
        if (Present(table, "scrollback_limit_bytes")) { settings.ScrollbackLimitBytes = ScrollbackLimit(table["scrollback_limit_bytes"], "scrollback_limit_bytes"); overrides.Add("ScrollbackLimitBytes"); }
        if (Present(table, "scrollback_limit_lines")) { settings.ScrollbackLimitLines = ScrollbackLimit(table["scrollback_limit_lines"], "scrollback_limit_lines"); overrides.Add("ScrollbackLimitLines"); }
        if (Present(table, "scrolling"))
        {
            var scrolling = Table(table["scrolling"], "scrolling");
            Keys(scrolling, "scrolling", "lines_per_notch", "app_events_per_notch", "reverse");
            int Speed(string key)
            {
                double number = Number(scrolling[key], "scrolling." + key, 1, 100);
                if (number != Math.Truncate(number)) throw new ArgumentException("scrolling." + key + " must be an integer.");
                return (int)number;
            }
            if (Present(scrolling, "lines_per_notch")) settings.Scrolling.LinesPerNotch = Speed("lines_per_notch");
            if (Present(scrolling, "app_events_per_notch")) settings.Scrolling.AppEventsPerNotch = Speed("app_events_per_notch");
            if (Present(scrolling, "reverse")) settings.Scrolling.Reverse = Boolean(scrolling["reverse"], "scrolling.reverse");
            overrides.Add("Scrolling");
        }
        if (Present(table, "keybindings"))
        {
            var bindings = Table(table["keybindings"], "keybindings");
            foreach (var pair in bindings)
            {
                string action = Text(pair.Key, "keybindings action"), chord = Text(pair.Value, "keybindings." + action, allowEmpty: true);
                if (!Keybindings.Defaults.ContainsKey(action)) throw new ArgumentException("Unknown keybinding action: " + action);
                settings.Keybindings[action] = chord;
            }
            overrides.Add("Keybindings");
        }
        if (Present(table, "browser"))
        {
            var browser = Table(table["browser"], "browser"); Keys(browser, "browser", "home_page", "profile", "open_terminal_links");
            if (Present(browser, "home_page")) { settings.Browser.HomePage = BrowserAddress.Resolve(Text(browser["home_page"], "browser.home_page")); overrides.Add("Browser.HomePage"); }
            if (Present(browser, "profile")) { settings.Browser.DefaultSourceId = Profile(browser["profile"]); overrides.Add("Browser.DefaultSourceId"); }
            if (Present(browser, "open_terminal_links")) { settings.Browser.OpenTerminalLinks = Boolean(browser["open_terminal_links"], "browser.open_terminal_links"); overrides.Add("Browser.OpenTerminalLinks"); }
        }
        if (Present(table, "status_bar"))
        {
            var bar = Table(table["status_bar"], "status_bar");
            Keys(bar, "status_bar", "enabled", "left", "right", "separator", "height", "font_size", "background", "foreground");
            if (Present(bar, "enabled")) settings.StatusBar.Enabled = Boolean(bar["enabled"], "status_bar.enabled");
            if (Present(bar, "left")) settings.StatusBar.Left = Array(bar["left"], "status_bar.left", 16).Select(value => Text(value, "status_bar.left segment", 256, true)).ToArray();
            if (Present(bar, "right")) settings.StatusBar.Right = Array(bar["right"], "status_bar.right", 16).Select(value => Text(value, "status_bar.right segment", 256, true)).ToArray();
            if (Present(bar, "separator")) settings.StatusBar.Separator = Text(bar["separator"], "status_bar.separator", 32, true);
            if (Present(bar, "height")) settings.StatusBar.Height = Number(bar["height"], "status_bar.height", 18, 64);
            if (Present(bar, "font_size")) settings.StatusBar.FontSize = Number(bar["font_size"], "status_bar.font_size", 9, 24);
            if (Present(bar, "background")) settings.StatusBar.Background = Text(bar["background"], "status_bar.background", 7, true);
            if (Present(bar, "foreground")) settings.StatusBar.Foreground = Text(bar["foreground"], "status_bar.foreground", 7, true);
            overrides.Add("StatusBar");
        }
        Validate(settings);
        if (Present(table, "startup"))
        {
            var startup = Table(table["startup"], "startup"); Keys(startup, "startup", "mode", "sessions");
            string mode = Present(startup, "mode") ? Text(startup["mode"], "startup.mode") : "when_empty";
            if (mode is not ("when_empty" or "replace_saved")) throw new ArgumentException("startup.mode must be 'when_empty' or 'replace_saved'.");
            result.ReplaceSavedStartup = mode == "replace_saved";
            if (Present(startup, "sessions")) foreach (var value in Array(startup["sessions"], "startup.sessions", 64))
            {
                var session = Table(value, "startup session"); Keys(session, "startup session", "name", "tabs", "theme");
                var workspace = new Workspace { Name = Text(session["name"], "session.name", 60), Tabs = [] };
                if (Present(session, "theme"))
                {
                    string theme = Text(session["theme"], "session.theme", 64);
                    workspace.Theme = theme.Equals("default", StringComparison.OrdinalIgnoreCase) ? null
                        : FindTheme(settings, theme) ?? throw new ArgumentException("Unknown session theme: " + theme);
                }
                foreach (var tabValue in Array(session["tabs"], "session.tabs", 64))
                {
                    var tab = Table(tabValue, "startup tab"); Keys(tab, "startup tab", "name", "root", "shell", "cwd", "kind", "url", "profile");
                    bool root = Present(tab, "root");
                    if (root && new[] { "shell", "cwd", "kind", "url", "profile" }.Any(key => Present(tab, key))) throw new ArgumentException("A tab must use either root or inline pane fields.");
                    var pane = root ? Pane(Table(tab["root"], "tab.root"), settings, 0) : Pane(tab, settings, 0, inline: true);
                    if (pane.Leaves().Count() > 16) throw new ArgumentException("Startup tabs support at most 16 panes.");
                    workspace.Tabs.Add(new TerminalTab { Name = Present(tab, "name") ? Text(tab["name"], "tab.name", 60) : (pane.IsBrowser ? "Browser" : "Shell"), Root = pane, FocusedPaneId = pane.Leaves().First().Id });
                }
                if (workspace.Tabs.Count == 0) throw new ArgumentException("A startup session needs at least one tab.");
                workspace.ActiveTabId = workspace.Tabs[0].Id; result.StartupSessions.Add(workspace);
            }
            if (result.StartupSessions.SelectMany(w => w.AllPanes()).Count() > 256) throw new ArgumentException("Startup layouts support at most 256 panes.");
            if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result.StartupSessions).Length > 768 * 1024) throw new ArgumentException("Startup layout is too large.");
        }
        var hooks = new Dictionary<string, LuaValue>();
        if (Present(table, "events"))
        {
            var events = Table(table["events"], "events"); Keys(events, "events", "format_tab_title", "format_status");
            foreach (var pair in events)
            {
                if (pair.Value.Type != LuaValueType.Function) throw new ArgumentException("events callbacks must be functions.");
                hooks.Add(pair.Key.Read<string>(), pair.Value);
            }
        }
        if (Present(table, "actions"))
        {
            var actions = Table(table["actions"], "actions"); if (actions.Count() > 64) throw new ArgumentException("At most 64 Lua actions are supported.");
            foreach (var pair in actions)
            {
                string name = Text(pair.Key, "Lua action name", 64);
                if (pair.Value.Type != LuaValueType.Function) throw new ArgumentException("Lua actions must be functions.");
                hooks.Add("action:" + name, pair.Value);
            }
        }
        foreach (var action in settings.Bindings.Concat(settings.KeyTables.Values.SelectMany(t => t.Bindings)).SelectMany(b => Flatten(b.Action)))
            if (action.Kind == "lua" && !hooks.ContainsKey("action:" + action.Value)) throw new ArgumentException("Unknown Lua action: " + action.Value);
        result.Hooks = hooks.Keys.ToArray();
        result.Overrides = overrides.ToArray(); result.Dependencies = loader.Dependencies.ToArray(); return new(state, result, hooks);
    }
    private static PaneNode Pane(LuaTable table, AppSettings settings, int depth, bool inline = false)
    {
        if (depth > 12) throw new ArgumentException("Startup split trees are too deep or cyclic.");
        Keys(table, "pane", inline ? ["name", "shell", "cwd", "kind", "url", "profile"] : ["shell", "cwd", "kind", "url", "profile", "split", "ratio", "first", "second"]);
        if (Present(table, "split"))
        {
            if (new[] { "shell", "cwd", "kind", "url", "profile" }.Any(key => Present(table, key))) throw new ArgumentException("A split cannot also be a shell or browser leaf.");
            string split = Text(table["split"], "pane.split");
            if (split is not ("right" or "down")) throw new ArgumentException("pane.split must be 'right' or 'down'.");
            return new PaneNode { Vertical = split == "right", Ratio = Present(table, "ratio") ? Number(table["ratio"], "pane.ratio", .1, .9) : .5,
                First = Pane(Table(table["first"], "pane.first"), settings, depth + 1), Second = Pane(Table(table["second"], "pane.second"), settings, depth + 1) };
        }
        if (Present(table, "first") || Present(table, "second") || Present(table, "ratio")) throw new ArgumentException("first, second, and ratio require a split.");
        string kind = Present(table, "kind") ? Text(table["kind"], "pane.kind") : "terminal";
        if (kind is not ("terminal" or "browser")) throw new ArgumentException("pane.kind must be 'terminal' or 'browser'.");
        var node = new PaneNode { Kind = kind, Command = settings.Shell, Directory = settings.StartDirectory };
        if (kind == "browser")
        {
            if (Present(table, "shell") || Present(table, "cwd")) throw new ArgumentException("Browser panes use url/profile, not shell/cwd.");
            node.BrowserUrl = Present(table, "url") ? BrowserAddress.Resolve(Text(table["url"], "pane.url")) : settings.Browser.HomePage;
            node.BrowserSourceId = Present(table, "profile") ? Profile(table["profile"]) : settings.Browser.DefaultSourceId;
        }
        else
        {
            if (Present(table, "url") || Present(table, "profile")) throw new ArgumentException("Terminal panes use shell/cwd, not url/profile.");
            if (Present(table, "shell")) node.Command = Text(table["shell"], "pane.shell");
            if (Present(table, "cwd")) node.Directory = Environment.ExpandEnvironmentVariables(Text(table["cwd"], "pane.cwd"));
            if (!Directory.Exists(node.Directory)) throw new ArgumentException("Startup pane directory does not exist: " + node.Directory);
        }
        return node;
    }
    public static void Validate(AppSettings settings)
    {
        if (settings.Scrolling is null) throw new ArgumentException("Missing scrolling settings.");
        settings.Scrolling.Validate();
        if (settings.StatusBar is null) throw new ArgumentException("Missing status_bar settings.");
        settings.StatusBar.Validate();
        ValidateAppearance(settings);
        ValidateInteractions(settings);
        if (settings.TerminalTheme is { IsValid: false }) throw new ArgumentException("Invalid terminal theme.");
        if (settings.ScrollbackLimitBytes is < 0 || settings.ScrollbackLimitLines is < 0)
            throw new ArgumentException("Scrollback limits must be nonnegative integers or unlimited.");
        if (string.IsNullOrWhiteSpace(settings.Shell) || settings.Shell.Length > 32768 || string.IsNullOrWhiteSpace(settings.FontFamily) || settings.FontFamily.Length > 256 ||
            !float.IsFinite(settings.FontSize) || settings.FontSize is < 9 or > 32 || !Directory.Exists(settings.StartDirectory) || !BrowserAddress.IsAllowed(settings.Browser.HomePage))
            throw new ArgumentException("Use a shell command, valid font/font size, existing start directory, and HTTP(S) browser home page.");
        Keybindings.ParseOverrides(string.Join("\n", settings.Keybindings.Select(p => p.Key + " = " + p.Value)));
    }
    private static string Profile(LuaValue value) { string profile = Text(value, "browser profile", 128, true); return profile == "independent" ? "" : profile; }
    private static bool Present(LuaTable table, string key) => table[key].Type != LuaValueType.Nil;
    private static LuaTable Table(LuaValue value, string name) => value.Type == LuaValueType.Table ? value.Read<LuaTable>() : throw new ArgumentException(name + " must be a table.");
    private static string Text(LuaValue value, string name, int limit = 32768, bool allowEmpty = false)
    {
        if (value.Type != LuaValueType.String) throw new ArgumentException(name + " must be a string.");
        string text = value.Read<string>();
        if (text.Length > limit || (!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Contains('\0')) throw new ArgumentException(name + " is empty or too long."); return text;
    }
    private static bool Boolean(LuaValue value, string name) => value.Type == LuaValueType.Boolean ? value.Read<bool>() : throw new ArgumentException(name + " must be true or false.");
    private static long? ScrollbackLimit(LuaValue value, string name)
    {
        if (value.Type == LuaValueType.String && value.Read<string>() == "unlimited") return null;
        double number = Number(value, name, 0, 9_007_199_254_740_991);
        if (number != Math.Truncate(number)) throw new ArgumentException(name + " must be an integer or 'unlimited'.");
        return (long)number;
    }
    private static double Number(LuaValue value, string name, double min, double max)
    {
        if (value.Type != LuaValueType.Number) throw new ArgumentException(name + " must be a number.");
        double number = value.Read<double>(); if (!double.IsFinite(number) || number < min || number > max) throw new ArgumentException($"{name} must be between {min} and {max}."); return number;
    }
    private static void Keys(LuaTable table, string name, params string[] allowed)
    {
        foreach (var pair in table) if (pair.Key.Type != LuaValueType.String || !allowed.Contains(pair.Key.Read<string>())) throw new ArgumentException("Unknown field in " + name + ": " + pair.Key);
    }
    private static IEnumerable<LuaValue> Array(LuaValue value, string name, int limit)
    {
        var table = Table(value, name);
        if (table.ArrayLength > limit || table.HashMapCount > 0 || table.Count() != table.ArrayLength) throw new ArgumentException(name + " must be a contiguous array with at most " + limit + " entries.");
        for (int i = 1; i <= table.ArrayLength; i++) yield return table[i];
    }
    private sealed class ConfigModuleLoader(string folder) : ILuaModuleLoader
    {
        private int _bytes;
        public HashSet<string> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
        private string ModulePath(string name)
        {
            if (name.Length > 180 || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_-]*(\\.[A-Za-z_][A-Za-z0-9_-]*)*$")) throw new ArgumentException("Use local Lua module names such as themes.dark.");
            return Path.Combine(folder, name.Replace('.', Path.DirectorySeparatorChar) + ".lua");
        }
        public bool Exists(string moduleName) => File.Exists(ModulePath(moduleName));
        public ValueTask<LuaModule> LoadAsync(string moduleName, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); string path = ModulePath(moduleName); return new(new LuaModule("@" + path, Read(path))); }
        public string Read(string path)
        {
            path = Path.GetFullPath(path);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Lua modules must be inside the configuration folder.");
            for (string? part = path; part is not null && part.StartsWith(root, StringComparison.OrdinalIgnoreCase); part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Lua modules cannot use symbolic links or junctions.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxSourceBytes || _bytes + stream.Length > MaxSourceBytes || Dependencies.Count >= 32) throw new ArgumentException("Lua configuration exceeds the 256 KiB / 32 file limit.");
            _bytes += (int)stream.Length; Dependencies.Add(path);
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }
    }
}
