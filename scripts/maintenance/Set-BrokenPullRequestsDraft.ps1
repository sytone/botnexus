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

# CLI rollups are scoped to the PR head but contain historical executions. Never
# infer chronology from array order or completion time when a start is available:
# an older cancelled execution can finish after its replacement has succeeded.
function Get-CurrentDraftChecks {
    param([Parameter(Mandatory)][object]$PullRequest)

    $groups = @{}
    foreach ($check in @($PullRequest.statusCheckRollup)) {
        if ($null -eq $check) { continue }
        $head = [string]$check.head_sha
        if (-not $head) { $head = [string]$check.headSha }
        if ($head -and $head -ne [string]$PullRequest.headRefOid) { continue }
        $name = ([string]$check.name).Trim()
        $kind = 'check'
        if (-not $name -and $check.context) { $name = [string]$check.context; $kind = 'status' }
        if (-not $name) { $name = 'unnamed' }
        $workflow = [string]$check.workflowName
        $app = [string]$check.app.id
        # JSON tuple prevents delimiter collisions in user-defined check names.
        $key = ConvertTo-Json -Compress -InputObject @($kind, $name, $workflow, $app)
        $run = [string]$check.run_id
        $url = [string]$check.detailsUrl
        if (-not $url) { $url = [string]$check.details_url }
        if (-not $run -and $url -match '/actions/runs/(\d+)') { $run = $Matches[1] }
        $attempt = 0L
        [void][long]::TryParse([string]$check.run_attempt, [ref]$attempt)
        $stamp = $null
        foreach ($value in @($check.startedAt, $check.started_at, $check.createdAt, $check.created_at, $check.completedAt, $check.completed_at)) {
            if (-not $value) { continue }
            $parsed = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse([string]$value, [ref]$parsed) -and $parsed.Year -gt 1970) {
                $stamp = $parsed
                break
            }
        }
        if (-not $groups.ContainsKey($key)) { $groups[$key] = [Collections.Generic.List[object]]::new() }
        $groups[$key].Add([pscustomobject]@{ Check=$check; Name=$name; Run=$run; Attempt=$attempt; Stamp=$stamp })
    }

    foreach ($key in @($groups.Keys | Sort-Object)) {
        $entries = $groups[$key]
        foreach ($candidate in $entries) {
            $newest = $true
            foreach ($other in $entries) {
                if ([object]::ReferenceEquals($candidate, $other)) { continue }
                if ($candidate.Run -and $candidate.Run -eq $other.Run -and
                    $candidate.Attempt -gt 0 -and $other.Attempt -gt 0 -and $candidate.Attempt -ne $other.Attempt) {
                    $later = $candidate.Attempt -gt $other.Attempt
                }
                else {
                    $later = $null -ne $candidate.Stamp -and $null -ne $other.Stamp -and $candidate.Stamp -gt $other.Stamp
                }
                if (-not $later) { $newest = $false; break }
            }
            # Missing/tied chronology cannot prove which duplicate is current.
            # Fail open rather than quarantine based on a possibly superseded run.
            if ($newest) {
                [pscustomobject]@{ name=$candidate.Name; conclusion=$candidate.Check.conclusion; status=$candidate.Check.status; state=$candidate.Check.state }
                break
            }
        }
    }
}

function Get-DraftReason {
    param([Parameter(Mandatory)][object]$PullRequest)

    if ([string]$PullRequest.mergeable -eq 'CONFLICTING' -or
        [string]$PullRequest.mergeStateStatus -eq 'DIRTY') {
        return 'merge-conflict'
    }

    $checks = @(Get-CurrentDraftChecks -PullRequest $PullRequest)
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
    $pullRequests = @($json | ConvertFrom-Json)
    # CLI list pagination is bounded. Refuse saturation BEFORE returning anything
    # to the mutating loop; 500 results cannot prove there is no 501st PR.
    if ($pullRequests.Count -ge 500) { throw 'Open PR enumeration reached cap 500; refusing an incomplete scan.' }
    foreach ($pr in $pullRequests) {
        # gh's nested GraphQL query requests contexts(first:100). The flattened
        # JSON omits pageInfo, so saturation is not evidence of completeness.
        if (@($pr.statusCheckRollup).Count -ge 100) {
            throw "PR #$($pr.number) check rollup reached cap 100; refusing an incomplete scan."
        }
    }
    return $pullRequests
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
    # Own cleanup before minting: a failed child can emit a partial credential,
    # or throw before assignment and leave an inherited credential in the process.
    $mintedToken = -not $SkipAuthentication
    try {
        if (-not $SkipAuthentication) {
            $env:GH_TOKEN = & pwsh -NoProfile -File $TokenScript
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
                throw 'failed to mint a Farnsworth GitHub App token'
            }
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
