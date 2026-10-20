namespace Toiqtty.Models;

public sealed class BrowserSettings
{
    public string HomePage { get; set; } = "about:blank";
    public string DefaultSourceId { get; set; } = "";
    public bool OpenTerminalLinks { get; set; } = true;
}

public static class BrowserAddress
{
    public static bool IsAllowed(string? value) => value is { Length: <= 8192 } && (value == "about:blank" ||
        (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)));

    public static string Resolve(string value)
    {
        value = value.Trim();
        if (value.Length > 8192) throw new ArgumentException("The address is too long.");
        if (value.Length == 0) return "about:blank";
        if (IsAllowed(value)) return value;
        bool local = Uri.TryCreate("http://" + value, UriKind.Absolute, out var localUri) && localUri.IsLoopback;
        if (!value.Any(char.IsWhiteSpace) && (value.Contains('.') || local))
        {
            var candidate = (local ? "http://" : "https://") + value;
            if (IsAllowed(candidate)) return candidate;
        }
        if (value.Contains("://") || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use an HTTP or HTTPS address.");
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(value);
    }

    public static string Origin(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Enter site origins such as https://github.com, one per line.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string ProfileName(string source) => source.Length == 0 ? "Toiqtty" :
        "Source_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))[..32];
}
