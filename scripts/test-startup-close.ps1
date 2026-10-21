param([string]$AppDirectory='artifacts/release')
$ErrorActionPreference='Stop'
if(-not ('ToiqttyStartupWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ToiqttyStartupWindow {
    private delegate bool Visit(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visit callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);
    public static IntPtr Find(uint process) {
        IntPtr found=IntPtr.Zero;
        EnumWindows((window,state)=> {
            GetWindowThreadProcessId(window,out var owner); if(owner!=process)return true;
            var title=new StringBuilder(512); GetWindowText(window,title,title.Capacity);
            if(!title.ToString().StartsWith("toiqtty",StringComparison.OrdinalIgnoreCase))return true;
            found=window; return false;
        },IntPtr.Zero);
        return found;
    }
}
'@
}
$root=Split-Path $PSScriptRoot -Parent
$source=if([IO.Path]::IsPathRooted($AppDirectory)){$AppDirectory}else{Join-Path $root $AppDirectory}
$run=Join-Path $root ('artifacts/startup-close-'+[Guid]::NewGuid().ToString('N'))
$app=Join-Path $run 'app'; $data=Join-Path $run 'data'
New-Item -ItemType Directory -Force -Path $app,$data | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $app -Recurse
$config=Join-Path $data 'toiqtty.lua'
Set-Content -LiteralPath $config -Value 'while true do end'
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $app 'toiqtty.exe'))
$start.UseShellExecute=$false
$start.Environment['TOIQTTY_DATA_DIR']=$data
$start.Environment['TOIQTTY_CONFIG']=$config
$process=$null; $children=@()
try {
    $process=[Diagnostics.Process]::Start($start)
    $until=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $process.Refresh()
        if($process.HasExited) { throw 'Startup-close window exited before the check.' }
        $window=[ToiqttyStartupWindow]::Find($process.Id)
        if($window -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 25
    } while([DateTime]::UtcNow -lt $until)
    if($window -eq [IntPtr]::Zero) { throw 'The startup-close window did not appear.' }
    $workerUntil=[DateTime]::UtcNow.AddSeconds(2)
    do {
        $children=@(Get-Process Toiqtty.SessionHost -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq (Join-Path $app 'Toiqtty.SessionHost.exe') })
        if($children.Count) { break }
        Start-Sleep -Milliseconds 25
    } while([DateTime]::UtcNow -lt $workerUntil)
    if($children.Count -ne 1) { throw 'Expected one active config evaluator before closing.' }
    if(![ToiqttyStartupWindow]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero) -or !$process.WaitForExit(10000)) { throw 'Closing during Lua evaluation did not finish.' }
    foreach($child in $children) {
        $remaining=Get-Process -Id $child.Id -ErrorAction SilentlyContinue
        if($remaining -and !$remaining.WaitForExit(3000)) { throw 'The config worker survived its owning window.' }
    }
    if(Test-Path -LiteralPath (Join-Path $data 'host.json')) { throw 'Closing while configuration was pending started an unnecessary shell host.' }
    [ordered]@{passed=$true;workerCount=$children.Count;checks=@('Window closes during a nonterminating Lua config','The evaluator job ends with its owning window','Cancelled startup creates no shell host')} |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $run 'result.json')
    Write-Output "PASS: early-close cancellation and evaluator ownership ($run)"
}
finally {
    # Only processes created by this isolated check may be terminated on failure.
    if($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    foreach($child in $children) {
        $remaining=Get-Process -Id $child.Id -ErrorAction SilentlyContinue
        if($remaining -and $remaining.Path -eq (Join-Path $app 'Toiqtty.SessionHost.exe')) { $remaining.Kill() }
    }
    if($process) { $process.Dispose() }
}
