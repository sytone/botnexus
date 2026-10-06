Set-StrictMode -Version Latest

function ConvertTo-CoreTestSafeDiagnosticText {
    [CmdletBinding()]
    param([AllowNull()][string] $Text)

    if ($null -eq $Text) { return '' }
    $safe = $Text -replace '(?i)\b(Bearer)\s+[^\s,;]+', '$1 [REDACTED]'
    $safe = $safe -replace '(?i)\b(password|passwd|pwd|client[_-]?secret|access[_-]?token|refresh[_-]?token|api[_-]?key|accountkey|sharedaccesssignature|authorization)(\s*[:=]\s*|\s+)[^\s,;]+', '$1$2[REDACTED]'
    $safe = $safe -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', ' '
    # GitHub Actions command data escaping. Newlines are encoded so untrusted diagnostic text
    # cannot start a new workflow-command line; colons and commas are encoded defensively too.
    return $safe.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A').Replace(':', '%3A').Replace(',', '%2C')
}

function Get-CoreTestFailureDiagnostics {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $TrxPaths,
        [Parameter(Mandatory)][string] $LogPath,
        [ValidateRange(1, 100000)][int] $MaxCharacters = 4000,
        [ValidateRange(1, 1000)][int] $MaxFailures = 20,
        [ValidateRange(1, 1000)][int] $MaxLogLines = 40,
        [ValidateRange(1, 200)][int] $MaxTrxFiles = 100,
        [ValidateRange(1024, 1048576)][int] $MaxLogBytes = 65536,
        [ValidateRange(1024, 16777216)][int] $MaxTrxBytes = 8388608
    )

    $lines = [Collections.Generic.List[string]]::new()
    foreach ($trxPath in ($TrxPaths | Select-Object -First $MaxTrxFiles)) {
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { continue }
        try {
            $item = Get-Item -LiteralPath $trxPath -ErrorAction Stop
            if ($item.Length -le 0 -or $item.Length -gt $MaxTrxBytes) { continue }
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $settings.MaxCharactersInDocument = $MaxTrxBytes
            $reader = [Xml.XmlReader]::Create($trxPath, $settings)
            try {
                $document = [Xml.XmlDocument]::new()
                $document.XmlResolver = $null
                $document.Load($reader)
            }
            finally { $reader.Dispose() }

            $failed = @($document.SelectNodes("//*[local-name()='UnitTestResult' and translate(@outcome,'FAILED','failed')='failed']"))
            foreach ($result in ($failed | Select-Object -First $MaxFailures)) {
                $name = [string]$result.GetAttribute('testName')
                if ([string]::IsNullOrWhiteSpace($name)) { $name = '(unnamed test)' }
                if ($name.Length -gt 300) { $name = $name.Substring(0, 300) + ' [truncated]' }
                $messageNode = $result.SelectSingleNode(".//*[local-name()='ErrorInfo']/*[local-name()='Message']")
                $message = if ($null -ne $messageNode) { [string]$messageNode.InnerText } else { '(no failure message recorded)' }
                $message = ($message -replace '\s+', ' ').Trim()
                if ($message.Length -gt 1000) { $message = $message.Substring(0, 1000) + ' [truncated]' }
                $lines.Add("- $name`: $message")
            }
            if ($lines.Count -ge $MaxFailures) { break }
        }
        catch {
            # TRX can be absent, oversized, or truncated when the test host fails abruptly.
            # In all those cases use the bounded process-log fallback below.
            continue
        }
    }

    if ($lines.Count -gt 0) {
        $content = @('Failed tests from TRX:') + @($lines)
    }
    else {
        $content = @('TRX failure details unavailable; bounded tail of core-test-process.log:')
        if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
            $stream = $null
            try {
                $stream = [IO.File]::Open($LogPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                $start = [Math]::Max(0, $stream.Length - $MaxLogBytes)
                [void]$stream.Seek($start, [IO.SeekOrigin]::Begin)
                $buffer = [byte[]]::new([int]($stream.Length - $start))
                $read = 0
                while ($read -lt $buffer.Length) {
                    $count = $stream.Read($buffer, $read, $buffer.Length - $read)
                    if ($count -le 0) { break }
                    $read += $count
                }
                $tailText = [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
                $tailLines = @($tailText -split '\r?\n' | Select-Object -Last $MaxLogLines)
                foreach ($line in $tailLines) {
                    if (-not [string]::IsNullOrWhiteSpace($line)) { $content += $line }
                }
            }
            catch { $content += '(process log could not be read)' }
            finally { if ($null -ne $stream) { $stream.Dispose() } }
        }
        else { $content += '(process log is missing)' }
    }

    $safeText = ConvertTo-CoreTestSafeDiagnosticText -Text ($content -join [Environment]::NewLine)
    if ($safeText.Length -gt $MaxCharacters) {
        $marker = ' [truncated]'
        $keep = [Math]::Max(0, $MaxCharacters - $marker.Length)
        $safeText = $safeText.Substring(0, $keep) + $marker
    }
    return $safeText
}
