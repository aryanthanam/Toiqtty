param([string]$OldReleaseDirectory='artifacts/release-0.6.9',[string]$ReleaseDirectory='artifacts/release-0.7.6')
$ErrorActionPreference='Stop'
. "$PSScriptRoot/release-tools.ps1"
$repo=Split-Path $PSScriptRoot -Parent
$run=Join-Path $repo ('artifacts/release-check-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $run | Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Value,[string]$Description) { if(!$Value){throw $Description};$checks.Add($Description);Write-Output "PASS: $Description" }
function Reject([scriptblock]$Action,[string]$Description) { $failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed $Description }
function Fixture([string]$Source,[string]$Name) {
    $target=Join-Path $run $Name; New-Item -ItemType Directory -Force -Path (Join-Path $target 'Assets') | Out-Null
    foreach($name in @('toiqtty.exe','toiqtty.dll','Toiqtty.SessionHost.exe','Toiqtty.SessionHost.dll','ghostty-vt.dll','toiqtty-native.dll','toiqtty.pri','MainWindow.xbf','Assets/AppIcon.ico')) {
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination (Join-Path $target $name)
    }
    foreach($name in @('Toiqtty.Core.dll','Toiqtty.Client.dll','toiqtty-renderer.dll','toiq.exe','toiq.dll','Configuration/toiqtty.example.lua')) {
        $sourceFile=Join-Path $Source $name
        if(Test-Path -LiteralPath $sourceFile) {
            New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $target $name)) | Out-Null
            Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $target $name)
        }
    }
    & "$PSScriptRoot/new-release-manifest.ps1" -Directory $target | Out-Null
    return $target
}
function State([string]$Root) { Get-Content -LiteralPath (Join-Path $Root 'install-state.json') -Raw | ConvertFrom-Json }
function Archive([string]$Path,[string[]]$Entries) {
    $stream=[IO.File]::Create($Path); $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
    try { foreach($name in $Entries){$entry=$zip.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open());try{$writer.Write('test')}finally{$writer.Dispose()}} }
    finally {$zip.Dispose();$stream.Dispose()}
}
$old=Fixture ([IO.Path]::GetFullPath((Join-Path $repo $OldReleaseDirectory))) 'old-source'
$new=Fixture ([IO.Path]::GetFullPath((Join-Path $repo $ReleaseDirectory))) 'new-source'
$oldVersion=(Test-ToiqttyRelease $old).version; $newVersion=(Test-ToiqttyRelease $new).version
if($oldVersion -eq $newVersion){throw 'Release checks require different old and new versions.'}
if([version](($newVersion -split '-')[0]) -ge [version]'0.8.0') {
    $missingRenderer=Get-Content -LiteralPath (Join-Path $new 'release-manifest.json') -Raw | ConvertFrom-Json
    $missingRenderer.files=@($missingRenderer.files | Where-Object path -ne 'toiqtty-renderer.dll')
    $missingPath=Join-Path $run 'missing-renderer-manifest.json'
    $missingRenderer | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $missingPath
    Reject { Test-ToiqttyRelease $new -ManifestPath $missingPath } 'Native DirectX releases reject a manifest that omits the renderer module'
}
$install=Join-Path $run 'installed'; $shortcuts=Join-Path $run 'shortcuts'; $readLock=$null
try {
    $result=& "$PSScriptRoot/install.ps1" -ReleaseDirectory $old -InstallDirectory $install -ShortcutDirectory $shortcuts -NoLaunch | ConvertFrom-Json
    Check ((State $install).currentVersion -eq $oldVersion) 'Initial installation verifies and activates the requested version'
    $oldExe=Join-Path $install "versions/$oldVersion/toiqtty.exe"; $oldHash=(Get-FileHash -LiteralPath $oldExe).Hash
    $readLock=[IO.File]::Open($oldExe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    & "$PSScriptRoot/install.ps1" -ReleaseDirectory $new -InstallDirectory $install -ShortcutDirectory $shortcuts -NoLaunch | Out-Null
    Check ((State $install).currentVersion -eq $newVersion -and (State $install).previousVersion -eq $oldVersion -and (Get-FileHash -LiteralPath $oldExe).Hash -eq $oldHash) 'Updating preserves a locked older executable and records its rollback version'
    & "$PSScriptRoot/install.ps1" -ReleaseDirectory $new -InstallDirectory $install -ShortcutDirectory $shortcuts -NoLaunch | Out-Null
    Check ((State $install).previousVersion -eq $oldVersion) 'Reinstalling identical contents is idempotent and retains rollback history'
    & "$PSScriptRoot/rollback.ps1" -InstallDirectory $install -NoLaunch | Out-Null
    Check ((State $install).currentVersion -eq $oldVersion) 'Rollback changes the launch target without replacing loaded binaries'
    $bytes=[IO.File]::ReadAllBytes((Join-Path $new 'toiqtty.dll')); $changed=[byte[]]$bytes.Clone();$changed[0]=$changed[0] -bxor 1
    [IO.File]::WriteAllBytes((Join-Path $new 'toiqtty.dll'),$changed)
    Reject { & "$PSScriptRoot/install.ps1" -ReleaseDirectory $new -InstallDirectory $install -ShortcutDirectory $shortcuts -NoLaunch } 'Tampered release payloads are rejected before changing the active version'
    [IO.File]::WriteAllBytes((Join-Path $new 'toiqtty.dll'),$bytes)
    Check ((State $install).currentVersion -eq $oldVersion) 'Failed verification leaves the previous installation active'
    $manifestPath=Join-Path $new 'release-manifest.json'; $manifestText=Get-Content -LiteralPath $manifestPath -Raw
    $manifest=$manifestText|ConvertFrom-Json; $manifest.files[0].path='../escaped.dll'; $manifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $manifestPath
    Reject { Test-ToiqttyRelease $new } 'Manifest paths cannot escape the release directory'
    Set-Content -LiteralPath $manifestPath -Value $manifestText -NoNewline
    Set-Content -LiteralPath (Join-Path $new 'unlisted.txt') -Value 'unexpected'
    Reject { Test-ToiqttyRelease $new } 'Unlisted files are rejected by complete-release verification'
    Remove-Item -LiteralPath (Join-Path $new 'unlisted.txt')
    $manifest=$manifestText|ConvertFrom-Json;$manifest.version='99.99.99';$manifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $manifestPath
    Reject { Test-ToiqttyRelease $new } 'The executable version must match its release manifest'
    Set-Content -LiteralPath $manifestPath -Value $manifestText -NoNewline
    $archive=Join-Path $run 'update.zip'; [IO.Compression.ZipFile]::CreateFromDirectory($new,$archive)
    Reject { & "$PSScriptRoot/update.ps1" -ArchivePath $archive -ExpectedSha256 ('0'*64) -InstallDirectory $install -ShortcutDirectory $shortcuts -NoLaunch } 'Update archives require their expected SHA-256 checksum'
    & "$PSScriptRoot/update.ps1" -ArchivePath $archive -ExpectedSha256 (Get-FileHash -LiteralPath $archive).Hash -InstallDirectory $install -ShortcutDirectory $shortcuts -NoLaunch | Out-Null
    Check ((State $install).currentVersion -eq $newVersion) 'A verified ZIP update stages, verifies and activates successfully'
    $bad=Join-Path $run 'traversal.zip'; Archive $bad @('../../escaped.txt')
    Reject { & "$PSScriptRoot/update.ps1" -ArchivePath $bad -ExpectedSha256 (Get-FileHash -LiteralPath $bad).Hash -InstallDirectory $install -ShortcutDirectory $shortcuts -NoLaunch } 'ZIP traversal entries are rejected before extraction'
    $duplicate=Join-Path $run 'duplicate.zip'; Archive $duplicate @('toiqtty.exe','toiqtty.exe')
    Reject { & "$PSScriptRoot/update.ps1" -ArchivePath $duplicate -ExpectedSha256 (Get-FileHash -LiteralPath $duplicate).Hash -InstallDirectory $install -ShortcutDirectory $shortcuts -NoLaunch } 'ZIP paths are checked for duplicates using Windows case rules'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $install 'updates') -Directory).Count -eq 0) 'Successful and rejected updates clean only their own staging directories'
    $link=Join-Path $shortcuts 'toiqtty.lnk';$stateBefore=Get-Content -LiteralPath (Join-Path $install 'install-state.json') -Raw
    $lockedLink=[IO.File]::Open($link,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { Reject { & "$PSScriptRoot/rollback.ps1" -InstallDirectory $install -NoLaunch } 'A locked shortcut prevents activation cleanly' }
    finally {$lockedLink.Dispose()}
    Check ((Get-Content -LiteralPath (Join-Path $install 'install-state.json') -Raw) -eq $stateBefore) 'Failed shortcut activation restores the exact previous installation state'
    $lease=[IO.File]::Open((Join-Path $install 'install.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { Reject { & "$PSScriptRoot/rollback.ps1" -InstallDirectory $install -NoLaunch } 'Concurrent installation changes require the exclusive install lease' }
    finally {$lease.Dispose()}
    [ordered]@{passed=$true;checks=$checks;oldVersion=$oldVersion;newVersion=$newVersion;fixture='Minimal real versioned payloads, no frontend or shell termination; complete runtime checks are separate'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $run 'result.json')
    Write-Output "Release checks: $run"
}
finally {if($readLock){$readLock.Dispose()}}
