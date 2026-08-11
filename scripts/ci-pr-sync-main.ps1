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

.PARAMETER Token
    GitHub token used to authenticate the force-push. Defaults to $env:GH_TOKEN.
    Issue #2961: without this the rebase succeeded locally and the push then
    died on an interactive credential prompt ('could not read Username').
    The token is embedded in the origin remote URL for the duration of the
    push only, and scrubbed in a finally; it is redacted from all output.

.OUTPUTS
    JSON object: { success: bool, message: string }

.EXAMPLE
    pwsh -NoProfile -File scripts/ci-pr-sync-main.ps1 -Branch feat/my-feature
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Branch,

    [Parameter()]
    [AllowNull()]
    [AllowEmptyString()]
    [string]$Token = $env:GH_TOKEN
)

$ErrorActionPreference = 'Stop'
$repo = 'Sytone/botnexus'
# Issue #2405: resolve the repository from the script's own location, never
# from the caller's working directory. This script is routinely invoked by
# absolute path from CI and from agent workspaces whose cwd is unrelated to
# (or outside) any git repository; an unqualified `git` call then fails with
# "fatal: not a git repository". Every git invocation below must therefore be
# qualified with -C against $repoRoot or a specific worktree directory.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'repo/Remove-Worktree.ps1')
. (Join-Path $PSScriptRoot 'repo/WorktreeSyncGuard.ps1')
. (Join-Path $PSScriptRoot 'repo/GitRemoteAuth.ps1')

function Output-Result {
    param([bool]$Success, [string]$Message)
    # Issue #2961: every message here can carry raw git output, which may echo
    # the authenticated remote URL. Redact before it reaches stdout or a log.
    $safe = Remove-SecretFromText -Text $Message -Secret @($Token)
    [pscustomobject]@{ success = $Success; message = $safe } | ConvertTo-Json -Compress
}

<#
.SYNOPSIS
    Force-pushes with lease, distinguishing a deleted remote branch from a
    genuine lease violation.
.DESCRIPTION
    Issue #2961 gotcha 2: `--force-with-lease` reports `(stale info)` both when
    someone else moved the branch AND when the remote branch does not exist at
    all (its PR merged and the branch was deleted). Those need different
    operator responses, so probe the remote before reporting.

    Gotcha 1: the push target is the remote NAME, never an explicit URL. A
    lease has no remote-tracking ref to compare against for an anonymous URL,
    so pushing to a URL makes --force-with-lease fail with `(stale info)`
    unconditionally. Authentication therefore lives on the remote URL itself.
#>
function Invoke-LeasedPush {
    param(
        [Parameter(Mandatory)][string]$Dir,
        [Parameter(Mandatory)][string]$Refspec,
        [Parameter(Mandatory)][string]$RemoteBranch
    )

    $pushResult = git -C $Dir push --force-with-lease origin $Refspec 2>&1
    if ($LASTEXITCODE -eq 0) {
        return [pscustomobject]@{ Ok = $true; Message = '' }
    }

    $text = Remove-SecretFromText -Text $pushResult -Secret @($Token)
    if ($text -match 'stale info' -and -not (Test-RemoteBranchExists -RepoRoot $repoRoot -Branch $RemoteBranch)) {
        return [pscustomobject]@{
            Ok      = $false
            Message = "Remote branch '$RemoteBranch' no longer exists on origin (its PR was likely merged and the branch deleted); nothing to sync. Push output: $text"
        }
    }

    return [pscustomobject]@{ Ok = $false; Message = "Push failed: $text" }
}

# Fetch latest from remote
$fetchResult = git -C $repoRoot fetch origin main $Branch 2>&1
if ($LASTEXITCODE -ne 0) {
    Output-Result -Success $false -Message "Fetch failed: $fetchResult"
    return
}

# Gate: only sync when the branch is actually behind main. A no-op sync
# otherwise produces churn (and, under the old merge strategy, foreign-author
# merge commits). A `rev-list --count origin/main ^origin/$Branch` counts
# main commits the branch does not yet contain.
$behindCount = (git -C $repoRoot rev-list --count "origin/$Branch..origin/main" 2>&1)
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
$worktrees = git -C $repoRoot worktree list --porcelain 2>$null
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
    $push = Invoke-LeasedPush -Dir $Dir -Refspec $Branch -RemoteBranch $Branch
    if (-not $push.Ok) {
        return [pscustomobject]@{ Ok = $false; Message = $push.Message }
    }

    return [pscustomobject]@{ Ok = $true; Message = "Rebased $Branch onto main ($behind commit(s)) and force-pushed successfully." }
}

if ($worktreePath) {
    # Issue #2330: NEVER rewrite history in a worktree that still has
    # uncommitted work. Rebasing moves HEAD under an active worker, whose index
    # then reflects the pre-rebase tree, so its next `git add -A` stages the
    # incoming commits as deletions and its edits appear to silently revert.
    # Refuse instead, and let the worker commit or stash first.
    $status = git -C $worktreePath status --porcelain 2>&1
    if (-not (Test-WorktreeIsIdle -StatusOutput $status)) {
        Output-Result -Success $false -Message (Get-DirtyWorktreeSyncMessage -Branch $Branch -WorktreePath $worktreePath)
        return
    }

    # Ensure the worktree branch matches the remote tip before rebasing so a
    # locally-stale worktree does not resurrect old commits.
    $result = Invoke-WithAuthenticatedRemote -RepoRoot $repoRoot -Token $Token -Body {
        Invoke-RebaseAndPush -Dir $worktreePath
    }
    Output-Result -Success $result.Ok -Message $result.Message
} else {
    # No worktree — rebase in a temporary worktree tracking the remote branch.
    $tempBranch = "__sync-temp-$Branch"
    git -C $repoRoot branch -f $tempBranch "origin/$Branch" 2>$null

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "botnexus-sync-$(Get-Random)"
    try {
        git -C $repoRoot worktree add $tempDir $tempBranch 2>$null
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

        $push = Invoke-WithAuthenticatedRemote -RepoRoot $repoRoot -Token $Token -Body {
            Invoke-LeasedPush -Dir $tempDir -Refspec "${tempBranch}:${Branch}" -RemoteBranch $Branch
        }
        if (-not $push.Ok) {
            Output-Result -Success $false -Message $push.Message
            return
        }

        Output-Result -Success $true -Message "Rebased $Branch onto main ($behind commit(s)) and force-pushed successfully."
    } finally {
        # Lock-aware cleanup: never delete the temp branch while the worktree
        # is still registered (Windows file locks otherwise leak a dangling
        # branch + registered-but-removed worktree). See issue #2104.
        $cleanup = Remove-WorktreeSafely -RepoRoot (git -C $repoRoot rev-parse --show-toplevel | Out-String).Trim() `
            -WorktreePath $tempDir -DeleteBranch:$false -Force
        if ($cleanup.outcome -eq 'removed') {
            git -C $repoRoot branch -D $tempBranch 2>$null
        }
        else {
            Write-Warning "Skipping temp branch deletion; worktree '$tempDir' cleanup outcome: $($cleanup.outcome). Branch '$tempBranch' retained."
        }
    }
}

