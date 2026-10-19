param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$folder = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Directory)) { $Directory } else { Join-Path $repoRoot $Directory }))
if (!$folder.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Asset cleanup is limited to workspace artifacts.' }
# Exact obsolete files only: preserve other release assets and all user data.
foreach ($relative in @('Integration/launch.sh', 'Integration/upstream/source.json',
    'Integration/upstream/bash/ghostty.bash', 'Integration/upstream/bash/bash-preexec.sh',
    'Integration/upstream/zsh/.zshenv', 'Integration/upstream/zsh/ghostty-integration',
    'licenses/GHOSTTY-SHELL-GPL-3.0.txt', 'licenses/BASH-PREEXEC-LICENSE')) {
    $target = Join-Path $folder $relative
    if (Test-Path -LiteralPath $target) {
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw "Expected an obsolete file: $target" }
        Remove-Item -LiteralPath $target -Force
    }
}
