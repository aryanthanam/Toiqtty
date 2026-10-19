param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$binaryRoot = Join-Path $repoRoot 'artifacts/hardware-test'
$clientRoot = Join-Path $repoRoot 'artifacts/hardware-client'
if (!$NoBuild) {
    dotnet build "$repoRoot/tests/Oroterm.Smoke" -c Release -o $clientRoot
    if ($LASTEXITCODE -ne 0) { throw 'Hardware input receiver build failed.' }
    dotnet build "$repoRoot/src/Oroterm" -c Debug -o $binaryRoot
    if ($LASTEXITCODE -ne 0) { throw 'Hardware test application build failed.' }
}
$data = Join-Path $repoRoot ('artifacts/hardware-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $data | Out-Null
$inventory = [ordered]@{
    timeUtc=[DateTime]::UtcNow.ToString('o')
    languages=@(Get-WinUserLanguageList | Select-Object LanguageTag,InputMethodTips)
    graphics=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,Status)
    releaseVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $repoRoot 'artifacts/release/Oroterm.dll')).ProductVersion
}
$inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $data 'inventory.json') -Encoding utf8
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $binaryRoot 'Oroterm.exe'))
$start.UseShellExecute = $false; $start.WorkingDirectory = $repoRoot
$start.ArgumentList.Add('--hardware-check')
$start.Environment['OROTERM_DATA_DIR'] = $data
$start.Environment['OROTERM_SMOKE_EXE'] = Join-Path $clientRoot 'Oroterm.Smoke.exe'
$start.Environment.Remove('NO_COLOR') | Out-Null
$process = [Diagnostics.Process]::Start($start)
[ordered]@{ processId=$process.Id; dataDirectory=$data; report=(Join-Path $data 'hardware-check.json'); fixture=(Join-Path $data 'drag fixture café.txt') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/hardware-current.json') -Encoding utf8
Write-Host "Hardware test process: $($process.Id)"
Write-Host "Disposable fixture and reports: $data"
