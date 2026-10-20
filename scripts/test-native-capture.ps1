param([string]$OutputDirectory='artifacts/native-capture-tests',[switch]$AddressSanitizer)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $repo $OutputDirectory}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'Visual Studio C++ x64 build tools are required.'}
$toolset=Get-ChildItem (Join-Path $installation 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk=Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Where-Object {Test-Path (Join-Path $_.FullName 'um/windows.h')} | Sort-Object Name -Descending | Select-Object -First 1
$arguments=@('/nologo','/std:c17','/W3','/O2','/MT','/Zi','/D_CRT_SECURE_NO_WARNINGS')
if($AddressSanitizer){$arguments+='/fsanitize=address'}
foreach($include in @((Join-Path $repo 'native'),(Join-Path $repo '.deps/ghostty/include'),(Join-Path $toolset.FullName 'include'),(Join-Path $sdk.FullName 'ucrt'),(Join-Path $sdk.FullName 'shared'),(Join-Path $sdk.FullName 'um'))){$arguments+='/I'+$include}
$arguments+=@((Join-Path $repo 'tests/native/terminal-capture-tests.c'),(Join-Path $repo 'native/toiqtty.c'),(Join-Path $repo 'native/png.c'),('/Fo'+$output+'\'),('/Fd'+(Join-Path $output 'capture-compile.pdb')),('/Fe'+(Join-Path $output 'terminal-capture-tests.exe')),'/link','/INCREMENTAL:NO',(Join-Path $repo '.deps/ghostty/zig-out/lib/ghostty-vt.lib'),'ole32.lib','windowscodecs.lib','user32.lib')
foreach($library in @((Join-Path $toolset.FullName 'lib/x64'),(Join-Path $sdkRoot "Lib/$($sdk.Name)/ucrt/x64"),(Join-Path $sdkRoot "Lib/$($sdk.Name)/um/x64"))){$arguments+='/LIBPATH:'+$library}
& (Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Native terminal capture checks failed to compile.'}
Copy-Item (Join-Path $repo 'artifacts/native/ghostty-vt.dll') $output -Force
if($AddressSanitizer){Copy-Item (Join-Path $toolset.FullName 'bin/Hostx64/x64/clang_rt.asan_dynamic-x86_64.dll') $output -Force}
& (Join-Path $output 'terminal-capture-tests.exe')
if($LASTEXITCODE -ne 0){throw 'Native terminal capture checks failed.'}
