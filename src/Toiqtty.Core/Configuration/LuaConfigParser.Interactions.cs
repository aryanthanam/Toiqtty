using Lua;
using Toiqtty.Models;

namespace Toiqtty.Configuration;

public static partial class LuaConfigParser
{
    internal static ActionSpec ReadAction(LuaValue value, int depth = 0)
    {
        if (depth > 8) throw new ArgumentException("Action sequences exceed eight levels.");
        if (value.Type == LuaValueType.String) return new() { Value = Text(value, "action", 64) };
        var table = Table(value, "action");
        Keys(table, "action", "send_text", "paste", "sequence", "lua", "activate_key_table", "pop_key_table");
        if (table.Count() != 1) throw new ArgumentException("An action table must contain exactly one operation.");
        var pair = table.First(); string kind = pair.Key.Read<string>();
        var action = new ActionSpec { Kind = kind };
        if (kind == "sequence") action.Steps = Array(pair.Value, "action sequence", 32).Select(v => ReadAction(v, depth + 1)).ToList();
        else if (kind == "pop_key_table") { if (!Boolean(pair.Value, kind)) throw new ArgumentException("pop_key_table must be true."); }
        else if (kind is "send_text" or "paste")
        {
            if (pair.Value.Type != LuaValueType.String) throw new ArgumentException(kind + " must be a string.");
            action.Value = pair.Value.Read<string>();
        }
        else action.Value = Text(pair.Value, kind, 64);
        action.Validate(); return action;
    }
    private static List<ActionBinding> ReadBindings(LuaValue value)
    {
        return Array(value, "bindings", 256).Select(v => {
            var binding = Table(v, "binding"); Keys(binding, "binding", "key", "action");
            return new ActionBinding { Key = Text(binding["key"], "binding.key", 128), Action = ReadAction(binding["action"]) };
        }).ToList();
    }
    private static void ParseInteractions(LuaTable table, AppSettings settings, List<string> owned)
    {
        if (Present(table, "bindings")) { settings.Bindings = ReadBindings(table["bindings"]); owned.Add("Bindings"); }
        if (Present(table, "leader"))
        {
            var leader = Table(table["leader"], "leader"); Keys(leader, "leader", "key", "timeout_ms");
            settings.Leader = new() { Key = Text(leader["key"], "leader.key", 128), TimeoutMs = Present(leader, "timeout_ms") ? Integer(leader["timeout_ms"], "leader.timeout_ms", 100, 30000) : 1000 };
            owned.Add("Leader");
        }
        if (Present(table, "key_tables"))
        {
            var tables = Table(table["key_tables"], "key_tables"); if (tables.Count() > 32) throw new ArgumentException("At most 32 key tables are supported.");
            settings.KeyTables = [];
            foreach (var pair in tables)
            {
                string name = Text(pair.Key, "key table name", 64); var definition = Table(pair.Value, "key table");
                Keys(definition, "key table", "bindings", "timeout_ms", "one_shot");
                settings.KeyTables.Add(name, new() { Bindings = ReadBindings(definition["bindings"]),
                    TimeoutMs = Present(definition, "timeout_ms") ? Integer(definition["timeout_ms"], "key table.timeout_ms", 100, 30000) : 1000,
                    OneShot = Present(definition, "one_shot") && Boolean(definition["one_shot"], "key table.one_shot") });
            }
            owned.Add("KeyTables");
        }
        if (Present(table, "appearance"))
        {
            var appearance = Table(table["appearance"], "appearance");
            Keys(appearance, "appearance", "padding", "line_height", "cell_width", "inactive_pane_opacity", "font_features");
            settings.Appearance = new();
            if (Present(appearance, "padding"))
            {
                var padding = Table(appearance["padding"], "appearance.padding"); Keys(padding, "padding", "left", "right", "top", "bottom");
                var p = settings.Appearance.Padding;
                if (Present(padding, "left")) p.Left = Number(padding["left"], "padding.left", 0, 128);
                if (Present(padding, "right")) p.Right = Number(padding["right"], "padding.right", 0, 128);
                if (Present(padding, "top")) p.Top = Number(padding["top"], "padding.top", 0, 128);
                if (Present(padding, "bottom")) p.Bottom = Number(padding["bottom"], "padding.bottom", 0, 128);
            }
            if (Present(appearance, "line_height")) settings.Appearance.LineHeight = Number(appearance["line_height"], "appearance.line_height", 1, 3);
            if (Present(appearance, "cell_width")) settings.Appearance.CellWidth = Number(appearance["cell_width"], "appearance.cell_width", .5, 2);
            if (Present(appearance, "inactive_pane_opacity")) settings.Appearance.InactivePaneOpacity = Number(appearance["inactive_pane_opacity"], "appearance.inactive_pane_opacity", .1, 1);
            if (Present(appearance, "font_features")) foreach (var pair in Table(appearance["font_features"], "font_features"))
                settings.Appearance.FontFeatures.Add(Text(pair.Key, "OpenType tag", 4), (uint)Integer(pair.Value, "OpenType value", 0, 65535));
            owned.Add("Appearance");
        }
        if (Present(table, "window"))
        {
            var window = Table(table["window"], "window"); Keys(window, "window", "width", "height", "state", "always_on_top", "backdrop", "background_opacity");
            settings.Window = new(); var w = settings.Window;
            if (Present(window, "width")) w.Width = Integer(window["width"], "window.width", 400, 16384);
            if (Present(window, "height")) w.Height = Integer(window["height"], "window.height", 300, 16384);
            if (Present(window, "state")) w.State = Text(window["state"], "window.state", 16);
            if (Present(window, "always_on_top")) w.AlwaysOnTop = Boolean(window["always_on_top"], "window.always_on_top");
            if (Present(window, "backdrop")) w.Backdrop = Text(window["backdrop"], "window.backdrop", 16);
            if (Present(window, "background_opacity")) w.BackgroundOpacity = Number(window["background_opacity"], "window.background_opacity", 0, 1);
            owned.Add("Window");
        }
    }
    private static int Integer(LuaValue value, string name, int min, int max)
    {
        double number = Number(value, name, min, max); if (number != Math.Truncate(number)) throw new ArgumentException(name + " must be an integer."); return (int)number;
    }
    private static void ValidateInteractions(AppSettings settings)
    {
        if (settings.Appearance is null || settings.Window is null) throw new ArgumentException("Missing appearance/window settings.");
        settings.Appearance.Validate(); settings.Window.Validate();
        if (settings.Bindings is null || settings.KeyTables is null || settings.Bindings.Count > 256 || settings.KeyTables.Count > 32) throw new ArgumentException("Invalid bindings/key tables.");
        if (settings.Leader is { } leader) { if (string.IsNullOrWhiteSpace(leader.Key)) throw new ArgumentException("Missing leader key."); Keybindings.Parse(leader.Key); if (leader.TimeoutMs is < 100 or > 30000) throw new ArgumentException("Invalid leader timeout."); }
        void ValidateBindings(List<ActionBinding> bindings, bool allowLeader)
        {
            var used = new HashSet<(bool, uint, uint)>();
            if (bindings is null || bindings.Count > 256) throw new ArgumentException("Invalid bindings.");
            foreach (var binding in bindings)
            {
                if (binding is null || string.IsNullOrWhiteSpace(binding.Key) || binding.Action is null) throw new ArgumentException("Invalid action binding.");
                bool prefix = binding.Key.StartsWith("LEADER+", StringComparison.OrdinalIgnoreCase);
                if (prefix && (!allowLeader || settings.Leader is null)) throw new ArgumentException("LEADER bindings need a leader and cannot appear inside key tables.");
                var (key, modifiers) = Keybindings.Parse(prefix ? binding.Key[7..] : binding.Key);
                if (!used.Add((prefix, key, modifiers))) throw new ArgumentException("Duplicate binding: " + binding.Key);
                binding.Action.Validate();
                foreach (var action in Flatten(binding.Action)) if (action.Kind == "activate_key_table" && !settings.KeyTables.ContainsKey(action.Value)) throw new ArgumentException("Unknown key table: " + action.Value);
            }
        }
        ValidateBindings(settings.Bindings, true);
        foreach (var (name, table) in settings.KeyTables)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || table is null || table.TimeoutMs is < 100 or > 30000) throw new ArgumentException("Invalid key table.");
            ValidateBindings(table.Bindings, false);
        }
    }
    internal static IEnumerable<ActionSpec> Flatten(ActionSpec action)
    {
        yield return action; foreach (var child in action.Steps) foreach (var step in Flatten(child)) yield return step;
    }
}
