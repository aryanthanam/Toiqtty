param([string]$AppDirectory = 'artifacts/ui-design', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appFolder = if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $repoRoot $AppDirectory }
if (!$SkipBuild) {
    dotnet build "$repoRoot/src/Oroterm/Oroterm.csproj" -c Debug -o $appFolder
    if ($LASTEXITCODE -ne 0) { throw 'UI design build failed.' }
}
$data = Join-Path $repoRoot "artifacts/design-$([Guid]::NewGuid().ToString('N'))"
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appFolder 'Oroterm.exe'))
$start.UseShellExecute = $false
$start.WorkingDirectory = $repoRoot
$start.ArgumentList.Add('--design-smoke')
$start.Environment['OROTERM_DATA_DIR'] = $data
$process = [Diagnostics.Process]::Start($start)
if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Design validation timed out.' }
if ($process.ExitCode -ne 0) { throw "Design validation exited abnormally ($($process.ExitCode))." }
$path = Join-Path $data 'design-smoke.json'
if (!(Test-Path -LiteralPath $path)) { throw "Design validation produced no report: $data" }
$report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if (!$report.passed) { throw $report.error }
$report.checks | ForEach-Object { Write-Host "PASS: $_" }
Write-Host "$($report.checks.Count) design checks passed."
Write-Host "Design evidence: $data"
