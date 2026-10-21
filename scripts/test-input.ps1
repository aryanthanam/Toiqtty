param([string]$AppDirectory='artifacts/input-test',[string]$TestDirectory='artifacts/input-client',[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $root
try {
    $app=[IO.Path]::GetFullPath($AppDirectory); $client=[IO.Path]::GetFullPath($TestDirectory)
    if(!$SkipBuild){
        & "$PSScriptRoot/build-native.ps1"
        dotnet build tests/Toiqtty.Smoke -c Release -o $client
        if($LASTEXITCODE -ne 0){throw 'Input fixture build failed.'}
        dotnet build src/Toiqtty -c Release -o $app
        if($LASTEXITCODE -ne 0){throw 'Input app build failed.'}
    }
    & "$client/Toiqtty.Smoke.exe" --input-dispatch
    if($LASTEXITCODE -ne 0){throw 'Input dispatch checks failed.'}
    & "$client/Toiqtty.Smoke.exe" --input-ui "$app/toiqtty.exe"
    if($LASTEXITCODE -ne 0){throw 'Input WinUI checks failed.'}
    & "$client/Toiqtty.Smoke.exe" --input-close-ui "$app/toiqtty.exe"
    if($LASTEXITCODE -ne 0){throw 'Input close checks failed.'}
    & "$client/Toiqtty.Smoke.exe" --input-native-close-ui "$app/toiqtty.exe"
    if($LASTEXITCODE -ne 0){throw 'Native input close checks failed.'}
}finally { Pop-Location }
