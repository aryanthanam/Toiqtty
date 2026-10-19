using System.Text.Json;

namespace Oroterm.Models;

public sealed class AppSettings
{
    public SessionTheme? TerminalTheme { get; set; }
    internal AppSettings WithTerminalTheme(SessionTheme? theme) { var copy = (AppSettings)MemberwiseClone(); copy.TerminalTheme = theme ?? TerminalTheme; return copy; }
    public Dictionary<string, SessionTheme> ColorSchemes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CursorSettings Cursor { get; set; } = new();
    public WallpaperSettings Wallpaper { get; set; } = new();
    public VisualSettings Appearance { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public List<ActionBinding> Bindings { get; set; } = [];
    public LeaderSettings? Leader { get; set; }
    public Dictionary<string, KeyTableSettings> KeyTables { get; set; } = new();
    public string Shell { get; set; } = ShellDefaults.Command;
    public string StartDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string FontFamily { get; set; } = TerminalFont.BundledFamily;
    public float FontSize { get; set; } = 14;
    public ushort FontWeight { get; set; } = 400;
    // Legacy settings/config field, accepted for compatibility. Cold startup
    // never recreates disk layouts; only a live host can restore its sessions.
    public bool RestoreWorkspaces { get; set; }
    public bool AllowClipboardRead { get; set; }
    public bool AllowClipboardWrite { get; set; } = true;
    public bool Ligatures { get; set; } = true;
    public long? ScrollbackLimitBytes { get; set; } = 50_000_000;
    public long? ScrollbackLimitLines { get; set; }
    public bool DesktopNotifications { get; set; } = true;
    public Dictionary<string, string> Keybindings { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();
    public StatusBarSettings StatusBar { get; set; } = new();
}
public sealed class SavedSession
{
    public int Version { get; set; } = 5;
    public string? SelectedWorkspace { get; set; }
    public List<Workspace> Workspaces { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}
public static class SessionStore
{
    public static string Folder => Environment.GetEnvironmentVariable("OROTERM_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oroterm");
    public static string FilePath => Path.Combine(Folder, "session.json");
    public static SavedSession Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            if (new FileInfo(FilePath).Length > 1024 * 1024) throw new InvalidDataException("Session file exceeds the limit.");
            var session = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(FilePath)) ?? new();
            if (session.Version is not (1 or 2 or 3 or 4 or 5)) throw new InvalidDataException("Unknown session version.");
            session.Settings ??= new();
            if (session.Version == 1 && session.Settings.FontFamily == "Cascadia Mono")
                session.Settings.FontFamily = TerminalFont.BundledFamily;
            if (session.Version < 4 && session.Settings.Shell == ShellDefaults.LegacyCommand)
                session.Settings.Shell = ShellDefaults.Command;
            bool migratePaneShells = session.Version < 4;
            session.Version = 5;
            session.Settings.FontSize = float.IsFinite(session.Settings.FontSize) ? Math.Clamp(session.Settings.FontSize, 9, 32) : 14;
            if (session.Settings.ScrollbackLimitBytes is < 0) session.Settings.ScrollbackLimitBytes = 50_000_000;
            if (session.Settings.ScrollbackLimitLines is < 0) session.Settings.ScrollbackLimitLines = null;
            session.Settings.Keybindings ??= new();
            session.Settings.Browser ??= new();
            session.Settings.StatusBar ??= new();
            session.Settings.Cursor ??= new(); session.Settings.Wallpaper ??= new(); session.Settings.ColorSchemes ??= new();
            session.Settings.Appearance ??= new(); session.Settings.Window ??= new(); session.Settings.Bindings ??= []; session.Settings.KeyTables ??= new();
            try { session.Settings.Appearance.Validate(); } catch (ArgumentException) { session.Settings.Appearance = new(); }
            try { session.Settings.Window.Validate(); } catch (ArgumentException) { session.Settings.Window = new(); }
            if (session.Settings.FontWeight is < 100 or > 900) session.Settings.FontWeight=400;
            if (session.Settings.TerminalTheme is { IsValid: false }) session.Settings.TerminalTheme=null;
            try { session.Settings.Cursor.Validate(); } catch(ArgumentException) { session.Settings.Cursor=new(); }
            try { session.Settings.Wallpaper.Validate(); } catch(ArgumentException) { session.Settings.Wallpaper=new(); }
            try { session.Settings.StatusBar.Validate(); } catch (ArgumentException) { session.Settings.StatusBar = new(); }
            if (!BrowserAddress.IsAllowed(session.Settings.Browser.HomePage)) session.Settings.Browser.HomePage = "about:blank";
            if (session.Settings.Browser.DefaultSourceId is null || session.Settings.Browser.DefaultSourceId.Length > 128) session.Settings.Browser.DefaultSourceId = "";
            if (string.IsNullOrWhiteSpace(session.Settings.Shell)) session.Settings.Shell = ShellDefaults.Command;
            if (string.IsNullOrWhiteSpace(session.Settings.FontFamily)) session.Settings.FontFamily = TerminalFont.BundledFamily;
            if (!Directory.Exists(session.Settings.StartDirectory)) session.Settings.StartDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (session.Workspaces.Count > 64) throw new InvalidDataException("Too many workspaces.");
            var identities = new HashSet<string>();
            int panes = 0;
            foreach (var workspace in session.Workspaces)
            {
                if (workspace.Theme is { IsValid: false }) workspace.Theme = null;
                if (string.IsNullOrWhiteSpace(workspace.Id) || !identities.Add(workspace.Id)) throw new InvalidDataException("Duplicate workspace identity.");
                if (workspace.Tabs is not { Count: > 0 and <= 64 }) throw new InvalidDataException("Invalid tab count.");
                foreach (var tab in workspace.Tabs)
                {
                    if (string.IsNullOrWhiteSpace(tab.Id) || !identities.Add(tab.Id)) throw new InvalidDataException("Invalid tab identity.");
                    Validate(tab.Root, 0);
                }
                foreach (var pane in workspace.AllPanes())
                {
                    if (++panes > 256 || string.IsNullOrWhiteSpace(pane.Id) || !identities.Add(pane.Id)) throw new InvalidDataException("Invalid pane identities or count.");
                    if (migratePaneShells && pane.Command == ShellDefaults.LegacyCommand)
                        pane.Command = ShellDefaults.Command;
                    if (!Directory.Exists(pane.Directory)) pane.Directory = session.Settings.StartDirectory;
                }
            }
            return session;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NullReferenceException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return new();
        }
    }
    private static void Validate(PaneNode node, int depth)
    {
        if (depth > 12 || node is null || string.IsNullOrWhiteSpace(node.Command) || node.Kind is not ("terminal" or "browser") ||
            node.BrowserSourceId is null || node.BrowserSourceId.Length > 128 || (node.IsBrowser && !BrowserAddress.IsAllowed(node.BrowserUrl))) throw new InvalidDataException("Invalid pane layout.");
        node.Ratio = double.IsFinite(node.Ratio) ? Math.Clamp(node.Ratio, .1, .9) : .5;
        if (node.First is not null || node.Second is not null)
        {
            if (node.First is null || node.Second is null) throw new InvalidDataException("Incomplete split.");
            Validate(node.First, depth + 1); Validate(node.Second, depth + 1);
        }
    }
    public static void Save(SavedSession session)
    {
        Directory.CreateDirectory(Folder);
        foreach (var pane in session.Workspaces.SelectMany(w => w.AllPanes()))
            pane.Directory = pane.Session?.WorkingDirectory ?? pane.Directory;
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, session, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
