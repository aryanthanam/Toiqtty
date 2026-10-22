# One gate for the frontend, renderer and native code that the CI build job
# does not exercise. Run it before and after any WinUI, renderer or native change.
param([switch]$SkipUi, [switch]$Performance)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$timings = [ordered]@{}
function Invoke-Stage([string]$name, [scriptblock]$body) {
    Write-Host "=== $name" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $body
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
    $timings[$name] = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
}
Push-Location -LiteralPath $root
try {
    Invoke-Stage 'Native CPU lifetimes' { & "$PSScriptRoot/test-native-lifetimes.ps1" }
    Invoke-Stage 'Native terminal capture' { & "$PSScriptRoot/test-native-capture.ps1" }
    Invoke-Stage 'Native GPU lifetimes (hardware and WARP)' { & "$PSScriptRoot/test-native-gpu-lifetimes.ps1" }
    Invoke-Stage 'DirectX renderer with debug layer (offscreen)' { & "$PSScriptRoot/test-directx.ps1" -DebugLayer -Offscreen }
    if (!$SkipUi) {
        # Opens isolated test windows on the desktop; each uses its own data folder.
        Invoke-Stage 'WinUI, sessions, browser and Lua' { & "$PSScriptRoot/test.ps1" -SkipNative -Ui -Sessions -Browser -Lua }
    }
    if ($Performance) {
        Invoke-Stage 'Publish release with diagnostics' { & "$PSScriptRoot/publish.ps1" -Diagnostics }
        Invoke-Stage 'Performance regressions' { & "$PSScriptRoot/test-performance-regressions.ps1" }
    }
    Write-Host ''
    $timings.GetEnumerator() | ForEach-Object { Write-Host ("PASS  {0,7}s  {1}" -f $_.Value, $_.Key) -ForegroundColor Green }
}
finally { Pop-Location }
