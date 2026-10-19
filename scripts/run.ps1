param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$NoBuild) {
    & "$PSScriptRoot/build-native.ps1"
    dotnet build "$repoRoot/src/Oroterm/Oroterm.csproj" -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'Oroterm build failed.' }
}
$exe = "$repoRoot/src/Oroterm/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Oroterm.exe"
Start-Process -FilePath $exe -WorkingDirectory $repoRoot
