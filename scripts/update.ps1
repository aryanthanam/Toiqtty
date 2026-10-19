param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSha256,
    [string]$InstallDirectory=(Join-Path $env:LOCALAPPDATA 'Programs/Oroterm'),
    [string]$ShortcutDirectory=[Environment]::GetFolderPath('Programs'),
    [switch]$NoLaunch,[switch]$RequireSigned,[string]$ExpectedPublisherThumbprint
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/release-tools.ps1"
$archive=[IO.Path]::GetFullPath($ArchivePath)
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Update archive checksum does not match.' }
$stagingRoot=Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'updates'
$stage=Resolve-OrotermChildPath $stagingRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$zip=$null
try {
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); [long]$total=0
    foreach($entry in $zip.Entries) {
        $path=Resolve-OrotermChildPath $stage $entry.FullName
        if(!$seen.Add($path)) { throw 'Update archive contains duplicate paths.' }
        if(($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000) { throw 'Update archive contains a symbolic link.' }
        $total+=$entry.Length
        if($total -gt 2GB -or $entry.Length -gt 512MB -or $seen.Count -gt 10000) { throw 'Update archive exceeds the supported size.' }
    }
    $zip.Dispose(); $zip=$null
    [IO.Compression.ZipFile]::ExtractToDirectory($archive,$stage)
    & "$PSScriptRoot/install.ps1" -ReleaseDirectory $stage -InstallDirectory $InstallDirectory -ShortcutDirectory $ShortcutDirectory -NoLaunch:$NoLaunch -RequireSigned:$RequireSigned -ExpectedPublisherThumbprint $ExpectedPublisherThumbprint
}
finally {
    if($zip) { $zip.Dispose() }
    $safeStage=Resolve-OrotermChildPath $stagingRoot ([IO.Path]::GetFileName($stage))
    if($safeStage -ne $stage) { throw 'Unexpected update staging directory.' }
    if(Test-Path -LiteralPath $safeStage) { Remove-Item -LiteralPath $safeStage -Recurse -Force }
}
