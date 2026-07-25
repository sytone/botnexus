<#
.SYNOPSIS
    Guard helpers that keep automated branch syncs from racing in-flight work
    inside a shared git worktree.
.DESCRIPTION
    Issue #2330: automated syncs (rebase + force-push) that run inside a
    worktree while a worker still has uncommitted edits move HEAD under that
    worker. The worker's index then reflects a pre-sync tree, so a later
    `git add -A` stages the newly-arrived files as DELETIONS and the edits
    look like they "silently reverted to origin state".

    The root cause is not a git bug and not filesystem interference: it is a
    history-rewriting operation applied to a checkout that is not idle. The
    only safe precondition is that the worktree has no uncommitted work, so
    these helpers make that precondition explicit and testable instead of
    leaving it implicit in the caller.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Returns $true when `git status --porcelain` output represents an idle
    worktree (no staged, unstaged, or untracked entries).
.PARAMETER StatusOutput
    Raw output of `git status --porcelain`. Accepts $null, an empty string,
    a whitespace-only string, or a string array (as PowerShell returns when
    git emits multiple lines).
#>
function Test-WorktreeIsIdle {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter()]
        [AllowNull()]
        [object]$StatusOutput
    )

    if ($null -eq $StatusOutput) { return $true }

    # git status --porcelain emits one entry per line; an array-valued result
    # is normal, so flatten before testing for content.
    $text = ($StatusOutput | Out-String)
    return [string]::IsNullOrWhiteSpace($text)
}

<#
.SYNOPSIS
    Builds the operator-facing refusal message for a dirty worktree sync.
.PARAMETER Branch
    Branch that was going to be rebased.
.PARAMETER WorktreePath
    Worktree that holds the uncommitted work.
#>
function Get-DirtyWorktreeSyncMessage {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$WorktreePath
    )

    return "Refusing to sync $Branch : worktree '$WorktreePath' has uncommitted work. " +
           'Rebasing it would move HEAD under an active worker and stage the incoming ' +
           'commits as deletions (issue #2330). Commit or stash the work, then re-run the sync.'
}
