using Toiqtty.Terminal;
using System.Diagnostics;

internal static class InteropChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string? distro = Environment.GetEnvironmentVariable("TOIQTTY_WSL_DISTRO");
        if (string.IsNullOrWhiteSpace(distro)) { Console.WriteLine("SKIP: optional WSL/Kitty/tmux matrix (scripts/test-interop.ps1)."); return; }
        if (distro.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')) throw new ArgumentException("Unsupported distro name.");
        string root = Environment.CurrentDirectory;
        string Linux(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
        string kitten = Linux(Path.Combine(root, ".tools", "kitten-linux-amd64"));
        string tmux = Linux(Path.Combine(root, ".tools", "tmux-3.8", "tmux"));
        string folder = Path.Combine(root, "artifacts", "interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string png = Path.Combine(folder, "red.png");
        File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
        // WSL's console bridge does not populate TIOCGWINSZ pixel fields.
        string command = Quote(kitten) + " icat --transfer-mode=stream --use-window-size=100,30,1000,600 --stdin=no --place=4x2@0x0 " + Quote(Linux(png));
        string socket = "toiqtty-" + Guid.NewGuid().ToString("N");
        string config = Path.Combine(folder, "tmux.conf");
        // Placeholder image IDs use exact RGB values. xterm-256color alone
        // permits tmux to quantize them into palette indices, losing that ID.
        File.WriteAllText(config, "set -g allow-passthrough on\nset -as terminal-features ',*:RGB'\nset -g default-terminal tmux-256color\nset -g status off\n");
        async Task Exercise(string invocation, bool multiplexer)
        {
            await using var session = new TerminalSession(invocation, folder);
            session.Resize(100, 30, 10, 20);
            var cells = new TerminalCell[100 * 30];
            async Task<bool> Wait(Func<TerminalSnapshot, bool> predicate)
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    var snapshot = session.ReadSnapshot(cells);
                    if (predicate(snapshot)) return true;
                    await Task.Delay(30);
                }
                Console.WriteLine(string.Concat(cells.Select(cell => cell.GetText())));
                return false;
            }
            await Task.Delay(1200);
            string suffix="READY_"+Guid.NewGuid().ToString("N"),ready="TOIQTTY_WSL_"+suffix;
            session.Key(0, 0, "printf '%s%s\\n' 'TOIQTTY_WSL_' '"+suffix+"'"); session.Key(13, 0);
            // The echo separates these strings. Only execution concatenates
            // the marker; unlike an OSC title this also crosses tmux unchanged.
            check(await Wait(_ => string.Concat(cells.Select(cell=>cell.GetText())).Contains(ready)),
                multiplexer ? "real tmux shell accepts input through WSL and ConPTY" : "real WSL bash accepts input through ConPTY");
            // Clear the echoed command before inspecting the actual rendered image.
            session.Key(0, 0, "export PATH=" + Quote(Path.GetDirectoryName(tmux)!.Replace('\\', '/')) + ":$PATH; clear; " + command + "; printf '\\033[10;1H'"); session.Key(13, 0);
            check(await Wait(snapshot => snapshot.Graphics.Count > 0),
                multiplexer ? "real Kitty icat negotiates tmux passthrough and renders Unicode-placeholder images" : "real Kitty icat negotiates direct streaming graphics over WSL/ConPTY");
            session.Resize(90, 28, 9, 20); cells = new TerminalCell[90 * 28];
            check(await Wait(snapshot => snapshot.Frame.Columns == 90 && snapshot.Graphics.Count > 0),
                multiplexer ? "tmux placeholder placements survive a real pseudoconsole resize" : "WSL Kitty placements survive a real pseudoconsole resize");
        }
        try
        {
            await Exercise($"wsl.exe -d {distro} --exec bash --noprofile --norc", false);
            await Exercise($"wsl.exe -d {distro} --exec {tmux} -vv -L {socket} -f {Linux(config)} new-session -- bash --noprofile --norc", true);
        }
        finally
        {
            // Only the isolated server created by this test may be stopped.
            var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-d", distro, "--exec", tmux, "-L", socket, "kill-server" }) start.ArgumentList.Add(argument);
            using var cleanup = Process.Start(start)!;
            await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
