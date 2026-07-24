<#
.SYNOPSIS
    Shared content-addressed validation receipt module for BotNexus.

.DESCRIPTION
    Provides a single, portable implementation of the "stage -> validate -> commit"
    receipt used by both the test entry points and the pre-commit hook (issue #2143).

    A receipt is a small JSON document that certifies that the *exact content being
    committed* has already passed the current required validation policy. The core
    correctness rule is exact-content addressing: a receipt is reusable only when its
    prospective Git tree hash equals the current staged tree hash AND every toolchain /
    policy identity input still matches. Any missing, malformed, failed, stale, expired,
    or mismatched receipt fails closed - the caller re-runs normal validation.

    Design notes:
      * The prospective tree hash is computed with `git write-tree` against a temporary
        index seeded from the staged snapshot, so unstaged/untracked working-tree noise
        cannot certify different staged content.
      * The policy hash covers this module plus the validation entry-point scripts and
        the mandatory safety-net patterns, so changing required tests invalidates old
        receipts.
      * The toolchain hash covers global.json and the tool manifest/lock inputs, so SDK
        or dependency policy changes invalidate old receipts.
      * Receipts are written atomically (temp file + move) only after every required
        command has succeeded. An interrupted producer never leaves a reusable receipt.
      * Storage is portable: the default location lives under the Git common directory
        (shared across linked worktrees) but an explicit path (parameter or the
        BOTNEXUS_VALIDATION_RECEIPT environment variable) supports container/CI exchange.
#>

Set-StrictMode -Version Latest

# Schema version for the receipt JSON. Bump when the required-field contract changes;
# a mismatched schema version fails closed by design.
$script:ReceiptSchemaVersion = 2

function Invoke-ReceiptGit {
    <#
        Runs git against a repository and returns trimmed stdout. Throws on failure so
        callers never proceed on a partially-computed identity.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & git -C $RepoRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFailure) { return $null }
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return ($output -join [Environment]::NewLine).Trim()
}

function Get-ContentHash {
    <#
        Returns a lowercase SHA-256 hex digest over UTF-8 bytes of the supplied string.
        Used for stable, cross-platform policy/toolchain fingerprints.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-FileHashOrEmpty {
    <#
        Returns "<relativePath>:<sha256>" for a file, or "<relativePath>:absent" when it
        does not exist. Absence is part of the identity: adding or removing a lock file
        must invalidate old receipts.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$RelativePath
    )
    $full = Join-Path $RepoRoot $RelativePath
    if (Test-Path $full -PathType Leaf) {
        $bytes = [IO.File]::ReadAllBytes($full)
        $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        return "${RelativePath}:${digest}"
    }
    return "${RelativePath}:absent"
}

function Resolve-BotNexusRepoRoot {
    <#
        Resolves the top-level working directory for a worktree path. Throws when the
        path is not inside a git repository.
    #>
    param([string]$WorktreePath = (Get-Location).Path)
    $root = Invoke-ReceiptGit -RepoRoot $WorktreePath -Arguments @('rev-parse', '--show-toplevel')
    if ([string]::IsNullOrWhiteSpace($root)) {
        throw "WorktreePath is not inside a git repository: $WorktreePath"
    }
    return $root
}

function Get-BotNexusStagedTreeHash {
    <#
    .SYNOPSIS
        Computes the prospective Git tree hash for the staged snapshot.

    .DESCRIPTION
        Runs `git write-tree` against a temporary index seeded from the current index
        (the staged snapshot). This is the content address of exactly what would be
        committed - it ignores unstaged/untracked working-tree changes, which is the
        whole point of the exact-content rule.
    #>
    param([Parameter(Mandatory)][string]$RepoRoot)

    $tempIndex = Join-Path ([IO.Path]::GetTempPath()) "botnexus-receipt-$([Guid]::NewGuid().ToString('N')).index"
    $previousIndex = $env:GIT_INDEX_FILE
    try {
        # Seed the temp index from the real index so only *staged* content is measured.
        $gitDir = Invoke-ReceiptGit -RepoRoot $RepoRoot -Arguments @('rev-parse', '--absolute-git-dir')
        $sourceIndex = Join-Path $gitDir 'index'
        if (Test-Path $sourceIndex) {
            Copy-Item -LiteralPath $sourceIndex -Destination $tempIndex -Force
        }
        $env:GIT_INDEX_FILE = $tempIndex
        $tree = Invoke-ReceiptGit -RepoRoot $RepoRoot -Arguments @('write-tree')
        if ([string]::IsNullOrWhiteSpace($tree)) { throw 'Unable to compute staged tree hash.' }
        return $tree
    }
    finally {
        if ($null -ne $previousIndex) { $env:GIT_INDEX_FILE = $previousIndex }
        else { Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue }
        Remove-Item $tempIndex -Force -ErrorAction SilentlyContinue
        Remove-Item "$tempIndex.lock" -Force -ErrorAction SilentlyContinue
    }
}

function Test-BotNexusWorkingTreeClean {
    <#
    .SYNOPSIS
        Reports whether the working tree matches the staged snapshot for tracked files.

    .DESCRIPTION
        `dotnet test` reads the working tree rather than the index, so a receipt-producing
        run may only certify staged content when the working tree already equals that
        staged content for tracked files. Untracked files that are not staged do not
        affect the tracked build/test graph and are ignored here; the caller is expected
        to run from a materialized snapshot when that guarantee is insufficient.
    #>
    param([Parameter(Mandatory)][string]$RepoRoot)
    $dirty = Invoke-ReceiptGit -RepoRoot $RepoRoot -Arguments @('diff', '--name-only')
    return [string]::IsNullOrWhiteSpace($dirty)
}

function Get-BotNexusValidationIdentity {
    <#
    .SYNOPSIS
        Computes the full content-addressed identity for the current staged candidate.

    .DESCRIPTION
        Returns an object with every field the exact-content rule depends on: repository
        identity, prospective staged tree hash, base commit, policy hash (validation
        scripts + safety-net patterns), and toolchain hash (SDK + tool manifest/lock).
        Two identities are equivalent only when all of these match.
    #>
    param(
        [string]$WorktreePath = (Get-Location).Path,
        [string]$BaseRef = 'origin/main',
        [string]$Configuration = 'Debug',
        [string]$TargetFramework = 'net10.0'
    )

    $repoRoot = Resolve-BotNexusRepoRoot -WorktreePath $WorktreePath
    $head = Invoke-ReceiptGit -RepoRoot $repoRoot -Arguments @('rev-parse', 'HEAD')
    $baseCommit = Invoke-ReceiptGit -RepoRoot $repoRoot -Arguments @('rev-parse', $BaseRef) -AllowFailure
    if ([string]::IsNullOrWhiteSpace($baseCommit)) {
        throw "Base ref cannot be resolved: $BaseRef"
    }
    $tree = Get-BotNexusStagedTreeHash -RepoRoot $repoRoot

    $repoIdentity = Invoke-ReceiptGit -RepoRoot $repoRoot -Arguments @('config', '--get', 'remote.origin.url') -AllowFailure
    if ([string]::IsNullOrWhiteSpace($repoIdentity)) { $repoIdentity = 'local' }
    # Strip embedded credentials so the receipt is portable and not secret-bearing.
    $repoIdentity = $repoIdentity -replace '://[^@/]+@', '://'

    # Policy hash: this module plus the validation entry points plus the safety-net
    # patterns. Changing what "required validation" means invalidates old receipts.
    $policyInputs = @(
        Get-FileHashOrEmpty -RepoRoot $repoRoot -RelativePath 'scripts/repo/ValidationReceipt.psm1'
        Get-FileHashOrEmpty -RepoRoot $repoRoot -RelativePath 'scripts/repo/Validate-PreCommit.ps1'
        Get-FileHashOrEmpty -RepoRoot $repoRoot -RelativePath 'scripts/repo/Invoke-LocalValidation.ps1'
        Get-FileHashOrEmpty -RepoRoot $repoRoot -RelativePath 'scripts/repo/test-impacted.ps1'
        'safety-nets:Architecture.Tests,Scenarios.Tests'
    ) -join "`n"
    $policyHash = Get-ContentHash -Text $policyInputs

    # Toolchain hash: SDK pin plus tool manifest/lock inputs.
    $toolchainInputs = @(
        Get-FileHashOrEmpty -RepoRoot $repoRoot -RelativePath 'global.json'
        Get-FileHashOrEmpty -RepoRoot $repoRoot -RelativePath 'dotnet-tools.json'
        Get-FileHashOrEmpty -RepoRoot $repoRoot -RelativePath '.config/dotnet-tools.json'
        Get-FileHashOrEmpty -RepoRoot $repoRoot -RelativePath 'Directory.Packages.props'
    ) -join "`n"
    $toolchainHash = Get-ContentHash -Text $toolchainInputs

    $sdkVersion = $null
    $globalJson = Join-Path $repoRoot 'global.json'
    if (Test-Path $globalJson) {
        try { $sdkVersion = (Get-Content $globalJson -Raw | ConvertFrom-Json).sdk.version } catch { $sdkVersion = $null }
    }

    return [pscustomobject]@{
        repoRoot = $repoRoot
        repository = $repoIdentity
        head = $head
        baseRef = $BaseRef
        baseCommit = $baseCommit
        tree = $tree
        policyHash = $policyHash
        toolchainHash = $toolchainHash
        configuration = $Configuration
        targetFramework = $TargetFramework
        sdkVersion = $sdkVersion
    }
}

function Get-BotNexusReceiptPath {
    <#
    .SYNOPSIS
        Resolves the receipt file path for a repository.

    .DESCRIPTION
        Resolution order (fail-closed friendly, portable):
          1. Explicit -Path parameter.
          2. BOTNEXUS_VALIDATION_RECEIPT environment variable (container/CI exchange).
          3. Default: <git-common-dir>/botnexus-validation/receipt.json, which is shared
             across all linked worktrees and is never a tracked workspace file.
    #>
    param(
        [string]$WorktreePath = (Get-Location).Path,
        [string]$Path
    )
    if (-not [string]::IsNullOrWhiteSpace($Path)) { return $Path }
    if (-not [string]::IsNullOrWhiteSpace($env:BOTNEXUS_VALIDATION_RECEIPT)) {
        return $env:BOTNEXUS_VALIDATION_RECEIPT
    }
    $repoRoot = Resolve-BotNexusRepoRoot -WorktreePath $WorktreePath
    # git-common-dir is shared by all linked worktrees, so receipts survive worktree churn.
    $commonDir = Invoke-ReceiptGit -RepoRoot $repoRoot -Arguments @('rev-parse', '--git-common-dir')
    if (-not [IO.Path]::IsPathRooted($commonDir)) { $commonDir = Join-Path $repoRoot $commonDir }
    return Join-Path (Join-Path $commonDir 'botnexus-validation') 'receipt.json'
}

function New-BotNexusValidationReceipt {
    <#
    .SYNOPSIS
        Atomically writes a validation receipt after successful validation.

    .DESCRIPTION
        MUST be called only after every required build/test command has succeeded. The
        receipt is written to a temp file and then moved into place so an interrupted
        producer never leaves a reusable receipt. When the working tree does not match
        the staged snapshot for tracked files, the function refuses to emit a reusable
        receipt (returns $null) unless -AllowDirtyWorkingTree is set (used by producers
        that already validated a materialized snapshot).
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('impacted', 'full', 'strict', 'playwright')][string]$Scope,
        [Parameter(Mandatory)][string[]]$TestProjects,
        [string]$WorktreePath = (Get-Location).Path,
        [string]$BaseRef = 'origin/main',
        [string]$Configuration = 'Debug',
        [string]$TargetFramework = 'net10.0',
        [string]$Path,
        [string]$RunId = ("{0}-{1}" -f ([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')), ([Guid]::NewGuid().ToString('N').Substring(0, 8))),
        [Nullable[TimeSpan]]$Ttl,
        [switch]$AllowDirtyWorkingTree
    )

    $identity = Get-BotNexusValidationIdentity -WorktreePath $WorktreePath -BaseRef $BaseRef -Configuration $Configuration -TargetFramework $TargetFramework

    if (-not $AllowDirtyWorkingTree -and -not (Test-BotNexusWorkingTreeClean -RepoRoot $identity.repoRoot)) {
        Write-Warning 'Working tree differs from the staged snapshot; declining to emit a reusable receipt.'
        return $null
    }

    $now = [DateTime]::UtcNow
    $expires = if ($Ttl) { $now.Add($Ttl).ToString('o') } else { $null }
    $receipt = [ordered]@{
        schemaVersion = $script:ReceiptSchemaVersion
        repository = $identity.repository
        tree = $identity.tree
        head = $identity.head
        baseRef = $identity.baseRef
        baseCommit = $identity.baseCommit
        policyHash = $identity.policyHash
        toolchainHash = $identity.toolchainHash
        configuration = $identity.configuration
        targetFramework = $identity.targetFramework
        sdkVersion = $identity.sdkVersion
        scope = $Scope
        testProjects = @($TestProjects | Sort-Object -Unique)
        buildResult = 'success'
        testResult = 'success'
        runId = $RunId
        createdUtc = $now.ToString('o')
        expiresUtc = $expires
    }

    $receiptPath = Get-BotNexusReceiptPath -WorktreePath $WorktreePath -Path $Path
    $directory = Split-Path $receiptPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    # Atomic replace: write to a sibling temp file, then move over the target.
    $tempPath = "$receiptPath.$([Guid]::NewGuid().ToString('N')).tmp"
    ($receipt | ConvertTo-Json -Depth 6) | Set-Content -Path $tempPath -Encoding utf8NoBOM
    Move-Item -LiteralPath $tempPath -Destination $receiptPath -Force

    return [pscustomobject]@{ Path = $receiptPath; Receipt = $receipt }
}

function Remove-BotNexusValidationReceipt {
    <#
    .SYNOPSIS
        Removes any receipt for the repository. Call at the start of a producing run so a
        failed or interrupted run cannot leave a stale-but-matching receipt behind.
    #>
    param(
        [string]$WorktreePath = (Get-Location).Path,
        [string]$Path
    )
    $receiptPath = Get-BotNexusReceiptPath -WorktreePath $WorktreePath -Path $Path
    Remove-Item -LiteralPath $receiptPath -Force -ErrorAction SilentlyContinue
}

function Test-BotNexusValidationReceipt {
    <#
    .SYNOPSIS
        Verifies whether a receipt certifies the current staged candidate. Fails closed.

    .DESCRIPTION
        Returns an object with a boolean Match and a human-readable Reason. Match is true
        only when the receipt exists, parses, has the expected schema version, is not
        expired, records success for both build and test, and every identity field
        (tree, head, baseCommit, policyHash, toolchainHash, configuration,
        targetFramework) equals the current computed identity. Any other condition
        returns Match=$false with the specific mismatch reason, so callers re-run
        validation (fail closed).
    #>
    param(
        [string]$WorktreePath = (Get-Location).Path,
        [string]$BaseRef = 'origin/main',
        [string]$Configuration = 'Debug',
        [string]$TargetFramework = 'net10.0',
        [string]$Path,
        [string[]]$RequiredScopes = @('strict', 'full')
    )

    $result = [pscustomobject]@{ Match = $false; Reason = 'unknown'; Receipt = $null }

    $receiptPath = Get-BotNexusReceiptPath -WorktreePath $WorktreePath -Path $Path
    if (-not (Test-Path $receiptPath -PathType Leaf)) {
        $result.Reason = 'No validation receipt is present.'
        return $result
    }

    $receipt = $null
    try {
        $receipt = Get-Content $receiptPath -Raw | ConvertFrom-Json
    }
    catch {
        $result.Reason = "Receipt is malformed and cannot be parsed: $($_.Exception.Message)"
        return $result
    }
    $result.Receipt = $receipt

    $requiredFields = @('schemaVersion', 'tree', 'head', 'baseCommit', 'policyHash', 'toolchainHash', 'configuration', 'targetFramework', 'scope', 'buildResult', 'testResult')
    foreach ($field in $requiredFields) {
        if (-not ($receipt.PSObject.Properties.Name -contains $field)) {
            $result.Reason = "Receipt is missing required field '$field'."
            return $result
        }
    }

    if ($receipt.schemaVersion -ne $script:ReceiptSchemaVersion) {
        $result.Reason = "Receipt schema version $($receipt.schemaVersion) does not match required $script:ReceiptSchemaVersion."
        return $result
    }
    if ($receipt.buildResult -ne 'success' -or $receipt.testResult -ne 'success') {
        $result.Reason = 'Receipt does not record a successful build and test.'
        return $result
    }
    if ($RequiredScopes -and ($receipt.scope -notin $RequiredScopes)) {
        $result.Reason = "Receipt scope '$($receipt.scope)' is not one of the required scopes: $($RequiredScopes -join ', ')."
        return $result
    }
    if ($receipt.expiresUtc) {
        try {
            $expiry = [DateTime]::Parse($receipt.expiresUtc, $null, [Globalization.DateTimeStyles]::RoundtripKind)
            if ([DateTime]::UtcNow -gt $expiry) {
                $result.Reason = "Receipt expired at $($receipt.expiresUtc)."
                return $result
            }
        }
        catch {
            $result.Reason = 'Receipt has an unparseable expiry timestamp.'
            return $result
        }
    }

    $identity = Get-BotNexusValidationIdentity -WorktreePath $WorktreePath -BaseRef $BaseRef -Configuration $Configuration -TargetFramework $TargetFramework

    $comparisons = [ordered]@{
        'staged tree hash' = @($receipt.tree, $identity.tree)
        'HEAD' = @($receipt.head, $identity.head)
        'base commit' = @($receipt.baseCommit, $identity.baseCommit)
        'validation policy' = @($receipt.policyHash, $identity.policyHash)
        'toolchain' = @($receipt.toolchainHash, $identity.toolchainHash)
        'configuration' = @($receipt.configuration, $identity.configuration)
        'target framework' = @($receipt.targetFramework, $identity.targetFramework)
    }
    foreach ($entry in $comparisons.GetEnumerator()) {
        if ($entry.Value[0] -ne $entry.Value[1]) {
            $result.Reason = "Receipt $($entry.Key) does not match the current candidate."
            return $result
        }
    }

    $result.Match = $true
    $result.Reason = "Receipt matches the exact staged candidate (run $($receipt.runId))."
    return $result
}

Export-ModuleMember -Function @(
    'Get-BotNexusValidationIdentity',
    'Get-BotNexusStagedTreeHash',
    'Test-BotNexusWorkingTreeClean',
    'Get-BotNexusReceiptPath',
    'New-BotNexusValidationReceipt',
    'Remove-BotNexusValidationReceipt',
    'Test-BotNexusValidationReceipt',
    'Resolve-BotNexusRepoRoot'
)
