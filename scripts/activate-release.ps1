param(
    [string]$SourceDirectory = 'artifacts/release-next',
    [string]$DestinationDirectory = 'artifacts/release',
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
function Resolve-ArtifactFolder([string]$value) {
    $resolved = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($value)) { $value } else { Join-Path $repoRoot $value }))
    if (!$resolved.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release folders must be inside this workspace artifacts directory.' }
    return $resolved
}
$source = Resolve-ArtifactFolder $SourceDirectory
$destination = Resolve-ArtifactFolder $DestinationDirectory
if ($source -eq $destination) { throw 'Source and destination must differ.' }
if (!(Test-Path -LiteralPath "$source/Oroterm.exe")) { throw 'Staged release is missing Oroterm.exe.' }
$destinationExe = [IO.Path]::GetFullPath("$destination/Oroterm.exe")
$destinationHost = [IO.Path]::GetFullPath("$destination/Oroterm.SessionHost.exe")
$log = Join-Path $artifactRoot 'activate-release.log'
try {
    # Wait for the user's existing terminals to close normally before replacing
    # their loaded runtime files. Never stop or terminate a running app.
    while ($true) {
        $running = @(Get-Process Oroterm,Oroterm.SessionHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $destinationExe -or $_.Path -eq $destinationHost })
        if (!$running.Count) { break }
        Add-Content -LiteralPath $log -Value "Waiting for Oroterm process $($running.Id -join ', ') to close."
        foreach ($process in $running) { $process.WaitForExit() }
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse -Force
    & "$PSScriptRoot/remove-obsolete-assets.ps1" -Directory $destination
    Add-Content -LiteralPath $log -Value "Activated release from $source."
    if (!$NoLaunch) {
        $start = [Diagnostics.ProcessStartInfo]::new($destinationExe)
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $repoRoot
        # Use the persistent user/system color preference rather than a coding
        # runner's transient NO_COLOR setting when reopening the desktop app.
        $noColor = [Environment]::GetEnvironmentVariable('NO_COLOR', 'User')
        if ($null -eq $noColor) { $noColor = [Environment]::GetEnvironmentVariable('NO_COLOR', 'Machine') }
        if ($null -eq $noColor) { $start.EnvironmentVariables.Remove('NO_COLOR') }
        else { $start.EnvironmentVariables['NO_COLOR'] = $noColor }
        $launched = [Diagnostics.Process]::Start($start)
        Add-Content -LiteralPath $log -Value "Started updated Oroterm process $($launched.Id)."
    }
}
catch { Add-Content -LiteralPath $log -Value $_.Exception.ToString(); throw }
