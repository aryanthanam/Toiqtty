using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Oroterm.Models;

namespace Oroterm.Configuration;

public static class LuaConfigService
{
    public static string ConfigPath => Path.GetFullPath(Environment.ExpandEnvironmentVariables(
        Environment.GetEnvironmentVariable("OROTERM_CONFIG") ?? Path.Combine(SessionStore.Folder, "oroterm.lua")));
    public static async Task<LuaConfigResult> Load(string path, string folder, AppSettings baseline, string? helper = null, CancellationToken cancellation = default)
    {
        var fallback = new LuaConfigResult { Settings = LuaConfigResult.Clone(baseline) };
        if (!File.Exists(path)) return fallback;
        helper ??= Path.Combine(AppContext.BaseDirectory, "Oroterm.SessionHost.exe");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var process = new Process { StartInfo = new(helper) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        } };
        process.StartInfo.ArgumentList.Add("--evaluate-config");
        try
        {
            process.Start();
            using var limits = ConfigWorkerLimits.Attach(process);
            var output = ReadBounded(process.StandardOutput, 2 * 1024 * 1024, deadline.Token);
            var errors = ReadBounded(process.StandardError, 8192, deadline.Token);
            string request = JsonSerializer.Serialize(new LuaConfigRequest { Path = Path.GetFullPath(path), DataDirectory = folder, Baseline = baseline });
            await process.StandardInput.WriteAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false); process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string json = await output.ConfigureAwait(false); await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || json.Length == 0) throw new IOException("Lua config worker stopped, possibly after exceeding its 256 MiB memory limit.");
            var result = JsonSerializer.Deserialize<LuaConfigResult>(json) ?? throw new IOException("Empty Lua configuration result.");
            if (result.Error is null) LuaConfigParser.Validate(result.Settings);
            return result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { fallback.Error = path + ": Lua configuration exceeded its 4 second execution limit."; return fallback; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or ArgumentException or InvalidOperationException)
        { fallback.Error = path + ": " + ex.Message; return fallback; }
        finally
        {
            try { if (process.Id > 0 && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (InvalidOperationException) { }
        }
    }
    public static async Task<string> ReadBounded(TextReader reader, int limit, CancellationToken cancellation)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
        {
            if (result.Length + read > limit) throw new IOException("Configuration worker output exceeds the limit.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
    public static async Task RunWorker()
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        LuaConfigResult result;
        try
        {
            var request = JsonSerializer.Deserialize<LuaConfigRequest>(await ReadBounded(Console.In, 512 * 1024, CancellationToken.None)) ?? throw new ArgumentException("Missing configuration request.");
            result = await LuaConfigParser.Evaluate(request);
        }
        catch (Exception ex) { result = new() { Error = ex.Message.Length > 8192 ? ex.Message[..8192] : ex.Message }; }
        string output = JsonSerializer.Serialize(result);
        if (output.Length > 2 * 1024 * 1024) output = JsonSerializer.Serialize(new LuaConfigResult { Error = "Lua configuration result is too large." });
        await Console.Out.WriteAsync(output);
    }
}
