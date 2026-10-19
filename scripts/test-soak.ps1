param([ValidateRange(30, 86400)][int]$Seconds = 600)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    $smokeFolder = "$repoRoot/artifacts/soak-test-$([Guid]::NewGuid().ToString('N'))"
    dotnet build tests/Oroterm.Smoke -c Release -o $smokeFolder
    if ($LASTEXITCODE -ne 0) { throw 'Terminal soak build failed.' }
    & "$smokeFolder/Oroterm.Smoke.exe" --soak $Seconds
    if ($LASTEXITCODE -ne 0) { throw 'Terminal soak failed.' }
}
finally {
    if ($smokeFolder) {
        $ownedRoot = [IO.Path]::GetFullPath("$repoRoot/artifacts") + [IO.Path]::DirectorySeparatorChar
        $ownedFolder = [IO.Path]::GetFullPath($smokeFolder)
        if ($ownedFolder.StartsWith($ownedRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($ownedFolder) -match '^soak-test-[0-9a-f]{32}$') {
            try { Remove-Item -LiteralPath $ownedFolder -Recurse -Force -ErrorAction Stop }
            catch { Write-Warning "Retained soak binaries still in use: $ownedFolder" }
        }
    }
    Pop-Location
}
