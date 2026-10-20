param(
    [string]$ReleaseDirectory='',
    [string]$InstallDirectory='',
    [string]$ShortcutDirectory=[Environment]::GetFolderPath('Programs'),
    [switch]$NoLaunch,
    [switch]$RequireSigned,
    [string]$ExpectedPublisherThumbprint
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/release-tools.ps1"
if(!$InstallDirectory) { $InstallDirectory=Get-ToiqttyInstallDirectory }
$repoRoot=Split-Path $PSScriptRoot -Parent
if(!$ReleaseDirectory) { $ReleaseDirectory=if(Test-Path -LiteralPath (Join-Path $repoRoot 'release-manifest.json')){$repoRoot}else{'artifacts/release'} }
$source=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($ReleaseDirectory)){$ReleaseDirectory}else{Join-Path $repoRoot $ReleaseDirectory}))
$root=[IO.Path]::GetFullPath($InstallDirectory)
$manifest=Test-ToiqttyRelease $source -RequireSigned:$RequireSigned -ExpectedPublisherThumbprint $ExpectedPublisherThumbprint
$versions=Join-Path $root 'versions'; $versionPath=Resolve-ToiqttyChildPath $versions $manifest.version
if($source.Equals($versionPath,[StringComparison]::OrdinalIgnoreCase)) { throw 'Install source and destination must differ.' }
New-Item -ItemType Directory -Force -Path $versions | Out-Null
$installLease=[IO.FileStream]::new((Join-Path $root 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
$statePath=Join-Path $root 'install-state.json'
$previous=if(Test-Path -LiteralPath $statePath){Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json}else{$null}
$stage=Join-Path $versions ('.stage-'+[Guid]::NewGuid().ToString('N'))
try {
    if(Test-Path -LiteralPath $versionPath) {
        $null=Test-ToiqttyRelease $versionPath -RequireSigned:$RequireSigned -ExpectedPublisherThumbprint $ExpectedPublisherThumbprint
        if((Get-FileHash -LiteralPath (Join-Path $source 'release-manifest.json')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $versionPath 'release-manifest.json')).Hash) { throw 'This version is already installed with different contents. Publish a new version.' }
    }
    else {
        New-Item -ItemType Directory -Path $stage | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $stage -Recurse -Force
        $null=Test-ToiqttyRelease $stage -RequireSigned:$RequireSigned -ExpectedPublisherThumbprint $ExpectedPublisherThumbprint
        Move-Item -LiteralPath $stage -Destination $versionPath
    }
    $executable=Get-ToiqttyExecutable $versionPath
    $previousVersion=if($previous -and $previous.currentVersion -ne $manifest.version){$previous.currentVersion}else{if($previous){$previous.previousVersion}else{$null}}
    $state=[ordered]@{schema=1;currentVersion=$manifest.version;previousVersion=$previousVersion;shortcutDirectory=$ShortcutDirectory;updated=[DateTimeOffset]::Now.ToString('o');publisherThumbprint=$manifest.publisherThumbprint}
    $shortcut=Set-ToiqttyActiveVersion $root $state $executable $ShortcutDirectory
    # Only the normal per-user install changes PATH; isolated/custom installs do not.
    $cliPathUpdated=$false
    $normalRoot=[IO.Path]::GetFullPath((Get-ToiqttyInstallDirectory))
    if($root.Equals($normalRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath (Join-Path $versionPath 'toiq.exe'))) {
        $userPath=[Environment]::GetEnvironmentVariable('Path','User')
        $versionPrefix=$versions+[IO.Path]::DirectorySeparatorChar
        $entries=@($userPath -split ';' | Where-Object { $_ -and !$_.Trim('"').StartsWith($versionPrefix,[StringComparison]::OrdinalIgnoreCase) })
        $nextPath=($entries+$versionPath)-join ';'
        if($nextPath -ne $userPath) {
            [Environment]::SetEnvironmentVariable('Path',$nextPath,'User')
            $cliPathUpdated=$true
            if(-not ('ToiqttyInstallEnvironment' -as [type])) {
                Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ToiqttyInstallEnvironment { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr parameter, string text, uint flags, uint timeout, out UIntPtr result); }'
            }
            $broadcastResult=[UIntPtr]::Zero
            [void][ToiqttyInstallEnvironment]::SendMessageTimeout([IntPtr]0xffff,0x1a,[UIntPtr]::Zero,'Environment',2,1000,[ref]$broadcastResult)
        }
    }
    $processId=if(!$NoLaunch){Start-ToiqttyInstalled $executable}else{$null}
    $cliName='toiq.exe'
    [pscustomobject]@{version=$manifest.version;executable=$executable;shortcut=$shortcut;cli=(Join-Path $versionPath $cliName);cliPathUpdated=$cliPathUpdated;verifiedFiles=$manifest.files.Count;frontendProcessId=$processId;previousVersion=$previousVersion;signed=$manifest.signed} | ConvertTo-Json
}
finally {
    $safeStage=Resolve-ToiqttyChildPath $versions ([IO.Path]::GetFileName($stage))
    if($safeStage -ne $stage) { throw 'Unexpected staging path.' }
    if(Test-Path -LiteralPath $safeStage) { Remove-Item -LiteralPath $safeStage -Recurse -Force }
}
}finally { $installLease.Dispose() }
