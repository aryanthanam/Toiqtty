function Initialize-OrotermTestPlacement {
    if(!('OrotermTestGeometry' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OrotermTestGeometry {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",SetLastError=true)] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool GetWindowRect(IntPtr window,out Rect rectangle);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool GetClientRect(IntPtr window,out Rect rectangle);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
}
'@
    }
    if(!('OrotermTestPlacement' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OrotermTestPlacement {
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
function Show-OrotermTestWindowForChecks([Diagnostics.Process]$Process,[string]$ExpectedExecutable) {
    $Process.Refresh()
    if($Process.HasExited -or ![string]::Equals($Process.Path,[IO.Path]::GetFullPath($ExpectedExecutable),[StringComparison]::OrdinalIgnoreCase)){
        throw 'Refusing to prepare a window whose isolated process identity does not match.'
    }
    Initialize-OrotermTestPlacement
    if($Process.MainWindowHandle -eq [IntPtr]::Zero){throw 'The isolated test window has no HWND.'}
    $wasMinimized=[OrotermTestPlacement]::IsIconic($Process.MainWindowHandle)
    [OrotermTestPlacement]::ShowWindowAsync($Process.MainWindowHandle,4)|Out-Null
    $placement=Move-OrotermTestWindow -Process $Process -Primary
    # Make this owned window drawable without changing foreground input focus.
    if(![OrotermTestPlacement]::SetWindowPos($Process.MainWindowHandle,[IntPtr]::Zero,0,0,0,0,0x13)){
        throw 'Could not expose the isolated window for its final drawing checks.'
    }
    return [ordered]@{prepared=(Get-Date -Format o);processId=$Process.Id;creation=$Process.StartTime.ToString('o');wasMinimized=$wasMinimized;activationRequested=$false;placement=$placement}
}
function Get-OrotermForegroundWindow {
    Initialize-OrotermTestPlacement
    return [OrotermTestPlacement]::GetForegroundWindow()
}
function Restore-OrotermForegroundWindow([IntPtr]$Window) {
    Initialize-OrotermTestPlacement
    if($Window -eq [IntPtr]::Zero -or ![OrotermTestPlacement]::IsWindow($Window) -or [OrotermTestPlacement]::IsIconic($Window)){return $false}
    return [OrotermTestPlacement]::SetForegroundWindow($Window)
}
function Get-OrotermTestWindowGeometry([IntPtr]$Window) {
    Initialize-OrotermTestPlacement
    $previous=[OrotermTestGeometry]::SetThreadDpiAwarenessContext([IntPtr](-4))
    if($previous -eq [IntPtr]::Zero){throw 'Cannot read test geometry in a per-monitor DPI context.'}
    try {
        $bounds=[OrotermTestGeometry+Rect]::new(); $client=[OrotermTestGeometry+Rect]::new()
        if(![OrotermTestGeometry]::GetWindowRect($Window,[ref]$bounds) -or ![OrotermTestGeometry]::GetClientRect($Window,[ref]$client)){throw 'Cannot read isolated window geometry.'}
        return [ordered]@{x=$bounds.Left;y=$bounds.Top;width=$bounds.Right-$bounds.Left;height=$bounds.Bottom-$bounds.Top;clientWidth=$client.Right;clientHeight=$client.Bottom;dpi=[OrotermTestGeometry]::GetDpiForWindow($Window);units='Physical pixels from a per-monitor DPI-aware thread'}
    }finally{[OrotermTestGeometry]::SetThreadDpiAwarenessContext($previous) | Out-Null}
}
function Move-OrotermTestWindow([Diagnostics.Process]$Process,[switch]$Topmost,[switch]$Primary,[string]$MonitorDevice,[int]$Width=0,[int]$Height=0) {
    Add-Type -AssemblyName System.Windows.Forms
    Initialize-OrotermTestPlacement
    $ready=[DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 50; $Process.Refresh() } while(!$Process.HasExited -and $Process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $ready)
    if($Process.HasExited -or $Process.MainWindowHandle -eq 0) { throw 'The isolated test window did not open.' }
    if(($Width -gt 0) -ne ($Height -gt 0) -or $Width -lt 0 -or $Height -lt 0){throw 'Test width and height must both be positive or both zero.'}
    $previous=[OrotermTestGeometry]::SetThreadDpiAwarenessContext([IntPtr](-4))
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
        if(![OrotermTestPlacement]::SetWindowPos($Process.MainWindowHandle,$after,$area.Left+12,$area.Top+12,$Width,$Height,$flags)) { throw 'Could not position the isolated test window.' }
        $placement=[ordered]@{monitor=$screen.DeviceName;primary=$screen.Primary;x=$area.Left+12;y=$area.Top+12;topmost=[bool]$Topmost;requestedWidth=$Width;requestedHeight=$Height;workingArea=@{x=$area.Left;y=$area.Top;width=$area.Width;height=$area.Height}}
    }finally{[OrotermTestGeometry]::SetThreadDpiAwarenessContext($previous) | Out-Null}
    $placement['geometry']=Get-OrotermTestWindowGeometry $Process.MainWindowHandle
    return $placement
}
