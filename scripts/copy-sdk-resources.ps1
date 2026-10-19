param([Parameter(Mandatory)][string]$RuntimeArchive, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
# The pinned self-contained SDK omits this telemetry resource from its native
# copy items. AppNotificationManager needs it even for local notifications.
$name = 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
$zip = [IO.Compression.ZipFile]::OpenRead($RuntimeArchive)
try {
    $entry = $zip.GetEntry($name)
    if (!$entry) { throw "Pinned Windows App SDK runtime is missing $name." }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $target = Join-Path $Destination $name
    if (!(Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -ne $entry.Length -or (Get-Item -LiteralPath $target).LastWriteTimeUtc -ne $entry.LastWriteTime.UtcDateTime) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        [IO.File]::SetLastWriteTimeUtc($target, $entry.LastWriteTime.UtcDateTime)
    }
}
finally { $zip.Dispose() }
