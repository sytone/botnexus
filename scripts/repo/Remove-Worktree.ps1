<#
.SYNOPSIS
    Removes a git worktree safely on Windows, handling transient file locks
    without retry storms or unsafe branch deletion.

.DESCRIPTION
    Centralized, reusable worktree cleanup for all BotNexus automation
    (maintenance loops, PR sync, dev worktree tooling). Behaviour:

      1. Preflights for processes/handles holding the worktree directory and
         reports likely lockers where they can be discovered.
      2. Retries `git worktree remove` a bounded number of times with short
         exponential backoff.
      3. Classifies the OWNER of a git worktree lock before giving up, reusing
         the shared liveness helpers from ValidationSteps.psm1 (issue #2409).
      4. On persistent failure returns a structured `locked` outcome that
         retains the path, branch, and metadata so callers can record-and-skip
         instead of retrying dozens of times.
      5. NEVER deletes the branch when worktree removal failed.
      6. Runs `git worktree prune` only AFTER the working directory is gone,
         then verifies both the filesystem path and the
         `.git/worktrees/<name>` metadata have been removed.

    The git invocation, directory removal, locker probe and sleep are injectable
    so the logic is deterministically testable without real OS locks.

.OUTPUTS
    A hashtable describing the outcome. `outcome` is one of:
      'removed'   - worktree and (optionally) branch removed cleanly
      'reclaimed' - a STALE worktree lock (dead owner) was reclaimed and the
                    removal then succeeded
      'locked'    - removal blocked by a lock after bounded retries; retained
      'absent'    - nothing to remove
      'error'     - a non-lock failure occurred

    Every record also carries `ownerState`, the classification of whoever holds
    the git worktree lock: 'Alive', 'Dead', 'Reused', 'Unknown' or 'None'.
    Issue #2409: without that classification a tombstone lock left by a killed
    worker silently disabled the guard it was supposed to provide, and callers
    could not tell "someone else is working on this" from "nobody is, but the
    lock file outlived them".
#>
# Copyright (c) Microsoft Corporation. All rights reserved.

Set-StrictMode -Version Latest

# Reuse the single owner-liveness classifier that issue #2393 added for the
# validation lock. A second, worktree-local classifier is exactly the drift that
# issue #2409 describes, so this path deliberately imports rather than
# reimplements Test-BotNexusLockOwnerAlive / Read-BotNexusLockOwner.
$script:ValidationStepsModule = Join-Path $PSScriptRoot 'ValidationSteps.psm1'
if (Test-Path -LiteralPath $script:ValidationStepsModule) {
    Import-Module $script:ValidationStepsModule -Force -DisableNameChecking
}

function Test-WorktreeLockError {
    <#
      Returns $true when git's failure output looks like a transient Windows
      file lock (as opposed to a logical error such as "not a working tree").
    #>
    param([string]$Output)
    if ([string]::IsNullOrWhiteSpace($Output)) { return $false }
    $patterns = @(
        'being used by another process',
        'Access is denied',
        'Permission denied',
        'The process cannot access the file',
        'Device or resource busy',
        'Resource temporarily unavailable',
        'unable to unlink',
        'unable to remove',
        'cannot remove',
        'Directory not empty',
        # git's own worktree lock. Issue #2409: this was NOT recognised as a lock
        # at all, so a locked worktree fell through to the generic 'error' path
        # and the owner was never classified.
        'is locked',
        'contains a locked'
    )
    foreach ($p in $patterns) {
        if ($Output -match [regex]::Escape($p)) { return $true }
    }
    return $false
}

function Get-WorktreeLikelyLockers {
    <#
      Best-effort discovery of processes holding handles under the worktree.
      Uses Get-Process module paths as a portable heuristic; richer probing
      (handle.exe / lsof) can be injected by callers/tests.
    #>
    param([string]$Path)
    $lockers = @()
    if ([string]::IsNullOrWhiteSpace($Path)) { return $lockers }
    try {
        $normalized = $Path.TrimEnd('\', '/')
        foreach ($proc in Get-Process -ErrorAction SilentlyContinue) {
            $modulePath = $null
            try { $modulePath = $proc.Path } catch { $modulePath = $null }
            if ($modulePath -and $modulePath.StartsWith($normalized, [StringComparison]::OrdinalIgnoreCase)) {
                $lockers += [ordered]@{ pid = $proc.Id; name = $proc.ProcessName; path = $modulePath }
            }
        }
    }
    catch {
        # Probing is best-effort; never let it become the failure.
    }
    return $lockers
}

function Get-WorktreeMetadataName {
    <#
      Reads the worktree's .git file to resolve the admin metadata directory
      name under <repo>/.git/worktrees/<name>.
    #>
    param([string]$WorktreePath)
    $gitFile = Join-Path $WorktreePath '.git'
    if (-not (Test-Path $gitFile)) { return $null }
    try {
        $content = Get-Content -LiteralPath $gitFile -Raw -ErrorAction Stop
        if ($content -match 'gitdir:\s*(.+)') {
            $gitdir = $Matches[1].Trim()
            return (Split-Path -Leaf $gitdir)
        }
    }
    catch { }
    return $null
}

function Get-WorktreeLockFilePath {
    <#
      Resolves <repo>/.git/worktrees/<name>/locked - the file git itself creates
      for `git worktree lock`, and the only durable place a worktree lock owner
      can be recorded. Returns $null when the metadata name is unknown.
    #>
    param([string]$RepoRoot, [string]$MetadataName)
    if ([string]::IsNullOrWhiteSpace($RepoRoot) -or [string]::IsNullOrWhiteSpace($MetadataName)) { return $null }
    $gitDir = Join-Path $RepoRoot '.git'
    # A repo root that is itself a linked worktree has .git as a FILE; only the
    # real admin directory carries worktrees/.
    if (-not (Test-Path -LiteralPath $gitDir -PathType Container)) { return $null }
    return (Join-Path (Join-Path (Join-Path $gitDir 'worktrees') $MetadataName) 'locked')
}

function Write-WorktreeLockOwner {
    <#
      Stamps an owner record into a worktree lock file that BotNexus itself
      writes. Issue #2409: git's `worktree lock --reason` is free text and our
      callers wrote an empty reason, so the lock carried NO owner at all and
      could never be classified - it could only be honoured forever or ignored
      blindly. Both of those are the bug.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LockFilePath)

    if (-not (Get-Command ConvertTo-BotNexusLockOwnerRecord -ErrorAction SilentlyContinue)) { return $null }
    $owner = ConvertTo-BotNexusLockOwnerRecord
    $dir = Split-Path -Parent $LockFilePath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Set-Content -LiteralPath $LockFilePath -Value ($owner | ConvertTo-Json -Compress) -Encoding utf8 -NoNewline
    return $owner
}

function Get-WorktreeLockOwnerState {
    <#
      Classifies the owner of a worktree lock as 'Alive', 'Dead', 'Reused',
      'Unknown', or 'None' (no lock file at all), delegating entirely to the
      shared helpers from ValidationSteps.psm1.

      A lock file that exists but carries no readable owner record is 'Unknown',
      never 'Dead': failing closed on an unclassifiable lock is the whole point
      of the classification.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$LockFilePath)

    if ([string]::IsNullOrWhiteSpace($LockFilePath)) { return 'None' }
    if (-not (Test-Path -LiteralPath $LockFilePath -PathType Leaf)) { return 'None' }
    if (-not (Get-Command Read-BotNexusLockOwner -ErrorAction SilentlyContinue) -or
        -not (Get-Command Test-BotNexusLockOwnerAlive -ErrorAction SilentlyContinue)) {
        return 'Unknown'
    }
    $owner = Read-BotNexusLockOwner -LockPath $LockFilePath
    if ($null -eq $owner) { return 'Unknown' }
    return (Test-BotNexusLockOwnerAlive -Owner $owner)
}

function Enter-WorktreeReclaimGuard {
    <#
      Single-winner guard for reclaiming a stale lock. Two processes can observe
      the same dead owner at the same instant; whoever creates this file
      exclusively (CreateNew fails when it already exists) is the one allowed to
      reclaim. Returns a guard handle, or $null when another process already won.
    #>
    param([Parameter(Mandatory)][string]$LockFilePath)
    $guardPath = "$LockFilePath.reclaim"
    try {
        $stream = [IO.File]::Open($guardPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        return @{ Path = $guardPath; Stream = $stream }
    }
    catch { return $null }
}

function Exit-WorktreeReclaimGuard {
    param([AllowNull()][object]$Guard)
    if ($null -eq $Guard) { return }
    try { $Guard.Stream.Dispose() } catch { }
    Remove-Item -LiteralPath $Guard.Path -Force -ErrorAction SilentlyContinue
}

function Remove-WorktreeSafely {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$RepoRoot,
        [Parameter(Mandatory)] [string]$WorktreePath,

        # Also delete the branch after a *successful* removal.
        [switch]$DeleteBranch,

        # Force removal even with uncommitted/unpushed work.
        [switch]$Force,

        [int]$MaxRetries = 4,
        [int]$BaseDelayMs = 200,

        # Injectable seams (defaults call the real tools).
        [scriptblock]$GitInvoker,
        [scriptblock]$DirectoryRemover,
        [scriptblock]$LockerProbe,
        [scriptblock]$Sleeper
    )

    if (-not $GitInvoker) {
        $GitInvoker = {
            param([string[]]$GitArgs)
            $out = & git @GitArgs 2>&1 | Out-String
            @{ exitCode = $LASTEXITCODE; output = $out }
        }
    }
    if (-not $DirectoryRemover) {
        $DirectoryRemover = {
            param([string]$Path)
            if (Test-Path $Path) {
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            }
        }
    }
    if (-not $LockerProbe) {
        $LockerProbe = { param([string]$Path) Get-WorktreeLikelyLockers -Path $Path }
    }
    if (-not $Sleeper) {
        $Sleeper = { param([int]$Ms) Start-Sleep -Milliseconds $Ms }
    }

    $normalizedWt = $WorktreePath.TrimEnd('\', '/')
    $normalizedRepo = $RepoRoot.TrimEnd('\', '/')

    # Never remove the main working tree.
    if ($normalizedWt -eq $normalizedRepo) {
        return @{
            outcome = 'error'
            path    = $WorktreePath
            branch  = $null
            error   = 'Refusing to remove the main working tree.'
        }
    }

    # Resolve branch + metadata name up front (needed for the retained record).
    $branch = $null
    $metadataName = $null
    if (Test-Path $WorktreePath) {
        $metadataName = Get-WorktreeMetadataName -WorktreePath $WorktreePath
        $b = & $GitInvoker @('-C', $WorktreePath, 'rev-parse', '--abbrev-ref', 'HEAD')
        if ($b.exitCode -eq 0) {
            $branch = ($b.output).Trim()
            if ($branch -eq 'HEAD') { $branch = $null }
        }
    }

    # Bounded retry with exponential backoff.
    $gitArgs = @('-C', $RepoRoot, 'worktree', 'remove', $WorktreePath)
    if ($Force) { $gitArgs += '--force' }

    $attempts = 0
    $lastOutput = ''
    $removed = $false
    for ($i = 0; $i -le $MaxRetries; $i++) {
        $attempts++
        $res = & $GitInvoker $gitArgs
        $lastOutput = ($res.output).Trim()
        if ($res.exitCode -eq 0) { $removed = $true; break }

        if (-not (Test-WorktreeLockError -Output $lastOutput)) {
            # A non-lock error (e.g. dirty tree without -Force) - do not retry,
            # and never touch the branch.
            return @{
                outcome  = 'error'
                path     = $WorktreePath
                branch   = $branch
                attempts = $attempts
                error    = $lastOutput
            }
        }

        # Transient lock: back off and retry (unless this was the last attempt).
        if ($i -lt $MaxRetries) {
            $delay = [int]($BaseDelayMs * [Math]::Pow(2, $i))
            & $Sleeper $delay
        }
    }

    # Issue #2409: before giving up, ask WHO holds the worktree lock and whether
    # that owner is still alive. A tombstone left by a killed worker must not be
    # honoured forever, and a live owner must NEVER be reclaimed.
    $lockFilePath = Get-WorktreeLockFilePath -RepoRoot $RepoRoot -MetadataName $metadataName
    $ownerState = 'None'
    $reclaimed = $false
    $reclaimAttempted = $false

    if (-not $removed) {
        $ownerState = Get-WorktreeLockOwnerState -LockFilePath $lockFilePath

        # ONLY 'Dead' is reclaimable. 'Alive' names a real holder; 'Reused' and
        # 'Unknown' cannot be trusted, so both fail CLOSED.
        if ($ownerState -eq 'Dead') {
            $guard = Enter-WorktreeReclaimGuard -LockFilePath $lockFilePath
            if ($null -ne $guard) {
                $reclaimAttempted = $true
                try {
                    Write-Host "[worktree] reclaiming STALE worktree lock $lockFilePath - its recorded owner process no longer exists (issue #2409)." -ForegroundColor Yellow
                    & $GitInvoker @('-C', $RepoRoot, 'worktree', 'unlock', $WorktreePath) | Out-Null
                    Remove-Item -LiteralPath $lockFilePath -Force -ErrorAction SilentlyContinue
                    # Retry the removal exactly ONCE after a successful reclaim.
                    $attempts++
                    $retry = & $GitInvoker $gitArgs
                    $lastOutput = ($retry.output).Trim()
                    if ($retry.exitCode -eq 0) {
                        $removed = $true
                        $reclaimed = $true
                    }
                }
                finally { Exit-WorktreeReclaimGuard -Guard $guard }
            }
        }
    }

    if (-not $removed) {
        # Persistent lock. Return a structured, retained 'locked' record.
        # Critically: DO NOT delete the branch, DO NOT prune.
        $lockers = & $LockerProbe $WorktreePath
        return @{
            outcome          = 'locked'
            path             = $WorktreePath
            branch           = $branch
            metadataName     = $metadataName
            attempts         = $attempts
            ownerState       = $ownerState
            reclaimed        = $false
            reclaimAttempted = $reclaimAttempted
            branchDeleted    = $false
            pruned           = $false
            likelyLockers    = @($lockers)
            lastError        = $lastOutput
        }
    }

    # git reported success; ensure the directory is actually gone before pruning.
    $dirRemoveError = $null
    if (Test-Path $WorktreePath) {
        try { & $DirectoryRemover $WorktreePath }
        catch { $dirRemoveError = $_.Exception.Message }
    }

    if (Test-Path $WorktreePath) {
        # Directory still present (lock on residual files). Treat as locked:
        # never prune, never delete the branch.
        $lockers = & $LockerProbe $WorktreePath
        return @{
            outcome          = 'locked'
            path             = $WorktreePath
            branch           = $branch
            metadataName     = $metadataName
            attempts         = $attempts
            ownerState       = $ownerState
            reclaimed        = $reclaimed
            reclaimAttempted = $reclaimAttempted
            branchDeleted    = $false
            pruned           = $false
            likelyLockers    = @($lockers)
            lastError        = if ($dirRemoveError) { $dirRemoveError } else { 'Worktree directory still present after removal.' }
        }
    }

    # Directory removal succeeded -> safe to prune.
    & $GitInvoker @('-C', $RepoRoot, 'worktree', 'prune') | Out-Null
    $pruned = $true

    # Verify .git/worktrees/<name> metadata is gone.
    $metadataGone = $true
    if ($metadataName) {
        $metaPath = Join-Path (Join-Path (Join-Path $RepoRoot '.git') 'worktrees') $metadataName
        $metadataGone = -not (Test-Path $metaPath)
    }

    # Only now, after fully successful removal, optionally delete the branch.
    $branchDeleted = $false
    if ($DeleteBranch -and $branch) {
        $bd = & $GitInvoker @('-C', $RepoRoot, 'branch', '-D', $branch)
        $branchDeleted = ($bd.exitCode -eq 0)
    }

    return @{
        outcome          = if ($reclaimed) { 'reclaimed' } else { 'removed' }
        path             = $WorktreePath
        branch           = $branch
        metadataName     = $metadataName
        attempts         = $attempts
        ownerState       = $ownerState
        reclaimed        = $reclaimed
        reclaimAttempted = $reclaimAttempted
        pruned           = $pruned
        metadataGone     = $metadataGone
        branchDeleted    = $branchDeleted
    }
}

# When dot-sourced, the functions above are available to callers/tests.
# When invoked directly, remove the requested worktree and emit JSON.
if ($MyInvocation.InvocationName -ne '.' -and $MyInvocation.Line -notmatch '\.\s') {
    if ($args.Count -ge 2) {
        $result = Remove-WorktreeSafely -RepoRoot $args[0] -WorktreePath $args[1]
        $result | ConvertTo-Json -Depth 6
        # Issue #2409: FAIL LOUDLY. The specific bug named in the issue is a
        # caller that swallows non-acquisition and proceeds UNPROTECTED; a
        # non-zero exit makes that impossible to miss.
        if ($result.outcome -eq 'locked' -or $result.outcome -eq 'error') {
            Write-Error "Remove-WorktreeSafely did not acquire the worktree: outcome='$($result.outcome)' ownerState='$(if ($result.ContainsKey('ownerState')) { $result.ownerState } else { 'None' })'."
            exit 1
        }
    }
}
