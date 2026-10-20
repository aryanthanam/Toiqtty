using System.Text.Json;
using Toiqtty.Configuration;
using Toiqtty.Models;

namespace Toiqtty;

// One launch owns this evaluator until its worker is transferred to the window's
// runtime hooks. Closing during startup cancels it and releases its Windows job.
internal sealed class StartupLuaEvaluation : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private LuaRuntimeClient? _runtime;
    private int _disposed;
    internal AppSettings Baseline { get; private set; }
    internal Task<LuaConfigResult> Result { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal StartupLuaEvaluation(AppSettings fallback)
    {
        Baseline = fallback;
        var cancellation = _stop.Token;
        Result = Task.Run(async () =>
        {
            StartupTrace.Mark("config-evaluation-begin");
            try
            {
                Baseline = UserPreferences.Load(SessionStore.Folder, fallback);
                if (!File.Exists(LuaConfigService.ConfigPath)) return new LuaConfigResult { Settings = LuaConfigResult.Clone(Baseline) };
                var runtime = await LuaRuntimeClient.Start(new() {
                    Path = LuaConfigService.ConfigPath, DataDirectory = SessionStore.Folder, Baseline = Baseline
                }, cancellation: cancellation).ConfigureAwait(false);
                var result = runtime.Config;
                Interlocked.Exchange(ref _runtime, runtime);
                if (Volatile.Read(ref _disposed) != 0) TakeRuntime()?.Dispose();
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
            {
                return new LuaConfigResult { Settings = LuaConfigResult.Clone(Baseline), Error = ex is OperationCanceledException
                    ? "Lua configuration exceeded its 4 second execution limit." : ex.Message };
            }
            finally { StartupTrace.Mark("config-evaluation-end"); }
        });
        // Observe constructor/early-close failures even if Loaded never consumes it.
        _ = Result.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal LuaRuntimeClient? TakeRuntime() => Interlocked.Exchange(ref _runtime, null);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); TakeRuntime()?.Dispose();
        _ = Result.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
