param([string]$AppDirectory = 'artifacts/lua-dev', [string]$TestDirectory = 'artifacts/lua-tests', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appFolder = if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $repoRoot $AppDirectory }
$testFolder = if ([IO.Path]::IsPathRooted($TestDirectory)) { $TestDirectory } else { Join-Path $repoRoot $TestDirectory }
if (!$SkipBuild) {
    dotnet build "$repoRoot/src/Oroterm/Oroterm.csproj" -c Debug -o $appFolder
    if ($LASTEXITCODE -ne 0) { throw 'Lua WinUI build failed.' }
    dotnet build "$repoRoot/tests/Oroterm.Smoke" -c Release -o $testFolder
    if ($LASTEXITCODE -ne 0) { throw 'Lua integration build failed.' }
}
& "$testFolder/Oroterm.Smoke.exe" --lua-config "$appFolder/Oroterm.SessionHost.exe"
if ($LASTEXITCODE -ne 0) { throw 'Lua config integration checks failed.' }
& "$testFolder/Oroterm.Smoke.exe" --lua-runtime "$appFolder/Oroterm.SessionHost.exe"
if ($LASTEXITCODE -ne 0) { throw 'Lua runtime integration checks failed.' }
& "$testFolder/Oroterm.Smoke.exe" --lua-ui "$appFolder/Oroterm.exe"
if ($LASTEXITCODE -ne 0) { throw 'Lua WinUI integration checks failed.' }
