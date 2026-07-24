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
      3. On persistent failure returns a structured `locked` outcome that
         retains the path, branch, and metadata so callers can record-and-skip
         instead of retrying dozens of times.
      4. NEVER deletes the branch when worktree removal failed.
      5. Runs `git worktree prune` only AFTER the working directory is gone,
         then verifies both the filesystem path and the
         `.git/worktrees/<name>` metadata have been removed.

    The git invocation, directory removal, locker probe and sleep are injectable
    so the logic is deterministically testable without real OS locks.

.OUTPUTS
    A hashtable describing the outcome. `outcome` is one of:
      'removed'  - worktree and (optionally) branch removed cleanly
      'locked'   - removal blocked by a lock after bounded retries; retained
      'absent'   - nothing to remove
      'error'    - a non-lock failure occurred
#>
# Copyright (c) Microsoft Corporation. All rights reserved.

Set-StrictMode -Version Latest

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
        'Directory not empty'
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

    if (-not $removed) {
        # Persistent lock. Return a structured, retained 'locked' record.
        # Critically: DO NOT delete the branch, DO NOT prune.
        $lockers = & $LockerProbe $WorktreePath
        return @{
            outcome      = 'locked'
            path         = $WorktreePath
            branch       = $branch
            metadataName = $metadataName
            attempts     = $attempts
            branchDeleted = $false
            pruned       = $false
            likelyLockers = @($lockers)
            lastError    = $lastOutput
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
            outcome       = 'locked'
            path          = $WorktreePath
            branch        = $branch
            metadataName  = $metadataName
            attempts      = $attempts
            branchDeleted = $false
            pruned        = $false
            likelyLockers = @($lockers)
            lastError     = if ($dirRemoveError) { $dirRemoveError } else { 'Worktree directory still present after removal.' }
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
        outcome       = 'removed'
        path          = $WorktreePath
        branch        = $branch
        metadataName  = $metadataName
        attempts      = $attempts
        pruned        = $pruned
        metadataGone  = $metadataGone
        branchDeleted = $branchDeleted
    }
}

# When dot-sourced, the functions above are available to callers/tests.
# When invoked directly, remove the requested worktree and emit JSON.
if ($MyInvocation.InvocationName -ne '.' -and $MyInvocation.Line -notmatch '\.\s') {
    if ($args.Count -ge 2) {
        $result = Remove-WorktreeSafely -RepoRoot $args[0] -WorktreePath $args[1]
        $result | ConvertTo-Json -Depth 6
    }
}
