using Oroterm.Models;
using Oroterm.Terminal;

internal static class ShellIntegrationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (string invocation in new[] { "pwsh.exe -c echo hi", "pwsh.exe -Co echo hi", "pwsh.exe -File script.ps1", "pwsh.exe script.ps1", "pwsh.exe -NonInteractive", "pwsh.exe -EncodedCommand AAA=", "wsl.exe -d archlinux --exec bash -c echo", "wsl.exe --exec python", "wsl.exe -l", "wsl.exe --list", "bash.exe script.sh", "zsh.exe -fc true", "\"notbash.exe\"", "\"notwsl.exe\"", "bash.exe --posix", "bash.exe --rcfile custom.bash", "wsl.exe --shell-type login" })
            check(ShellIntegration.PrepareCommand(invocation) == invocation, "explicit shell invocation is unchanged: " + invocation);
        foreach (string invocation in new[] { "wsl.exe", "wsl.exe -d archlinux --exec bash --norc", "wsl.exe -u test --cd /tmp --exec zsh -il", "bash.exe -i", "zsh.exe -l" })
            check(ShellIntegration.PrepareCommand(invocation).Contains("launch-posix.sh"), "interactive POSIX launch uses Oroterm's own adapters: " + invocation);
        check(TerminalDirectory.Parse("file:///D:/a%20b/%E4%B8%AD", "pwsh.exe")?.LocalPath == "D:\\a b\\中", "Windows OSC 7 paths decode Unicode and spaces");
        check(TerminalDirectory.Parse("file://ssh-host/home/test", "pwsh.exe") == new TerminalDirectory("ssh-host:/home/test", null), "remote file authorities stay display-only");
        check(TerminalDirectory.Parse("kitty-shell-cwd://ssh-host/home/100%20 literal", "wsl.exe")?.Display == "ssh-host:/home/100%20 literal", "Kitty cwd paths preserve literal percent sequences");
        check(TerminalDirectory.Parse("file:///mnt/d/repo", "wsl.exe")?.LocalPath == "D:\\repo", "local WSL drive mounts map to Windows working directories");
        check(TerminalDirectory.Parse("file://ssh-host/D:/wrong", "pwsh.exe")?.LocalPath is null
            && TerminalDirectory.Parse("https://site/path", "pwsh.exe") is null
            && TerminalDirectory.Parse("file:///D:/bad%00path", "pwsh.exe") is null, "remote and invalid cwd metadata cannot become a Windows process directory");
        check(TerminalDirectory.Parse("file:///home/me", "wsl.exe -d archlinux")?.LocalPath == @"\\wsl.localhost\archlinux\home\me", "local WSL Linux directories map through the selected distro's filesystem");
        using var session = new TerminalSession(ShellDefaults.Command + " -NoProfile", Environment.CurrentDirectory);
        async Task<bool> Wait(Func<bool> condition, int seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (condition()) return true; await Task.Delay(30); }
            var cells = new TerminalCell[3000]; session.ReadFrame(cells);
            Console.WriteLine(string.Concat(cells.Select(c => c.GetText()))); return false;
        }
        check(await Wait(() => session.Title != "Terminal" || session.Version > 0), "integrated PowerShell starts through ConPTY");
        // Wait for the prompt's cwd report, not merely the PowerShell banner.
        await Task.Delay(700);
        async Task<TerminalCommand> Command(string command)
        {
            int before = session.Status.Commands.Count;
            session.Paste(command); session.Key(13, 0);
            check(await Wait(() => session.Status.Commands.Count > before && session.Status.Commands[^1].Finished is not null), "PowerShell command completes: " + command);
            return session.Status.Commands[^1];
        }
        var success = await Command("Write-Output 'café 中; %'");
        check(success.Text == "Write-Output 'café 中; %'" && success.ExitCode == 0 && success.Duration >= TimeSpan.Zero, "PowerShell command metadata round-trips Unicode, delimiters, exit status and duration");
        var longCommand = await Command("$null = '" + new string('中', 700) + "'");
        check(longCommand.ExitCode == 0 && longCommand.Text.Length < 600 && !longCommand.Text.Contains('\ufffd'),
            "long Unicode commands keep valid bounded metadata while the full command executes");
        var nativeFailure = await Command("cmd.exe /c exit 7");
        check(nativeFailure.ExitCode == 7, "PowerShell preserves native failure exit codes");
        var cmdletFailure = await Command("Write-Error 'expected test failure'");
        check(cmdletFailure.ExitCode == 1, "PowerShell cmdlet failure does not inherit an earlier native exit code");
        check((await Command("Write-Output 'recovered'")).ExitCode == 0, "PowerShell successful cmdlet clears command failure without changing LASTEXITCODE");
        session.Paste("Start-Sleep -Seconds 2"); session.Key(13, 0);
        check(await Wait(() => session.Status.CommandRunning), "PowerShell marks a running command before its output finishes");
        check(await Wait(() => !session.Status.CommandRunning) && session.Status.Commands[^1].Duration?.TotalSeconds >= 1.5, "PowerShell records the elapsed duration of a real command");
        await Command(". '" + Path.Combine(AppContext.BaseDirectory, "Integration", "powershell.ps1").Replace("'", "''") + "'");
        // Re-sourcing does not wrap the line reader twice, even after a user replaces prompt.
        check(session.Status.Commands.Count < 20, "PowerShell integration does not duplicate command starts");
        await Command("$global:OrotermOriginalPrompt = { $ok=$?; 'CUSTOM:' + $ok + ':' + $global:LASTEXITCODE + '> ' }");
        await Command("cmd.exe /c exit 9");
        var promptCells = new TerminalCell[3000];
        // The completion marker can arrive before the prompt's later write.
        check(await Wait(()=> { session.ReadFrame(promptCells); return string.Concat(promptCells.Select(c=>c.GetText())).Contains("CUSTOM:False:9>"); }), "custom PowerShell prompts receive the original failure flag and native exit code");
        await Command("Set-StrictMode -Version Latest; Write-Output 'strict'");
        check((await Command("Write-Output 'still strict'")).ExitCode == 0, "integration works when the user enables strict mode");
        session.Paste("exit 0"); session.Key(13, 0);
        check(await Wait(() => session.HasExited) && session.Status.Commands[^1].Finished is not null && session.Status.Commands[^1].ExitCode == 0,
            "shell exit completes the final command when no next prompt is produced");
        using (var legacy = new TerminalSession("powershell.exe -NoLogo -NoProfile", Environment.CurrentDirectory))
        {
            await Task.Delay(900); legacy.Paste("Write-Output 'legacy'"); legacy.Key(13, 0);
            var until = DateTime.UtcNow.AddSeconds(15);
            while ((legacy.Status.Commands.Count == 0 || legacy.Status.Commands[^1].Finished is null) && DateTime.UtcNow < until) await Task.Delay(30);
            check(legacy.Status.Commands.Count == 1 && legacy.Status.LastExitCode == 0, "Windows PowerShell 5.1 receives automatic command integration");
        }
        string strictScript = "Set-StrictMode -Version Latest; . '" + Path.Combine(AppContext.BaseDirectory, "Integration", "powershell.ps1").Replace("'", "''") + "'";
        string encodedStrict = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(strictScript));
        using (var strict = new TerminalSession(ShellDefaults.Command + " -NoProfile -NoExit -EncodedCommand " + encodedStrict, Environment.CurrentDirectory))
        {
            await Task.Delay(900); strict.Paste("Write-Output 'strict startup'"); strict.Key(13, 0);
            var until = DateTime.UtcNow.AddSeconds(15);
            while ((strict.Status.Commands.Count == 0 || strict.Status.Commands[^1].Finished is null) && DateTime.UtcNow < until) await Task.Delay(30);
            check(strict.Status.Commands.Count == 1 && strict.Status.LastExitCode == 0, "integration initializes when a profile already enabled strict mode");
        }
        string? distro = Environment.GetEnvironmentVariable("OROTERM_WSL_DISTRO");
        if (!string.IsNullOrEmpty(distro)) await Posix(check, distro);
    }

    private static async Task Posix(Action<bool, string> check, string distro)
    {
        if (distro.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')) throw new ArgumentException("Invalid distro");
        async Task Exercise(string invocation, string name, bool commandText = false, string? prompt = null)
        {
            using var session = new TerminalSession(invocation, Environment.CurrentDirectory);
            var cells = new TerminalCell[3000];
            async Task<bool> Wait(Func<bool> predicate)
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline) { if (predicate()) return true; await Task.Delay(30); }
                session.ReadFrame(cells); Console.WriteLine(string.Concat(cells.Select(c => c.GetText()))); return false;
            }
            check(await Wait(() => session.Directory.StartsWith('/')), name + ": startup cwd reaches libghostty over WSL/ConPTY");
            if (prompt is not null) check(await Wait(() => { session.ReadFrame(cells); return string.Concat(cells.Select(c => c.GetText())).Contains(prompt); }), name + ": user ZDOTDIR and custom prompt are preserved");
            async Task<TerminalCommand> Command(string command)
            {
                int before = session.Status.Commands.Count;
                session.Paste(command); session.Key(13, 0);
                check(await Wait(() => session.Status.Commands.Count == before + 1 && session.Status.Commands[^1].Finished is not null), name + ": one completed command: " + command);
                return session.Status.Commands[^1];
            }
            var failure = await Command("sleep 0.25; false");
            check(failure.ExitCode == 1 && failure.Duration?.TotalSeconds >= .2, name + ": real failure exit code and duration survive the terminal engine");
            if (commandText) check(failure.Text == "sleep 0.25; false", name + ": command text reaches the upstream semantic callback");
            var unicode = await Command("printf 'café 中; %%\\n'");
            check(unicode.ExitCode == 0 && (!commandText || unicode.Text.Contains("café 中")), name + ": Unicode command executes and reports success");
            int beforeChange = session.Status.Commands.Count;
            await Command("cd /tmp; true");
            check(await Wait(() => session.Directory == "/tmp") && session.Status.Commands.Count == beforeChange + 1, name + ": cwd updates without phantom commands");
            session.Paste("sleep 10"); session.Key(13, 0);
            check(await Wait(() => session.Status.CommandRunning), name + ": command is marked running before completion");
            session.Key(67, 2);
            check(await Wait(() => !session.Status.CommandRunning) && session.Status.LastExitCode == 130, name + ": Ctrl+C completion reports 130");
            var exit = await Command("exit 7");
            check(await Wait(() => session.HasExited) && exit.ExitCode == 7, name + ": shell EOF completes the final command");
        }
        await Exercise($"wsl.exe -d {distro} --exec bash --noprofile --norc", "Bash");
        await Exercise($"wsl.exe -d {distro}", "WSL default shell");
        string? zshRoot = Environment.GetEnvironmentVariable("OROTERM_ZSH_ROOT");
        if (zshRoot is null || !File.Exists(Path.Combine(zshRoot, "usr", "bin", "zsh"))) { Console.WriteLine("SKIP: optional extracted zsh client (scripts/test-interop.ps1 -Zsh)."); return; }
        string Linux(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        string fixture = Path.Combine(Environment.CurrentDirectory, "artifacts", "zsh-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, ".zshenv"), "fpath=(" + Linux(zshRoot) + "/usr/share/zsh/functions/Misc $fpath)\nmodule_path=(" + Linux(zshRoot) + "/usr/lib/zsh/5.9.1 $module_path)\n");
        File.WriteAllText(Path.Combine(fixture, ".zshrc"), "PROMPT='CUSTOM-ZSH> '\n");
        var previous = new Dictionary<string, string?>();
        foreach (string name in new[] { "WSLENV", "ZDOTDIR" }) previous[name] = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable("ZDOTDIR", Linux(fixture));
            Environment.SetEnvironmentVariable("WSLENV", (previous["WSLENV"] is { Length: > 0 } value ? value + ":" : "") + "ZDOTDIR");
            await Exercise($"wsl.exe -d {distro} --exec {Linux(zshRoot)}/usr/bin/zsh", "Zsh", true, "CUSTOM-ZSH>");
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            File.Delete(Path.Combine(fixture, ".zshenv")); File.Delete(Path.Combine(fixture, ".zshrc"));
            Directory.Delete(fixture);
        }
    }
}
