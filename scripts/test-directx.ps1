param([string]$AppDirectory='artifacts/directx-dev',[switch]$SkipBuild,[switch]$DebugLayer,[switch]$CaptureCrash,[switch]$Offscreen,[switch]$TrackHandles,[ValidatePattern('^[0-9A-Fa-f]{8}$')][string]$CaptureFirstChanceCode,[ValidateRange(0,86400)][int]$Seconds=0)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$app=if([IO.Path]::IsPathRooted($AppDirectory)){$AppDirectory}else{Join-Path $root $AppDirectory}
if($TrackHandles -and !(Test-Path (Join-Path $root '.tools/handle/handle64.exe'))){throw 'TrackHandles requires the Microsoft Sysinternals Handle utility at .tools/handle/handle64.exe.'}
if(!$SkipBuild){dotnet build (Join-Path $root 'src/Oroterm') -c Release -o $app;if($LASTEXITCODE -ne 0){throw 'DirectX diagnostic build failed.'}}
$data=Join-Path $root ('artifacts/directx-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $data | Out-Null
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $app 'Oroterm.exe'))
$start.UseShellExecute=$false;$start.WorkingDirectory=$root;$start.ArgumentList.Add('--directx-smoke')
$start.Environment['OROTERM_DATA_DIR']=$data
if($DebugLayer){$start.Environment['OROTERM_DIRECTX_DEBUG']='1'}
if($Offscreen){$start.Environment['OROTERM_DIRECTX_OFFSCREEN']='1'}
$start.Environment['OROTERM_DIRECTX_STRESS_SECONDS']=$Seconds.ToString()
$process=$null;$monitor=$null
try {
    $process=[Diagnostics.Process]::Start($start)
    if($CaptureCrash -or $CaptureFirstChanceCode){
        $dumps=Join-Path $data 'dumps';New-Item -ItemType Directory -Force -Path $dumps | Out-Null
        $dumpOptions=@('-accepteula','-ma','-g','-e')
        if($CaptureFirstChanceCode){$dumpOptions+=@('1','-f',$CaptureFirstChanceCode)}
        $dumpArguments=($dumpOptions+@($process.Id.ToString(),$dumps)) | ForEach-Object {'"'+$_+'"'}
        $monitor=Start-Process -FilePath (Join-Path $root '.tools/procdump/procdump64.exe') -ArgumentList $dumpArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $data 'procdump.log') -RedirectStandardError (Join-Path $data 'procdump-errors.log')
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(60+$Seconds)
    $nextHandleSample=[DateTime]::MinValue;$handleSample=0
    while(!$process.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline){
        if($TrackHandles -and [DateTime]::UtcNow -ge $nextHandleSample){
            $process.Refresh()
            if($process.HasExited){break}
            if($process.Path -ne (Join-Path $app 'Oroterm.exe')){throw 'Handle sampling process identity changed.'}
            & (Join-Path $root '.tools/handle/handle64.exe') -accepteula -nobanner -s -p $process.Id *> (Join-Path $data ('handles-'+$handleSample+'.txt'))
            [ordered]@{sample=$handleSample;timestamp=[DateTimeOffset]::Now.ToString('o');processId=$process.Id} | ConvertTo-Json -Compress | Add-Content (Join-Path $data 'handle-samples.jsonl')
            $handleSample++;$nextHandleSample=[DateTime]::UtcNow.AddSeconds(15)
        }
    }
    if(!$process.HasExited){throw 'Native renderer diagnostic timed out.'}
    if($process.ExitCode -ne 0){throw "Native renderer exited abnormally: $($process.ExitCode). See $data"}
    $path=Join-Path $data 'directx-smoke.json'
    if(!(Test-Path -LiteralPath $path)){throw "Native renderer produced no report: $data"}
    $report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if(!$report.passed){throw $report.error}
    $report.checks | ForEach-Object {Write-Host "PASS: $_"}
    Write-Host "DirectX evidence: $data"
}finally {
    if($null -ne $process -and !$process.HasExited){$process.CloseMainWindow()|Out-Null;if(!$process.WaitForExit(5000)){$process.Kill();$process.WaitForExit()}}
    if($null -ne $monitor -and !$monitor.HasExited){$monitor.WaitForExit(10000)|Out-Null}
}
