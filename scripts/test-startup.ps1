param([string]$AppDirectory = 'artifacts/release-0.10.2-startup', [ValidateRange(1,10)][int]$Iterations = 3, [switch]$Profile, [switch]$UserConfig, [switch]$ImmediateShell)
$ErrorActionPreference = 'Stop'
if ($Profile -and $ImmediateShell) { throw 'Choose either the PowerShell profile or the immediate-output shell.' }
$repoRoot = Split-Path $PSScriptRoot -Parent
$appFolder = if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $repoRoot $AppDirectory }
$runId = [Guid]::NewGuid().ToString('N')
$testFolder = Join-Path $repoRoot ('artifacts/startup-tests-' + $runId)
$copyFolder = Join-Path $repoRoot ('artifacts/startup-app-' + $runId)
if (!(Test-Path -LiteralPath (Join-Path $appFolder 'toiqtty.exe'))) { throw 'The startup benchmark application is missing.' }
# App SDK notification identity is scoped to the executable path, not the data
# directory. Benchmark a separate portable copy and retain it with the evidence.
New-Item -ItemType Directory -Force $copyFolder | Out-Null
Copy-Item -Path (Join-Path $appFolder '*') -Destination $copyFolder -Recurse
dotnet build "$repoRoot/tests/Toiqtty.Smoke" -c Release -o $testFolder
if ($LASTEXITCODE -ne 0) { throw 'Startup harness build failed.' }
$arguments = @('--startup-benchmark',(Join-Path $copyFolder 'toiqtty.exe'),$Iterations.ToString())
if ($Profile) { $arguments += 'profile' }
if ($ImmediateShell) { $arguments += 'immediate' }
$previousConfigSource=$env:TOIQTTY_STARTUP_CONFIG_SOURCE
try {
    if($UserConfig) { $env:TOIQTTY_STARTUP_CONFIG_SOURCE=Join-Path $env:LOCALAPPDATA 'toiqtty' }
    & "$testFolder/Toiqtty.Smoke.exe" @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Startup benchmark failed.' }
}
finally { $env:TOIQTTY_STARTUP_CONFIG_SOURCE=$previousConfigSource }

