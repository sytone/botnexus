<#
.SYNOPSIS
    Detects and repairs the #1602 ".git/config flips to bare" corruption at source, and
    optionally prunes stale [branch] stanzas. Designed to be a first-class, idempotent
    guard that automation (cron, hooks, CI) calls BEFORE any git work.

.DESCRIPTION
    Issue #1602: the canonical clone's .git/config intermittently gains `core.bare = true`
    (and sometimes a `core.worktree` line and/or `user.email = test@example.com` identity
    pollution). This breaks every git operation:

        fatal: this operation must be run in a work tree

    Root-cause investigation (2026-06-29) eliminated every in-repo writer: the pre-commit
    hook, test-impacted.ps1, ci-pr-sync-main.ps1, the E2E fixture (temp-sandboxed) and the
    product itself (which invokes git zero times) are all clean, and benign concurrency
    provably never produces bare=true. The flip therefore comes from an EXTERNAL writer
    (e.g. an IDE git extension rescanning a worktree-heavy, branch-bloated config). That
    cannot be removed from inside the repo, so the durable fix is to make the symptom
    impossible to persist: detect it, reset it, and shrink the rewrite surface.

    For a clone that has working-tree checkouts (i.e. NOT a real bare repo), bare=true is
    always wrong. This script:
      - asserts core.bare=false (resets when flipped, logs an incident),
      - removes any spurious core.worktree line,
      - repairs test@example.com / name=test identity pollution back to the configured one,
      - (optional) prunes stale [branch] stanzas with no live worktree to reduce torn-write
        surface.

    Exit 0 = healthy or repaired. Exit 1 = could not repair (genuine bare repo / git error).

.PARAMETER RepoPath
    Repo to guard. Defaults to the repo this script lives in.

.PARAMETER Prune
    Also remove [branch] sections that have no live worktree and no local branch.

.PARAMETER Quiet
    Suppress non-incident output.

.EXAMPLE
    pwsh -NoProfile -File scripts/repo/guard-bare.ps1
.EXAMPLE
    pwsh -NoProfile -File scripts/repo/guard-bare.ps1 -Prune
#>
[CmdletBinding()]
param(
    [string]$RepoPath,
    [switch]$Prune,
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepoPath) {
    $RepoPath = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
}

function Note([string]$m, [string]$lvl = 'INFO') {
    if ($Quiet -and $lvl -eq 'INFO') { return }
    $color = switch ($lvl) { 'INCIDENT' { 'Red' } 'WARN' { 'Yellow' } default { 'DarkGray' } }
    Write-Host "[guard-bare] $m" -ForegroundColor $color
}

# A genuine bare repo has no working tree dir; a clone does. Real bare repos must be left alone.
$insideWorkTree = (& git -C $RepoPath rev-parse --is-inside-work-tree 2>$null)
$hasWorktrees = (& git -C $RepoPath worktree list --porcelain 2>$null) -match '^worktree '
$repaired = $false

$bare = (& git -C $RepoPath config --local --get core.bare 2>$null)
if ($bare -eq 'true' -and ($insideWorkTree -eq 'false' -or $hasWorktrees)) {
    Note "core.bare flipped to TRUE on a working clone (#1602). Resetting to false." 'INCIDENT'
    & git -C $RepoPath config --local core.bare false
    $repaired = $true
}

# Spurious core.worktree on the main config breaks bare+worktree coexistence.
$wt = (& git -C $RepoPath config --local --get core.worktree 2>$null)
if ($wt) {
    Note "Removing spurious core.worktree='$wt' (#1602)." 'INCIDENT'
    & git -C $RepoPath config --local --unset core.worktree 2>$null
    $repaired = $true
}

# Identity pollution from a stray test repo bleeding in (no -C, CWD=main).
$email = (& git -C $RepoPath config --local --get user.email 2>$null)
$name = (& git -C $RepoPath config --local --get user.name 2>$null)
if ($email -eq 'test@example.com' -or $name -eq 'test') {
    Note "Identity pollution detected (user.email=$email user.name=$name) (#1602)." 'INCIDENT'
    if ($env:BOTNEXUS_GIT_EMAIL) { & git -C $RepoPath config --local user.email $env:BOTNEXUS_GIT_EMAIL }
    if ($env:BOTNEXUS_GIT_NAME) { & git -C $RepoPath config --local user.name $env:BOTNEXUS_GIT_NAME }
    $repaired = $true
}

if ($Prune) {
    $live = @(& git -C $RepoPath worktree list --porcelain 2>$null |
        Where-Object { $_ -match '^branch refs/heads/(.+)$' } |
        ForEach-Object { ($_ -replace 'branch refs/heads/', '').Trim() })
    $localBranches = @(& git -C $RepoPath branch --format '%(refname:short)' 2>$null)
    $sections = @(& git -C $RepoPath config --local --get-regexp '^branch\.' 2>$null |
        ForEach-Object { ($_ -split '\.')[1] } | Select-Object -Unique)
    $removed = 0
    foreach ($b in $sections) {
        if ($b -and $b -notin $live -and $b -notin $localBranches) {
            & git -C $RepoPath config --local --remove-section "branch.$b" 2>$null
            $removed++
        }
    }
    if ($removed -gt 0) { Note "Pruned $removed stale [branch] stanzas (torn-write surface)." 'WARN' }
}

$final = (& git -C $RepoPath config --local --get core.bare 2>$null)
if ($final -eq 'true' -and ($insideWorkTree -eq 'false' -or $hasWorktrees)) {
    Note "core.bare still true after repair attempt; manual intervention needed." 'INCIDENT'
    exit 1
}

if ($repaired) { Note "Repaired and verified core.bare=false." 'WARN' } else { Note "Healthy (core.bare=$final)." }
exit 0