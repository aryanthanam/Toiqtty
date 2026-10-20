param([string]$AppDirectory='artifacts/resize-test',[string]$TestDirectory='artifacts/resize-client',[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$appFolder=if([IO.Path]::IsPathRooted($AppDirectory)){$AppDirectory}else{Join-Path $repoRoot $AppDirectory}
$testFolder=if([IO.Path]::IsPathRooted($TestDirectory)){$TestDirectory}else{Join-Path $repoRoot $TestDirectory}
Push-Location -LiteralPath $repoRoot
try {
    if(!$SkipBuild){
        & "$PSScriptRoot/build-native.ps1"
        dotnet build "$repoRoot/tests/Toiqtty.Smoke" -c Release -o $testFolder
        if($LASTEXITCODE -ne 0){throw 'Resize fixture build failed.'}
        dotnet build "$repoRoot/src/Toiqtty" -c Release -o $appFolder
        if($LASTEXITCODE -ne 0){throw 'Resize app build failed.'}
    }
    & "$testFolder/Toiqtty.Smoke.exe" --resize-ui "$appFolder/toiqtty.exe"
    if($LASTEXITCODE -ne 0){throw 'Resize WinUI checks failed.'}
}finally { Pop-Location }
