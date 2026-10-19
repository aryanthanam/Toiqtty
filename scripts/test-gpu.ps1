param(
    [ValidateRange(30,86400)][int]$Seconds=14400,
    [string]$AppDirectory='artifacts/release-0.7.6',
    [string]$RunDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
. (Join-Path $PSScriptRoot 'test-window.ps1')
$stage=if([IO.Path]::IsPathRooted($AppDirectory)){$AppDirectory}else{Join-Path $root $AppDirectory}
if(!$RunDirectory){$RunDirectory=Join-Path $root ('artifacts/gpu-run-'+[Guid]::NewGuid().ToString('N'))}
$run=[IO.Path]::GetFullPath($RunDirectory)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $root 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if(!$run.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($run) -notmatch '^gpu-run-[0-9a-f]{32}$'){
    throw 'GPU diagnostics require their own artifacts/gpu-run-GUID directory.'
}
New-Item -ItemType Directory -Force $run | Out-Null
$resultPath=Join-Path $run 'result.json'
$record=[ordered]@{status='preparing';passed=$false;started=[DateTimeOffset]::Now.ToString('o');requestedSeconds=$Seconds;runDirectory=$run;sourceApp=$stage
    mode='Exact published app copy; original local-terminal GPU/UIA smoke scenario; offscreen rendering, not onscreen FPS or a physical driver reset.'
    productionProcessIds=@(Get-Process Oroterm,Oroterm.SessionHost -ErrorAction SilentlyContinue | ForEach-Object Id)}
function Save-Record { $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath }
Save-Record
$process=$null; $dumpMonitor=$null; $counters=$null
try {
    $app=Join-Path $run 'app'; $client=Join-Path $run 'client'; $data=Join-Path $run 'data'; $dumps=Join-Path $run 'dumps'
    New-Item -ItemType Directory -Force $app,$data,$dumps | Out-Null
    Copy-Item -Path (Join-Path $stage '*') -Destination $app -Recurse
    $matched=0
    foreach($file in Get-ChildItem -LiteralPath $stage -Recurse -File){
        $relative=[IO.Path]::GetRelativePath($stage,$file.FullName)
        if((Get-FileHash -LiteralPath (Join-Path $app $relative)).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Application copy mismatch: $relative"}
        $matched++
    }
    $record['releaseCopyFilesVerified']=$matched
    dotnet build (Join-Path $root 'tests/Oroterm.Smoke') -c Release -o $client *> (Join-Path $run 'client-build.log')
    if($LASTEXITCODE -ne 0){throw 'GPU fixture build failed.'}
    $procDump=Join-Path $root '.tools/procdump/procdump64.exe'
    $signature=Get-AuthenticodeSignature -LiteralPath $procDump
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Microsoft Corporation*'){throw 'A Microsoft-signed ProcDump is required.'}
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $app 'Oroterm.exe'))
    $start.UseShellExecute=$false; $start.WorkingDirectory=$root
    $start.ArgumentList.Add('--ui-smoke')
    foreach($key in @($start.Environment.Keys)){
        if($key -like 'OROTERM_*PROBE*' -or $key -like 'OROTERM_*SMOKE*' -or $key -in @('OROTERM_CONFIG','OROTERM_STARTUP_TRACE')){$start.Environment.Remove($key) | Out-Null}
    }
    $start.Environment['OROTERM_DATA_DIR']=$data
    $start.Environment['OROTERM_SMOKE_EXE']=Join-Path $client 'Oroterm.Smoke.exe'
    $start.Environment['OROTERM_UI_SOAK_SECONDS']=$Seconds.ToString()
    $start.Environment['OROTERM_UI_SOAK_OFFSCREEN']='1'
    $start.Environment['OROTERM_NOTIFICATION_SMOKE']='0'
    $start.Environment.Remove('NO_COLOR') | Out-Null
    $process=[Diagnostics.Process]::Start($start)
    $record['appProcessId']=$process.Id; $record['appExecutable']=$start.FileName
    $record['dataDirectory']=$data; $record['dumpDirectory']=$dumps
    $dumpArguments=@('-accepteula','-ma','-g','-e',$process.Id.ToString(),$dumps) | ForEach-Object {'"'+$_+'"'}
    $dumpMonitor=Start-Process -FilePath $procDump -ArgumentList $dumpArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'procdump.log') -RedirectStandardError (Join-Path $run 'procdump-errors.log')
    $record['dumpMonitorProcessId']=$dumpMonitor.Id
    $counterTool=Join-Path $root '.tools/dotnet-diagnostics/dotnet-counters.exe'
    $duration=[TimeSpan]::FromSeconds($Seconds+180).ToString('hh\:mm\:ss')
    $counterArguments=@('collect','--process-id',$process.Id.ToString(),'--counters','System.Runtime','--refresh-interval','10','--duration',$duration,'--format','csv','--output',(Join-Path $run 'runtime-counters.csv')) | ForEach-Object {'"'+$_+'"'}
    $counters=Start-Process -FilePath $counterTool -ArgumentList $counterArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'counter-collector.log') -RedirectStandardError (Join-Path $run 'counter-errors.log')
    $record['counterCollectorProcessId']=$counters.Id
    $record.status='running'; Save-Record
    $clock=[Diagnostics.Stopwatch]::StartNew(); $deadline=[DateTime]::UtcNow.AddSeconds($Seconds+180); $nextGpu=0; $seenWindow=$false; $windowMissingSince=$null; $postSoakWindowPrepared=$false
    while(!$process.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline){
        $process.Refresh()
        if($process.MainWindowHandle -ne 0){$seenWindow=$true; $windowMissingSince=$null}
        elseif($seenWindow){
            $finalUiPath=Join-Path $data 'ui-smoke.json'
            if(Test-Path -LiteralPath $finalUiPath){
                # A completed or failed fixture writes its report before closing.
                if($process.WaitForExit(10000)){break}
                throw 'GPU fixture reported a result but did not finish teardown.'
            }
            # A fatal dispatcher exception can destroy the HWND before ProcDump
            # finishes writing and the process exits. Give that exit time to be
            # observed instead of attributing a vanished window to user closure.
            if($null -eq $windowMissingSince){$windowMissingSince=[DateTime]::UtcNow}
            if(([DateTime]::UtcNow-$windowMissingSince).TotalSeconds -ge 15){
                $record.status='interrupted'
                throw 'The isolated GPU window disappeared without a fixture result or process exit; the cause is unknown.'
            }
            continue
        }
        # The soak explicitly draws its graphics pane offscreen. Subsequent
        # Starship checks create another pane and require normal drawing.
        # Expose only this test window as the requested interval ends; retain
        # all existing assertions and deadlines, and do not take input focus.
        if(!$postSoakWindowPrepared -and $clock.Elapsed.TotalSeconds -ge $Seconds){
            $record['postSoakWindowPreparation']=Show-OrotermTestWindowForChecks -Process $process -ExpectedExecutable $start.FileName
            $postSoakWindowPrepared=$true;Save-Record
        }
        if($clock.Elapsed.TotalSeconds -lt $nextGpu){continue}
        $process.Refresh()
        $gpu=@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -ErrorAction SilentlyContinue | Where-Object Name -Like "pid_$($process.Id)_*")
        $sample=[ordered]@{timestamp=[DateTimeOffset]::Now.ToString('o');elapsedSeconds=$clock.Elapsed.TotalSeconds;privateBytes=$process.PrivateMemorySize64;workingSetBytes=$process.WorkingSet64;handles=$process.HandleCount;threads=$process.Threads.Count
            gpuDedicatedBytes=($gpu.DedicatedUsage | Measure-Object -Sum).Sum;gpuCommittedBytes=($gpu.TotalCommitted | Measure-Object -Sum).Sum}
        $sample | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $run 'process-memory.jsonl')
        $record['latestProcessSample']=$sample
        $rendererPath=Join-Path $data 'renderer-soak.json'
        if(Test-Path -LiteralPath $rendererPath){
            try{
                $renderer=Get-Content -LiteralPath $rendererPath -Raw | ConvertFrom-Json
                $record['rendererElapsedSeconds']=$renderer.elapsedSeconds
                $record['latestRendererSample']=$renderer.samples[-1]
            }catch{ $record['rendererReadNote']='Report was being rewritten; retry on next sample.' }
        }
        $dumpMonitor.Refresh(); $counters.Refresh()
        $record['dumpMonitorRunning']=!$dumpMonitor.HasExited; $record['counterCollectorRunning']=!$counters.HasExited
        if($dumpMonitor.HasExited -and !$process.HasExited){throw 'Full crash-dump monitor stopped while the test was running; see procdump.log.'}
        if($counters.HasExited -and !$process.HasExited){throw 'Runtime counter collection stopped while the test was running; see counter-errors.log.'}
        Save-Record
        Write-Output ("GPU run {0:N0}s: {1:N1} MiB private; {2} handles; crash monitor active" -f $clock.Elapsed.TotalSeconds,($process.PrivateMemorySize64/1MB),$process.HandleCount)
        $nextGpu=$clock.Elapsed.TotalSeconds+30
    }
    if(!$process.HasExited){throw 'Isolated GPU run timed out.'}
    $record['appExitCode']=$process.ExitCode
    if($process.ExitCode -ne 0){$record.status='crashed';throw "GPU window exited abnormally ($($process.ExitCode)); inspect full dumps."}
    $ui=Get-Content -LiteralPath (Join-Path $data 'ui-smoke.json') -Raw | ConvertFrom-Json
    if(!$ui.passed){throw $ui.error}
    $renderer=Get-Content -LiteralPath (Join-Path $data 'renderer-soak.json') -Raw | ConvertFrom-Json
    if(!$renderer.passed -or $renderer.durationSeconds -lt $Seconds){throw 'GPU soak did not complete its requested duration.'}
    $record.status='passed'; $record.passed=$true
    $record['checks']=$ui.checks.Count; $record['durationSeconds']=$renderer.durationSeconds
    $record['privateMemoryGrowthBytes']=$renderer.privateMemoryGrowthBytes
    $record['maxTextures']=$renderer.maxTextures; $record['maxLayouts']=$renderer.maxLayouts
}catch{
    if($record.status -notin @('crashed','interrupted')){$record.status='failed'}
    # Preserve native crash evidence even when the fixture loses its HWND first.
    $monitorLog=Join-Path $run 'procdump.log'
    if(Test-Path -LiteralPath $monitorLog){
        $monitorText=Get-Content -LiteralPath $monitorLog -Raw -Encoding Unicode
        if($monitorText -match 'Unhandled:\s+([0-9A-F]+)'){
            $record.status='crashed'; $record['unhandledExceptionCode']=$Matches[1]
        }
    }
    $record['error']=$_.Exception.ToString()
    Write-Error $_ -ErrorAction Continue
}finally{
    if($null -ne $process -and !$process.HasExited){
        # Only the isolated test process can be closed or terminated here.
        $process.CloseMainWindow() | Out-Null
        if(!$process.WaitForExit(10000)){$process.Kill(); $process.WaitForExit()}
    }
    if($null -ne $dumpMonitor -and !$dumpMonitor.HasExited){
        & $procDump -cancel $process.Id *> (Join-Path $run 'procdump-cancel.log')
        $dumpMonitor.WaitForExit(10000) | Out-Null
    }
    if($null -ne $counters -and !$counters.HasExited){$counters.Kill();$counters.WaitForExit()}
    $record['finished']=[DateTimeOffset]::Now.ToString('o')
    $record['fullDumps']=@(Get-ChildItem -LiteralPath (Join-Path $run 'dumps') -Filter '*.dmp' -ErrorAction SilentlyContinue | ForEach-Object FullName)
    Save-Record
}
if(!$record.passed){exit 1}
exit 0

