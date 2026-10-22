param([switch]$SkipNative, [switch]$Ui, [switch]$Published, [switch]$Notifications, [switch]$Sessions, [switch]$Browser, [switch]$Lua, [string]$ReleaseDirectory = 'artifacts/release', [ValidateRange(0, 86400)][int]$UiSoakSeconds = 0)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
if ($UiSoakSeconds -gt 0) { $Ui = $true }
if ($Notifications) { $Ui = $true }
if ($Sessions -or $Browser -or $Lua) { $Ui = $true }
if (!$SkipNative) { & "$PSScriptRoot/build-native.ps1" }
$smokeFolder = "$repoRoot/artifacts/smoke-test-$([Guid]::NewGuid().ToString('N'))"
dotnet build "$repoRoot/tests/Toiqtty.Smoke" -c Release -o $smokeFolder
if ($LASTEXITCODE -ne 0) { throw 'Terminal integration build failed.' }
if($Published) {
    $publishedRoot=if([IO.Path]::IsPathRooted($ReleaseDirectory)){$ReleaseDirectory}else{Join-Path $repoRoot $ReleaseDirectory}
    if(Test-Path -LiteralPath (Join-Path $publishedRoot 'Toiqtty.Core.dll')) {
        $sharedHashes=foreach($name in @('Toiqtty.Core.dll','Toiqtty.Client.dll','ghostty-vt.dll','toiqtty-native.dll')) {
            $source=Join-Path $publishedRoot $name
            Copy-Item -LiteralPath $source -Destination (Join-Path $smokeFolder $name) -Force
            [ordered]@{file=$name;sha256=(Get-FileHash -LiteralPath (Join-Path $smokeFolder $name)).Hash}
        }
        $sharedHashes | ConvertTo-Json | Set-Content -LiteralPath ($smokeFolder+'-published-assemblies.json')
        Write-Host 'Core checks are loading the exact published Core, Client and native DLLs.'
    }
}
& "$smokeFolder/Toiqtty.Smoke.exe"
if ($LASTEXITCODE -ne 0) { throw 'Terminal integration checks failed.' }
if ($Ui -or $Published) {
    $appFolder = if ([IO.Path]::IsPathRooted($ReleaseDirectory)) { $ReleaseDirectory } else { Join-Path $repoRoot $ReleaseDirectory }
    if (!$Published) {
        $appFolder = "$repoRoot/artifacts/ui-test"
        dotnet build "$repoRoot/src/Toiqtty/Toiqtty.csproj" -c Debug -o $appFolder
        if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    }
    $data = "$repoRoot/artifacts/ui-smoke-$([Guid]::NewGuid().ToString('N'))"
    $start = [Diagnostics.ProcessStartInfo]::new("$appFolder/toiqtty.exe")
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $repoRoot
    $start.ArgumentList.Add('--ui-smoke')
    $start.Environment['TOIQTTY_DATA_DIR'] = $data
    $start.Environment['TOIQTTY_SMOKE_EXE'] = "$smokeFolder/Toiqtty.Smoke.exe"
    $start.Environment['TOIQTTY_UI_SOAK_SECONDS'] = $UiSoakSeconds.ToString()
    $start.Environment['TOIQTTY_NOTIFICATION_SMOKE'] = $(if ($Notifications) { '1' } else { '0' })
    # The prompt color check must start PowerShell without the runner's NO_COLOR.
    # Toiqtty itself preserves an explicit user NO_COLOR preference.
    $start.Environment.Remove('NO_COLOR') | Out-Null
    $process = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(60 + $UiSoakSeconds)
    while (!$process.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) { }
    if (!$process.HasExited) { $process.Kill(); throw 'UI integration timed out.' }
    if (!(Test-Path "$data/ui-smoke.json")) { throw "UI integration produced no report. See $data/crash.log" }
    $report = Get-Content "$data/ui-smoke.json" -Raw | ConvertFrom-Json
    if (!$report.passed) { throw $report.error }
    $report.checks | ForEach-Object { Write-Host "PASS: $_" }
    if ($Notifications) {
        & "$smokeFolder/Toiqtty.Smoke.exe" --notification-activation "$appFolder/toiqtty.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Native notification activation checks failed.' }
    }
    if ($Sessions) {
        & "$smokeFolder/Toiqtty.Smoke.exe" --session-host "$appFolder/Toiqtty.SessionHost.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Persistent session host checks failed.' }
        & "$smokeFolder/Toiqtty.Smoke.exe" --session-ui "$appFolder/toiqtty.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Persistent WinUI reconnect checks failed.' }
        & "$smokeFolder/Toiqtty.Smoke.exe" --session-ui-crash "$appFolder/toiqtty.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Persistent WinUI crash recovery checks failed.' }
        & "$smokeFolder/Toiqtty.Smoke.exe" --navigation-ui "$appFolder/toiqtty.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Persistent pane move and reorder checks failed.' }
        & "$smokeFolder/Toiqtty.Smoke.exe" --scrolling-ui "$appFolder/toiqtty.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Persistent scrollbar and middle-click scrolling checks failed.' }
        & "$smokeFolder/Toiqtty.Smoke.exe" --resize-ui "$appFolder/toiqtty.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Persistent pane resize checks failed.' }
        & "$smokeFolder/Toiqtty.Smoke.exe" --input-ui "$appFolder/toiqtty.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Persistent input and frame notification checks failed.' }
        & "$smokeFolder/Toiqtty.Smoke.exe" --pane-reuse-ui "$appFolder/toiqtty.exe"
        if ($LASTEXITCODE -ne 0) { throw 'Retained pane and tab layout checks failed.' }
    }
    if ($Browser) {
        & "$PSScriptRoot/test-browser.ps1" -AppDirectory $appFolder -TestDirectory $smokeFolder -SkipBuild
    }
    if ($Lua) {
        & "$PSScriptRoot/test-lua.ps1" -AppDirectory $appFolder -TestDirectory $smokeFolder -SkipBuild
    }
}
}
finally {
    # These are this run's generated binaries, separate from retained reports.
    if ($smokeFolder) {
        $ownedRoot = [IO.Path]::GetFullPath("$repoRoot/artifacts") + [IO.Path]::DirectorySeparatorChar
        $ownedFolder = [IO.Path]::GetFullPath($smokeFolder)
        if ($ownedFolder.StartsWith($ownedRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($ownedFolder) -match '^smoke-test-[0-9a-f]{32}$') {
            try { Remove-Item -LiteralPath $ownedFolder -Recurse -Force -ErrorAction Stop }
            catch { Write-Warning "Retained test binaries still in use: $ownedFolder" }
        }
    }
    Pop-Location
}
