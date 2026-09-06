Set-StrictMode -Version Latest

# NUL-delimited UTF-8 avoids Git's quoted display paths and PowerShell line splitting.
function Invoke-SnapshotGit {
    param([string]$Root, [string[]]$Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $start.ArgumentList.Add('-C'); $start.ArgumentList.Add($Root)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        $output = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Snapshot git failed: $($errors.GetAwaiter().GetResult())" }
        return $output
    }
    finally { $process.Dispose() }
}

function Assert-SourceSnapshotPath {
    param([string]$Path)
    if ([string]::IsNullOrEmpty($Path) -or $Path -match '[\\\x00-\x1f\x7f:<>"|?*]' -or $Path.StartsWith('/')) {
        throw "Unsafe source path: $Path"
    }
    foreach ($part in $Path.Split('/')) {
        if ($part -in @('', '.', '..', '.git') -or $part -match '[. ]$|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw "Unsafe source path: $Path"
        }
    }
}

function Assert-SnapshotNoLinks {
    param([string]$Root, [string]$Path)
    $current = $Root
    foreach ($part in @('') + $Path.Split('/')) {
        if ($part) { $current = Join-Path $current $part }
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unsupported source link/reparse point: $current" }
        }
    }
}

function Get-SnapshotDigest {
    param([object[]]$Files)
    $material = [Text.StringBuilder]::new("botnexus-source-snapshot-v1`n")
    foreach ($file in $Files) {
        Assert-SourceSnapshotPath $file.path
        if ($file.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $file.length -lt 0) { throw 'Invalid source manifest hash/length.' }
        [void]$material.Append("$($file.path)`n$($file.length)`n$($file.sha256)`n")
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material.ToString()))).ToLowerInvariant()
}

function Get-SourceSnapshotManifest {
    param([Parameter(Mandatory)][string]$RepoRoot, [string]$CaptureRoot)
    $staged = Invoke-SnapshotGit $RepoRoot @('ls-files', '--stage', '-z')
    foreach ($entry in $staged.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($entry -notmatch '^(\d+) [a-f0-9]+ (\d)\t') { throw 'Invalid Git index entry.' }
        if ($Matches[2] -ne '0') { throw 'Cannot snapshot unmerged index entries. Resolve and stage conflicts first.' }
        if ($Matches[1] -notin @('100644', '100755')) { throw 'Unsupported source index mode (link or submodule).' }
    }
    $names = (Invoke-SnapshotGit $RepoRoot @('ls-files', '--cached', '--others', '--exclude-standard', '-z')).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    $set = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $names) { [void]$set.Add($name) }
    $portable = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = @(foreach ($name in $set) {
        Assert-SourceSnapshotPath $name
        Assert-SnapshotNoLinks $RepoRoot $name
        $path = Join-Path $RepoRoot $name
        # An indexed file replaced by a directory is deleted; enumerated descendants are
        # separate candidate files. Only surviving regular files reserve portable names.
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        if (-not $portable.Add($name)) { throw "Unsafe case-colliding source path: $name" }
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($CaptureRoot) {
            $destination = Join-Path $CaptureRoot $name
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.File]::WriteAllBytes($destination, $bytes)
        }
        [pscustomobject][ordered]@{path=$name; length=$bytes.LongLength; sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()}
    })
    [pscustomobject][ordered]@{version=1; digest=(Get-SnapshotDigest $files); files=$files}
}

function Get-SnapshotDiskFiles {
    param([string]$Root, [string]$Relative = '')
    foreach ($item in Get-ChildItem -LiteralPath (Join-Path $Root $Relative) -Force) {
        if (-not $Relative -and $item.Name -eq '.git') { continue }
        $name = if ($Relative) { "$Relative/$($item.Name)" } else { $item.Name }
        Assert-SourceSnapshotPath $name
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unsupported source link/reparse point: $name" }
        if ($item.PSIsContainer) { Get-SnapshotDiskFiles $Root $name } else { $name }
    }
}

function Assert-SourceSnapshot {
    param([string]$Root, $Manifest)
    if ($Manifest.version -ne 1 -or $Manifest.digest -cne (Get-SnapshotDigest @($Manifest.files))) { throw 'Invalid source manifest version/digest.' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $portable = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Manifest.files) {
        if (-not $expected.Add($file.path) -or -not $portable.Add($file.path)) { throw 'Duplicate source manifest path.' }
        Assert-SnapshotNoLinks $Root $file.path
        $path = Join-Path $Root $file.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing source file: $($file.path)" }
        $bytes = [IO.File]::ReadAllBytes($path)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($hash -cne $file.sha256 -or $bytes.LongLength -ne $file.length) { throw "Tampered source file: $($file.path)" }
    }
    $actual = @(Get-SnapshotDiskFiles $Root)
    if ($actual.Count -ne $expected.Count) { throw 'Source file set mismatch.' }
    foreach ($name in $actual) { if (-not $expected.Contains($name)) { throw "Extra source file: $name" } }
}

function Restore-SourceSnapshot {
    param([string]$Root, [string]$Archive, $Manifest, [string]$RunId)
    # Validate all ZIP entries BEFORE deleting or writing anything. No generic archive extraction.
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $entries = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($entry in $zip.Entries) {
            Assert-SourceSnapshotPath $entry.FullName
            $kind = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($kind -notin @(0, 0x8000) -or ($entry.ExternalAttributes -band 0x400)) { throw 'Unsupported archive link/directory.' }
            if (-not $entries.TryAdd($entry.FullName, $entry)) { throw 'Duplicate archive path.' }
        }
        if ($Manifest.version -ne 1 -or $Manifest.digest -cne (Get-SnapshotDigest @($Manifest.files))) { throw 'Invalid source manifest.' }
        if ($entries.Count -ne @($Manifest.files).Count) { throw 'Archive file set mismatch.' }
        foreach ($file in $Manifest.files) { if (-not $entries.ContainsKey($file.path)) { throw "Missing archive file: $($file.path)" } }
        # Refuse traversal through pre-existing reparse points; retain only clone metadata.
        Assert-SnapshotNoLinks $Root ''
        @(Get-SnapshotDiskFiles $Root) | Out-Null
        Get-ChildItem -LiteralPath $Root -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force
        foreach ($file in $Manifest.files) {
            $destination = Join-Path $Root $file.path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            $inputStream = $entries[$file.path].Open()
            $outputStream = [IO.File]::Create($destination)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
        Assert-SourceSnapshot -Root $Root -Manifest $Manifest
        [pscustomobject]@{version=1;digest=$Manifest.digest;runId=$RunId;verified=$true}
    }
    finally { $zip.Dispose() }
}

function Assert-SourceSnapshotResult {
    param($Result, [string]$Digest, [string]$RunId, [string]$Mode)
    if (-not $Result -or -not $Result.PSObject.Properties['sourceSnapshot'] -or -not $Result.sourceSnapshot) {
        throw 'Missing exact-source proof: operator must deploy the updated runner image before using this sender.'
    }
    $proof = $Result.sourceSnapshot
    if ($proof.version -ne 1 -or $proof.verified -isnot [bool] -or -not $proof.verified -or $proof.digest -cne $Digest -or $proof.runId -cne $RunId -or $Result.runId -cne $RunId -or $Result.mode -cne $Mode) { throw 'Source proof does not match this validation run.' }
    if ($Result.exitCode -ne 0 -or -not $Result.PSObject.Properties['tests'] -or -not $Result.tests) { throw 'Validation did not report successful tests.' }
    $tests = $Result.tests
    foreach ($field in @('total','executed','passed','failed','skipped','fixtureFailures')) {
        $value = $tests.$field
        if ($value -isnot [int] -and $value -isnot [long]) { throw 'Invalid test counters.' }
        if ($value -lt 0) { throw 'Invalid test counters.' }
    }
    if ($tests.isComplete -isnot [bool] -or -not $tests.isComplete -or $tests.failureReason -or $tests.total -le 0 -or $tests.executed -le 0 -or $tests.failed -ne 0 -or $tests.fixtureFailures -ne 0 -or $tests.passed -ne $tests.executed -or $tests.total -ne ($tests.executed + $tests.skipped)) { throw 'Incomplete or inconsistent test result.' }
    if ($Mode -in @('core','full') -and $tests.total -lt 12000) { throw 'Test result below minimum total.' }
}

Export-ModuleMember -Function Invoke-SnapshotGit, Assert-SourceSnapshotPath, Get-SourceSnapshotManifest, Assert-SourceSnapshot, Restore-SourceSnapshot, Assert-SourceSnapshotResult
