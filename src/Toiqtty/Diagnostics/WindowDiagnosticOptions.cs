using Toiqtty.Models;

namespace Toiqtty.Diagnostics;

/// <summary>One startup-mode decision shared by application composition and the window.</summary>
internal sealed record WindowDiagnosticOptions(bool Browser, bool Config, bool Design, bool Dpi, bool Startup, bool Hardware, bool Ui,
    string? SessionProbe, string? BrowserProbe, string? ConfigProbe)
{
    internal bool Persistent => (!Ui && !Hardware) || SessionProbe is not null || BrowserProbe is not null || ConfigProbe is not null;
    internal bool LuaEnabled => (!Ui && !Hardware) || Config;

    /// <summary>A validation mode was requested from a product build without diagnostics.</summary>
    internal static string? Unavailable()
    {
#if DIAGNOSTICS
        return null;
#else
        string[] modes = ["--ui-smoke", "--directx-smoke", "--browser-smoke", "--config-smoke", "--design-smoke", "--dpi-smoke", "--hardware-check", "--notification-probe"];
        string[] probes = ["TOIQTTY_SESSION_UI_PROBE", "TOIQTTY_BROWSER_UI_PROBE", "TOIQTTY_CONFIG_UI_PROBE"];
        string? requested = Environment.GetCommandLineArgs().FirstOrDefault(modes.Contains)
            ?? probes.FirstOrDefault(name => Environment.GetEnvironmentVariable(name) is not null);
        return requested is null ? null : $"This toiqtty build has no validation drivers ({requested} requested). Publish with scripts/publish.ps1 -Diagnostics.";
#endif
    }

    internal static WindowDiagnosticOptions Read()
    {
        var args = Environment.GetCommandLineArgs().ToHashSet(StringComparer.Ordinal);
        bool browser = args.Contains("--browser-smoke"), config = args.Contains("--config-smoke"), design = args.Contains("--design-smoke"), dpi = args.Contains("--dpi-smoke");
        return new(browser, config, design, dpi, args.Contains("--startup-probe"), args.Contains("--hardware-check"),
            args.Contains("--ui-smoke") || args.Contains("--directx-smoke") || browser || config || design || dpi,
            Environment.GetEnvironmentVariable("TOIQTTY_SESSION_UI_PROBE"), Environment.GetEnvironmentVariable("TOIQTTY_BROWSER_UI_PROBE"),
            Environment.GetEnvironmentVariable("TOIQTTY_CONFIG_UI_PROBE"));
    }

    internal void PrepareFixture(SavedSession saved)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(SessionStore.Folder));
        if (Startup && (!StartupTrace.Enabled || !name.StartsWith("startup-check-", StringComparison.Ordinal)))
            throw new InvalidOperationException("Startup probes require tracing and an isolated startup-check-* directory.");
        if (!Ui && !Hardware) return;
        if (Environment.GetEnvironmentVariable("TOIQTTY_DATA_DIR") is null)
            throw new InvalidOperationException("Validation requires an isolated TOIQTTY_DATA_DIR.");
        if (Design && !name.StartsWith("design-", StringComparison.Ordinal)) throw new InvalidOperationException("Design validation requires a dedicated design-* directory.");
        if (Dpi && !name.StartsWith("dpi-check-", StringComparison.Ordinal)) throw new InvalidOperationException("DPI validation requires a dedicated dpi-check-* directory.");
        if (Hardware && !name.StartsWith("hardware-check-", StringComparison.Ordinal)) throw new InvalidOperationException("Hardware checks require a dedicated hardware-check-* directory.");
        if (SessionProbe is not ("reconnect" or "navigation-reconnect") && BrowserProbe != "reconnect" && ConfigProbe != "reconnect") saved.Workspaces.Clear();
        if (SessionProbe is null) saved.Settings.Shell = "cmd.exe /Q /K";
        saved.Settings.StartDirectory = Environment.CurrentDirectory;
    }
}
