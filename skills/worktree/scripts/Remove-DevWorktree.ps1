<#
.SYNOPSIS
    Removes a git worktree and cleans up its directory.

.DESCRIPTION
    Removes a dev worktree created by New-DevWorktree.ps1. Checks for
    uncommitted changes and unpushed commits before removing.
    Can also remove all dev worktrees at once.

.PARAMETER WorktreePath
    The absolute path to the worktree to remove.

.PARAMETER Force
    Remove even if there are uncommitted changes or unpushed commits.

.PARAMETER All
    Remove all dev worktrees (not the main working tree).

.PARAMETER RepoRoot
    Explicit repo root path. Auto-detected from current directory if omitted.

.EXAMPLE
    .\Remove-DevWorktree.ps1 -WorktreePath "Q:\repos\usage-billing-feature-add-retry"

.EXAMPLE
    .\Remove-DevWorktree.ps1 -All

.OUTPUTS
    JSON document with removal results.
#>
# Copyright (c) Microsoft Corporation. All rights reserved.

[CmdletBinding(DefaultParameterSetName = 'Single')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Single')]
    [string]$WorktreePath,

    [Parameter(ParameterSetName = 'Single')]
    [Parameter(ParameterSetName = 'All')]
    [switch]$Force,

    [Parameter(Mandatory, ParameterSetName = 'All')]
    [switch]$All,

    [Parameter()]
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Result {
    param([Parameter(Mandatory)] $Data)
    $Data | ConvertTo-Json -Depth 5
}

function Test-WorktreeSafe {
    param([string]$Path)

    $result = @{ safe = $true; warnings = @() }

    if (-not (Test-Path $Path)) {
        $result.safe = $true
        $result.warnings += 'Directory does not exist (already removed?).'
        return $result
    }

    # Check for uncommitted changes
    $status = git -C $Path status --porcelain 2>&1
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        $result.safe = $false
        $changeCount = ($status -split "`n").Count
        $result.warnings += "Has $changeCount uncommitted change(s)."
    }

    # Check for unpushed commits
    $branch = git -C $Path rev-parse --abbrev-ref HEAD 2>&1
    if ($LASTEXITCODE -eq 0 -and $branch -ne 'HEAD') {
        $ahead = git -C $Path rev-list --count "origin/$branch..HEAD" 2>&1
        if ($LASTEXITCODE -eq 0 -and [int]$ahead.Trim() -gt 0) {
            $result.safe = $false
            $result.warnings += "Has $($ahead.Trim()) unpushed commit(s) on branch '$branch'."
        }
    }

    return $result
}

function Remove-SingleWorktree {
    param(
        [string]$RepoRoot,
        [string]$WtPath,
        [bool]$ForceRemove
    )

    $normalizedWt = $WtPath.TrimEnd('\', '/')
    $normalizedRepo = $RepoRoot.TrimEnd('\', '/')

    # Safety: never remove the main worktree
    if ($normalizedWt -eq $normalizedRepo) {
        return @{
            path    = $WtPath
            removed = $false
            error   = 'Cannot remove the main working tree.'
        }
    }

    # Check safety
    $safety = Test-WorktreeSafe -Path $WtPath
    if (-not $safety.safe -and -not $ForceRemove) {
        return @{
            path     = $WtPath
            removed  = $false
            error    = 'Worktree has unsaved work. Use -Force to override.'
            warnings = $safety.warnings
        }
    }

    # Get branch name before removal
    $branch = $null
    if (Test-Path $WtPath) {
        $branch = git -C $WtPath rev-parse --abbrev-ref HEAD 2>&1
        if ($LASTEXITCODE -ne 0) { $branch = $null }
        else { $branch = $branch.Trim() }
    }

    # Remove worktree.
    # On Windows a background process (IDE, dotnet build server, file indexer) routinely holds a
    # handle under bin/ or obj/, which surfaces as 'Permission denied'. Retry with bounded backoff
    # before giving up so a transient handle does not look like a hard failure (#2419).
    $gitArgs = @('-C', $RepoRoot, 'worktree', 'remove', $WtPath)
    if ($ForceRemove) { $gitArgs += '--force' }

    $removeOutput = $null
    $removeExit = 1
    $delays = @(0, 250, 500, 1000, 2000)
    foreach ($delay in $delays) {
        if ($delay -gt 0) { Start-Sleep -Milliseconds $delay }
        $removeOutput = & git @gitArgs 2>&1
        $removeExit = $LASTEXITCODE
        if ($removeExit -eq 0) { break }
        # Only a lock/permission failure is worth retrying; anything else fails immediately.
        if ($removeOutput -notmatch 'Permission denied|being used by another process') { break }
    }

    if ($removeExit -ne 0) {
        $locked = [bool]($removeOutput -match 'Permission denied|being used by another process')
        return @{
            path    = $WtPath
            removed = $false
            locked  = $locked
            # CRITICAL (#2419/#2104): return BEFORE any branch deletion. A branch must never be
            # deleted when its worktree removal failed - that orphans the directory and strands
            # the commits. Callers must use this helper rather than hand-rolling the two git
            # commands as one unconditional chain, because a chained second command runs the
            # delete regardless of the first command's exit code.
            error   = "git worktree remove failed: $removeOutput"
        }
    }

    # git reported success; ensure the directory is actually gone before we treat this as
    # removed. A residual lock under bin/obj leaves the directory behind, and reporting that
    # as removed lets the caller prune the registration off a live directory (#3722).
    if (Test-Path $WtPath) {
        Remove-Item -Path $WtPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $WtPath) {
        return @{
            path    = $WtPath
            removed = $false
            locked  = $true
            # Never delete the branch and never let the caller prune: the worktree must stay
            # REGISTERED so the next sweep can retry it.
            error   = 'Worktree directory still present after git reported removal; leaving registration intact for retry.'
        }
    }

    # Optionally delete the branch
    $branchDeleted = $false
    if ($branch -and $branch -ne 'HEAD') {
        git -C $RepoRoot branch -D $branch 2>&1 | Out-Null
        $branchDeleted = ($LASTEXITCODE -eq 0)
    }

    return @{
        path           = $WtPath
        removed        = $true
        branch         = $branch
        branchDeleted  = $branchDeleted
        warnings       = $safety.warnings
    }
}

try {
    # Resolve repo root
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        # Resolve the repo from the TARGET worktree, not the caller's current directory (#2419/#2405).
        # An unqualified `git rev-parse` here made every absolute-path invocation from outside a repo
        # fail with the misleading 'Not inside a git repository'.
        $anchor = if ($PSCmdlet.ParameterSetName -eq 'Single' -and (Test-Path $WorktreePath)) { $WorktreePath } else { (Get-Location).Path }
        $RepoRoot = git -C $anchor rev-parse --path-format=absolute --git-common-dir 2>&1
        if ($LASTEXITCODE -eq 0) {
            # --git-common-dir points at <repo>/.git for the main tree; its parent is the repo root.
            $RepoRoot = (Split-Path -Parent ($RepoRoot.Trim() -replace '/', '\'))
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Result @{
                success = $false
                error   = 'Not inside a git repository.'
            }
            exit 1
        }
    }
    $RepoRoot = $RepoRoot.Trim() -replace '/', '\'

    if ($All) {
        # Remove all non-main worktrees
        $porcelain = git -C $RepoRoot worktree list --porcelain 2>&1
        $worktreePaths = @()

        foreach ($line in ($porcelain -split "`n")) {
            if ($line -match '^worktree (.+)$') {
                $wtPath = $Matches[1].Trim() -replace '/', '\'
                $normalized = $wtPath.TrimEnd('\', '/')
                if ($normalized -ne $RepoRoot.TrimEnd('\', '/')) {
                    $worktreePaths += $wtPath
                }
            }
        }

        if ($worktreePaths.Count -eq 0) {
            Write-Result @{
                success = $true
                message = 'No dev worktrees to remove.'
                removed = @()
            }
            exit 0
        }

        $results = foreach ($wtPath in $worktreePaths) {
            Remove-SingleWorktree -RepoRoot $RepoRoot -WtPath $wtPath -ForceRemove $Force.IsPresent
        }

        # Prune ONLY when every removal succeeded (#3722). `worktree prune` drops the
        # registration of any worktree whose directory git considers unusable - so pruning
        # after a FAILED removal de-registers a directory that is still on disk, producing an
        # orphan that `git worktree list` can never report again. Thirteen such directories
        # accumulated ~15 GB. A failed removal must leave the worktree REGISTERED so the next
        # sweep retries it.
        $anyFailed = @(@($results) | Where-Object { -not $_.removed }).Count -gt 0
        if (-not $anyFailed) {
            git -C $RepoRoot worktree prune 2>&1 | Out-Null
        }

        Write-Result @{
            success = $true
            removed = @($results)
            count   = ($results | Where-Object { $_.removed }).Count
            total   = $results.Count
        }
    }
    else {
        # Remove single worktree
        $result = Remove-SingleWorktree -RepoRoot $RepoRoot -WtPath $WorktreePath -ForceRemove $Force.IsPresent

        # Prune ONLY on success (#3722) - see the -All branch for the full rationale. Pruning
        # after a failed removal is what turns a locked worktree into an invisible orphan.
        if ($result.removed) {
            git -C $RepoRoot worktree prune 2>&1 | Out-Null
        }

        Write-Result @{
            success = $result.removed
            result  = $result
        }
    }
}
catch {
    Write-Result @{
        success   = $false
        error     = $_.Exception.Message
        exception = $_.Exception.GetType().FullName
    }
    exit 1
}
