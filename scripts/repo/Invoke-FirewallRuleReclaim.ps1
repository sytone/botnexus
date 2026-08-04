<#
.SYNOPSIS
    One-shot reclaim of orphaned BotNexus firewall rules (issue #2774 AC5).
.DESCRIPTION
    Removes Windows Firewall rules whose program path is under this repository
    or its worktree container AND no longer exists on disk. This includes the
    ungrouped `TCP Query User{<GUID>}<path>` rules that interactive firewall
    prompts create, which no previous code path could remove - hence the
    monotonic growth described in issue #2774.

    Selection is delegated to Select-OrphanedFirewallRuleName in
    FirewallRulePrune.ps1, which is unit-tested for narrowness: rules outside
    the roots, rules whose binary still exists, and lease rules owned by a live
    process are never selected.

    Requires elevation to remove rules. Runs -WhatIf-style by default: pass
    -Apply to actually remove. Use -ListOnly for a report with no changes.
.EXAMPLE
    pwsh -NoProfile -File scripts/repo/Invoke-FirewallRuleReclaim.ps1
    Reports what would be removed. Changes nothing.
.EXAMPLE
    pwsh -NoProfile -File scripts/repo/Invoke-FirewallRuleReclaim.ps1 -Apply
    Removes the orphans. Run from an elevated shell.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$Apply,
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'FirewallRulePrune.ps1')

function Write-Info {
    param([string]$Message, [string]$Color = 'DarkGray')
    if (-not $Quiet) { Write-Host $Message -ForegroundColor $Color }
}

$isWindowsOs = $true
if (Get-Variable -Name 'IsWindows' -ErrorAction SilentlyContinue) { $isWindowsOs = $IsWindows }
if (-not $isWindowsOs) {
    Write-Info 'Not Windows - nothing to reclaim.'
    return
}

if (-not $RepositoryRoot) {
    $RepositoryRoot = (git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $RepositoryRoot) {
        throw 'Could not determine the repository root; pass -RepositoryRoot explicitly.'
    }
    $RepositoryRoot = ([string]$RepositoryRoot).Trim()
}
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)

# `git worktree list` only reports LIVE worktrees. Deleted ones - the bulk of
# the orphans - are covered because Get-BotNexusFirewallRoot adds each
# worktree's parent container as a root.
$worktreePaths = @()
$worktreeLines = @(git -C $RepositoryRoot worktree list --porcelain 2>$null)
foreach ($line in $worktreeLines) {
    if ($line -like 'worktree *') { $worktreePaths += $line.Substring(9).Trim() }
}

$roots = @(Get-BotNexusFirewallRoot -RepositoryRoot $RepositoryRoot -WorktreePath $worktreePaths)
Write-Info ("Reclaim roots: " + ($roots -join '; '))

$rules = @()
foreach ($rule in @(Get-NetFirewallRule -ErrorAction SilentlyContinue)) {
    $program = $null
    try {
        $filter = $rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
        if ($filter) { $program = $filter.Program }
    }
    catch { $program = $null }
    if ([string]::IsNullOrWhiteSpace($program) -or $program -eq 'Any') { continue }
    $rules += [pscustomobject]@{
        Name        = $rule.Name
        Group       = $rule.Group
        Program     = $program
        DisplayName = $rule.DisplayName
    }
}

$pathExists = { param($p) Test-Path -LiteralPath $p }
$leaseOwnerIsAlive = {
    param($processId, $startTicks)
    $owner = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (-not $owner) { return $false }
    return $owner.StartTime.ToUniversalTime().Ticks -eq $startTicks
}

$orphanNames = @(Select-OrphanedFirewallRuleName -Rule $rules -RepoRoot $roots -PathExists $pathExists -LeaseOwnerIsAlive $leaseOwnerIsAlive)

if ($orphanNames.Count -eq 0) {
    Write-Info 'No orphaned BotNexus firewall rules found.' 'Green'
    return
}

$byName = @{}
foreach ($r in $rules) { $byName[$r.Name] = $r }
foreach ($name in $orphanNames) {
    Write-Info ("orphan: {0}  ->  {1}" -f $byName[$name].DisplayName, $byName[$name].Program)
}

if (-not $Apply) {
    Write-Host ("{0} orphaned rule(s) would be removed. Re-run with -Apply from an elevated shell." -f $orphanNames.Count) -ForegroundColor Yellow
    return
}

$removed = 0
$failed = 0
foreach ($name in $orphanNames) {
    try {
        Remove-NetFirewallRule -Name $name -ErrorAction Stop
        $removed++
    }
    catch {
        $failed++
        Write-Warning ("Could not remove rule '{0}': {1}" -f $name, $_.Exception.Message)
    }
}

Write-Host ("Reclaimed {0} orphaned firewall rule(s); {1} failure(s)." -f $removed, $failed) -ForegroundColor Green
