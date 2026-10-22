namespace Toiqtty.Terminal;

/// <summary>OSC 7 locations are display metadata; remote paths are never local process directories.</summary>
public sealed record TerminalDirectory(string Display, string? LocalPath)
{
    internal const string TokenQuery = "?toiqtty-cwd=";

    /// <param name="requiredToken">For shells running toiqtty's integration: only a location carrying
    /// this session's token may become a local directory. A program inside the shell, such as ssh,
    /// can send any OSC 7; without the token its location stays display-only.</param>
    public static TerminalDirectory? Parse(string value, string command, string? requiredToken = null)
    {
        if (value.Length > 32768 || value.Any(char.IsControl)) return null;
        string? token = null;
        int marker = value.LastIndexOf(TokenQuery, StringComparison.Ordinal);
        if (marker >= 0) { token = value[(marker + TokenQuery.Length)..]; value = value[..marker]; }
        bool trusted = requiredToken is null || token == requiredToken;
        bool kitty = value.StartsWith("kitty-shell-cwd://", StringComparison.Ordinal);
        if (!kitty && !value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        // Kitty's cwd URI contains the literal (not percent-encoded) path.
        string host, path;
        if (kitty)
        {
            int slash = value.IndexOf('/', "kitty-shell-cwd://".Length);
            if (slash < 0) return null;
            host = value["kitty-shell-cwd://".Length..slash]; path = value[slash..];
        }
        else
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
            host = uri.Host; path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        if (path.Length == 0 || path.Any(char.IsControl) || host.Any(c => char.IsControl(c) || c is '\\' or '/' or '@' or ':')) return null;
        bool localHost = host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        bool wsl = ShellIntegration.IsWsl(command);
        if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\')
        {
            string windows = path.Replace('/', '\\');
            return !localHost ? new(host + ":" + path, null) : trusted ? new(windows, windows) : new(windows, null);
        }
        if (!path.StartsWith('/')) return null;
        if (!localHost) return new(host + ":" + path, null);
        if (!trusted) return new(path, null);
        if (wsl && path.Length >= 7 && path.StartsWith("/mnt/", StringComparison.Ordinal)
            && char.IsAsciiLetter(path[5]) && path[6] == '/')
            return new(path, char.ToUpperInvariant(path[5]) + ":" + path[6..].Replace('/', '\\'));
        if (wsl && ShellIntegration.WslDistribution(command) is { Length: > 0 } distro
            && !distro.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '.' ))
            return new(path, @"\\wsl.localhost\" + distro + path.Replace('/', '\\'));
        return new(path, null);
    }
}
