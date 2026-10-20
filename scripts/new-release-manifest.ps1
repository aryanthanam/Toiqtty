param([Parameter(Mandatory)][string]$Directory,[string]$OutputPath,[switch]$Signed,[string]$PublisherThumbprint)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($Directory)
if(!$OutputPath) { $OutputPath=Join-Path $root 'release-manifest.json' }
$version=([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'toiqtty.exe')).ProductVersion -split '\+')[0]
if($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Release executable has no valid version.' }
$files=@(foreach($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName) {
    $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
    if($relative -in @('release-manifest.json','release.cat')) { continue }
    [ordered]@{path=$relative;size=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
})
[ordered]@{schema=1;version=$version;platform='win-x64';signed=[bool]$Signed;publisherThumbprint=$PublisherThumbprint;files=$files} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output $OutputPath
