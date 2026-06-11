<#
.SYNOPSIS
    Merges main into a PR branch and pushes the result.

.DESCRIPTION
    Fetches latest main, merges it into the specified branch using a temporary
    local checkout, and pushes. Outputs JSON with success status and message.

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

# Check if branch exists locally or needs to be checked out
$localBranches = git branch --list $Branch 2>$null
$worktreePath = $null

# Find worktree for this branch
$worktrees = git worktree list --porcelain 2>$null
$currentWorktree = $null
foreach ($line in $worktrees -split "`n") {
    if ($line -match '^worktree (.+)$') {
        $currentWorktree = $Matches[1]
    }
    if ($line -match "^branch refs/heads/$([regex]::Escape($Branch))$") {
        $worktreePath = $currentWorktree
        break
    }
}

if ($worktreePath) {
    # Use existing worktree
    $mergeResult = git -C $worktreePath merge origin/main -m "chore: merge main into $Branch" 2>&1
    if ($LASTEXITCODE -ne 0) {
        # Abort the merge so we don't leave the worktree dirty
        git -C $worktreePath merge --abort 2>$null
        Output-Result -Success $false -Message "Merge conflict: $mergeResult"
        return
    }

    $pushResult = git -C $worktreePath push origin $Branch 2>&1
    if ($LASTEXITCODE -ne 0) {
        Output-Result -Success $false -Message "Push failed: $pushResult"
        return
    }

    Output-Result -Success $true -Message "Merged main into $Branch and pushed successfully."
} else {
    # No worktree — use detached merge via remote refs
    # Create a temporary local branch tracking the remote
    git branch -f "__sync-temp-$Branch" "origin/$Branch" 2>$null
    $tempBranch = "__sync-temp-$Branch"

    # Try merge using a temporary worktree
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "botnexus-sync-$(Get-Random)"
    try {
        git worktree add $tempDir $tempBranch 2>$null
        if ($LASTEXITCODE -ne 0) {
            Output-Result -Success $false -Message "Failed to create temporary worktree for sync."
            return
        }

        $mergeResult = git -C $tempDir merge origin/main -m "chore: merge main into $Branch" 2>&1
        if ($LASTEXITCODE -ne 0) {
            git -C $tempDir merge --abort 2>$null
            Output-Result -Success $false -Message "Merge conflict: $mergeResult"
            return
        }

        $pushResult = git -C $tempDir push origin "${tempBranch}:${Branch}" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Output-Result -Success $false -Message "Push failed: $pushResult"
            return
        }

        Output-Result -Success $true -Message "Merged main into $Branch and pushed successfully."
    } finally {
        if (Test-Path $tempDir) {
            git worktree remove $tempDir --force 2>$null
        }
        git branch -D $tempBranch 2>$null
    }
}
