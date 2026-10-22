param([string]$AppDirectory='artifacts/performance-dev', [string]$TestDirectory='artifacts/performance-tests', [ValidateRange(1,10)][int]$Iterations=3, [switch]$SkipBuild, [switch]$ResizeStress, [switch]$Scale16, [switch]$RenderProfile, [switch]$Translucent, [string]$MonitorDevice, [ValidateScript({$_ -eq 0 -or ($_ -ge 9 -and $_ -le 32)})][int]$FontSize=0,
    [ValidateSet(0,1,4,8,16)][int]$PaneCount=0,[ValidateSet('','quiet','controlled-output','saturated-output','mixed-output')][string]$Phase='',[ValidateRange(1,30)][int]$OutputSeconds=5,
    [switch]$ExpandedQuads,[switch]$CompositionRetry,[switch]$NativeLayouts=$true,[switch]$CompositionBulk,[switch]$LegacyAtlasHash,[switch]$Foreground,[switch]$AsciiShapes=$true,[switch]$EphemeralReads,[switch]$LegacyJson,[ValidateRange(0,100)][int]$HistoryCommands=0,[ValidateSet(16,64)][int]$ReadBufferKiB=16,[switch]$ReserveResize=$true,[string]$PresentMonExe,[ValidateRange(1,120)][int]$PresentMonSeconds=15,[switch]$CancelRetiredWatch,[switch]$StreamedAscii=$true,[ValidateSet(1,2)][int]$FrameLatency=2,[ValidateSet(0,1)][int]$PresentInterval=0,[ValidateSet('ascii','unicode','styled')][string]$TextCorpus='ascii',[switch]$EagerBackgrounds,[switch]$ColorGlyphCache=$true,[switch]$PreparedLayouts=$true)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/test-window.ps1"
. "$PSScriptRoot/test-suspended-process.ps1"
$app=if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $root $AppDirectory }
$tests=if ([IO.Path]::IsPathRooted($TestDirectory)) { $TestDirectory } else { Join-Path $root $TestDirectory }
if (!('ToiqttyPerformanceWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ToiqttyPerformanceWindow {
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);
}
'@
}
if (!$SkipBuild) {
    dotnet build "$root/tests/Toiqtty.Smoke/Toiqtty.Smoke.csproj" -c Release -o $tests
    if ($LASTEXITCODE -ne 0) { throw 'Performance client build failed.' }
    dotnet publish "$root/src/Toiqtty/Toiqtty.csproj" -c Release -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Performance app build failed.' }
}
for ($iteration=0; $iteration -lt $Iterations; $iteration++) {
    $folder=Join-Path $root ('artifacts/performance-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    [ordered]@{textCorpus=$TextCorpus;eagerBackgrounds=[bool]$EagerBackgrounds;streamedAscii=[bool]$StreamedAscii;colorGlyphCache=[bool]$ColorGlyphCache;preparedLayouts=[bool]$PreparedLayouts;
        reserveResize=[bool]$ReserveResize;frameLatency=$FrameLatency;presentInterval=$PresentInterval;cancelRetiredWatch=[bool]$CancelRetiredWatch} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'benchmark-experiment.json')
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
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $app 'toiqtty.exe'))
    $start.UseShellExecute=$false; $start.WorkingDirectory=$root
    $start.Environment['TOIQTTY_PERFORMANCE_TEXT_CORPUS']=$TextCorpus
    if($ResizeStress){$start.Environment['TOIQTTY_PERFORMANCE_RESIZE_STRESS']='1'}else{$start.Environment.Remove('TOIQTTY_PERFORMANCE_RESIZE_STRESS')|Out-Null}
    $start.Environment['TOIQTTY_PERFORMANCE_BACKGROUND_LAYERS']=if($EagerBackgrounds){'eager'}else{'lazy'}
    if($ColorGlyphCache){$start.Environment['TOIQTTY_COLOR_GLYPH_CACHE']='1'}else{$start.Environment['TOIQTTY_COLOR_GLYPH_CACHE']='0'}
    if($PreparedLayouts){$start.Environment['TOIQTTY_PREPARED_LAYOUTS']='1'}else{$start.Environment['TOIQTTY_PREPARED_LAYOUTS']='0'}
    $start.ArgumentList.Add('--ui-smoke')
    $start.Environment['TOIQTTY_DATA_DIR']=$folder; $start.Environment['TOIQTTY_SESSION_UI_PROBE']='performance'
    $start.Environment['TOIQTTY_SMOKE_EXE']=Join-Path $tests 'Toiqtty.Smoke.exe'
    if($PaneCount){$start.Environment['TOIQTTY_PERFORMANCE_PANES']=$PaneCount.ToString()}
    if($Phase){$start.Environment['TOIQTTY_PERFORMANCE_PHASE']=$Phase}
    $start.Environment['TOIQTTY_PERFORMANCE_OUTPUT_MS']=($OutputSeconds*1000).ToString()
    $start.Environment['TOIQTTY_PERFORMANCE_FRAME_LATENCY']=$FrameLatency.ToString()
    $start.Environment['TOIQTTY_PERFORMANCE_PRESENT_INTERVAL']=$PresentInterval.ToString()
    if($ExpandedQuads){$start.Environment['TOIQTTY_PERFORMANCE_QUADS']='expanded'}
    if($CompositionRetry){$start.Environment['TOIQTTY_PERFORMANCE_RETRY']='composition'}
    if($NativeLayouts){$start.Environment['TOIQTTY_PERFORMANCE_LAYOUTS']='native'}
    if($AsciiShapes){$start.Environment['TOIQTTY_PERFORMANCE_ASCII']='harfbuzz'}
    if($EphemeralReads){$start.Environment['TOIQTTY_PERFORMANCE_READS']='ephemeral'}
    if($LegacyJson){$start.Environment['TOIQTTY_PERFORMANCE_JSON']='legacy'}
    if($HistoryCommands){$start.Environment['TOIQTTY_PERFORMANCE_HISTORY_COMMANDS']=$HistoryCommands.ToString()}
    $start.Environment['TOIQTTY_PERFORMANCE_READ_KIB']=$ReadBufferKiB.ToString()
    if($ReserveResize){$start.Environment['TOIQTTY_PERFORMANCE_RESIZE']='capacity'}
    if($CancelRetiredWatch){$start.Environment['TOIQTTY_PERFORMANCE_WATCH_CANCEL']='retired'}
    if($StreamedAscii){$start.Environment['TOIQTTY_PERFORMANCE_ASCII_STORAGE']='font-scratch'}
    if($CompositionBulk){$start.Environment['TOIQTTY_PERFORMANCE_BULK']='composition'}
    if($LegacyAtlasHash){$start.Environment['TOIQTTY_PERFORMANCE_ATLAS_HASH']='legacy'}
    if($RenderProfile){$start.Environment['TOIQTTY_RENDER_PROFILE']='1'}
    if($Scale16) { $start.Environment['TOIQTTY_PERFORMANCE_SCALE']='16' }
    $process=$null; $displayRequest=$null; $previousForeground=[IntPtr]::Zero; $presentMon=$null; $suspended=$null
    try {
        $displayRequest=New-ToiqttyDisplayRequest
        $previousForeground=Get-ToiqttyForegroundWindow
        if($PresentMonExe){
            $captureExe=(Resolve-Path -LiteralPath $PresentMonExe).Path
            $captureSession='Toiqtty-'+[Guid]::NewGuid().ToString('N')
            $existingCapture=& logman query $captureSession -ets 2>&1
            if($LASTEXITCODE -eq 0){throw 'The new ETW session name already exists; no existing session was replaced.'}
            # Subscribe before any app code or composition surfaces run. The
            # native initial-thread lease identifies the exact owned PID.
            $suspended=New-ToiqttySuspendedTestProcess $start;$process=$suspended.Process
            $capture=[Diagnostics.ProcessStartInfo]::new($captureExe);$capture.UseShellExecute=$false;$capture.CreateNoWindow=$true;$capture.RedirectStandardOutput=$true;$capture.RedirectStandardError=$true
            foreach($argument in @('--process_id',$process.Id.ToString(),'--session_name',$captureSession,'--timed',$PresentMonSeconds.ToString(),'--terminate_after_timed','--no_console_stats','--no_track_gpu','--no_track_input','--qpc_time','--write_display_metadata','--output_file',(Join-Path $folder 'presentmon.csv'))){$capture.ArgumentList.Add($argument)}
            $presentMon=[Diagnostics.Process]::Start($capture)
            $captureOutput=$presentMon.StandardOutput.ReadToEndAsync();$captureErrors=$presentMon.StandardError.ReadToEndAsync()
            $captureStartedUtc=[DateTime]::UtcNow.ToString('o');Start-Sleep -Milliseconds 400
            if($presentMon.HasExited){throw 'PresentMon exited before the isolated app launched.'}
            $suspended.Resume()
        }
        else {$process=[Diagnostics.Process]::Start($start)}
        if($presentMon){
            [ordered]@{executable=$captureExe;sha256=(Get-FileHash -LiteralPath $captureExe -Algorithm SHA256).Hash;processId=$process.Id;sessionName=$captureSession;startedUtc=$captureStartedUtc;meaning='Instrumented comparison: ETW capture can affect timings. A verified-new session subscribes to the owned PID before its initial thread resumes; no user process, elevation or existing-session replacement.'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $folder 'presentmon-capture.json')
        }
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
        $initialPlacement=Move-ToiqttyTestWindow $process -Topmost @monitorArguments -Width 640 -Height 480
        Start-Sleep -Milliseconds 300
        $geometry=Get-ToiqttyTestWindowGeometry $process.MainWindowHandle
        $width=[int][Math]::Min([Math]::Round(1280*$geometry.dpi/96),$initialPlacement.workingArea.width-24)
        $height=[int][Math]::Min([Math]::Round(820*$geometry.dpi/96),$initialPlacement.workingArea.height-24)
        $placement=Move-ToiqttyTestWindow $process -Topmost @monitorArguments -Width $width -Height $height
        $placement | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $folder 'window-placement.json')
        if($placement.geometry.width -ne $width -or $placement.geometry.height -ne $height -or $placement.geometry.dpi -ne $geometry.dpi){throw "The benchmark window cannot hold the requested viewport on this monitor: $($placement | ConvertTo-Json -Compress -Depth 4)"}
        if($Foreground){
            $activated=Restore-ToiqttyForegroundWindow $process.MainWindowHandle
            if(!$activated -or (Get-ToiqttyForegroundWindow) -ne $process.MainWindowHandle){throw 'The isolated benchmark could not become the foreground window.'}
        } else {$activated=$false;Restore-ToiqttyForegroundWindow $previousForeground | Out-Null}
        [ordered]@{previousWindow=$previousForeground.ToInt64();foregroundRequested=[bool]$Foreground;activated=$activated;observedWindow=(Get-ToiqttyForegroundWindow).ToInt64();benchmarkWindow=$process.MainWindowHandle.ToInt64()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'foreground-restoration.json')
        if ($ResizeStress) {
            $deadline=[DateTime]::UtcNow.AddSeconds(90)
            $resizeSteps=0
            while (!$process.WaitForExit(200) -and [DateTime]::UtcNow -lt $deadline) {
                $wide=$resizeSteps % 2 -eq 0
                $stressWidth=[int][Math]::Min([Math]::Round($(if($wide){1700}else{1100})*$geometry.dpi/96),$initialPlacement.workingArea.width-24)
                $stressHeight=[int][Math]::Min([Math]::Round($(if($wide){1050}else{760})*$geometry.dpi/96),$initialPlacement.workingArea.height-24)
                $dpiContext=[ToiqttyTestGeometry]::SetThreadDpiAwarenessContext([IntPtr](-4))
                if($dpiContext -eq [IntPtr]::Zero){throw 'Cannot resize the benchmark in physical pixels.'}
                try{$positioned=[ToiqttyPerformanceWindow]::SetWindowPos($process.MainWindowHandle,[IntPtr]::Zero,0,0,$stressWidth,$stressHeight,0x16)}
                finally{[ToiqttyTestGeometry]::SetThreadDpiAwarenessContext($dpiContext)|Out-Null}
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
                if($Foreground -and $process.MainWindowHandle -ne 0 -and (Get-ToiqttyForegroundWindow) -ne $process.MainWindowHandle){
                    [ordered]@{timestamp=[DateTimeOffset]::Now.ToString('o');reason='The explicitly foreground benchmark lost foreground eligibility.'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'invalid-foreground.json')
                    throw "Benchmark lost foreground; exclude this measurement: $folder"
                }
                if($process.MainWindowHandle -ne 0 -and [ToiqttyPerformanceWindow]::IsIconic($process.MainWindowHandle)) {
                    [ordered]@{timestamp=[DateTimeOffset]::Now.ToString('o');processId=$process.Id;reason='The isolated benchmark window was minimized; presentation is intentionally suspended. This run is not a latency comparison.'} |
                        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'invalid-measurement.json')
                    throw "Benchmark window was minimized; exclude this measurement: $folder"
                }
                if($process.MainWindowHandle -ne 0) {
                    try{$currentGeometry=Get-ToiqttyTestWindowGeometry $process.MainWindowHandle}
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
      try {
        if($suspended){$suspended.Dispose()}
        if ($null -ne $process -and !$process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (!$process.WaitForExit(5000)) { $process.Kill();$process.WaitForExit() }
        }
        & "$tests/Toiqtty.Smoke.exe" --stop-performance-host $folder
      } finally {
       try {
        if($presentMon){
          try {
            if(!$presentMon.WaitForExit(10000)){
                # Stop only this invocation's verified-new session, allowing
                # the consumer to flush CSV before a last-resort process kill.
                & $captureExe --session_name $captureSession --terminate_existing_session *> (Join-Path $folder 'presentmon-stop.log')
                if(!$presentMon.WaitForExit(5000)){$presentMon.Kill();$presentMon.WaitForExit()}
            }
            $captureOutput.GetAwaiter().GetResult()|Set-Content -LiteralPath (Join-Path $folder 'presentmon-output.log')
            $captureErrors.GetAwaiter().GetResult()|Set-Content -LiteralPath (Join-Path $folder 'presentmon-errors.log')
            $remaining=& logman query $captureSession -ets 2>&1
            if($LASTEXITCODE -eq 0){& logman stop $captureSession -ets *> (Join-Path $folder 'presentmon-session-cleanup.log')}
            [ordered]@{exitCode=$presentMon.ExitCode;csvExists=(Test-Path -LiteralPath (Join-Path $folder 'presentmon.csv'))}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $folder 'presentmon-exit.json')
          } finally {$presentMon.Dispose()}
        }
       } finally {
        if($displayRequest){$displayRequest.Dispose()}
        if($Foreground -and $previousForeground -ne [IntPtr]::Zero){Restore-ToiqttyForegroundWindow $previousForeground | Out-Null}
       }
      }
    }
}
