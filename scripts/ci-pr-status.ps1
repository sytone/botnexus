<#
.SYNOPSIS
    Returns CI status for all open PRs in Sytone/botnexus as a JSON array.

.DESCRIPTION
    Queries GitHub for all open PRs, their check statuses, and how far behind
    main they are. Outputs a JSON array suitable for consumption by maintenance
    automation scripts.

.OUTPUTS
    JSON array of objects with: number, title, branch, ciStatus, behindBy,
    failingChecks, pendingChecks, mergeable.

.EXAMPLE
    pwsh -NoProfile -File scripts/ci-pr-status.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = 'Sytone/botnexus'

$prs = gh pr list --repo $repo --state open --json number,title,headRefName,mergeable | ConvertFrom-Json

if (-not $prs -or $prs.Count -eq 0) {
    Write-Output '[]'
    return
}

$results = @()
foreach ($pr in $prs) {
    $checks = gh pr checks $pr.number --repo $repo --json name,state 2>$null | ConvertFrom-Json
    if (-not $checks) { $checks = @() }

    $behind = 0
    try {
        $behind = [int](gh api "repos/$repo/compare/main...$($pr.headRefName)" --jq '.behind_by' 2>$null)
    } catch {
        $behind = 0
    }

    $failing = @($checks | Where-Object { $_.state -eq 'FAILURE' })
    $pending = @($checks | Where-Object { $_.state -eq 'PENDING' })

    $ciStatus = if ($failing.Count -gt 0) {
        'failing'
    } elseif ($pending.Count -gt 0) {
        'pending'
    } elseif ($checks.Count -eq 0) {
        'unknown'
    } else {
        'passing'
    }

    $results += [pscustomobject]@{
        number        = $pr.number
        title         = $pr.title
        branch        = $pr.headRefName
        ciStatus      = $ciStatus
        behindBy      = $behind
        failingChecks = @($failing | ForEach-Object { $_.name })
        pendingChecks = @($pending | ForEach-Object { $_.name })
        mergeable     = $pr.mergeable
    }
}

$results | ConvertTo-Json -Depth 5
