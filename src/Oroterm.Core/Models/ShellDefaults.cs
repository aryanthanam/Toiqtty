namespace Oroterm.Models;

public static class ShellDefaults
{
    public const string LegacyCommand = "powershell.exe -NoLogo";
    public static string? FindPowerShell7() => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), "pwsh.exe"))
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"))
        .FirstOrDefault(File.Exists);
    public static string Command => FindPowerShell7() is { } path ? $"\"{path}\" -NoLogo" : LegacyCommand;
}
