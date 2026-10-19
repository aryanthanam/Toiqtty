param([string]$AppDirectory='artifacts/performance-dev', [string]$TestDirectory='artifacts/performance-tests', [ValidateRange(1,10)][int]$Iterations=3, [switch]$SkipBuild, [switch]$ResizeStress, [switch]$Scale16, [switch]$LegacyWatch, [switch]$RenderProfile, [switch]$Translucent, [string]$MonitorDevice, [ValidateScript({$_ -eq 0 -or ($_ -ge 9 -and $_ -le 32)})][int]$FontSize=0)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/test-window.ps1"
$app=if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $root $AppDirectory }
$tests=if ([IO.Path]::IsPathRooted($TestDirectory)) { $TestDirectory } else { Join-Path $root $TestDirectory }
if (!('OrotermPerformanceWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OrotermPerformanceWindow {
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);
}
'@
}
if (!$SkipBuild) {
    dotnet build "$root/tests/Oroterm.Smoke/Oroterm.Smoke.csproj" -c Release -o $tests
    if ($LASTEXITCODE -ne 0) { throw 'Performance client build failed.' }
    dotnet publish "$root/src/Oroterm/Oroterm.csproj" -c Release -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Performance app build failed.' }
}
for ($iteration=0; $iteration -lt $Iterations; $iteration++) {
    $folder=Join-Path $root ('artifacts/performance-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    if($Translucent -or $FontSize){
        # Exercise the alpha CompositionDrawingSurface/11-on-12 copy route on
        # both old and new releases, without enabling Lua or personal config.
        $settings=@{}
        if($Translucent){$settings.Window=@{Backdrop='mica';BackgroundOpacity=.72}}
        if($FontSize){$settings.FontSize=$FontSize}
        [ordered]@{Version=5;Settings=$settings} |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $folder 'session.json')
    }
    [ordered]@{translucent=[bool]$Translucent;renderProfile=[bool]$RenderProfile;requestedFontSize=$FontSize;meaning='Default opaque swapchain, or 72% terminal alpha via CompositionDrawingSurface. Optional font size is isolated fixture configuration. Lua and system-backdrop initialization are disabled by the fixture; the translucent case measures the terminal composition route, not Mica backdrop cost.'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'benchmark-appearance.json')
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $app 'Oroterm.exe'))
    $start.UseShellExecute=$false; $start.WorkingDirectory=$root
    $start.ArgumentList.Add('--ui-smoke')
    $start.Environment['OROTERM_DATA_DIR']=$folder; $start.Environment['OROTERM_SESSION_UI_PROBE']='performance'
    $start.Environment['OROTERM_SMOKE_EXE']=Join-Path $tests 'Oroterm.Smoke.exe'
    if($RenderProfile){$start.Environment['OROTERM_RENDER_PROFILE']='1'}
    if($Scale16) { $start.Environment['OROTERM_PERFORMANCE_SCALE']='16' }
    if($LegacyWatch) { $start.Environment['OROTERM_PERFORMANCE_WATCH_MODE']='legacy' }
    $process=$null
    try {
        $foreground=Get-OrotermForegroundWindow
        $process=[Diagnostics.Process]::Start($start)
        # Only the just-created isolated benchmark window is placed above other
        # windows. Do not take keyboard focus. Occluded WinUI presentation is
        # throttled by Windows and cannot serve as a visible-frame benchmark.
        $ready=[DateTime]::UtcNow.AddSeconds(10)
        do { Start-Sleep -Milliseconds 50; $process.Refresh() } while (!$process.HasExited -and $process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $ready)
        if ($process.HasExited -or $process.MainWindowHandle -eq 0) { throw 'Benchmark window did not open.' }
        $monitorArguments=if($MonitorDevice){@{MonitorDevice=$MonitorDevice}}else{@{Primary=$true}}
        # Fit the initial window inside the destination before reading its DPI.
        # A large window straddling a narrow portrait display can still belong
        # to the primary monitor, then rescale after its final resize.
        $initialPlacement=Move-OrotermTestWindow $process -Topmost @monitorArguments -Width 640 -Height 480
        Start-Sleep -Milliseconds 300
        $geometry=Get-OrotermTestWindowGeometry $process.MainWindowHandle
        $width=[int][Math]::Min([Math]::Round(1280*$geometry.dpi/96),$initialPlacement.workingArea.width-24)
        $height=[int][Math]::Min([Math]::Round(820*$geometry.dpi/96),$initialPlacement.workingArea.height-24)
        $placement=Move-OrotermTestWindow $process -Topmost @monitorArguments -Width $width -Height $height
        $placement | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $folder 'window-placement.json')
        if($placement.geometry.width -ne $width -or $placement.geometry.height -ne $height -or $placement.geometry.dpi -ne $geometry.dpi){throw "The benchmark window cannot hold the requested viewport on this monitor: $($placement | ConvertTo-Json -Compress -Depth 4)"}
        [ordered]@{previousWindow=$foreground.ToInt64();restored=(Restore-OrotermForegroundWindow $foreground)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'foreground-restoration.json')
        if ($ResizeStress) {
            $deadline=[DateTime]::UtcNow.AddSeconds(90)
            $resizeSteps=0
            while (!$process.WaitForExit(200) -and [DateTime]::UtcNow -lt $deadline) {
                $wide=$resizeSteps % 2 -eq 0
                $stressWidth=[int][Math]::Min([Math]::Round($(if($wide){1700}else{1100})*$geometry.dpi/96),$initialPlacement.workingArea.width-24)
                $stressHeight=[int][Math]::Min([Math]::Round($(if($wide){1050}else{760})*$geometry.dpi/96),$initialPlacement.workingArea.height-24)
                $dpiContext=[OrotermTestGeometry]::SetThreadDpiAwarenessContext([IntPtr](-4))
                if($dpiContext -eq [IntPtr]::Zero){throw 'Cannot resize the benchmark in physical pixels.'}
                try{$positioned=[OrotermPerformanceWindow]::SetWindowPos($process.MainWindowHandle,[IntPtr]::Zero,0,0,$stressWidth,$stressHeight,0x16)}
                finally{[OrotermTestGeometry]::SetThreadDpiAwarenessContext($dpiContext)|Out-Null}
                if (!$positioned) {
                    $errorCode=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
                    # The app can destroy its window just before the process exits.
                    if ($process.WaitForExit(5000)) { break }
                    throw [ComponentModel.Win32Exception]::new($errorCode,'Could not resize the isolated test window.')
                }
                $resizeSteps++
            }
            [ordered]@{resizeSteps=$resizeSteps;intervalMs=200;windowProcessId=$process.Id;dpi=$geometry.dpi;largeDip='1700x1050';smallDip='1100x760';mode='Alternating window sizes during one/four/eight-pane output; physical pixels scaled from the recorded DPI; not a fixed-viewport performance comparison.'} |
                ConvertTo-Json | Set-Content (Join-Path $folder 'resize-stress.json')
            if (!$process.HasExited) { $process.Kill(); throw "Resize stress probe timed out: $folder" }
        }
        else {
            $deadline=[DateTime]::UtcNow.AddSeconds($(if($Scale16){180}else{120}))
            while(!$process.WaitForExit(200) -and [DateTime]::UtcNow -lt $deadline) {
                $process.Refresh()
                if($process.MainWindowHandle -ne 0 -and [OrotermPerformanceWindow]::IsIconic($process.MainWindowHandle)) {
                    [ordered]@{timestamp=[DateTimeOffset]::Now.ToString('o');processId=$process.Id;reason='The isolated benchmark window was minimized; presentation is intentionally suspended. This run is not a latency comparison.'} |
                        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'invalid-measurement.json')
                    throw "Benchmark window was minimized; exclude this measurement: $folder"
                }
                if($process.MainWindowHandle -ne 0) {
                    try{$currentGeometry=Get-OrotermTestWindowGeometry $process.MainWindowHandle}
                    catch{if($process.WaitForExit(1000)){break};throw}
                    if($currentGeometry.width -ne $width -or $currentGeometry.height -ne $height -or $currentGeometry.dpi -ne $geometry.dpi -or $currentGeometry.x -ne $placement.geometry.x -or $currentGeometry.y -ne $placement.geometry.y) {
                        $currentGeometry | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'invalid-geometry.json')
                        throw "Benchmark position, size or DPI changed; exclude this measurement: $folder"
                    }
                }
            }
            if(!$process.HasExited) { $process.Kill(); throw "Isolated performance probe timed out: $folder" }
        }
        if ($process.ExitCode -ne 0) { throw "Performance probe exited with code $($process.ExitCode)." }
        $report=Get-Content (Join-Path $folder 'session-ui-performance.json') -Raw | ConvertFrom-Json
        if (!$report.passed) { throw $report.error }
        Get-Content (Join-Path $folder 'performance.json') -Raw
        Write-Host "Performance evidence: $folder"
    }
    finally {
        if ($null -ne $process -and !$process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (!$process.WaitForExit(5000)) { $process.Kill() }
        }
        & "$tests/Oroterm.Smoke.exe" --stop-performance-host $folder
    }
}
