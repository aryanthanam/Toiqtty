param([string]$AppDirectory = 'artifacts/browser-dev', [string]$TestDirectory = 'artifacts/browser-tests', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appFolder = if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $repoRoot $AppDirectory }
$testFolder = if ([IO.Path]::IsPathRooted($TestDirectory)) { $TestDirectory } else { Join-Path $repoRoot $TestDirectory }
if (!$SkipBuild) {
    dotnet build "$repoRoot/src/Toiqtty/Toiqtty.csproj" -c Debug -o $appFolder
    if ($LASTEXITCODE -ne 0) { throw 'Browser WinUI build failed.' }
    dotnet build "$repoRoot/tests/Toiqtty.Smoke" -c Release -o $testFolder
    if ($LASTEXITCODE -ne 0) { throw 'Browser integration build failed.' }
}
& "$testFolder/Toiqtty.Smoke.exe" --browser-only
if ($LASTEXITCODE -ne 0) { throw 'Browser model and bridge checks failed.' }
node --test "$repoRoot/tests/browser-companion.test.cjs"
if ($LASTEXITCODE -ne 0) { throw 'Browser companion checks failed.' }
$data = "$repoRoot/artifacts/browser-smoke-$([Guid]::NewGuid().ToString('N'))"
$start = [Diagnostics.ProcessStartInfo]::new("$appFolder/toiqtty.exe")
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.WorkingDirectory = $repoRoot
$start.ArgumentList.Add('--browser-smoke')
$start.Environment['TOIQTTY_DATA_DIR'] = $data
$process = [Diagnostics.Process]::Start($start)
$deadline = [DateTime]::UtcNow.AddSeconds(100)
while (!$process.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) { }
if (!$process.HasExited) { $process.Kill(); throw "Browser UI integration timed out. See $data" }
if (!(Test-Path "$data/browser-smoke.json")) { throw "Browser UI integration produced no report. See $data/crash.log" }
$report = Get-Content "$data/browser-smoke.json" -Raw | ConvertFrom-Json
if (!$report.passed) { throw $report.error }
$report.checks | ForEach-Object { Write-Host "PASS: $_" }
Write-Host "Browser UI report: $data/browser-smoke.json"
$persistentData = "$repoRoot/artifacts/browser-persistence-$([Guid]::NewGuid().ToString('N'))"
foreach ($phase in @('prepare', 'reconnect')) {
    $start.Environment['TOIQTTY_DATA_DIR'] = $persistentData
    $start.Environment['TOIQTTY_BROWSER_UI_PROBE'] = $phase
    $process = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (!$process.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) { }
    if (!$process.HasExited) { $process.Kill(); throw "Browser persistence timed out. See $persistentData" }
    $reportPath = "$persistentData/browser-$phase.json"
    if (!(Test-Path $reportPath)) { throw "No browser persistence report. See $persistentData" }
    $report = Get-Content $reportPath -Raw | ConvertFrom-Json
    if (!$report.passed) { throw $report.error }
    $report.checks | ForEach-Object { Write-Host "PASS: $_" }
}
Write-Host "Browser persistence reports: $persistentData"
& "$testFolder/Toiqtty.Smoke.exe" --session-upgrade-window "$appFolder/toiqtty.exe"
if ($LASTEXITCODE -ne 0) { throw 'Browser release compatibility guard checks failed.' }
