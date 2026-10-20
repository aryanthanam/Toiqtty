# Per-process integration. Profiles and PSReadLine configuration remain intact.
if (Get-Variable -Name ToiqttyOriginalPrompt -Scope Global -ErrorAction Ignore) { return }
$global:ToiqttyOriginalPrompt = (Get-Item Function:\prompt).ScriptBlock
$global:ToiqttyCommandActive = $false

# Wrap the host's line reader, rather than replacing Enter bindings or history
# callbacks. This also covers vi mode, multiline input and custom key bindings.
if (Get-Command PSConsoleHostReadLine -ErrorAction Ignore) {
    $global:ToiqttyOriginalReadLine = (Get-Item Function:\PSConsoleHostReadLine).ScriptBlock
    function global:PSConsoleHostReadLine {
        $line = & $global:ToiqttyOriginalReadLine
        Set-StrictMode -Off
        if (![string]::IsNullOrWhiteSpace($line)) {
            $global:ToiqttyCommandActive = $true
            $global:ToiqttyLastError = $global:Error[0]
            # cmdline_url is decoded by upstream Ghostty, including multiline
            # commands. Cap metadata independently of what the shell executes.
            $metadata = if ($line.Length -gt 8192) { $line.Substring(0, 8192) } else { $line }
            # OSC 133 uses the upstream parser's 2048-byte fixed buffer. Keep
            # the percent-encoded form below it without splitting UTF-8.
            $bytes = [Text.Encoding]::UTF8.GetBytes($metadata)
            if ($bytes.Length -gt 600) {
                $length = 600
                while (($bytes[$length] -band 0xC0) -eq 0x80) { $length-- }
                $metadata = [Text.Encoding]::UTF8.GetString($bytes, 0, $length)
            }
            $encoded = [Uri]::EscapeDataString($metadata)
            [Console]::Write("$([char]27)]133;C;cmdline_url=$encoded$([char]7)")
        }
        $line
    }
}

function global:prompt {
    $succeeded = $?
    Set-StrictMode -Off
    $savedExitCode = $global:LASTEXITCODE
    $escape = [char]27
    if ($global:ToiqttyCommandActive) {
        $code = if ($succeeded) { 0 } elseif ($global:Error[0] -ne $global:ToiqttyLastError) { 1 }
            elseif ($null -ne $savedExitCode -and $savedExitCode -ne 0) { $savedExitCode } else { 1 }
        [Console]::Write("$escape]133;D;$code$([char]7)")
        $global:ToiqttyCommandActive = $false
    }
    try {
        $location = $ExecutionContext.SessionState.Path.CurrentFileSystemLocation.Path
        $uri = [Uri]::new($location)
        [Console]::Write("$escape]7;$($uri.AbsoluteUri)$([char]7)")
    } catch { }
    $global:LASTEXITCODE = $savedExitCode
    # Restore the previous success flag for Starship's status module without
    # adding to $Error. Prompt rendering is excluded from the command duration.
    if ($succeeded) { $text = & $global:ToiqttyOriginalPrompt }
    else {
        Write-Error 'Last command failed' -ErrorAction Ignore
        $text = & $global:ToiqttyOriginalPrompt
    }
    $global:LASTEXITCODE = $savedExitCode
    # Return markers with the prompt so PSReadLine measures visible text only.
    "$escape]133;A$([char]7)$text$escape]133;B$([char]7)"
}
