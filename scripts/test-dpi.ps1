param([switch]$SkipBuild, [string]$AppDirectory = 'artifacts/dpi-test', [string]$ClientDirectory = 'artifacts/dpi-client')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appFolder = if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $repoRoot $AppDirectory }
$clientFolder = if ([IO.Path]::IsPathRooted($ClientDirectory)) { $ClientDirectory } else { Join-Path $repoRoot $ClientDirectory }
if (!$SkipBuild) {
    dotnet build "$repoRoot/tests/Oroterm.Smoke" -c Release -o $clientFolder
    if ($LASTEXITCODE -ne 0) { throw 'DPI fixture build failed.' }
    dotnet build "$repoRoot/src/Oroterm" -c Debug -o $appFolder
    if ($LASTEXITCODE -ne 0) { throw 'DPI WinUI build failed.' }
}
$data = Join-Path $repoRoot ('artifacts/dpi-check-' + [Guid]::NewGuid().ToString('N'))
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appFolder 'Oroterm.exe'))
$start.UseShellExecute = $false
$start.WorkingDirectory = $repoRoot
$start.ArgumentList.Add('--dpi-smoke')
$start.Environment['OROTERM_DATA_DIR'] = $data
$start.Environment['OROTERM_SMOKE_EXE'] = Join-Path $clientFolder 'Oroterm.Smoke.exe'
$start.Environment.Remove('NO_COLOR') | Out-Null
$process = [Diagnostics.Process]::Start($start)
$deadline = [DateTime]::UtcNow.AddSeconds(90)
while (!$process.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) { }
if (!$process.HasExited) { $process.Kill(); throw 'Isolated DPI test timed out.' }
if ($process.ExitCode -ne 0) { throw "Isolated DPI window exited abnormally ($($process.ExitCode)): $data" }
$reportPath = Join-Path $data 'dpi-smoke.json'
if (!(Test-Path -LiteralPath $reportPath)) { throw "DPI test produced no report: $data" }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$report.checks | ForEach-Object { Write-Host "PASS: $_" }
Write-Host "DPI evidence: $data"
if (!$report.passed) { throw $report.error }
Write-Host "$($report.checks.Count) physical DPI checks passed."
