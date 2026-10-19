param([string]$OutputDirectory='artifacts/native-lifetime-tests',[switch]$AddressSanitizer)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $repo $OutputDirectory}
New-Item -ItemType Directory -Force $output | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'Visual Studio C++ x64 tools are required.'}
$toolset=Get-ChildItem (Join-Path $installation 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk=Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Where-Object {Test-Path (Join-Path $_.FullName 'ucrt/stdio.h')} | Sort-Object Name -Descending | Select-Object -First 1
if(!$sdk){throw 'Windows SDK UCRT headers are required.'}
$compiler=Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
$arguments=@('/nologo','/std:c++20','/EHsc','/W4','/WX','/O2','/MT')
if($AddressSanitizer){
    $runtime=Join-Path $toolset.FullName 'bin/Hostx64/x64/clang_rt.asan_dynamic-x86_64.dll'
    if(!(Test-Path $runtime)){throw 'The Visual Studio AddressSanitizer component is required.'}
    $arguments+=@('/fsanitize=address','/Zi');Copy-Item $runtime $output -Force
}
$arguments+=@(('/I'+(Join-Path $repo 'native')),('/I'+(Join-Path $toolset.FullName 'include')),('/I'+(Join-Path $sdk.FullName 'ucrt')),
    (Join-Path $repo 'tests/native/lifetime-tests.cpp'),('/Fo'+(Join-Path $output 'lifetime-tests.obj')),('/Fd'+(Join-Path $output 'lifetime-tests.pdb')),('/Fe'+(Join-Path $output 'lifetime-tests.exe')),'/link',('/LIBPATH:'+(Join-Path $toolset.FullName 'lib/x64')),('/LIBPATH:'+(Join-Path $sdkRoot "Lib/$($sdk.Name)/ucrt/x64")),('/LIBPATH:'+(Join-Path $sdkRoot "Lib/$($sdk.Name)/um/x64")))
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Lifetime checks failed to compile.'}
& (Join-Path $output 'lifetime-tests.exe')
if($LASTEXITCODE -ne 0){throw 'Native lifetime checks failed.'}
