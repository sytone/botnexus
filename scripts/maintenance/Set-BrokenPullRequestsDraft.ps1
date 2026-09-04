#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Converts conclusively broken open pull requests to draft state.
.DESCRIPTION
  This is deliberately mechanical command-cron work: it spends no agent/model turn.
  A ready-for-review PR becomes draft when GitHub confirms a merge conflict or a
  terminal check reports failure, error, action-required, startup-failure, cancelled,
  timed-out, or stale. Pending and unknown states fail open and remain visible.

  The Sensitive File Guard's maintainer-ack failure is not a broken build when it is
  the sole failure, so it is excluded consistently with scripts/ci-pr-status.ps1.

  Draft is a one-way automation transition. This script never marks a draft PR ready
  because GitHub does not preserve whether a human or this script set draft state.
.PARAMETER DryRun
  Report intended transitions without changing GitHub state.
.PARAMETER SkipAuthentication
  For tests or a caller that already supplied GH_TOKEN. Production command jobs omit it.
#>
[CmdletBinding()]
param(
    [string]$Repo = 'Sytone/botnexus',
    [switch]$DryRun,
    [switch]$SkipAuthentication,
    [string]$TokenScript = (Join-Path $HOME '.botnexus/scripts/get-farnsworth-token.ps1')
)

$ErrorActionPreference = 'Stop'

# Reuse the maintenance board's established check vocabulary. A second list here
# would eventually disagree about whether a new GitHub conclusion is broken.
. (Join-Path $PSScriptRoot '../ci-pr-status.ps1')

function Get-DraftReason {
    param([Parameter(Mandatory)][object]$PullRequest)

    if ([string]$PullRequest.mergeable -eq 'CONFLICTING' -or
        [string]$PullRequest.mergeStateStatus -eq 'DIRTY') {
        return 'merge-conflict'
    }

    $checks = @($PullRequest.statusCheckRollup)
    foreach ($check in $checks) {
        $name = ([string]$check.name).Trim()
        if (-not $name) { $name = 'unnamed' }

        $bucket = Get-CheckBucket -Check $check
        if ($bucket -eq 'failing') {
            # The guard is a workflow gate, not a broken build. When another
            # check really failed, report that actionable check rather than
            # whichever entry GitHub happened to return first.
            if (Test-SensitiveFileGuardCheck -Check $check) { continue }
            return "failed-check:$name"
        }
        if ($bucket -eq 'stuck') { return "stuck-check:$name" }
    }
    return $null
}

function Get-OpenPullRequests {
    param([Parameter(Mandatory)][string]$Repo)

    $json = gh pr list --repo $Repo --state open --limit 500 --json number,title,isDraft,mergeable,mergeStateStatus,statusCheckRollup,headRefOid,updatedAt
    if ($LASTEXITCODE -ne 0) { throw "gh pr list failed for $Repo" }
    if (-not $json) { return @() }
    return @($json | ConvertFrom-Json)
}

function Set-PullRequestDraft {
    param([Parameter(Mandatory)][int]$Number, [Parameter(Mandatory)][string]$Repo)

    $output = gh pr ready $Number --repo $Repo --undo 2>&1
    if ($LASTEXITCODE -ne 0) { throw "failed to convert PR #$Number to draft: $($output -join ' ')" }
}

function Invoke-BrokenPullRequestDrafting {
    param([Parameter(Mandatory)][string]$Repo, [switch]$DryRun)

    $results = foreach ($pr in @(Get-OpenPullRequests -Repo $Repo)) {
        $reason = Get-DraftReason -PullRequest $pr
        if (-not $reason) {
            [pscustomobject]@{ number = [int]$pr.number; title = [string]$pr.title; action = 'unchanged'; reason = $null }
            continue
        }
        if ([bool]$pr.isDraft) {
            [pscustomobject]@{ number = [int]$pr.number; title = [string]$pr.title; action = 'already-draft'; reason = $reason }
            continue
        }
        if ($DryRun) {
            [pscustomobject]@{ number = [int]$pr.number; title = [string]$pr.title; action = 'would-convert'; reason = $reason }
            continue
        }

        Set-PullRequestDraft -Number ([int]$pr.number) -Repo $Repo
        [pscustomobject]@{ number = [int]$pr.number; title = [string]$pr.title; action = 'converted'; reason = $reason }
    }
    return @($results)
}

if ($MyInvocation.InvocationName -ne '.') {
    $mintedToken = $false
    try {
        if (-not $SkipAuthentication) {
            $env:GH_TOKEN = & pwsh -NoProfile -File $TokenScript
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
                throw 'failed to mint a Farnsworth GitHub App token'
            }
            $mintedToken = $true
        }

        $results = @(Invoke-BrokenPullRequestDrafting -Repo $Repo -DryRun:$DryRun)
        [pscustomobject]@{
            ok = $true
            repo = $Repo
            dryRun = [bool]$DryRun
            scanned = $results.Count
            converted = @($results | Where-Object action -eq 'converted').Count
            wouldConvert = @($results | Where-Object action -eq 'would-convert').Count
            results = $results
        } | ConvertTo-Json -Depth 6 -Compress
    }
    finally {
        if ($mintedToken) { $env:GH_TOKEN = $null }
    }
}
