param([switch]$SkipDownload,[switch]$DisableSimd)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$lock = Get-Content "$repoRoot/native/ghostty.lock.json" -Raw | ConvertFrom-Json
$deps = "$repoRoot/.deps/ghostty"
$tools = "$repoRoot/.tools"
$zig = "$tools/zig-x86_64-windows-$($lock.zig)/zig.exe"
function Invoke-Checked([scriptblock]$Action) {
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Native build command failed ($LASTEXITCODE)." }
}
New-Item -ItemType Directory -Force $tools, "$repoRoot/artifacts/native" | Out-Null
if (!(Test-Path $zig)) {
    if ($SkipDownload) { throw 'Pinned Zig compiler is missing.' }
    $archive = "$tools/zig.zip"
    Invoke-WebRequest "https://ziglang.org/download/$($lock.zig)/zig-x86_64-windows-$($lock.zig).zip" -OutFile $archive
    $expected = '68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e'
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Zig download checksum mismatch.' }
    Expand-Archive $archive -DestinationPath $tools -Force
}
if (!(Test-Path "$deps/.git")) {
    if ($SkipDownload) { throw 'Ghostty source is missing.' }
    Invoke-Checked { git clone --no-checkout --depth 1 $lock.repository $deps }
}
$current = git -C $deps rev-parse HEAD
if ($current -ne $lock.commit) {
    if ((git -C $deps status --porcelain)) { throw 'Ghostty has local changes; refusing to replace them.' }
    Invoke-Checked { git -C $deps fetch origin $lock.commit --depth 1 }
    Invoke-Checked { git -C $deps checkout --detach $lock.commit }
}
foreach ($hostPatch in @("$repoRoot/native/patches/graphics-host.patch", "$repoRoot/native/patches/dnd-host.patch")) {
    $savedErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        git -C $deps apply --reverse --check $hostPatch 2>$null
        $patchApplied = $LASTEXITCODE -eq 0
    }
    finally { $ErrorActionPreference = $savedErrorPreference }
    if (!$patchApplied) {
        Invoke-Checked { git -C $deps apply --check $hostPatch }
        Invoke-Checked { git -C $deps apply $hostPatch }
    }
}
Push-Location $deps
try { $simd=if($DisableSimd){'false'}else{'true'}; Invoke-Checked { & $zig build -Demit-lib-vt -Doptimize=ReleaseFast "-Dsimd=$simd" } }
finally { Pop-Location }
Copy-Item "$deps/zig-out/bin/ghostty-vt.dll" "$repoRoot/artifacts/native" -Force
Invoke-Checked { & $zig cc -shared -O2 -I "$deps/include" "$repoRoot/native/toiqtty.c" "$repoRoot/native/png.c" "$deps/zig-out/lib/ghostty-vt.lib" -lole32 -lwindowscodecs -o "$repoRoot/artifacts/native/toiqtty-native.dll" }
Copy-Item "$deps/LICENSE" "$repoRoot/artifacts/native/GHOSTTY-LICENSE" -Force
Write-Host 'libghostty-vt and Toiqtty bridge built for Windows x64.'
