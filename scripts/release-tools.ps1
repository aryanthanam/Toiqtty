# Shared release verification and activation. Never terminate live processes.
function Get-ToiqttyInstallDirectory { return Join-Path $env:LOCALAPPDATA 'Programs/toiqtty' }
function Get-ToiqttyExecutable([string]$Directory) { return Join-Path $Directory 'toiqtty.exe' }
function Resolve-ToiqttyChildPath([string]$Root,[string]$Relative) {
    $base=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw "Invalid release path: $Relative" }
    $path=[IO.Path]::GetFullPath((Join-Path $base $Relative))
    if(!$path.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw "Release path escapes its directory: $Relative" }
    return $path
}
function Test-ToiqttyRelease([string]$Directory,[switch]$RequireSigned,[string]$ExpectedPublisherThumbprint,[string]$ManifestPath) {
    $root=[IO.Path]::GetFullPath($Directory)
    $manifestPath=if($ManifestPath){$ManifestPath}else{Join-Path $root 'release-manifest.json'}
    if(!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Release manifest is missing. Publish a complete release before installing it.' }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if($manifest.schema -ne 1 -or $manifest.platform -ne 'win-x64' -or $manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Unsupported release manifest.' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $manifest.files) {
        if($entry.path.Contains('\') -or ($entry.path.Split('/') | Where-Object { $_ -in @('','.', '..') })) { throw 'Release manifest paths must be normalized relative paths.' }
        $path=Resolve-ToiqttyChildPath $root $entry.path
        if(!$seen.Add($entry.path) -or $entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid or duplicate release manifest entry.' }
        $file=Get-Item -LiteralPath $path -ErrorAction Stop
        if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -ne $entry.size -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Release file verification failed: $($entry.path)" }
    }
    foreach($required in @('toiqtty.exe','toiqtty.dll','Toiqtty.SessionHost.exe','Toiqtty.SessionHost.dll','ghostty-vt.dll','toiqtty-native.dll','toiqtty.pri','MainWindow.xbf','Assets/AppIcon.ico')) {
        if(!$seen.Contains($required)) { throw "Release manifest omits $required." }
    }
    if([version](($manifest.version -split '-')[0]) -ge [version]'0.7.8') {
        foreach($required in @('Toiqtty.Core.dll','Toiqtty.Client.dll')) {
            if(!$seen.Contains($required)) { throw "Release manifest omits $required." }
        }
    }
    if([version](($manifest.version -split '-')[0]) -ge [version]'0.8.0' -and !$seen.Contains('toiqtty-renderer.dll')) {
        throw 'Release manifest omits toiqtty-renderer.dll.'
    }
    foreach($required in @('toiq.exe','toiq.dll','Configuration/toiqtty.example.lua')) {
        if(!$seen.Contains($required)) { throw "Release manifest omits $required." }
    }
    $binaryVersion=([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'toiqtty.exe')).ProductVersion -split '\+')[0]
    if($binaryVersion -ne $manifest.version) { throw 'Release manifest version does not match the executable.' }
    $unexpected=@(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object {
        $relative=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/')
        $relative -notin @('release-manifest.json','release.cat') -and !$seen.Contains($relative)
    })
    if($unexpected.Count) { throw 'Release contains files absent from its manifest.' }
    if($RequireSigned -and !$manifest.signed) { throw 'A signed release is required.' }
    if($manifest.signed) {
        $publisher=$manifest.publisherThumbprint
        if($publisher -notmatch '^[0-9A-Fa-f]{40}$' -or ($ExpectedPublisherThumbprint -and $publisher -ne $ExpectedPublisherThumbprint)) { throw 'Release publisher does not match the expected certificate.' }
        foreach($binary in @('toiqtty.exe','toiqtty.dll','Toiqtty.SessionHost.exe','Toiqtty.SessionHost.dll','toiqtty-native.dll','release.cat')) {
            $signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $root $binary)
            if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $publisher) { throw "Invalid publisher signature: $binary" }
        }
        $catalog=Test-FileCatalog -Path $root -CatalogFilePath (Join-Path $root 'release.cat') -FilesToSkip 'release.cat'
        if($catalog -ne 'Valid') { throw 'Signed catalog does not match the complete release.' }
    }
    elseif($ExpectedPublisherThumbprint) { throw 'An unsigned release cannot satisfy a publisher requirement.' }
    return $manifest
}
function Write-ToiqttyInstallState([string]$Root,$State) {
    $path=Join-Path $Root 'install-state.json'; $temporary=$path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try { $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8; [IO.File]::Move($temporary,$path,$true) }
    finally { if(Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
function Set-ToiqttyShortcut([string]$Executable,[string]$ShortcutDirectory) {
    New-Item -ItemType Directory -Force -Path $ShortcutDirectory | Out-Null
    $path=Join-Path $ShortcutDirectory 'toiqtty.lnk'; $temporary=Join-Path $ShortcutDirectory ('toiqtty-'+[Guid]::NewGuid().ToString('N')+'.lnk')
    $shell=New-Object -ComObject WScript.Shell
    try {
        $shortcut=$shell.CreateShortcut($temporary)
        $shortcut.TargetPath=$Executable; $shortcut.Arguments=''; $shortcut.WorkingDirectory=$env:USERPROFILE
        $shortcut.IconLocation=(Join-Path (Split-Path $Executable) 'Assets/AppIcon.ico')+',0'; $shortcut.Description='toiqtty terminal'; $shortcut.Save()
        [IO.File]::Move($temporary,$path,$true)
        if($shell.CreateShortcut($path).TargetPath -ne $Executable) { throw 'Start menu shortcut verification failed.' }
    }
    finally { if(Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }; [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    return $path
}
function Set-ToiqttyActiveVersion([string]$Root,$State,[string]$Executable,[string]$ShortcutDirectory) {
    $statePath=Join-Path $Root 'install-state.json'; $linkPath=Join-Path $ShortcutDirectory 'toiqtty.lnk'
    $oldState=if(Test-Path -LiteralPath $statePath){[IO.File]::ReadAllBytes($statePath)}else{$null}
    $oldLink=if(Test-Path -LiteralPath $linkPath){[IO.File]::ReadAllBytes($linkPath)}else{$null}
    try {
        Write-ToiqttyInstallState $Root $State
        return Set-ToiqttyShortcut $Executable $ShortcutDirectory
    }
    catch {
        $failure=$_
        foreach($entry in @(@{path=$statePath;bytes=$oldState},@{path=$linkPath;bytes=$oldLink})) {
            if($null -eq $entry.bytes) { if(Test-Path -LiteralPath $entry.path){Remove-Item -LiteralPath $entry.path}; continue }
            # Restore only a changed pointer. A locked, unchanged shortcut needs
            # no write and must not prevent restoration of the install state.
            if((Test-Path -LiteralPath $entry.path) -and [Linq.Enumerable]::SequenceEqual([byte[]][IO.File]::ReadAllBytes($entry.path),[byte[]]$entry.bytes)) { continue }
            $temp=$entry.path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
            try { [IO.File]::WriteAllBytes($temp,$entry.bytes); [IO.File]::Move($temp,$entry.path,$true) }
            finally { if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp} }
        }
        throw $failure
    }
}
function Start-ToiqttyInstalled([string]$Executable) {
    $start=[Diagnostics.ProcessStartInfo]::new($Executable); $start.UseShellExecute=$false; $start.WorkingDirectory=$env:USERPROFILE
    foreach($name in @($start.Environment.Keys)) { if($name -like 'TOIQTTY_*') { $start.Environment.Remove($name) | Out-Null } }
    $noColor=[Environment]::GetEnvironmentVariable('NO_COLOR','User')
    if($null -eq $noColor) { $noColor=[Environment]::GetEnvironmentVariable('NO_COLOR','Machine') }
    if($null -eq $noColor) { $start.Environment.Remove('NO_COLOR') | Out-Null } else { $start.Environment['NO_COLOR']=$noColor }
    $process=[Diagnostics.Process]::Start($start); $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $process.Refresh(); if($process.HasExited) { throw 'Installed Toiqtty exited before opening a window.' } }
    while($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if($process.MainWindowHandle -eq 0) { throw 'Installed Toiqtty did not open a window.' }
    return $process.Id
}
