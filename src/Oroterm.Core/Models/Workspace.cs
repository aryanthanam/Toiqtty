using Oroterm.Terminal;

namespace Oroterm.Models;

public sealed class PaneNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = ShellDefaults.Command;
    public string Directory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string Kind { get; set; } = "terminal";
    public string BrowserUrl { get; set; } = "about:blank";
    public string BrowserSourceId { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsBrowser => Kind == "browser";
    public bool Vertical { get; set; } = true;
    public double Ratio { get; set; } = .5;
    public PaneNode? First { get; set; }
    public PaneNode? Second { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public ITerminalSession? Session { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Attention { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsLeaf => First is null || Second is null;
    public IEnumerable<PaneNode> Leaves() => IsLeaf ? [this] : First!.Leaves().Concat(Second!.Leaves());
    public bool Split(string id, bool vertical, string command, PaneNode? added = null)
    {
        if (IsLeaf)
        {
            if (Id != id) return false;
            First = new PaneNode { Id = Id, Command = Command, Directory = Session?.WorkingDirectory ?? Directory, Session = Session, Attention = Attention,
                Kind = Kind, BrowserUrl = BrowserUrl, BrowserSourceId = BrowserSourceId };
            Second = added ?? new PaneNode { Command = command, Directory = First.Directory };
            Id = Guid.NewGuid().ToString("N"); Session = null; Kind = "terminal"; Vertical = vertical; Ratio = .5;
            return true;
        }
        return First!.Split(id, vertical, command, added) || Second!.Split(id, vertical, command, added);
    }
    public static PaneNode? Remove(PaneNode node, string id)
    {
        if (node.IsLeaf)
        {
            if (node.Id != id) return node;
            return null;
        }
        var first = Remove(node.First!, id); var second = Remove(node.Second!, id);
        if (first is null) return second;
        if (second is null) return first;
        node.First = first; node.Second = second; return node;
    }
}

public sealed class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Workspace";
    public SessionTheme? Theme { get; set; }
    public List<TerminalTab> Tabs { get; set; } = [new()];
    public string? ActiveTabId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public TerminalTab ActiveTab => Tabs.FirstOrDefault(t => t.Id == ActiveTabId) ?? Tabs.First();
    [System.Text.Json.Serialization.JsonIgnore] public PaneNode Root { get => ActiveTab.Root; set => ActiveTab.Root = value; }
    [System.Text.Json.Serialization.JsonIgnore] public string? FocusedPaneId { get => ActiveTab.FocusedPaneId; set => ActiveTab.FocusedPaneId = value; }
    // Read the old one-layout workspace format; new saves contain tabs only.
    [System.Text.Json.Serialization.JsonPropertyName("Root")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PaneNode? LegacyRoot { get => null; set { if (value is not null) Root = value; } }
    [System.Text.Json.Serialization.JsonPropertyName("FocusedPaneId")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyFocus { get => null; set { if (value is not null) FocusedPaneId = value; } }
    public IEnumerable<PaneNode> AllPanes() => Tabs.SelectMany(t => t.Root.Leaves());
    [System.Text.Json.Serialization.JsonIgnore] public bool Attention { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Branch { get; set; } = "";
}

public sealed class TerminalTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Shell";
    public PaneNode Root { get; set; } = new();
    public string? FocusedPaneId { get; set; }
}
