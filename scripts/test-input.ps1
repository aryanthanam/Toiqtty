param([string]$AppDirectory='artifacts/input-test',[string]$TestDirectory='artifacts/input-client',[string]$LegacyHostDirectory='artifacts/release-0.6.6',[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $root
try {
    $app=[IO.Path]::GetFullPath($AppDirectory); $client=[IO.Path]::GetFullPath($TestDirectory)
    if(!$SkipBuild){
        & "$PSScriptRoot/build-native.ps1"
        dotnet build tests/Oroterm.Smoke -c Release -o $client
        if($LASTEXITCODE -ne 0){throw 'Input fixture build failed.'}
        dotnet build src/Oroterm -c Release -o $app
        if($LASTEXITCODE -ne 0){throw 'Input app build failed.'}
    }
    & "$client/Oroterm.Smoke.exe" --input-dispatch
    if($LASTEXITCODE -ne 0){throw 'Input dispatch checks failed.'}
    & "$client/Oroterm.Smoke.exe" --input-ui "$app/Oroterm.exe"
    if($LASTEXITCODE -ne 0){throw 'Input WinUI checks failed.'}
    & "$client/Oroterm.Smoke.exe" --input-close-ui "$app/Oroterm.exe"
    if($LASTEXITCODE -ne 0){throw 'Input close checks failed.'}
    & "$client/Oroterm.Smoke.exe" --input-native-close-ui "$app/Oroterm.exe"
    if($LASTEXITCODE -ne 0){throw 'Native input close checks failed.'}
    if($LegacyHostDirectory){
        $legacy=[IO.Path]::GetFullPath($LegacyHostDirectory)
        & "$client/Oroterm.Smoke.exe" --input-compat-ui "$app/Oroterm.exe" "$legacy/Oroterm.SessionHost.exe"
        if($LASTEXITCODE -ne 0){throw 'Older-host input compatibility checks failed.'}
    }
}finally { Pop-Location }
