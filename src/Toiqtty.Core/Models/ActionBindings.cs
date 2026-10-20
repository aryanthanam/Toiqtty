namespace Toiqtty.Models;

public sealed class ActionSpec
{
    public string Kind { get; set; } = "action";
    public string Value { get; set; } = "";
    public List<ActionSpec> Steps { get; set; } = [];
    public void Validate(int depth = 0)
    {
        if (depth > 8 || Value is null || Value.Length > 65536 || Steps is null || Steps.Count > 32) throw new ArgumentException("Action exceeds its limits.");
        if (Kind == "action") { if (!Keybindings.Defaults.ContainsKey(Value)) throw new ArgumentException("Unknown action: " + Value); }
        else if (Kind is "send_text" or "paste") { }
        else if (Kind is "lua" or "activate_key_table") { if (string.IsNullOrWhiteSpace(Value) || Value.Length > 64) throw new ArgumentException("Action name must contain 1–64 characters."); }
        else if (Kind == "pop_key_table") { if (Value.Length != 0) throw new ArgumentException("pop_key_table takes no value."); }
        else if (Kind == "sequence") { if (Steps.Count == 0 || Steps.Any(s => s is null)) throw new ArgumentException("An action sequence cannot be empty or contain null steps."); foreach (var step in Steps) step.Validate(depth + 1); }
        else throw new ArgumentException("Unknown action kind: " + Kind);
        if (Kind != "sequence" && Steps.Count != 0) throw new ArgumentException("Only sequences contain steps.");
    }
}
public sealed class ActionBinding
{
    public string Key { get; set; } = "";
    public ActionSpec Action { get; set; } = new();
}
public sealed class LeaderSettings
{
    public string Key { get; set; } = "Ctrl+Alt+Space";
    public int TimeoutMs { get; set; } = 1000;
}
public sealed class KeyTableSettings
{
    public List<ActionBinding> Bindings { get; set; } = [];
    public int TimeoutMs { get; set; } = 1000;
    public bool OneShot { get; set; }
}

// Pure input state: no timers, IPC or renderer work on every keystroke.
public sealed class ActionBindingState
{
    private long _leaderUntil;
    private readonly Stack<(string Name, long Until)> _tables = new();
    private List<ActionBinding>? _bindings;
    private Dictionary<string,string>? _legacy;
    private Dictionary<string,KeyTableSettings>? _definitions;
    private LeaderSettings? _leader;
    private (uint Key,uint Modifiers)? _leaderChord;
    private readonly Dictionary<(bool Leader,uint Key,uint Modifiers),ActionSpec> _chords = new();
    private readonly Dictionary<string,Dictionary<(uint Key,uint Modifiers),ActionSpec>> _tableChords = new();
    public void Reset() { _leaderUntil = 0; _tables.Clear(); _bindings = null; }
    private void Compile(AppSettings settings)
    {
        if (ReferenceEquals(_bindings,settings.Bindings) && ReferenceEquals(_legacy,settings.Keybindings)
            && ReferenceEquals(_definitions,settings.KeyTables) && ReferenceEquals(_leader,settings.Leader)) return;
        _chords.Clear(); _tableChords.Clear();
        foreach (var (name,normal) in Keybindings.Defaults)
        {
            string chord=settings.Keybindings.GetValueOrDefault(name,normal); if(chord.Length==0) continue;
            var parsed=Keybindings.Parse(chord); _chords[(false,parsed.Key,parsed.Modifiers)]=new() { Value=name };
        }
        foreach(var binding in settings.Bindings)
        {
            bool leader=binding.Key.StartsWith("LEADER+",StringComparison.OrdinalIgnoreCase);
            var parsed=Keybindings.Parse(leader?binding.Key[7..]:binding.Key); _chords[(leader,parsed.Key,parsed.Modifiers)]=binding.Action;
        }
        foreach(var (name,table) in settings.KeyTables)
            _tableChords[name]=table.Bindings.ToDictionary(b=>Keybindings.Parse(b.Key),b=>b.Action);
        _leaderChord=settings.Leader is { } definition?Keybindings.Parse(definition.Key):null;
        _bindings=settings.Bindings; _legacy=settings.Keybindings; _definitions=settings.KeyTables; _leader=settings.Leader;
    }
    public string ActiveTable(long now) { while (_tables.TryPeek(out var table) && table.Until <= now) _tables.Pop(); return _tables.TryPeek(out var current) ? current.Name : ""; }
    public bool LeaderActive(long now) => _leaderUntil > now;
    public void Push(AppSettings settings, string name, long now)
    {
        if (!settings.KeyTables.TryGetValue(name, out var table)) throw new ArgumentException("Unknown key table: " + name);
        if (_tables.Count >= 8) throw new ArgumentException("Key table nesting exceeds eight levels.");
        _tables.Push((name, now + table.TimeoutMs));
    }
    public void Pop() { if (_tables.Count > 0) _tables.Pop(); }
    public bool Match(AppSettings settings, uint key, uint modifiers, long now, out ActionSpec? action)
    {
        action = null;
        Compile(settings);
        string table = ActiveTable(now);
        if (table.Length > 0)
        {
            if (key == 27 && modifiers == 0) { Pop(); return true; }
            var definition = settings.KeyTables[table];
            _tableChords[table].TryGetValue((key,modifiers&15),out action);
            if (action is not null) { if (definition.OneShot) Pop(); else { _tables.Pop(); _tables.Push((table, now + definition.TimeoutMs)); } return true; }
            return true; // Modal keys never leak into a shell.
        }
        bool leader = LeaderActive(now);
        if (leader) _leaderUntil = 0;
        if (!leader && settings.Leader is { } leaderSettings && _leaderChord == (key, modifiers & 15))
        { _leaderUntil = now + leaderSettings.TimeoutMs; return true; }
        _chords.TryGetValue((leader,key,modifiers&15),out action);
        return action is not null || leader;
    }
}
