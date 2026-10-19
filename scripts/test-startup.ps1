param([string]$AppDirectory = 'artifacts/release-0.7.6', [ValidateRange(1,10)][int]$Iterations = 3, [switch]$Profile)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appFolder = if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $repoRoot $AppDirectory }
$runId = [Guid]::NewGuid().ToString('N')
$testFolder = Join-Path $repoRoot ('artifacts/startup-tests-' + $runId)
$copyFolder = Join-Path $repoRoot ('artifacts/startup-app-' + $runId)
if (!(Test-Path -LiteralPath (Join-Path $appFolder 'Oroterm.exe'))) { throw 'The startup benchmark application is missing.' }
# App SDK notification identity is scoped to the executable path, not the data
# directory. Benchmark a separate portable copy and retain it with the evidence.
New-Item -ItemType Directory -Force $copyFolder | Out-Null
Copy-Item -Path (Join-Path $appFolder '*') -Destination $copyFolder -Recurse
dotnet build "$repoRoot/tests/Oroterm.Smoke" -c Release -o $testFolder
if ($LASTEXITCODE -ne 0) { throw 'Startup harness build failed.' }
$arguments = @('--startup-benchmark',(Join-Path $copyFolder 'Oroterm.exe'),$Iterations.ToString())
if ($Profile) { $arguments += 'profile' }
& "$testFolder/Oroterm.Smoke.exe" @arguments
if ($LASTEXITCODE -ne 0) { throw 'Startup benchmark failed.' }

