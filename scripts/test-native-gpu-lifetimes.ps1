param([string]$OutputDirectory='artifacts/native-gpu-lifetime-tests',[switch]$AddressSanitizer,[switch]$SkipRendererBuild)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $repo $OutputDirectory}
if(!$SkipRendererBuild){& "$PSScriptRoot/build-renderer.ps1" -OutputDirectory $output -AddressSanitizer:$AddressSanitizer}
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$toolset=Get-ChildItem (Join-Path $installation 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk=Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Where-Object {Test-Path (Join-Path $_.FullName 'um/d3d12.h')} | Sort-Object Name -Descending | Select-Object -First 1
$arguments=@('/nologo','/std:c++20','/EHsc','/W4','/WX','/O2','/MT','/Zi','/DWIN32_LEAN_AND_MEAN','/DNOMINMAX')
if($AddressSanitizer){$arguments+='/fsanitize=address'}
$arguments+='/I'+(Join-Path $repo 'native')
foreach($include in @((Join-Path $toolset.FullName 'include'),(Join-Path $sdk.FullName 'ucrt'),(Join-Path $sdk.FullName 'shared'),(Join-Path $sdk.FullName 'um'),(Join-Path $sdk.FullName 'winrt'))){$arguments+='/I'+$include}
$arguments+=@((Join-Path $repo 'tests/native/gpu-lifetime-tests.cpp'),(Join-Path $output 'renderer.obj'),(Join-Path $output 'harfbuzz.obj'),('/Fo'+(Join-Path $output 'gpu-lifetime-tests.obj')),('/Fd'+(Join-Path $output 'gpu-lifetime-tests-compile.pdb')),('/Fe'+(Join-Path $output 'gpu-lifetime-tests.exe')),'/link','/INCREMENTAL:NO','d3d12.lib','d3d11.lib','dxgi.lib','d2d1.lib','dwrite.lib','ole32.lib')
foreach($library in @((Join-Path $toolset.FullName 'lib/x64'),(Join-Path $sdkRoot "Lib/$($sdk.Name)/ucrt/x64"),(Join-Path $sdkRoot "Lib/$($sdk.Name)/um/x64"))){$arguments+='/LIBPATH:'+$library}
& (Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Native GPU lifetime checks failed to compile.'}
& (Join-Path $output 'gpu-lifetime-tests.exe')
if($LASTEXITCODE -ne 0){throw 'Native GPU lifetime checks failed.'}
