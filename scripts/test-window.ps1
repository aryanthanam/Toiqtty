function Initialize-ToiqttyTestPlacement {
    if(!('ToiqttyDisplayRequest' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public sealed class ToiqttyDisplayRequest : IDisposable {
    [StructLayout(LayoutKind.Sequential)] private struct Reason {public uint Version,Flags;public IntPtr Text;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle PowerCreateRequest(ref Reason reason);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool PowerSetRequest(SafeFileHandle handle,int type);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool PowerClearRequest(SafeFileHandle handle,int type);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint SetThreadExecutionState(uint flags);
    private SafeFileHandle handle;
    private bool display,system,disposed;
    public ToiqttyDisplayRequest() {
        // Reset the existing idle timer once; the scoped power request below
        // then holds the test's display requirement without changing a plan.
        if(SetThreadExecutionState(3)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
        var text=Marshal.StringToHGlobalUni("Toiqtty isolated visible performance measurement");
        try {var reason=new Reason {Flags=1,Text=text};handle=PowerCreateRequest(ref reason);}
        finally {Marshal.FreeHGlobal(text);}
        if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}
        try {
            if(!PowerSetRequest(handle,0))throw new Win32Exception(Marshal.GetLastWin32Error());display=true;
            if(!PowerSetRequest(handle,1))throw new Win32Exception(Marshal.GetLastWin32Error());system=true;
        } catch {Dispose();throw;}
    }
    public void Dispose() {
        if(disposed)return;disposed=true;
        try {if(system)PowerClearRequest(handle,1);if(display)PowerClearRequest(handle,0);}
        finally {handle.Dispose();}
    }
}
'@
    }
    if(!('ToiqttyTestGeometry' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ToiqttyTestGeometry {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",SetLastError=true)] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool GetWindowRect(IntPtr window,out Rect rectangle);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool GetClientRect(IntPtr window,out Rect rectangle);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll",EntryPoint="EnumDisplaySettingsA",CharSet=CharSet.Ansi)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplaySettings(string device,int mode,[In,Out]byte[] settings);
    public static int[] DisplayMode(string device) {
        var settings=new byte[156];settings[36]=156;
        if(!EnumDisplaySettings(device,-1,settings))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new[]{BitConverter.ToInt32(settings,108),BitConverter.ToInt32(settings,112),BitConverter.ToInt32(settings,120)};
    }
}
'@
    }
    if(!('ToiqttyTestPlacement' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ToiqttyTestPlacement {
    [DllImport("user32.dll",SetLastError=true)]
    public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr window,int command);
}
'@
    }
}
function New-ToiqttyDisplayRequest {
    Initialize-ToiqttyTestPlacement
    return [ToiqttyDisplayRequest]::new()
}
function Show-ToiqttyTestWindowForChecks([Diagnostics.Process]$Process,[string]$ExpectedExecutable) {
    $Process.Refresh()
    if($Process.HasExited -or ![string]::Equals($Process.Path,[IO.Path]::GetFullPath($ExpectedExecutable),[StringComparison]::OrdinalIgnoreCase)){
        throw 'Refusing to prepare a window whose isolated process identity does not match.'
    }
    Initialize-ToiqttyTestPlacement
    if($Process.MainWindowHandle -eq [IntPtr]::Zero){throw 'The isolated test window has no HWND.'}
    $wasMinimized=[ToiqttyTestPlacement]::IsIconic($Process.MainWindowHandle)
    [ToiqttyTestPlacement]::ShowWindowAsync($Process.MainWindowHandle,4)|Out-Null
    $placement=Move-ToiqttyTestWindow -Process $Process -Primary
    # Make this owned window drawable without changing foreground input focus.
    if(![ToiqttyTestPlacement]::SetWindowPos($Process.MainWindowHandle,[IntPtr]::Zero,0,0,0,0,0x13)){
        throw 'Could not expose the isolated window for its final drawing checks.'
    }
    return [ordered]@{prepared=(Get-Date -Format o);processId=$Process.Id;creation=$Process.StartTime.ToString('o');wasMinimized=$wasMinimized;activationRequested=$false;placement=$placement}
}
function Get-ToiqttyForegroundWindow {
    Initialize-ToiqttyTestPlacement
    return [ToiqttyTestPlacement]::GetForegroundWindow()
}
function Restore-ToiqttyForegroundWindow([IntPtr]$Window) {
    Initialize-ToiqttyTestPlacement
    if($Window -eq [IntPtr]::Zero -or ![ToiqttyTestPlacement]::IsWindow($Window) -or [ToiqttyTestPlacement]::IsIconic($Window)){return $false}
    return [ToiqttyTestPlacement]::SetForegroundWindow($Window)
}
function Get-ToiqttyTestWindowGeometry([IntPtr]$Window) {
    Initialize-ToiqttyTestPlacement
    $previous=[ToiqttyTestGeometry]::SetThreadDpiAwarenessContext([IntPtr](-4))
    if($previous -eq [IntPtr]::Zero){throw 'Cannot read test geometry in a per-monitor DPI context.'}
    try {
        $bounds=[ToiqttyTestGeometry+Rect]::new(); $client=[ToiqttyTestGeometry+Rect]::new()
        if(![ToiqttyTestGeometry]::GetWindowRect($Window,[ref]$bounds) -or ![ToiqttyTestGeometry]::GetClientRect($Window,[ref]$client)){throw 'Cannot read isolated window geometry.'}
        return [ordered]@{x=$bounds.Left;y=$bounds.Top;width=$bounds.Right-$bounds.Left;height=$bounds.Bottom-$bounds.Top;clientWidth=$client.Right;clientHeight=$client.Bottom;dpi=[ToiqttyTestGeometry]::GetDpiForWindow($Window);units='Physical pixels from a per-monitor DPI-aware thread'}
    }finally{[ToiqttyTestGeometry]::SetThreadDpiAwarenessContext($previous) | Out-Null}
}
function Move-ToiqttyTestWindow([Diagnostics.Process]$Process,[switch]$Topmost,[switch]$Primary,[string]$MonitorDevice,[int]$Width=0,[int]$Height=0) {
    Add-Type -AssemblyName System.Windows.Forms
    Initialize-ToiqttyTestPlacement
    $ready=[DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 50; $Process.Refresh() } while(!$Process.HasExited -and $Process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $ready)
    if($Process.HasExited -or $Process.MainWindowHandle -eq 0) { throw 'The isolated test window did not open.' }
    if(($Width -gt 0) -ne ($Height -gt 0) -or $Width -lt 0 -or $Height -lt 0){throw 'Test width and height must both be positive or both zero.'}
    $previous=[ToiqttyTestGeometry]::SetThreadDpiAwarenessContext([IntPtr](-4))
    if($previous -eq [IntPtr]::Zero){throw 'Cannot place the isolated test in a per-monitor DPI context.'}
    try {
        if($Primary -and $MonitorDevice){throw 'Choose Primary or an explicit monitor device, not both.'}
        $screen=if($MonitorDevice){[Windows.Forms.Screen]::AllScreens | Where-Object DeviceName -EQ $MonitorDevice | Select-Object -First 1}elseif($Primary){[Windows.Forms.Screen]::PrimaryScreen}else{[Windows.Forms.Screen]::AllScreens | Where-Object { !$_.Primary } | Select-Object -First 1}
        if($MonitorDevice -and !$screen){throw "The requested test monitor is not connected: $MonitorDevice"}
        if(!$screen) { $screen=[Windows.Forms.Screen]::PrimaryScreen }
        $area=$screen.WorkingArea; $flags=[uint32]0x50
        if($Width -eq 0){$flags=$flags -bor 1}
        if(!$Topmost) { $flags=$flags -bor 4 }
        $after=if($Topmost){[IntPtr](-1)}else{[IntPtr]::Zero}
        if(![ToiqttyTestPlacement]::SetWindowPos($Process.MainWindowHandle,$after,$area.Left+12,$area.Top+12,$Width,$Height,$flags)) { throw 'Could not position the isolated test window.' }
        $mode=[ToiqttyTestGeometry]::DisplayMode($screen.DeviceName)
        $placement=[ordered]@{monitor=$screen.DeviceName;primary=$screen.Primary;displayMode=@{width=$mode[0];height=$mode[1];refreshHz=$mode[2]};x=$area.Left+12;y=$area.Top+12;topmost=[bool]$Topmost;requestedWidth=$Width;requestedHeight=$Height;workingArea=@{x=$area.Left;y=$area.Top;width=$area.Width;height=$area.Height}}
    }finally{[ToiqttyTestGeometry]::SetThreadDpiAwarenessContext($previous) | Out-Null}
    $placement['geometry']=Get-ToiqttyTestWindowGeometry $Process.MainWindowHandle
    return $placement
}
