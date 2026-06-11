<#
.SYNOPSIS
    Retrieves failing CI workflow run logs for a given PR.

.DESCRIPTION
    Finds the most recent failing workflow run(s) on the given PR and downloads
    their logs. Outputs log text for each failing job to stdout.

.PARAMETER PR
    The PR number to inspect.

.EXAMPLE
    pwsh -NoProfile -File scripts/ci-pr-get-logs.ps1 -PR 1127
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$PR
)

$ErrorActionPreference = 'Stop'
$repo = 'Sytone/botnexus'

# Get the head branch for this PR
$prData = gh pr view $PR --repo $repo --json headRefName | ConvertFrom-Json
if (-not $prData) {
    Write-Error "PR #$PR not found."
    return
}

$branch = $prData.headRefName

# Get workflow runs for this branch
$runs = gh run list --repo $repo --branch $branch --limit 5 --json databaseId,status,conclusion,name,headBranch |
    ConvertFrom-Json |
    Where-Object { $_.conclusion -eq 'failure' }

if (-not $runs -or $runs.Count -eq 0) {
    Write-Output "No failing workflow runs found for PR #$PR (branch: $branch)."
    return
}

foreach ($run in $runs | Select-Object -First 2) {
    Write-Output "=== Run $($run.databaseId) ($($run.name)) ==="
    Write-Output ""

    # Get failed jobs for this run
    $jobs = gh run view $run.databaseId --repo $repo --json jobs --jq '.jobs[] | select(.conclusion == "failure") | .name' 2>$null
    if ($jobs) {
        Write-Output "Failed jobs: $($jobs -join ', ')"
        Write-Output ""
    }

    # Download and output logs (truncated to last 200 lines per run)
    $logOutput = gh run view $run.databaseId --repo $repo --log-failed 2>$null
    if ($logOutput) {
        $lines = $logOutput -split "`n"
        if ($lines.Count -gt 200) {
            Write-Output "... (truncated, showing last 200 lines) ..."
            $lines | Select-Object -Last 200 | ForEach-Object { Write-Output $_ }
        } else {
            $logOutput | ForEach-Object { Write-Output $_ }
        }
    } else {
        Write-Output "(No log output available for this run)"
    }

    Write-Output ""
}
