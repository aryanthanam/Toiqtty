param([string]$OutputDirectory='artifacts/native',[switch]$AddressSanitizer,[string]$HarfBuzzSource,[switch]$DisableHarfBuzz)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$HarfBuzzSource -and !$DisableHarfBuzz){$HarfBuzzSource=& (Join-Path $PSScriptRoot 'get-harfbuzz.ps1')}
if($HarfBuzzSource -and $DisableHarfBuzz){throw 'Select either HarfBuzz source or the diagnostic disabled build.'}
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){$OutputDirectory}else{Join-Path $root $OutputDirectory}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'Visual Studio C++ x64 build tools are required for the DirectX renderer.'}
$toolset=Get-ChildItem -LiteralPath (Join-Path $installation 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk=Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'um/d3d12.h')} | Sort-Object Name -Descending | Select-Object -First 1
if(!$sdk){throw 'A Windows SDK with Direct3D 12 headers is required.'}
$compiler=Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
$includes=@((Join-Path $toolset.FullName 'include'),(Join-Path $sdk.FullName 'ucrt'),(Join-Path $sdk.FullName 'shared'),(Join-Path $sdk.FullName 'um'),(Join-Path $sdk.FullName 'winrt'))
$libraries=@((Join-Path $toolset.FullName 'lib/x64'),(Join-Path $sdkRoot "Lib/$($sdk.Name)/ucrt/x64"),(Join-Path $sdkRoot "Lib/$($sdk.Name)/um/x64"))
$shaderCompiler=Join-Path $sdkRoot "bin/$($sdk.Name)/x64/fxc.exe"
if(!(Test-Path -LiteralPath $shaderCompiler)){throw 'The Windows SDK FXC shader compiler is required.'}
foreach($shader in @(@('vs','OtrVertexShader','vs'),@('vs','OtrQuadShader','vs_quad'),@('ps','OtrPixelShader','ps'))){
    & $shaderCompiler /nologo /WX /O3 /T ($shader[0]+'_5_0') /E $shader[2] /Vn $shader[1] /Fh (Join-Path $output ('renderer-'+$shader[2]+'.h')) (Join-Path $root 'native/renderer.hlsl')
    if($LASTEXITCODE -ne 0){throw 'DirectX shader compilation failed.'}
}
$arguments=@('/nologo','/std:c++20','/EHsc','/W4','/WX','/O2','/MT','/LD','/Zi','/DWIN32_LEAN_AND_MEAN','/DNOMINMAX')
if($AddressSanitizer){
    $runtime=Join-Path $toolset.FullName 'bin/Hostx64/x64/clang_rt.asan_dynamic-x86_64.dll'
    if(!(Test-Path $runtime)){throw 'The Visual Studio AddressSanitizer component is required.'}
    $arguments+='/fsanitize=address';Copy-Item $runtime $output -Force
}
$arguments+='/I'+$output
foreach($include in $includes){$arguments+='/I'+$include}
if($HarfBuzzSource){
    $harfbuzz=if([IO.Path]::IsPathRooted($HarfBuzzSource)){$HarfBuzzSource}else{Join-Path $root $HarfBuzzSource}
    if(!(Test-Path "$harfbuzz/src/harfbuzz.cc") -or !(Test-Path "$harfbuzz/COPYING")){throw 'The HarfBuzz core source and license are required.'}
    # HarfBuzz's MS feature-range implementation deliberately stores a small
    # integer in a pointer slot; suppress only that upstream x64 warning.
    $hbArguments=@('/nologo','/std:c++20','/EHsc','/O2','/MT','/c','/wd4312','/DWIN32_LEAN_AND_MEAN','/DNOMINMAX','/DHB_STATIC','/DHAVE_DIRECTWRITE')
    foreach($include in $includes){$hbArguments+='/I'+$include}
    $hbArguments+=@(('/I'+(Join-Path $harfbuzz 'src')),(Join-Path $harfbuzz 'src/harfbuzz.cc'),('/Fo'+(Join-Path $output 'harfbuzz.obj')))
    if($AddressSanitizer){$hbArguments+=@('/fsanitize=address','/Zi','/bigobj',('/Fd'+(Join-Path $output 'harfbuzz-compile.pdb')))}
    & $compiler @hbArguments
    if($LASTEXITCODE){throw 'The HarfBuzz core build failed.'}
    $arguments+=@('/DTOIQTTY_HARFBUZZ','/DHB_STATIC',('/I'+(Join-Path $harfbuzz 'src')),(Join-Path $output 'harfbuzz.obj'))
    Copy-Item -LiteralPath (Join-Path $harfbuzz 'COPYING') -Destination (Join-Path $output 'HarfBuzz.LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $harfbuzz 'src/ms-use/COPYING') -Destination (Join-Path $output 'HarfBuzz-MS-USE.LICENSE') -Force
}
$arguments+=@((Join-Path $root 'native/renderer.cpp'),('/Fo'+(Join-Path $output 'renderer.obj')),('/Fd'+(Join-Path $output 'renderer-compile.pdb')),'/link',('/OUT:'+(Join-Path $output 'toiqtty-renderer.dll')),('/IMPLIB:'+(Join-Path $output 'toiqtty-renderer.lib')),('/PDB:'+(Join-Path $output 'toiqtty-renderer.pdb')),'/DEBUG','/INCREMENTAL:NO','/OPT:REF','/OPT:ICF','d3d12.lib','d3d11.lib','dxgi.lib','d2d1.lib','dwrite.lib','ole32.lib')
foreach($library in $libraries){$arguments+='/LIBPATH:'+ $library}
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Native DirectX renderer build failed.'}
Write-Host "Native Direct3D 12 renderer built: $output"
