<#
.SYNOPSIS
    Rebases a PR branch onto the latest main and force-pushes the result.

.DESCRIPTION
    Fetches the latest main, and if the branch is behind main, rebases the
    branch onto origin/main using a worktree (existing or temporary) and
    force-pushes with lease. Outputs JSON with success status and message.

    Rebase is used instead of merge so that the branch's commit range stays
    single-author (the bot). GitHub's "Squash and merge" attributes the
    squashed commit to the branch author only when every commit in the range
    shares one author; a `git merge origin/main` pulls foreign-authored main
    commits into the range, which makes GitHub fall back to attributing the
    squash to whoever clicked merge (the repo owner) instead of the bot.
    See docs/development/git-worktree-config-hardening.md.

.PARAMETER Branch
    The head branch name to sync with main.

.OUTPUTS
    JSON object: { success: bool, message: string }

.EXAMPLE
    pwsh -NoProfile -File scripts/ci-pr-sync-main.ps1 -Branch feat/my-feature
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Branch
)

$ErrorActionPreference = 'Stop'
$repo = 'Sytone/botnexus'
. (Join-Path $PSScriptRoot 'repo/Remove-Worktree.ps1')

function Output-Result {
    param([bool]$Success, [string]$Message)
    [pscustomobject]@{ success = $Success; message = $Message } | ConvertTo-Json -Compress
}

# Fetch latest from remote
$fetchResult = git fetch origin main $Branch 2>&1
if ($LASTEXITCODE -ne 0) {
    Output-Result -Success $false -Message "Fetch failed: $fetchResult"
    return
}

# Gate: only sync when the branch is actually behind main. A no-op sync
# otherwise produces churn (and, under the old merge strategy, foreign-author
# merge commits). `git rev-list --count origin/main ^origin/$Branch` counts
# main commits the branch does not yet contain.
$behindCount = (git rev-list --count "origin/$Branch..origin/main" 2>&1)
if ($LASTEXITCODE -ne 0) {
    Output-Result -Success $false -Message "Could not determine behind count: $behindCount"
    return
}
$behind = 0
[void][int]::TryParse(($behindCount | Out-String).Trim(), [ref]$behind)
if ($behind -eq 0) {
    Output-Result -Success $true -Message "Branch $Branch is already up to date with main; nothing to sync."
    return
}

# Locate an existing worktree for this branch
$worktrees = git worktree list --porcelain 2>$null
$worktreePath = $null
$currentWorktree = $null
foreach ($line in $worktrees -split "`n") {
    if ($line -match '^worktree (.+)$') {
        $currentWorktree = $Matches[1].Trim()
    }
    if ($line -match "^branch refs/heads/$([regex]::Escape($Branch))$") {
        $worktreePath = $currentWorktree
        break
    }
}

function Invoke-RebaseAndPush {
    param([string]$Dir)

    $rebaseResult = git -C $Dir rebase origin/main 2>&1
    if ($LASTEXITCODE -ne 0) {
        # Abort so we never leave the worktree mid-rebase / dirty
        git -C $Dir rebase --abort 2>$null
        return [pscustomobject]@{ Ok = $false; Message = "Rebase conflict: $rebaseResult" }
    }

    # Rebase rewrites history, so a plain push is rejected; use lease so we
    # never clobber commits pushed to the branch since we fetched.
    $pushResult = git -C $Dir push --force-with-lease origin $Branch 2>&1
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Ok = $false; Message = "Push failed: $pushResult" }
    }

    return [pscustomobject]@{ Ok = $true; Message = "Rebased $Branch onto main ($behind commit(s)) and force-pushed successfully." }
}

if ($worktreePath) {
    # Ensure the worktree branch matches the remote tip before rebasing so a
    # locally-stale worktree does not resurrect old commits.
    $result = Invoke-RebaseAndPush -Dir $worktreePath
    Output-Result -Success $result.Ok -Message $result.Message
} else {
    # No worktree — rebase in a temporary worktree tracking the remote branch.
    $tempBranch = "__sync-temp-$Branch"
    git branch -f $tempBranch "origin/$Branch" 2>$null

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "botnexus-sync-$(Get-Random)"
    try {
        git worktree add $tempDir $tempBranch 2>$null
        if ($LASTEXITCODE -ne 0) {
            Output-Result -Success $false -Message "Failed to create temporary worktree for sync."
            return
        }

        $rebaseResult = git -C $tempDir rebase origin/main 2>&1
        if ($LASTEXITCODE -ne 0) {
            git -C $tempDir rebase --abort 2>$null
            Output-Result -Success $false -Message "Rebase conflict: $rebaseResult"
            return
        }

        $pushResult = git -C $tempDir push --force-with-lease origin "${tempBranch}:${Branch}" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Output-Result -Success $false -Message "Push failed: $pushResult"
            return
        }

        Output-Result -Success $true -Message "Rebased $Branch onto main ($behind commit(s)) and force-pushed successfully."
    } finally {
        # Lock-aware cleanup: never delete the temp branch while the worktree
        # is still registered (Windows file locks otherwise leak a dangling
        # branch + registered-but-removed worktree). See issue #2104.
        $cleanup = Remove-WorktreeSafely -RepoRoot (git rev-parse --show-toplevel).Trim() `
            -WorktreePath $tempDir -DeleteBranch:$false -Force
        if ($cleanup.outcome -eq 'removed') {
            git branch -D $tempBranch 2>$null
        }
        else {
            Write-Warning "Skipping temp branch deletion; worktree '$tempDir' cleanup outcome: $($cleanup.outcome). Branch '$tempBranch' retained."
        }
    }
}

