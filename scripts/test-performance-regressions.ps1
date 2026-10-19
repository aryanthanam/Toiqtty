param([string]$AppDirectory='artifacts/release-0.7.6',[string]$TestDirectory='artifacts/performance-regression-tests',[string]$LegacyHostDirectory='artifacts/release-0.6.6',[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $root
try {
    $app=[IO.Path]::GetFullPath($AppDirectory); $client=Join-Path ([IO.Path]::GetFullPath($TestDirectory)) 'Oroterm.Smoke.exe'
    $folder=Join-Path $root ('artifacts/performance-regressions-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $folder | Out-Null
    if(!$SkipBuild){
        dotnet build (Join-Path $root 'tests/Oroterm.Smoke') -c Release -o ([IO.Path]::GetFullPath($TestDirectory)) *> (Join-Path $folder 'fixture-build.log')
        if($LASTEXITCODE -ne 0){throw "Performance regression fixture build failed: $folder"}
    }
    $results=@()
    & $client --process-waits *> (Join-Path $folder 'process-waits.log')
    if($LASTEXITCODE -ne 0){throw "Process wait regression failed: $folder"}
    $results+=@{phase='process-waits';exitCode=0;checks=@(Select-String -LiteralPath (Join-Path $folder 'process-waits.log') -Pattern '^PASS:').Count}
    & $client --flood-handshake *> (Join-Path $folder 'flood-handshake.log')
    if($LASTEXITCODE -ne 0){throw "Flood completion handshake regression failed: $folder"}
    $results+=@{phase='flood-handshake';exitCode=0;checks=3}
    foreach($phase in @('pane-reuse','frame-delta','background','navigation','resize','scrolling','input','input-close','input-native-close')){
        $watch=[Diagnostics.Stopwatch]::StartNew()
        & $client ("--$phase-ui") (Join-Path $app 'Oroterm.exe') *> (Join-Path $folder "$phase.log")
        $code=$LASTEXITCODE
        $checks=@(Select-String -LiteralPath (Join-Path $folder "$phase.log") -Pattern '^PASS:').Count
        $results+=@{phase=$phase;exitCode=$code;checks=$checks;seconds=$watch.Elapsed.TotalSeconds}
        $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'results.json')
        Get-Content -LiteralPath (Join-Path $folder "$phase.log") -Tail 2
        if($code -ne 0){throw "$phase regression failed: $folder"}
    }
    if($LegacyHostDirectory){
        foreach($phase in @('frame-delta-compat','input-compat')){
            & $client ("--$phase-ui") (Join-Path $app 'Oroterm.exe') (Join-Path ([IO.Path]::GetFullPath($LegacyHostDirectory)) 'Oroterm.SessionHost.exe') *> (Join-Path $folder "$phase.log")
            if($LASTEXITCODE -ne 0){throw "$phase regression failed: $folder"}
            $results+=@{phase=$phase;exitCode=$LASTEXITCODE;checks=@(Select-String -LiteralPath (Join-Path $folder "$phase.log") -Pattern '^PASS:').Count}
        }
    }
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'results.json')
    Write-Output "Performance/lifecycle regression evidence: $folder"
}finally { Pop-Location }

