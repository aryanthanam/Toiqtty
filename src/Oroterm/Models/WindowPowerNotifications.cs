using System.Runtime.InteropServices;

namespace Oroterm.Models;

/// <summary>Owns power notifications for one HWND; no global hooks or power changes.</summary>
internal sealed class WindowPowerNotifications : IDisposable
{
    private readonly nint _window, _registration;
    private readonly SubclassProc _callback;
    private readonly Action<bool> _changed;
    private bool _disposed;
    private nint _displayRegistration;
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    internal int? DisplayState { get; private set; }
    internal void ObserveDisplayState()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowPowerNotifications));
        if (_displayRegistration != 0) return;
        var setting = SessionDisplayStatus;
        _displayRegistration = RegisterPowerSettingNotification(_window, ref setting, 0);
        if (_displayRegistration == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public WindowPowerNotifications(nint window, Action<bool> changed)
    {
        _window = window; _changed = changed; _callback = WindowProc;
        if (!SetWindowSubclass(window, _callback, 0x4f545057, 0))
            throw new InvalidOperationException("Could not register window power notifications.");
        _registration = RegisterSuspendResumeNotification(window, 0);
    }
    private nint WindowProc(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        try
        {
            if (!_disposed && message == 0x218)
            {
                if (wparam == 4) _changed(false); // PBT_APMSUSPEND
                else if (wparam is 7 or 0x12) _changed(true); // PBT_APMRESUME*
                else if (wparam == 0x8013 && lparam != 0 && Marshal.PtrToStructure<Guid>(lparam) == SessionDisplayStatus && Marshal.ReadInt32(lparam, 16) == 4)
                    DisplayState = Marshal.ReadInt32(lparam, 20);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        return DefSubclassProc(window, message, wparam, lparam);
    }
    internal void SendTestNotification(bool resumed) => SendMessage(_window, 0x218, resumed ? 0x12u : 4u, 0);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registration != 0) UnregisterSuspendResumeNotification(_registration);
        if (_displayRegistration != 0) UnregisterPowerSettingNotification(_displayRegistration);
        RemoveWindowSubclass(_window, _callback, 0x4f545057);
        GC.KeepAlive(_callback);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint RegisterSuspendResumeNotification(nint recipient, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterSuspendResumeNotification(nint registration);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterPowerSettingNotification(nint hwnd, ref Guid setting, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterPowerSettingNotification(nint registration);
}
