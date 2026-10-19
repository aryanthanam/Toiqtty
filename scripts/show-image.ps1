param(
    [Parameter(Mandatory = $true)][string]$Path,
    [ValidateRange(1, 1000)][int]$Columns = 40,
    [ValidateRange(0, 500)][int]$Rows = 0
)
$ErrorActionPreference = 'Stop'
$imagePath = (Resolve-Path -LiteralPath $Path).ProviderPath
$bytes = [IO.File]::ReadAllBytes($imagePath)
if ($bytes.Length -lt 8 -or [BitConverter]::ToString($bytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') {
    throw 'This helper accepts PNG files.'
}
$payload = [Convert]::ToBase64String($bytes)
$escape = [char]27
# Quiet transmission: this non-interactive helper does not read protocol replies.
$header = "a=T,f=100,q=2,c=$Columns"
if ($Rows -gt 0) { $header += ",r=$Rows" }
for ($offset = 0; $offset -lt $payload.Length; $offset += 4096) {
    $length = [Math]::Min(4096, $payload.Length - $offset)
    $more = if ($offset + $length -lt $payload.Length) { 1 } else { 0 }
    $parameters = if ($offset -eq 0) { "$header,m=$more" } else { "m=$more" }
    [Console]::Write("${escape}_G$parameters;" + $payload.Substring($offset, $length) + "${escape}\")
}
[Console]::Out.Flush()
