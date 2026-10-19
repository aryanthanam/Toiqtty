param([string]$OutputDirectory = 'artifacts/release',[string]$SigningCertificateThumbprint,[switch]$AllowKeyPrompt)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$publishFolder = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
& "$PSScriptRoot/build-native.ps1"
dotnet publish "$repoRoot/src/Oroterm/Oroterm.csproj" -c Release -o $publishFolder
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& "$PSScriptRoot/remove-obsolete-assets.ps1" -Directory $publishFolder
# Interrupted ReadyToRun publishes can leave cached, zero-filled binaries.
# Reject those before packaging or installing the output.
foreach ($binary in Get-ChildItem -LiteralPath $publishFolder -Recurse -File | Where-Object Extension -In @('.dll', '.exe')) {
    $stream = [IO.File]::OpenRead($binary.FullName)
    try {
        if ($stream.Length -lt 64 -or $stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) {
            throw "Published binary is invalid: $($binary.FullName). Regenerate the project's ReadyToRun build cache before publishing again."
        }
    }
    finally { $stream.Dispose() }
}
foreach ($resource in @('Oroterm.pri', 'App.xbf', 'MainWindow.xbf', 'conpty.dll', 'OpenConsole.exe', 'ghostty-vt.dll', 'oroterm-native.dll', 'oroterm-renderer.dll',
    'Oroterm.SessionHost.exe', 'Oroterm.SessionHost.dll', 'Oroterm.SessionHost.runtimeconfig.json', 'Oroterm.SessionHost.deps.json',
    'Oroterm.Core.dll', 'Oroterm.Client.dll', 'oro.exe', 'oro.dll', 'oro.deps.json', 'oro.runtimeconfig.json',
    'Microsoft.WindowsAppRuntime.Insights.Resource.dll',
    'Lua.dll', 'Lua.Annotations.dll', 'LUA_CONFIG.md', 'Configuration/oroterm.example.lua',
    'Configuration/appearance.example.lua',
    'Configuration/interactions.example.lua',
    'Microsoft.Web.WebView2.Core.dll', 'BrowserCompanion/chromium/manifest.json', 'BrowserCompanion/chromium/popup.html',
    'BrowserCompanion/chromium/background.js', 'BrowserCompanion/chromium/bridge.js', 'BrowserCompanion/chromium/popup.js',
    'BrowserCompanion/firefox/manifest.json', 'BrowserCompanion/firefox/popup.html', 'BrowserCompanion/firefox/bridge.js',
    'Assets/Fonts/JetBrainsMonoNerdFontMono-Regular.ttf', 'Assets/Fonts/JetBrainsMonoNerdFontMono-Bold.ttf',
    'Assets/Fonts/JetBrainsMonoNerdFontMono-Italic.ttf', 'Assets/Fonts/JetBrainsMonoNerdFontMono-BoldItalic.ttf',
    'Assets/Fonts/JetBrainsMonoNerdFontMono-Medium.ttf', 'Assets/Fonts/JetBrainsMonoNerdFontMono-MediumItalic.ttf',
    'Integration/powershell.ps1', 'Integration/posix/LICENSE', 'Integration/posix/launch-posix.sh',
    'Integration/posix/bash-startup.bash', 'Integration/posix/bash.bash', 'Integration/posix/zsh.zsh',
    'Integration/posix/zsh/.zshenv', 'Integration/posix/zsh/.zprofile', 'Integration/posix/zsh/.zshrc', 'Integration/posix/zsh/.zlogin')) {
    if (!(Test-Path "$publishFolder/$resource")) { throw "Published application is missing runtime asset $resource." }
}
Copy-Item "$repoRoot/artifacts/native/GHOSTTY-LICENSE" $publishFolder -Force
Copy-Item "$repoRoot/THIRD_PARTY_NOTICES.md" $publishFolder -Force
$licenses = "$publishFolder/licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item "$repoRoot/third_party/*" $licenses -Force
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
Copy-Item "$nugetRoot/microsoft.windowsappsdk/2.5.1/license.txt" "$licenses/WINDOWS-APP-SDK-LICENSE" -Force
$webViewPackage = (Get-Content "$repoRoot/src/Oroterm/obj/project.assets.json" -Raw | ConvertFrom-Json).libraries.PSObject.Properties |
    Where-Object Name -Like 'Microsoft.Web.WebView2/*' | Select-Object -First 1
if ($webViewPackage) { Copy-Item "$nugetRoot/$($webViewPackage.Value.path)/LICENSE.txt" "$licenses/WEBVIEW2-LICENSE" -Force }
$runtime = Get-Content "$publishFolder/Oroterm.runtimeconfig.json" -Raw | ConvertFrom-Json
$runtimeVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
if (!$runtimeVersion) { throw 'Published runtime configuration is invalid or incomplete.' }
Copy-Item "$nugetRoot/microsoft.netcore.app.runtime.win-x64/$runtimeVersion/LICENSE.TXT" "$licenses/DOTNET-LICENSE" -Force
Copy-Item "$nugetRoot/microsoft.netcore.app.runtime.win-x64/$runtimeVersion/THIRD-PARTY-NOTICES.TXT" "$licenses/DOTNET-THIRD-PARTY-NOTICES" -Force
$installTools=Join-Path $publishFolder 'tools'
New-Item -ItemType Directory -Force -Path $installTools | Out-Null
foreach($script in @('install.ps1','update.ps1','rollback.ps1','release-tools.ps1','new-release-manifest.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $installTools -Force
}
if($SigningCertificateThumbprint) {
    & "$PSScriptRoot/sign-release.ps1" -Directory $publishFolder -CertificateThumbprint $SigningCertificateThumbprint -AllowKeyPrompt:$AllowKeyPrompt
} else {
    if(Test-Path -LiteralPath (Join-Path $publishFolder 'release.cat')) { Remove-Item -LiteralPath (Join-Path $publishFolder 'release.cat') }
    & "$PSScriptRoot/new-release-manifest.ps1" -Directory $publishFolder | Out-Null
}
. "$PSScriptRoot/release-tools.ps1"
$manifest=Test-OrotermRelease $publishFolder -RequireSigned:([bool]$SigningCertificateThumbprint)
Compress-Archive -Path "$publishFolder/*" -DestinationPath "$repoRoot/artifacts/Oroterm-win-x64.zip" -Force
$versionedArchive=Join-Path $repoRoot "artifacts/Oroterm-win-x64-$($manifest.version).zip"
Copy-Item -LiteralPath "$repoRoot/artifacts/Oroterm-win-x64.zip" -Destination $versionedArchive -Force
[ordered]@{version=$manifest.version;signed=$manifest.signed;archive=[IO.Path]::GetFileName($versionedArchive);sha256=(Get-FileHash -LiteralPath $versionedArchive -Algorithm SHA256).Hash} |
    ConvertTo-Json | Set-Content -LiteralPath ($versionedArchive+'.json') -Encoding utf8
Write-Host "Portable build: $repoRoot/artifacts/Oroterm-win-x64.zip"
