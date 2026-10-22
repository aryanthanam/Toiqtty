using System.Text.RegularExpressions;

namespace Toiqtty.Terminal;

public static partial class ShellIntegration
{
    public static bool IsWsl(string command) => WslCommand().IsMatch(command);
    internal static string? WslDistribution(string command)
    {
        if (!IsWsl(command)) return null;
        var match = WslDistributionOption().Match(command);
        if (match.Success) return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (root?.GetValue("DefaultDistribution") is not string id) return null;
        using var distro = root.OpenSubKey(id);
        return distro?.GetValue("DistributionName") as string;
    }

    // Quote CreateProcess argv (not cmd.exe syntax). No intermediate Windows
    // shell interprets $, &, quotes or backticks in the integration path.
    private static string WindowsArgument(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static string QuoteDroppedPaths(string command, IEnumerable<string> paths)
    {
        string program = Path.GetFileNameWithoutExtension(CommandProgram(command)).ToLowerInvariant();
        bool cmd = program == "cmd";
        bool wsl = program == "wsl";
        bool posix = wsl || program is "bash" or "zsh" or "sh";
        if (wsl) paths = paths.Select(path => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            ? "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/') : path);
        return string.Join(' ', paths.Select(path => cmd ? "\"" + path + "\"" : "'" + (posix ? path.Replace("'", "'\\''") : QuotePowerShellLiteral(path)) + "'")) + " ";
    }
    private static string CommandProgram(string command)
    {
        string text = command.TrimStart();
        if (text.StartsWith('"')) { int end = text.IndexOf('"', 1); return end < 0 ? text[1..] : text[1..end]; }
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
    // PowerShell also ends single-quoted strings at ‘ ’ ‚ ‛; doubling each one
    // keeps it literal, so a dropped file name cannot terminate the string.
    private static string QuotePowerShellLiteral(string text)
    {
        var result = new System.Text.StringBuilder(text.Length + 2);
        foreach (char c in text) { result.Append(c); if (c is '\'' or '‘' or '’' or '‚' or '‛') result.Append(c); }
        return result.ToString();
    }
    public const string DirectoryTokenVariable = "TOIQTTY_CWD_TOKEN";

    public static string CreateEnvironmentBlock(IEnumerable<KeyValuePair<string, string>> inherited, string? directoryToken = null)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inherited) values[entry.Key] = entry.Value;
        values.Remove(DirectoryTokenVariable);
        if (directoryToken is not null)
        {
            // WSLENV carries the token into WSL for the Bash/Zsh adapters.
            values[DirectoryTokenVariable] = directoryToken;
            string wsl = values.GetValueOrDefault("WSLENV", "");
            if (!wsl.Split(':').Any(item => item.Split('/')[0].Equals(DirectoryTokenVariable, StringComparison.OrdinalIgnoreCase)))
                values["WSLENV"] = (wsl.Length == 0 ? "" : wsl + ":") + DirectoryTokenVariable + "/u";
        }
        // A new terminal must advertise its own capabilities before shell startup.
        // In particular, PowerShell caches TERM=dumb as no VT support.
        values["TERM"] = "xterm-256color";
        values["COLORTERM"] = "truecolor";
        values["TERM_PROGRAM"] = "toiqtty";
        values["TERM_PROGRAM_VERSION"] = Toiqtty.Models.BuildInfo.Version;
        return string.Join('\0', values.Select(entry => entry.Key + "=" + entry.Value)) + "\0\0";
    }

    public static string PrepareCommand(string command)
    {
        // Only augment interactive PowerShell. Explicit scripts/commands retain
        // their exact invocation, and no user profile is written or replaced.
        var match = PowerShellCommand().Match(command);
        if (match.Success)
        {
            if (!PowerShellInteractiveOptions().IsMatch(match.Groups[2].Value)) return command;
            var script = Path.Combine(AppContext.BaseDirectory, "Integration", "powershell.ps1");
            if (!File.Exists(script)) return command;
            return command + " -NoExit -Command " + WindowsArgument(". '" + script.Replace("'", "''") + "'");
        }
        return PreparePosix(command);
    }

    private static string PreparePosix(string command)
    {
        // Explicitly inherited Bash modes/options retain their exact semantics.
        if (Environment.GetEnvironmentVariable("POSIXLY_CORRECT") is not null
            || Environment.GetEnvironmentVariable("BASHOPTS") is not null
            || Environment.GetEnvironmentVariable("SHELLOPTS") is not null) return command;
        var wsl = InteractiveWsl().Match(command);
        var direct = InteractivePosix().Match(command);
        if (!wsl.Success && !direct.Success) return command;
        var parsed = wsl.Success ? wsl : direct;
        string shell = parsed.Groups["shell"].Value.Trim('"');
        string[] flags = Regex.Matches(parsed.Groups["flags"].Value, @"\S+").Select(m => m.Value).ToArray();
        if (flags.Any(flag => flag is not ("-i" or "-l" or "-il" or "-li" or "--login" or "--norc" or "--noprofile"))) return command;
        if (shell.Length == 0 && flags.Length != 0) return command;
        if (Path.GetFileNameWithoutExtension(shell).Equals("zsh", StringComparison.OrdinalIgnoreCase)
            && flags.Any(flag => flag is "--norc" or "--noprofile")) return command;
        string launcher = Path.Combine(AppContext.BaseDirectory, "Integration", "posix", "launch-posix.sh");
        if (!File.Exists(launcher)) return command;
        string entry = wsl.Success ? parsed.Groups["prefix"].Value + " --exec /bin/sh" : parsed.Groups["shell"].Value;
        string bridge = wsl.Success
            ? "file=$(wslpath -u \"$1\") || exit; shift; exec /bin/sh \"$file\" \"$@\""
            : "file=$1; shift; if command -v cygpath >/dev/null 2>&1; then file=$(cygpath -u \"$file\") || exit; fi; exec /bin/sh \"$file\" \"$@\"";
        return entry + " -c " + WindowsArgument(bridge) + " toiqtty " + WindowsArgument(launcher.Replace('\\', '/'))
            + " " + WindowsArgument(shell.Replace('\\', '/')) + string.Concat(flags.Select(flag => " " + WindowsArgument(flag)));
    }
    [GeneratedRegex("^\\s*(\"(?:[^\"]*[\\\\/])?(?:pwsh|powershell)(?:\\.exe)?\"|(?:[^\\s]*[\\\\/])?(?:pwsh|powershell)(?:\\.exe)?)(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellCommand();
    [GeneratedRegex("^(?:\\s+-(?:NoLogo|NoProfile|NoExit|Sta|Mta|Login)|\\s+-(?:ExecutionPolicy|ep|WorkingDirectory|wd)\\s+(?:\"[^\"]*\"|\\S+))*\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellInteractiveOptions();
    [GeneratedRegex("^\\s*(?:\"[^\"]*[\\\\/]wsl(?:\\.exe)?\"|\"wsl(?:\\.exe)?\"|(?:[^\\s]*[\\\\/])?wsl(?:\\.exe)?)(?:\\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WslCommand();
    [GeneratedRegex("(?:^|\\s)(?:-d|--distribution)\\s+(?:\"([^\"]+)\"|(\\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex WslDistributionOption();
    [GeneratedRegex("""^\s*(?<prefix>(?:"(?:[^"]*[\\/])?wsl(?:\.exe)?"|(?:[^\s]*[\\/])?wsl(?:\.exe)?)(?:\s+(?:-d|--distribution|-u|--user|--cd)\s+(?:"[^"]+"|[^\s]+))*)(?:\s+(?:--exec|-e|--)\s+(?<shell>"(?:[^"]*/)?(?:bash|zsh)"|(?:/[^\s]+/)?(?:bash|zsh)))?(?<flags>(?:\s+[^\s]+)*)\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex InteractiveWsl();
    [GeneratedRegex("""^\s*(?<shell>"(?:[^"]*[\\/])?(?:bash|zsh)(?:\.exe)?"|(?:[^\s]*[\\/])?(?:bash|zsh)(?:\.exe)?)(?<flags>(?:\s+[^\s]+)*)\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex InteractivePosix();
}
