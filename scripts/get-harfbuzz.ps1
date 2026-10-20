param([switch]$SkipDownload)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$pin=Get-Content -LiteralPath (Join-Path $root 'native/harfbuzz.lock.json') -Raw | ConvertFrom-Json
$dependencyRoot=Join-Path $root '.deps/harfbuzz'
$source=Join-Path $dependencyRoot ('harfbuzz-'+$pin.version)
$stamp=Join-Path $source '.toiqtty-source-sha256'
if(Test-Path -LiteralPath $source){
    if(!(Test-Path -LiteralPath $stamp) -or (Get-Content -LiteralPath $stamp -Raw).Trim() -ne $pin.sha256){throw "HarfBuzz source exists without the expected source stamp: $source. Preserve it or select another explicit source directory."}
    if(!(Test-Path -LiteralPath (Join-Path $source 'src/harfbuzz.cc')) -or !(Test-Path -LiteralPath (Join-Path $source 'COPYING'))){throw 'The pinned HarfBuzz source is incomplete.'}
    Write-Output $source;return
}
$archive=Join-Path $root ('.deps/harfbuzz-'+$pin.version+'.tar.xz')
if(!(Test-Path -LiteralPath $archive)){
    if($SkipDownload){throw 'Pinned HarfBuzz source archive is missing.'}
    [IO.Directory]::CreateDirectory((Split-Path $archive -Parent))|Out-Null
    $download=$archive+'.'+[Guid]::NewGuid().ToString('N')+'.download'
    Invoke-WebRequest -Uri $pin.url -OutFile $download
    if((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.sha256){throw "HarfBuzz download checksum mismatch: $download"}
    Move-Item -LiteralPath $download -Destination $archive
}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.sha256){throw 'HarfBuzz source archive checksum mismatch.'}
[IO.Directory]::CreateDirectory($dependencyRoot)|Out-Null
& tar -xf $archive -C $dependencyRoot
if($LASTEXITCODE){throw 'Pinned HarfBuzz source extraction failed.'}
if(!(Test-Path -LiteralPath (Join-Path $source 'src/harfbuzz.cc')) -or !(Test-Path -LiteralPath (Join-Path $source 'COPYING'))){throw 'The extracted HarfBuzz source is incomplete.'}
Set-Content -LiteralPath $stamp -Value $pin.sha256 -Encoding ascii
Write-Output $source
