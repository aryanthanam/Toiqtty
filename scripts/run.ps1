param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$NoBuild) {
    & "$PSScriptRoot/build-native.ps1"
    dotnet build "$repoRoot/src/Toiqtty/Toiqtty.csproj" -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'toiqtty build failed.' }
}
$exe = "$repoRoot/src/Toiqtty/bin/Debug/net10.0-windows10.0.26100.0/win-x64/toiqtty.exe"
Start-Process -FilePath $exe -WorkingDirectory $repoRoot
