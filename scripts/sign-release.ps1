param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CertificateThumbprint,
    [string]$TimestampUrl='http://timestamp.digicert.com',
    [switch]$AllowKeyPrompt,
    [switch]$CheckKeyOnly
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($Directory)
$certificate=Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if(!$certificate.HasPrivateKey -or $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'The signing certificate is not currently usable.' }
if(!('ToiqttySigningKey' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ToiqttySigningKey {
    [DllImport("crypt32.dll",SetLastError=true)]
    static extern bool CryptAcquireCertificatePrivateKey(IntPtr certificate,uint flags,IntPtr parameters,out IntPtr key,out uint keySpec,out bool free);
    [DllImport("ncrypt.dll")] static extern int NCryptFreeObject(IntPtr key);
    [DllImport("advapi32.dll")] static extern bool CryptReleaseContext(IntPtr key,uint flags);
    public static int Check(IntPtr certificate) {
        // Silent acquisition never requests or supplies a smart-card PIN.
        if(!CryptAcquireCertificatePrivateKey(certificate,0x10040,IntPtr.Zero,out var key,out var spec,out var free)) return Marshal.GetLastWin32Error();
        if(free) { if(spec==0xffffffff) NCryptFreeObject(key); else CryptReleaseContext(key,0); }
        return 0;
    }
}
'@
}
if(!$AllowKeyPrompt) {
    $errorCode=[ToiqttySigningKey]::Check($certificate.Handle)
    if($errorCode -ne 0) { throw ('The signing key is unavailable without interaction (0x{0:X8}). Connect/unlock the signing token, or rerun explicitly with -AllowKeyPrompt. No PIN or private key was read.' -f $errorCode) }
}
if($CheckKeyOnly) { [pscustomobject]@{thumbprint=$certificate.Thumbprint;subject=$certificate.Subject;silentKeyAvailable=$true}|ConvertTo-Json; return }
$tool=Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse |
    Where-Object FullName -Match '\\x64\\signtool.exe$' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if(!$tool) { throw 'The Windows SDK SignTool is required.' }
foreach($name in @('toiqtty.exe','toiqtty.dll','Toiqtty.SessionHost.exe','Toiqtty.SessionHost.dll','toiqtty-native.dll')) {
    & $tool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 (Join-Path $root $name)
    if($LASTEXITCODE -ne 0) { throw "Signing failed: $name" }
    & $tool verify /pa /all /tw (Join-Path $root $name)
    if($LASTEXITCODE -ne 0) { throw "Signature verification failed: $name" }
}
& "$PSScriptRoot/new-release-manifest.ps1" -Directory $root -Signed -PublisherThumbprint $CertificateThumbprint | Out-Null
$catalogStage=Join-Path (Split-Path $root) ('release-'+[Guid]::NewGuid().ToString('N')+'.cat')
try {
    if(Test-Path -LiteralPath (Join-Path $root 'release.cat')) { Remove-Item -LiteralPath (Join-Path $root 'release.cat') }
    New-FileCatalog -Path $root -CatalogFilePath $catalogStage -CatalogVersion 2 | Out-Null
    & $tool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $catalogStage
    if($LASTEXITCODE -ne 0) { throw 'Release catalog signing failed.' }
    Move-Item -LiteralPath $catalogStage -Destination (Join-Path $root 'release.cat') -Force
    . "$PSScriptRoot/release-tools.ps1"
    $null=Test-ToiqttyRelease $root -RequireSigned -ExpectedPublisherThumbprint $CertificateThumbprint
}
finally { if(Test-Path -LiteralPath $catalogStage) { Remove-Item -LiteralPath $catalogStage } }
[pscustomobject]@{signed=$true;publisher=$certificate.Subject;thumbprint=$CertificateThumbprint;catalog='release.cat'} | ConvertTo-Json
