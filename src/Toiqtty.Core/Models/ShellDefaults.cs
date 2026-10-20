namespace Toiqtty.Models;

public static class ShellDefaults
{
    public const string LegacyCommand = "powershell.exe -NoLogo";
    // Settings/layout deserialization constructs many default objects. Resolve
    // their fallback once per process rather than probing PATH for each one.
    private static readonly Lazy<string> DefaultCommand = new(() => FindPowerShell7() is { } path ? $"\"{path}\" -NoLogo" : LegacyCommand);
    public static string? FindPowerShell7() => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), "pwsh.exe"))
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"))
        .FirstOrDefault(File.Exists);
    public static string Command => DefaultCommand.Value;
}
