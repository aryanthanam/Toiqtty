param(
    [string]$ReleaseDirectory='',
    [string]$InstallDirectory=(Join-Path $env:LOCALAPPDATA 'Programs/Oroterm'),
    [string]$ShortcutDirectory=[Environment]::GetFolderPath('Programs'),
    [switch]$NoLaunch,
    [switch]$RequireSigned,
    [string]$ExpectedPublisherThumbprint
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/release-tools.ps1"
$repoRoot=Split-Path $PSScriptRoot -Parent
if(!$ReleaseDirectory) { $ReleaseDirectory=if(Test-Path -LiteralPath (Join-Path $repoRoot 'release-manifest.json')){$repoRoot}else{'artifacts/release'} }
$source=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($ReleaseDirectory)){$ReleaseDirectory}else{Join-Path $repoRoot $ReleaseDirectory}))
$root=[IO.Path]::GetFullPath($InstallDirectory)
$manifest=Test-OrotermRelease $source -RequireSigned:$RequireSigned -ExpectedPublisherThumbprint $ExpectedPublisherThumbprint
$versions=Join-Path $root 'versions'; $versionPath=Resolve-OrotermChildPath $versions $manifest.version
if($source.Equals($versionPath,[StringComparison]::OrdinalIgnoreCase)) { throw 'Install source and destination must differ.' }
New-Item -ItemType Directory -Force -Path $versions | Out-Null
$installLease=[IO.FileStream]::new((Join-Path $root 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
$statePath=Join-Path $root 'install-state.json'
$previous=if(Test-Path -LiteralPath $statePath){Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json}else{$null}
if(!$previous -and (Test-Path -LiteralPath (Join-Path $ShortcutDirectory 'Oroterm.lnk'))) {
    $shell=New-Object -ComObject WScript.Shell
    try { $oldTarget=$shell.CreateShortcut((Join-Path $ShortcutDirectory 'Oroterm.lnk')).TargetPath }
    finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    $versionRoot=[IO.Path]::GetFullPath($versions)+[IO.Path]::DirectorySeparatorChar
    if($oldTarget -and $oldTarget.StartsWith($versionRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($oldTarget) -eq 'Oroterm.exe') {
        $oldVersion=[IO.Path]::GetFileName((Split-Path $oldTarget))
        if($oldVersion -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -and (Test-Path -LiteralPath $oldTarget)) {
            $metadata=Join-Path $root 'rollback-metadata'; New-Item -ItemType Directory -Force -Path $metadata | Out-Null
            & "$PSScriptRoot/new-release-manifest.ps1" -Directory (Split-Path $oldTarget) -OutputPath (Join-Path $metadata "$oldVersion.json") | Out-Null
            $previous=[pscustomobject]@{currentVersion=$oldVersion;previousVersion=$null;publisherThumbprint=$null}
        }
    }
}
$stage=Join-Path $versions ('.stage-'+[Guid]::NewGuid().ToString('N'))
try {
    if(Test-Path -LiteralPath $versionPath) {
        $null=Test-OrotermRelease $versionPath -RequireSigned:$RequireSigned -ExpectedPublisherThumbprint $ExpectedPublisherThumbprint
        if((Get-FileHash -LiteralPath (Join-Path $source 'release-manifest.json')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $versionPath 'release-manifest.json')).Hash) { throw 'This version is already installed with different contents. Publish a new version.' }
    }
    else {
        New-Item -ItemType Directory -Path $stage | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $stage -Recurse -Force
        $null=Test-OrotermRelease $stage -RequireSigned:$RequireSigned -ExpectedPublisherThumbprint $ExpectedPublisherThumbprint
        Move-Item -LiteralPath $stage -Destination $versionPath
    }
    $executable=Join-Path $versionPath 'Oroterm.exe'
    $previousVersion=if($previous -and $previous.currentVersion -ne $manifest.version){$previous.currentVersion}else{if($previous){$previous.previousVersion}else{$null}}
    $state=[ordered]@{schema=1;currentVersion=$manifest.version;previousVersion=$previousVersion;shortcutDirectory=$ShortcutDirectory;updated=[DateTimeOffset]::Now.ToString('o');publisherThumbprint=$manifest.publisherThumbprint}
    $shortcut=Set-OrotermActiveVersion $root $state $executable $ShortcutDirectory
    # Only the normal per-user install changes PATH; isolated/custom installs do not.
    $cliPathUpdated=$false
    $normalRoot=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/Oroterm'))
    if($root.Equals($normalRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath (Join-Path $versionPath 'oro.exe'))) {
        $userPath=[Environment]::GetEnvironmentVariable('Path','User')
        $versionPrefix=$versions+[IO.Path]::DirectorySeparatorChar
        $entries=@($userPath -split ';' | Where-Object { $_ -and !$_.Trim('"').StartsWith($versionPrefix,[StringComparison]::OrdinalIgnoreCase) })
        $nextPath=($entries+$versionPath)-join ';'
        if($nextPath -ne $userPath) {
            [Environment]::SetEnvironmentVariable('Path',$nextPath,'User')
            $cliPathUpdated=$true
            if(-not ('OrotermInstallEnvironment' -as [type])) {
                Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class OrotermInstallEnvironment { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr parameter, string text, uint flags, uint timeout, out UIntPtr result); }'
            }
            $broadcastResult=[UIntPtr]::Zero
            [void][OrotermInstallEnvironment]::SendMessageTimeout([IntPtr]0xffff,0x1a,[UIntPtr]::Zero,'Environment',2,1000,[ref]$broadcastResult)
        }
    }
    $processId=if(!$NoLaunch){Start-OrotermInstalled $executable}else{$null}
    [pscustomobject]@{version=$manifest.version;executable=$executable;shortcut=$shortcut;cli=(Join-Path $versionPath 'oro.exe');cliPathUpdated=$cliPathUpdated;verifiedFiles=$manifest.files.Count;frontendProcessId=$processId;previousVersion=$previousVersion;signed=$manifest.signed} | ConvertTo-Json
}
finally {
    $safeStage=Resolve-OrotermChildPath $versions ([IO.Path]::GetFileName($stage))
    if($safeStage -ne $stage) { throw 'Unexpected staging path.' }
    if(Test-Path -LiteralPath $safeStage) { Remove-Item -LiteralPath $safeStage -Recurse -Force }
}
}finally { $installLease.Dispose() }
