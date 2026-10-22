param([string]$InstallDirectory='',[string]$Version,[switch]$NoLaunch,[switch]$RequireSigned,[string]$ExpectedPublisherThumbprint)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/release-tools.ps1"
if(!$InstallDirectory) { $InstallDirectory=Get-ToiqttyInstallDirectory }
$root=[IO.Path]::GetFullPath($InstallDirectory)
$installLease=[IO.FileStream]::new((Join-Path $root 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
$state=Get-Content -LiteralPath (Join-Path $root 'install-state.json') -Raw | ConvertFrom-Json
if(!$Version) { $Version=$state.previousVersion }
if(!$Version -or $Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'No valid rollback version is available.' }
$target=Resolve-ToiqttyChildPath (Join-Path $root 'versions') $Version
$externalManifest=Join-Path $root "rollback-metadata/$Version.json"
# A signed installation only rolls back to a release signed by the same publisher.
if(!$ExpectedPublisherThumbprint -and $state.publisherThumbprint) { $ExpectedPublisherThumbprint=$state.publisherThumbprint }
if($ExpectedPublisherThumbprint) { $RequireSigned=$true }
$manifest=Test-ToiqttyRelease $target -RequireSigned:$RequireSigned -ExpectedPublisherThumbprint $ExpectedPublisherThumbprint -ManifestPath $(if(Test-Path -LiteralPath (Join-Path $target 'release-manifest.json')){$null}else{$externalManifest})
$newState=[ordered]@{schema=1;currentVersion=$Version;previousVersion=$state.currentVersion;shortcutDirectory=$state.shortcutDirectory;publisherThumbprint=$manifest.publisherThumbprint;updated=[DateTimeOffset]::Now.ToString('o')}
$executable=Get-ToiqttyExecutable $target
$shortcut=Set-ToiqttyActiveVersion $root $newState $executable $state.shortcutDirectory
$processId=if(!$NoLaunch){Start-ToiqttyInstalled $executable}else{$null}
[pscustomobject]@{version=$Version;executable=$executable;shortcut=$shortcut;frontendProcessId=$processId;preservesRunningSessions=$true} | ConvertTo-Json
}finally { $installLease.Dispose() }
