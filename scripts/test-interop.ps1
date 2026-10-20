param([Parameter(Mandatory)][string]$WslDistro, [switch]$Ui, [switch]$Zsh)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if ($WslDistro -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid WSL distro name.' }
function LinuxPath([string]$path) { '/mnt/' + $path.Substring(0,1).ToLowerInvariant() + $path.Substring(2).Replace('\','/') }
function Fetch([string]$url, [string]$destination, [string]$sha256) {
    if (!(Test-Path -LiteralPath $destination)) { Invoke-WebRequest $url -OutFile $destination }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $sha256) { throw "Checksum mismatch: $destination" }
}
New-Item -ItemType Directory -Force "$repoRoot/.tools" | Out-Null
Fetch 'https://github.com/kovidgoyal/kitty/releases/download/v0.49.2/kitten-linux-amd64' "$repoRoot/.tools/kitten-linux-amd64" '2a803b4d411f327cc41b7193cb529f6de06ae3b169a346b3060212bbfeafc8bc'
Fetch 'https://github.com/tmux/tmux/releases/download/3.8/tmux-3.8.tar.gz' "$repoRoot/.tools/tmux-3.8.tar.gz" 'e79c699c7e949dccd0a4a125e17b8d1261e311b16979dbc8eb34542f3966d82e'
if ($Zsh) {
    New-Item -ItemType Directory -Force "$repoRoot/.tools/zsh-package" | Out-Null
    Fetch 'https://archive.archlinux.org/packages/z/zsh/zsh-5.9.1-1-x86_64.pkg.tar.zst' "$repoRoot/.tools/zsh-package/zsh-5.9.1-1-x86_64.pkg.tar.zst" '64333f6e05467935a8d3573d124d1cbaa3f737dd1758117cc39570001cdd5de6'
}
$linuxRoot = LinuxPath $repoRoot
# Build inside the workspace; no distro packages or global executables are installed.
& wsl.exe -d $WslDistro --exec chmod +x "$linuxRoot/.tools/kitten-linux-amd64"
if ($LASTEXITCODE -ne 0) { throw 'Cannot access the selected WSL distro.' }
if ($Zsh -and !(Test-Path "$repoRoot/.tools/zsh-package/usr/bin/zsh")) {
    & wsl.exe -d $WslDistro --exec tar -xf "$linuxRoot/.tools/zsh-package/zsh-5.9.1-1-x86_64.pkg.tar.zst" -C "$linuxRoot/.tools/zsh-package"
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the isolated zsh test client.' }
}
if (!(Test-Path "$repoRoot/.tools/tmux-3.8/tmux")) {
    $build = @'
set -eu
cd "$1/.tools"
tar -xzf tmux-3.8.tar.gz
cd tmux-3.8
./configure --disable-utf8proc >configure.log 2>&1
make -j4 >build.log 2>&1
'@
    & wsl.exe -d $WslDistro --exec bash -c $build toiqtty $linuxRoot
    if ($LASTEXITCODE -ne 0) { throw 'tmux build failed. The distro needs a C compiler, make, libevent and ncurses development files. See .tools/tmux-3.8 logs.' }
}
$previous = $env:TOIQTTY_WSL_DISTRO
$previousZsh = $env:TOIQTTY_ZSH_ROOT
try {
    $env:TOIQTTY_WSL_DISTRO = $WslDistro
    if ($Zsh) { $env:TOIQTTY_ZSH_ROOT = "$repoRoot/.tools/zsh-package" }
    & "$PSScriptRoot/test.ps1" -SkipNative -Ui:$Ui
}
finally { $env:TOIQTTY_WSL_DISTRO = $previous; $env:TOIQTTY_ZSH_ROOT = $previousZsh }
