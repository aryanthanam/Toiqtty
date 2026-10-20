param(
    [ValidateRange(30,86400)][int]$Seconds=10800,
    [string]$AppDirectory='artifacts/release-0.7.6',
    [string]$TestDirectory='',
    [switch]$CommandTrace,
    [switch]$PauseCheck,
    [switch]$Topmost,
    [switch]$RuntimeCounters,
    [switch]$FixedViewport,
    [ValidateSet('Native','Managed','None')][string]$HostDebuggerMode='Managed',
    [ValidateSet('Native','Managed','None')][string]$FrontendDebuggerMode='Native'
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/test-window.ps1"
Push-Location -LiteralPath $root
$appProcess=$null; $monitor=$null; $counters=$null; $hostMonitor=$null; $hostProcess=$null
$run=Join-Path $root ('artifacts/endurance-'+[Guid]::NewGuid().ToString('N'))
$record=[ordered]@{status='preparing';passed=$false;started=[DateTimeOffset]::Now.ToString('o');requestedSeconds=$Seconds;runDirectory=$run}
function Save-Record { $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'result.json') }
try {
    $source=[IO.Path]::GetFullPath($AppDirectory)
    $client=Join-Path $run 'client'
    $app=Join-Path $run 'app'; $data=Join-Path $run 'endurance-data'; $dumps=Join-Path $run 'dumps'
    New-Item -ItemType Directory -Force $app,$client,$data,$dumps | Out-Null
    Save-Record
    Copy-Item -Path (Join-Path $source '*') -Destination $app -Recurse
    if($TestDirectory){
        $sourceClient=[IO.Path]::GetFullPath($TestDirectory)
        Copy-Item -Path (Join-Path $sourceClient '*') -Destination $client -Recurse
    }else{
        dotnet build (Join-Path $root 'tests/Toiqtty.Smoke') -c Release -o $client *> (Join-Path $run 'fixture-build.log')
        if($LASTEXITCODE -ne 0){throw 'Endurance fixture build failed.'}
        $sourceClient=$client
    }
    $fixtureHashes=@(foreach($file in Get-ChildItem -LiteralPath $sourceClient -File -Recurse){
        $relative=[IO.Path]::GetRelativePath($sourceClient,$file.FullName); $hash=(Get-FileHash -LiteralPath $file.FullName).Hash
        if((Get-FileHash -LiteralPath (Join-Path $client $relative)).Hash -ne $hash){throw "Pinned fixture mismatch: $relative"}
        [ordered]@{file=$relative;sha256=$hash}
    })
    $fixtureHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'fixture-hashes.json')
    $record['verifiedFixtureFiles']=$fixtureHashes.Count
    $hashes=@(foreach($file in Get-ChildItem -LiteralPath $source -File -Recurse){
        $relative=[IO.Path]::GetRelativePath($source,$file.FullName); $hash=(Get-FileHash -LiteralPath $file.FullName).Hash
        if((Get-FileHash -LiteralPath (Join-Path $app $relative)).Hash -ne $hash){throw "Pinned build mismatch: $relative"}
        [ordered]@{file=$relative;sha256=$hash}
    })
    $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'build-hashes.json')
    $record['verifiedFiles']=$hashes.Count
    $procDump=Join-Path $root '.tools/procdump/procdump64.exe'
    $signature=Get-AuthenticodeSignature -LiteralPath $procDump
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Microsoft Corporation*'){throw 'Microsoft-signed ProcDump is required.'}
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $app 'toiqtty.exe'))
    $start.UseShellExecute=$false; $start.WorkingDirectory=$root; $start.ArgumentList.Add('--ui-smoke')
    foreach($key in @($start.Environment.Keys)){if($key -like 'TOIQTTY_*'){$start.Environment.Remove($key) | Out-Null}}
    $start.Environment['TOIQTTY_DATA_DIR']=$data
    $start.Environment['TOIQTTY_SESSION_UI_PROBE']='endurance'
    if($CommandTrace){$start.Environment['TOIQTTY_PERFORMANCE_COMMAND_TRACE']='1'}
    if($PauseCheck){$start.Environment['TOIQTTY_ENDURANCE_PAUSE_CHECK']='1'}
    $start.Environment['TOIQTTY_SESSION_SOAK_SECONDS']=$Seconds.ToString()
    $start.Environment['TOIQTTY_SMOKE_EXE']=Join-Path $client 'Toiqtty.Smoke.exe'
    $start.Environment['TOIQTTY_NOTIFICATION_SMOKE']='0'; $start.Environment.Remove('NO_COLOR') | Out-Null
    $foreground=Get-ToiqttyForegroundWindow
    $appProcess=[Diagnostics.Process]::Start($start)
    $record['appProcessId']=$appProcess.Id; $record['dataDirectory']=$data
    $record['windowPlacement']=Move-ToiqttyTestWindow $appProcess -Topmost:$Topmost -Primary:$FixedViewport
    if($FixedViewport){
        Start-Sleep -Milliseconds 300
        $geometry=Get-ToiqttyTestWindowGeometry $appProcess.MainWindowHandle
        $width=[int][Math]::Round(1280*$geometry.dpi/96); $height=[int][Math]::Round(820*$geometry.dpi/96)
        $record['windowPlacement']=Move-ToiqttyTestWindow $appProcess -Topmost:$Topmost -Primary -Width $width -Height $height
    }
    $record['foregroundRestored']=Restore-ToiqttyForegroundWindow $foreground
    if($FrontendDebuggerMode -ne 'None'){
        $frontendArguments=@('-accepteula','-ma','-e')
        if($FrontendDebuggerMode -eq 'Native'){$frontendArguments+='-g'}
        $frontendArguments+=@($appProcess.Id.ToString(),('"'+$dumps+'"'))
        $monitor=Start-Process -FilePath $procDump -ArgumentList $frontendArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'procdump.log') -RedirectStandardError (Join-Path $run 'procdump-errors.log')
    }
    $record['frontendDebuggerMode']=$FrontendDebuggerMode
    $hostReady=[DateTime]::UtcNow.AddSeconds(15); $hostIdentity=$null
    do {
        $hostIdentityPath=Join-Path $data 'host.json'
        if(Test-Path -LiteralPath $hostIdentityPath){
            try{$hostIdentity=Get-Content -LiteralPath $hostIdentityPath -Raw | ConvertFrom-Json}catch{$hostIdentity=$null}
        }
        if($null -eq $hostIdentity){Start-Sleep -Milliseconds 50;$appProcess.Refresh()}
    }while($null -eq $hostIdentity -and !$appProcess.HasExited -and [DateTime]::UtcNow -lt $hostReady)
    if($null -eq $hostIdentity){throw 'The isolated session host did not start for crash monitoring.'}
    $ownedHost=Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$hostIdentity.processId)"
    if(!$ownedHost -or $ownedHost.ParentProcessId -ne $appProcess.Id -or $ownedHost.ExecutablePath -ne (Join-Path $app 'Toiqtty.SessionHost.exe') -or $ownedHost.CommandLine -notlike "*$data*"){
        throw 'The session-host identity does not belong to this isolated test.'
    }
    $hostProcess=Get-Process -Id $ownedHost.ProcessId; $hostProcess.EnableRaisingEvents=$true
    $hostDumps=Join-Path $dumps 'host'; New-Item -ItemType Directory $hostDumps | Out-Null
    if($HostDebuggerMode -ne 'None'){
        $hostArguments=@('-accepteula','-ma','-e')
        if($HostDebuggerMode -eq 'Native'){$hostArguments+='-g'}
        $hostArguments+=@($hostProcess.Id.ToString(),('"'+$hostDumps+'"'))
        $hostMonitor=Start-Process -FilePath $procDump -ArgumentList $hostArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'host-procdump.log') -RedirectStandardError (Join-Path $run 'host-procdump-errors.log')
        $record['hostDumpMonitorProcessId']=$hostMonitor.Id
    }
    $record['hostProcessId']=$hostProcess.Id; $record['hostDebuggerMode']=$HostDebuggerMode
    if($RuntimeCounters){
        $counterTool=Join-Path $root '.tools/dotnet-diagnostics/dotnet-counters.exe'
        $counterArguments=@('collect','--process-id',$appProcess.Id.ToString(),'--counters','System.Runtime','--refresh-interval','10','--duration',[TimeSpan]::FromSeconds($Seconds+180).ToString(),'--format','csv','--output',(Join-Path $run 'runtime-counters.csv')) | ForEach-Object {'"'+$_+'"'}
        $counters=Start-Process -FilePath $counterTool -ArgumentList $counterArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'counter-collector.log') -RedirectStandardError (Join-Path $run 'counter-errors.log')
        $record['counterCollectorProcessId']=$counters.Id
    }
    $record.status='running'; Save-Record
    $deadlineOrigin=[DateTime]::UtcNow
    $deadline=$deadlineOrigin.AddSeconds($Seconds+180); $nextSample=[DateTime]::UtcNow; $capturedFailure=$false
    while(!$appProcess.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline){
        $captureErrors=Join-Path $data 'capture-errors.jsonl'
        if(!$capturedFailure -and (Test-Path -LiteralPath $captureErrors) -and (Get-Item -LiteralPath $captureErrors).Length -gt 0 -and !$hostProcess.HasExited){
            $capturedFailure=$true
            $record['firstCaptureFailure']=Get-Content -LiteralPath $captureErrors -Tail 1
            $failureDump=Join-Path $hostDumps 'first-capture-failure.dmp'
            & $procDump -accepteula -ma -r $hostProcess.Id $failureDump *> (Join-Path $run 'capture-failure-dump.log')
            $record['captureFailureDumpExitCode']=$LASTEXITCODE; $record['captureFailureDump']=$failureDump; Save-Record
        }
        if([DateTime]::UtcNow -lt $nextSample){continue}
        $appProcess.Refresh(); if($null -ne $monitor){$monitor.Refresh()}
        $pausePath=Join-Path $data 'endurance-pause.json'
        if(Test-Path -LiteralPath $pausePath){
            $pause=Get-Content -LiteralPath $pausePath -Raw | ConvertFrom-Json
            if(![double]::IsFinite($pause.pausedSeconds) -or $pause.pausedSeconds -lt 0 -or $pause.pausedSeconds -gt 1805){throw 'Invalid or excessive external suspension duration.'}
            $deadline=$deadlineOrigin.AddSeconds($Seconds+180+[Math]::Ceiling($pause.pausedSeconds))
            $record['externalSuspension']=$pause
        }
        if($null -ne $monitor -and $monitor.HasExited -and !$appProcess.HasExited){throw 'Crash monitor exited before the isolated test.'}
        if($null -ne $hostMonitor){$hostMonitor.Refresh();if($hostMonitor.HasExited -and !$appProcess.HasExited){throw 'Session-host crash monitor exited before the isolated test.'}}
        if($FixedViewport -and ![ToiqttyTestPlacement]::IsIconic($appProcess.MainWindowHandle)){
            try{$currentGeometry=Get-ToiqttyTestWindowGeometry $appProcess.MainWindowHandle}
            catch{if($appProcess.WaitForExit(1000)){break};throw}
            if($currentGeometry.width -ne $width -or $currentGeometry.height -ne $height -or $currentGeometry.dpi -ne $geometry.dpi){throw 'Fixed-viewport endurance size or DPI changed; exclude this performance measurement.'}
        }
        if($null -ne $counters){$counters.Refresh();if($counters.HasExited -and !$appProcess.HasExited){throw 'Runtime counter collection stopped before the isolated test.'}}
        $sample=[ordered]@{timestamp=[DateTimeOffset]::Now.ToString('o');privateBytes=$appProcess.PrivateMemorySize64;handles=$appProcess.HandleCount;threads=$appProcess.Threads.Count}
        $sample | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $run 'process-memory.jsonl')
        $record['latestProcessSample']=$sample; Save-Record
        Write-Output ("Endurance {0}: {1:N1} MiB private; {2} handles" -f $sample.timestamp,($sample.privateBytes/1MB),$sample.handles)
        $nextSample=[DateTime]::UtcNow.AddSeconds(30)
    }
    if(!$appProcess.HasExited){throw 'Session endurance timed out.'}
    $record['exitCode']=$appProcess.ExitCode
    if($appProcess.ExitCode -ne 0){throw "Session endurance exited abnormally: $($appProcess.ExitCode)"}
    $report=Get-Content -LiteralPath (Join-Path $data 'session-ui-endurance.json') -Raw | ConvertFrom-Json
    if(!$report.passed){if($report.interrupted){$record.status='interrupted'};throw $report.error}
    $endurance=Get-Content -LiteralPath (Join-Path $data 'endurance.json') -Raw | ConvertFrom-Json
    if(!$endurance.finished -or $endurance.elapsedSeconds -lt $Seconds){throw 'Endurance did not complete its requested duration.'}
    $record['endurance']=$endurance; $record['checks']=$report.checks.Count
    $record.status='passed'; $record.passed=$true
}catch { if($record.status -ne 'interrupted'){$record.status='failed'}; $record['error']=$_.Exception.ToString(); Write-Error $_ -ErrorAction Continue }
finally {
    if($null -ne $appProcess -and !$appProcess.HasExited){$appProcess.CloseMainWindow() | Out-Null; if(!$appProcess.WaitForExit(10000)){$appProcess.Kill(); $appProcess.WaitForExit()}}
    if($null -ne $monitor -and !$monitor.HasExited){ & $procDump -cancel $appProcess.Id *> (Join-Path $run 'procdump-cancel.log'); $monitor.WaitForExit(10000) | Out-Null }
    if($null -ne $counters -and !$counters.HasExited){$counters.Kill();$counters.WaitForExit()}
    if(Test-Path -LiteralPath (Join-Path $client 'Toiqtty.Smoke.exe')){ & (Join-Path $client 'Toiqtty.Smoke.exe') --stop-endurance-host $data }
    if($null -ne $hostProcess){
        if(!$hostProcess.WaitForExit(15000)){$record.status='failed';$record.passed=$false;$record['hostCleanupError']='The isolated host did not exit after its authorized shutdown.'}
        else{$record['hostExitCode']=$hostProcess.ExitCode;if($hostProcess.ExitCode -ne 0){$record.status='failed';$record.passed=$false;$record['hostCleanupError']="Host exited abnormally: $($hostProcess.ExitCode)"}}
    }
    if($null -ne $hostMonitor -and !$hostMonitor.HasExited){
        if(!$hostMonitor.WaitForExit(10000)){& $procDump -cancel $hostProcess.Id *> (Join-Path $run 'host-procdump-cancel.log');$hostMonitor.WaitForExit(10000)|Out-Null}
    }
    $record['finished']=[DateTimeOffset]::Now.ToString('o'); Save-Record; Pop-Location
}
if(!$record.passed){exit 1}
Write-Output "Endurance evidence: $run"
exit 0

