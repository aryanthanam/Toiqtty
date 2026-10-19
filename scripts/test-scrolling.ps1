param([string]$AppDirectory = 'artifacts/scroll-dev', [string]$TestDirectory = 'artifacts/scroll-tests', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appFolder = if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $repoRoot $AppDirectory }
$testFolder = if ([IO.Path]::IsPathRooted($TestDirectory)) { $TestDirectory } else { Join-Path $repoRoot $TestDirectory }
Push-Location $repoRoot
try {
    if (!$SkipBuild) {
        & "$PSScriptRoot/build-native.ps1"
        dotnet build "$repoRoot/tests/Oroterm.Smoke/Oroterm.Smoke.csproj" -c Release -o $testFolder
        if ($LASTEXITCODE -ne 0) { throw 'Scrolling harness build failed.' }
        dotnet build "$repoRoot/src/Oroterm/Oroterm.csproj" -c Release -o $appFolder
        if ($LASTEXITCODE -ne 0) { throw 'Scrolling app build failed.' }
    }
    & "$testFolder/Oroterm.Smoke.exe" --scrolling
    if ($LASTEXITCODE -ne 0) { throw 'Native scrolling checks failed.' }
    & "$testFolder/Oroterm.Smoke.exe" --scrolling-ui "$appFolder/Oroterm.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Scrolling WinUI checks failed.' }
}
finally { Pop-Location }
