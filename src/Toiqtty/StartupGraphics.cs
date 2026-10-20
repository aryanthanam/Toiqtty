using Microsoft.Graphics.Canvas;
using Toiqtty.Controls;
using Toiqtty.Models;

namespace Toiqtty;

// The launch owns preparation until Loaded publishes it on the UI thread.
// A window closed during preparation releases only its unpublished device;
// the worker can never repopulate the application cache after shutdown.
internal sealed class StartupGraphics : IDisposable
{
    private readonly Task _ready;
    private DirectXRenderer.PreparedDeviceHandle? _prepared;
    private int _disposed;

    internal StartupGraphics()
    {
        _ready = Task.Run(() =>
        {
            StartupTrace.Mark("graphics-prepare-begin");
            try
            {
                var device = CanvasDevice.GetSharedDevice();
                var prepared = DirectXRenderer.PrepareDevice(device);
                Interlocked.Exchange(ref _prepared, prepared);
                if (Volatile.Read(ref _disposed) != 0) Interlocked.Exchange(ref _prepared, null)?.Dispose();
            }
            // First draw retains normal hardware/WARP fallback and recovery.
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
            { StartupTrace.Mark("graphics-prepare-fallback"); }
            finally { StartupTrace.Mark("graphics-prepare-end"); }
        });
    }

    internal async Task Install()
    {
        await _ready;
        using var prepared = Interlocked.Exchange(ref _prepared, null);
        if (Volatile.Read(ref _disposed) != 0 || prepared is null) return;
        try { DirectXRenderer.InstallDevice(prepared); }
        catch (System.Runtime.InteropServices.COMException) { StartupTrace.Mark("graphics-install-fallback"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _prepared, null)?.Dispose();
        // Observe an unexpected preparation failure even if Loaded never ran.
        _ = _ready.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
