Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'SourceSnapshot.psm1') -Force

function Write-AzureBuildTestEvidence {
    <#
    .SYNOPSIS
    Atomically installs immutable validation evidence.
    .DESCRIPTION
    Writes a same-directory temporary file, then publishes using a no-overwrite rename.
    Concurrent identical writers succeed; an existing different document is never replaced.
    .PARAMETER Path
    Destination JSON path in the worktree Git directory.
    .PARAMETER Evidence
    JSON-serializable evidence with deterministic property order.
    .EXAMPLE
    Write-AzureBuildTestEvidence -Path $path -Evidence $context
    .OUTPUTS
    System.String. Destination path.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Evidence
    )
    $json = ConvertTo-Json -InputObject $Evidence -Depth 30
    if (-not $PSCmdlet.ShouldProcess($Path, 'Atomically install immutable evidence')) {
        return
    }
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = Join-Path $directory ".evidence-$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
        try {
            [IO.File]::Move($temporary, $Path, $false)
        }
        catch [IO.IOException] {
            if (-not [IO.File]::Exists($Path) -or [IO.File]::ReadAllText($Path) -cne $json) {
                throw "Conflicting validation evidence already exists: $Path"
            }
        }
    }
    finally {
        if ([IO.File]::Exists($temporary)) {
            [IO.File]::Delete($temporary)
        }
    }
    return $Path
}

function Get-AzureBuildTestReceiptToken {
    <#
    .SYNOPSIS
    Reads the current singleton receipt byte identity.
    .DESCRIPTION
    Returns explicit absence or SHA256 of installed bytes. The finalizer rechecks under
    its exclusive lock, so an observation racing another writer safely fails comparison.
    .PARAMETER Path
    Singleton receipt path.
    .EXAMPLE
    Get-AzureBuildTestReceiptToken -Path $path
    .OUTPUTS
    System.String. absent or sha256:<hex>.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)
    if (-not [IO.File]::Exists($Path)) {
        return 'absent'
    }
    $hash = [Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($Path))
    return 'sha256:' + [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Set-AzureBuildTestReceipt {
    <#
    .SYNOPSIS
    Atomically finalizes the current receipt using a persisted predecessor token.
    .DESCRIPTION
    Takes a bounded cross-process exclusive file lock across compare and replacement.
    Identical installed bytes succeed idempotently; distinct intervening evidence rejects.
    Archives predecessor bytes by hash before an atomic same-directory replacement.
    .PARAMETER Path
    Singleton receipt path in the worktree Git directory.
    .PARAMETER Evidence
    Desired receipt document with deterministic property order.
    .PARAMETER ExpectedPrevious
    Receipt byte token captured before execution start, or explicit absent token.
    .PARAMETER LockTimeoutMilliseconds
    Maximum exclusive-lock acquisition wait; timeout fails without modifying the receipt.
    .EXAMPLE
    Set-AzureBuildTestReceipt -Path $path -Evidence $receipt -ExpectedPrevious $context.expectedPreviousReceipt
    .OUTPUTS
    System.String. Installed receipt path.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Evidence,
        [Parameter(Mandatory)][string]$ExpectedPrevious,
        [ValidateRange(1, 10000)][int]$LockTimeoutMilliseconds = 5000
    )
    if ($ExpectedPrevious -cnotmatch '^(absent|sha256:[0-9a-f]{64})$') {
        throw 'Missing or malformed expected predecessor token.'
    }
    if (-not $PSCmdlet.ShouldProcess($Path, 'Compare and atomically replace current receipt')) {
        return
    }
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $lock = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $lock) {
        try {
            $lock = [IO.File]::Open("$Path.lock", [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        catch [IO.IOException] {
            if ($clock.ElapsedMilliseconds -ge $LockTimeoutMilliseconds) {
                throw 'Receipt exclusive lock acquisition timed out.'
            }
            Start-Sleep -Milliseconds 25
        }
    }
    $temporary = Join-Path $directory ".receipt-$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = ConvertTo-Json -InputObject $Evidence -Depth 30
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        $desiredHash = 'sha256:' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $installed = Get-AzureBuildTestReceiptToken -Path $Path
        if ($installed -ceq $desiredHash) {
            return $Path
        }
        if ($installed -cne $ExpectedPrevious) {
            throw 'Receipt predecessor conflict: an intervening distinct writer changed the current receipt.'
        }
        if ($installed -cne 'absent') {
            $archive = Join-Path $directory ('archive/' + $installed.Substring(7) + '.json')
            [IO.Directory]::CreateDirectory((Split-Path -Parent $archive)) | Out-Null
            if ([IO.File]::Exists($archive)) {
                if ((Get-AzureBuildTestReceiptToken -Path $archive) -cne $installed) {
                    throw 'Receipt archive conflict.'
                }
            }
            else {
                [IO.File]::Copy($Path, $archive, $false)
            }
        }
        [IO.File]::WriteAllBytes($temporary, $bytes)
        if ($installed -ceq 'absent') {
            [IO.File]::Move($temporary, $Path, $false)
        }
        else {
            [IO.File]::Replace($temporary, $Path, [NullString]::Value)
        }
        return $Path
    }
    finally {
        if ([IO.File]::Exists($temporary)) {
            [IO.File]::Delete($temporary)
        }
        $lock.Dispose()
    }
}

function Assert-AzureBuildTestArtifact {
    <#
    .SYNOPSIS
    Requires mode-specific artifacts in the fresh canonical run download.
    .DESCRIPTION
    Inspects staging before destination merging; stale local files cannot prove a run.
    .PARAMETER Mode
    Validation mode requested from the execution.
    .PARAMETER Path
    Fresh canonical run staging directory, not the merged output directory.
    .EXAMPLE
    Assert-AzureBuildTestArtifact -Mode strict -Path $freshRun
    .OUTPUTS
    None. Throws when required evidence is absent.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Mode, [Parameter(Mandatory)][string]$Path)
    if ($Mode -ceq 'strict' -and -not (Test-Path -LiteralPath (Join-Path $Path 'playwright.log') -PathType Leaf)) {
        throw 'Strict Playwright artifact is missing from fresh canonical download.'
    }
}

function Complete-AzureBuildTestRecovery {
    <#
    .SYNOPSIS
    Certifies one successful existing Azure execution for the unchanged candidate.
    .DESCRIPTION
    Requires provider terminal success and execution template run/mode identity, the
    persisted start fingerprint, canonical runner source proof and complete numeric test
    counters. Replaces the normal strict-consumer receipt using the persisted predecessor token.
    Does not contact Azure or start any execution. The caller must fetch Provider directly
    from Azure and Result from that run's artifact prefix, not accept arbitrary local JSON.
    .PARAMETER Context
    Persisted execution identity and start-time fingerprint.
    .PARAMETER Provider
    Current Azure execution resource, including properties.template.containers.env.
    .PARAMETER Result
    Downloaded canonical result.json from the execution's run prefix.
    .PARAMETER WorktreePath
    Current Git candidate to compare against the start-time fingerprint.
    .PARAMETER ArtifactPath
    Downloaded run artifact directory; strict mode also requires playwright.log.
    .EXAMPLE
    Complete-AzureBuildTestRecovery -Context $context -Provider $execution -Result $result -WorktreePath $repo
    .OUTPUTS
    System.String. Normal Azure validation receipt path.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)]$Provider,
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][string]$WorktreePath,
        [string]$ArtifactPath
    )
    if ($Context.version -isnot [int] -and $Context.version -isnot [long] -or $Context.version -ne 1) {
        throw 'Invalid recovery context version.'
    }
    if ($Context.runId -cnotmatch '^[A-Za-z0-9-]+$' -or $Context.executionName -cnotmatch '^[A-Za-z0-9-]+$' -or
        $Context.mode -cnotin @('strict', 'impacted', 'full', 'core', 'playwright')) {
        throw 'Invalid recovery run or mode identity.'
    }
    $expectedId = "/subscriptions/$($Context.subscriptionId)/resourceGroups/$($Context.resourceGroup)/providers/Microsoft.App/jobs/$($Context.jobName)/executions/$($Context.executionName)"
    if ($Provider.name -cne $Context.executionName -or $Provider.id -ine $expectedId -or $Provider.properties.status -cne 'Succeeded') {
        throw 'Provider does not prove terminal success for this execution.'
    }
    $containers = @($Provider.properties.template.containers)
    if ($containers.Count -ne 1) {
        throw 'Ambiguous provider execution container identity.'
    }
    foreach ($identity in @(@{ name = 'RUN_ID'; value = $Context.runId }, @{ name = 'TEST_MODE'; value = $Context.mode })) {
        $entries = @($containers[0].env | Where-Object { $_.name -ceq $identity.name })
        if ($entries.Count -ne 1 -or $entries[0].value -cne $identity.value) {
            throw 'Provider execution run/mode identity mismatch.'
        }
    }
    $fingerprint = $Context.fingerprint
    foreach ($field in @('fingerprint', 'head', 'baseCommit', 'tree')) {
        if ($fingerprint.$field -isnot [string] -or $fingerprint.$field -cnotmatch '^[0-9a-f]{40,64}$') {
            throw 'Malformed persisted candidate fingerprint.'
        }
    }
    if ($fingerprint.sourceSnapshot.version -ne 1 -or $fingerprint.sourceSnapshot.digest -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Malformed persisted source digest.'
    }
    if ($Result.exitCode -isnot [int] -and $Result.exitCode -isnot [long] -or $Result.exitCode -ne 0) {
        throw 'Runner exit code is not numeric success.'
    }
    if (-not $Result.PSObject.Properties['sourceSnapshot'] -or $null -eq $Result.sourceSnapshot) {
        throw 'Missing exact-source proof: operator must deploy the updated runner image before using this sender.'
    }
    if ($Result.sourceSnapshot.version -isnot [int] -and $Result.sourceSnapshot.version -isnot [long]) {
        throw 'Noncanonical runner source proof.'
    }
    $completed = [DateTimeOffset]::MinValue
    $completionText = if ($Result.completedUtc -is [DateTime]) {
        $Result.completedUtc.ToUniversalTime().ToString('o')
    }
    else {
        $Result.completedUtc
    }
    if ($completionText -isnot [string] -or -not [DateTimeOffset]::TryParse($completionText, [ref]$completed)) {
        throw 'Missing or malformed runner completion time.'
    }
    Assert-SourceSnapshotResult -Result $Result -Digest $fingerprint.sourceSnapshot.digest -RunId $Context.runId -Mode $Context.mode
    if ($Context.mode -ceq 'strict' -and (-not $ArtifactPath -or -not (Test-Path -LiteralPath (Join-Path $ArtifactPath 'playwright.log') -PathType Leaf))) {
        throw 'Strict Playwright artifact is missing.'
    }
    $current = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $WorktreePath -BaseRef $fingerprint.baseRef
    foreach ($field in @('fingerprint', 'head', 'baseRef', 'baseCommit', 'tree')) {
        if ($current.$field -cne $fingerprint.$field) {
            throw 'Source changed before receipt; validation cannot certify this worktree.'
        }
    }
    if ($current.sourceSnapshot.digest -cne $fingerprint.sourceSnapshot.digest) {
        throw 'Source changed before receipt; source digest differs.'
    }
    $gitDirectory = (Invoke-SnapshotGit $WorktreePath @('rev-parse', '--absolute-git-dir')).Trim()
    $receipt = [ordered]@{
        version = 1
        sourceSnapshot = $Result.sourceSnapshot
        fingerprint = $fingerprint.fingerprint
        head = $fingerprint.head
        baseRef = $fingerprint.baseRef
        baseCommit = $fingerprint.baseCommit
        tree = $fingerprint.tree
        mode = $Context.mode
        runId = $Context.runId
        executionName = $Context.executionName
        completedUtc = $completed.ToUniversalTime().ToString('o')
    }
    $path = Join-Path $gitDirectory 'botnexus-validation/azure-buildtest.json'
    if ($PSCmdlet.ShouldProcess($path, 'Certify existing Azure execution')) {
        Set-AzureBuildTestReceipt -Path $path -Evidence $receipt -ExpectedPrevious $Context.expectedPreviousReceipt
    }
}

Export-ModuleMember -Function Write-AzureBuildTestEvidence, Get-AzureBuildTestReceiptToken, Set-AzureBuildTestReceipt, Assert-AzureBuildTestArtifact, Complete-AzureBuildTestRecovery
