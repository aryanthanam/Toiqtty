param([string]$InstallDirectory=(Join-Path $env:LOCALAPPDATA 'Programs/Oroterm'),[string]$Version,[switch]$NoLaunch)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/release-tools.ps1"
$root=[IO.Path]::GetFullPath($InstallDirectory)
$installLease=[IO.FileStream]::new((Join-Path $root 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
$state=Get-Content -LiteralPath (Join-Path $root 'install-state.json') -Raw | ConvertFrom-Json
if(!$Version) { $Version=$state.previousVersion }
if(!$Version -or $Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'No valid rollback version is available.' }
$target=Resolve-OrotermChildPath (Join-Path $root 'versions') $Version
$externalManifest=Join-Path $root "rollback-metadata/$Version.json"
$manifest=Test-OrotermRelease $target -ManifestPath $(if(Test-Path -LiteralPath (Join-Path $target 'release-manifest.json')){$null}else{$externalManifest})
$newState=[ordered]@{schema=1;currentVersion=$Version;previousVersion=$state.currentVersion;shortcutDirectory=$state.shortcutDirectory;publisherThumbprint=$manifest.publisherThumbprint;updated=[DateTimeOffset]::Now.ToString('o')}
$shortcut=Set-OrotermActiveVersion $root $newState (Join-Path $target 'Oroterm.exe') $state.shortcutDirectory
$processId=if(!$NoLaunch){Start-OrotermInstalled (Join-Path $target 'Oroterm.exe')}else{$null}
[pscustomobject]@{version=$Version;executable=(Join-Path $target 'Oroterm.exe');shortcut=$shortcut;frontendProcessId=$processId;preservesRunningSessions=$true} | ConvertTo-Json
}finally { $installLease.Dispose() }
