# Shared release verification and activation. Never terminate live processes.
function Resolve-OrotermChildPath([string]$Root,[string]$Relative) {
    $base=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw "Invalid release path: $Relative" }
    $path=[IO.Path]::GetFullPath((Join-Path $base $Relative))
    if(!$path.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw "Release path escapes its directory: $Relative" }
    return $path
}
function Test-OrotermRelease([string]$Directory,[switch]$RequireSigned,[string]$ExpectedPublisherThumbprint,[string]$ManifestPath) {
    $root=[IO.Path]::GetFullPath($Directory)
    $manifestPath=if($ManifestPath){$ManifestPath}else{Join-Path $root 'release-manifest.json'}
    if(!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Release manifest is missing. Publish a complete release before installing it.' }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if($manifest.schema -ne 1 -or $manifest.platform -ne 'win-x64' -or $manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Unsupported release manifest.' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $manifest.files) {
        if($entry.path.Contains('\') -or ($entry.path.Split('/') | Where-Object { $_ -in @('','.', '..') })) { throw 'Release manifest paths must be normalized relative paths.' }
        $path=Resolve-OrotermChildPath $root $entry.path
        if(!$seen.Add($entry.path) -or $entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid or duplicate release manifest entry.' }
        $file=Get-Item -LiteralPath $path -ErrorAction Stop
        if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -ne $entry.size -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Release file verification failed: $($entry.path)" }
    }
    foreach($required in @('Oroterm.exe','Oroterm.dll','Oroterm.SessionHost.exe','Oroterm.SessionHost.dll','ghostty-vt.dll','oroterm-native.dll','Oroterm.pri','MainWindow.xbf','Assets/AppIcon.ico')) {
        if(!$seen.Contains($required)) { throw "Release manifest omits $required." }
    }
    if([version](($manifest.version -split '-')[0]) -ge [version]'0.7.8') {
        foreach($required in @('Oroterm.Core.dll','Oroterm.Client.dll')) {
            if(!$seen.Contains($required)) { throw "Release manifest omits $required." }
        }
    }
    if([version](($manifest.version -split '-')[0]) -ge [version]'0.8.0' -and !$seen.Contains('oroterm-renderer.dll')) {
        throw 'Release manifest omits oroterm-renderer.dll.'
    }
    $binaryVersion=([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'Oroterm.exe')).ProductVersion -split '\+')[0]
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
        foreach($binary in @('Oroterm.exe','Oroterm.dll','Oroterm.SessionHost.exe','Oroterm.SessionHost.dll','oroterm-native.dll','release.cat')) {
            $signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $root $binary)
            if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $publisher) { throw "Invalid publisher signature: $binary" }
        }
        $catalog=Test-FileCatalog -Path $root -CatalogFilePath (Join-Path $root 'release.cat') -FilesToSkip 'release.cat'
        if($catalog -ne 'Valid') { throw 'Signed catalog does not match the complete release.' }
    }
    elseif($ExpectedPublisherThumbprint) { throw 'An unsigned release cannot satisfy a publisher requirement.' }
    return $manifest
}
function Write-OrotermInstallState([string]$Root,$State) {
    $path=Join-Path $Root 'install-state.json'; $temporary=$path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try { $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8; [IO.File]::Move($temporary,$path,$true) }
    finally { if(Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
function Set-OrotermShortcut([string]$Executable,[string]$ShortcutDirectory) {
    New-Item -ItemType Directory -Force -Path $ShortcutDirectory | Out-Null
    $path=Join-Path $ShortcutDirectory 'Oroterm.lnk'; $temporary=Join-Path $ShortcutDirectory ('Oroterm-'+[Guid]::NewGuid().ToString('N')+'.lnk')
    $shell=New-Object -ComObject WScript.Shell
    try {
        $shortcut=$shell.CreateShortcut($temporary)
        $shortcut.TargetPath=$Executable; $shortcut.Arguments=''; $shortcut.WorkingDirectory=$env:USERPROFILE
        $shortcut.IconLocation=(Join-Path (Split-Path $Executable) 'Assets/AppIcon.ico')+',0'; $shortcut.Description='Oroterm terminal'; $shortcut.Save()
        [IO.File]::Move($temporary,$path,$true)
        if($shell.CreateShortcut($path).TargetPath -ne $Executable) { throw 'Start menu shortcut verification failed.' }
    }
    finally { if(Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }; [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    return $path
}
function Set-OrotermActiveVersion([string]$Root,$State,[string]$Executable,[string]$ShortcutDirectory) {
    $statePath=Join-Path $Root 'install-state.json'; $linkPath=Join-Path $ShortcutDirectory 'Oroterm.lnk'
    $oldState=if(Test-Path -LiteralPath $statePath){[IO.File]::ReadAllBytes($statePath)}else{$null}
    $oldLink=if(Test-Path -LiteralPath $linkPath){[IO.File]::ReadAllBytes($linkPath)}else{$null}
    try {
        Write-OrotermInstallState $Root $State
        return Set-OrotermShortcut $Executable $ShortcutDirectory
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
function Start-OrotermInstalled([string]$Executable) {
    $start=[Diagnostics.ProcessStartInfo]::new($Executable); $start.UseShellExecute=$false; $start.WorkingDirectory=$env:USERPROFILE
    foreach($name in @($start.Environment.Keys)) { if($name -like 'OROTERM_*') { $start.Environment.Remove($name) | Out-Null } }
    $noColor=[Environment]::GetEnvironmentVariable('NO_COLOR','User')
    if($null -eq $noColor) { $noColor=[Environment]::GetEnvironmentVariable('NO_COLOR','Machine') }
    if($null -eq $noColor) { $start.Environment.Remove('NO_COLOR') | Out-Null } else { $start.Environment['NO_COLOR']=$noColor }
    $process=[Diagnostics.Process]::Start($start); $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $process.Refresh(); if($process.HasExited) { throw 'Installed Oroterm exited before opening a window.' } }
    while($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if($process.MainWindowHandle -eq 0) { throw 'Installed Oroterm did not open a window.' }
    return $process.Id
}
