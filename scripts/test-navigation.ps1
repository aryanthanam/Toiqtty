param([string]$AppDirectory = 'artifacts/navigation-dev', [string]$TestDirectory = 'artifacts/navigation-tests', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appFolder = if ([IO.Path]::IsPathRooted($AppDirectory)) { $AppDirectory } else { Join-Path $repoRoot $AppDirectory }
$testFolder = if ([IO.Path]::IsPathRooted($TestDirectory)) { $TestDirectory } else { Join-Path $repoRoot $TestDirectory }
Push-Location $repoRoot
try {
    if (!$SkipBuild) {
        dotnet build "$repoRoot/tests/Oroterm.Smoke/Oroterm.Smoke.csproj" -c Release -o $testFolder
        if ($LASTEXITCODE -ne 0) { throw 'Navigation test build failed.' }
        dotnet build "$repoRoot/src/Oroterm/Oroterm.csproj" -c Debug -o $appFolder
        if ($LASTEXITCODE -ne 0) { throw 'Navigation app build failed.' }
    }
    & "$testFolder/Oroterm.Smoke.exe" --navigation-layout
    if ($LASTEXITCODE -ne 0) { throw 'Navigation layout checks failed.' }
    & "$testFolder/Oroterm.Smoke.exe" --navigation-ui "$appFolder/Oroterm.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Persistent navigation checks failed.' }
}
finally { Pop-Location }
