<#
.SYNOPSIS
    Returns CI status for all open PRs in Sytone/botnexus as a JSON array.

.DESCRIPTION
    Queries GitHub for all open PRs, their check statuses, and how far behind
    main they are. Outputs a JSON array suitable for consumption by maintenance
    automation scripts.

    Check entries arrive in two different shapes depending on which GitHub API
    surface produced them:
      * CheckRun     - carries 'conclusion' (SUCCESS/FAILURE/CANCELLED/TIMED_OUT/
                       SKIPPED/NEUTRAL/ACTION_REQUIRED/STALE) plus 'status'
                       (COMPLETED/IN_PROGRESS/QUEUED).
      * StatusContext - carries 'state' (SUCCESS/FAILURE/PENDING/ERROR).
    Get-NormalizedCheckState folds both into a single vocabulary so a cancelled
    or timed-out run can never be mistaken for a pass.

.OUTPUTS
    JSON array of objects with: number, title, branch, ciStatus, behindBy,
    failingChecks, pendingChecks, stuckChecks, checkConclusions, mergeable.

.EXAMPLE
    pwsh -NoProfile -File scripts/ci-pr-status.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
    Folds a CheckRun or StatusContext entry into a single state vocabulary.
.DESCRIPTION
    Prefers an explicit terminal 'conclusion' (CheckRun). Falls back to 'state'
    (StatusContext, and the normalised shape emitted by 'gh pr checks'). When a
    CheckRun has not completed there is no conclusion at all, so the 'status'
    field is mapped to PENDING. Returns 'UNKNOWN' when nothing is populated.
#>
function Get-NormalizedCheckState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()][object]$Check)

    if ($null -eq $Check) { return 'UNKNOWN' }

    $conclusion = [string]$Check.conclusion
    if (-not [string]::IsNullOrWhiteSpace($conclusion)) { return $conclusion.ToUpperInvariant() }

    $state = [string]$Check.state
    if (-not [string]::IsNullOrWhiteSpace($state)) { return $state.ToUpperInvariant() }

    $status = [string]$Check.status
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        $status = $status.ToUpperInvariant()
        # A CheckRun that has not completed carries no conclusion yet.
        if ($status -ne 'COMPLETED') { return 'PENDING' }
    }

    return 'UNKNOWN'
}

<#
.SYNOPSIS
    Buckets a normalised check state into failing / stuck / pending / ok / unknown.
.DESCRIPTION
    'stuck' is deliberately distinct from 'failing': a cancelled or timed-out run
    conveys no signal about the change, and blindly re-running a hang simply
    reproduces it, whereas a genuine FAILURE often warrants a re-run.
    SKIPPED and NEUTRAL are normal, non-blocking outcomes and are treated as ok.
#>
function Get-CheckBucket {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()][object]$Check)

    switch (Get-NormalizedCheckState -Check $Check) {
        'SUCCESS' { return 'ok' }
        'SKIPPED' { return 'ok' }
        'NEUTRAL' { return 'ok' }
        'FAILURE' { return 'failing' }
        'ERROR' { return 'failing' }
        'ACTION_REQUIRED' { return 'failing' }
        'STARTUP_FAILURE' { return 'failing' }
        'CANCELLED' { return 'stuck' }
        'TIMED_OUT' { return 'stuck' }
        'STALE' { return 'stuck' }
        'PENDING' { return 'pending' }
        'IN_PROGRESS' { return 'pending' }
        'QUEUED' { return 'pending' }
        'WAITING' { return 'pending' }
        'REQUESTED' { return 'pending' }
        'EXPECTED' { return 'pending' }
        default { return 'unknown' }
    }
}

<#
.SYNOPSIS
    Derives the overall ciStatus for a PR from its check entries.
.DESCRIPTION
    Precedence: failing > stuck > pending > unknown-check > passing. An empty
    check set is 'unknown'. A check whose state cannot be interpreted is also
    'unknown' rather than being silently swallowed into 'passing'.
#>
function Get-PrCiStatus {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Checks)

    if ($null -eq $Checks -or $Checks.Count -eq 0) { return 'unknown' }

    $buckets = @($Checks | ForEach-Object { Get-CheckBucket -Check $_ })

    if ($buckets -contains 'failing') { return 'failing' }
    if ($buckets -contains 'stuck') { return 'stuck' }
    if ($buckets -contains 'pending') { return 'pending' }
    if ($buckets -contains 'unknown') { return 'unknown' }
    return 'passing'
}

function Get-CiPrStatusReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Repo)

    $prs = gh pr list --repo $Repo --state open --json number,title,headRefName,mergeable | ConvertFrom-Json

    if (-not $prs -or @($prs).Count -eq 0) { return @() }

    $results = @()
    foreach ($pr in $prs) {
        $checks = gh pr checks $pr.number --repo $Repo --json name,state 2>$null | ConvertFrom-Json
        if (-not $checks) { $checks = @() }
        $checks = @($checks)

        $behind = 0
        try {
            $behind = [int](gh api "repos/$Repo/compare/main...$($pr.headRefName)" --jq '.behind_by' 2>$null)
        } catch {
            $behind = 0
        }

        $failing = @($checks | Where-Object { (Get-CheckBucket -Check $_) -eq 'failing' })
        $pending = @($checks | Where-Object { (Get-CheckBucket -Check $_) -eq 'pending' })
        $stuck = @($checks | Where-Object { (Get-CheckBucket -Check $_) -eq 'stuck' })

        $results += [pscustomobject]@{
            number           = $pr.number
            title            = $pr.title
            branch           = $pr.headRefName
            ciStatus         = Get-PrCiStatus -Checks $checks
            behindBy         = $behind
            failingChecks    = @($failing | ForEach-Object { $_.name })
            pendingChecks    = @($pending | ForEach-Object { $_.name })
            stuckChecks      = @($stuck | ForEach-Object { $_.name })
            checkConclusions = @($checks | ForEach-Object {
                    [pscustomobject]@{
                        name       = $_.name
                        conclusion = Get-NormalizedCheckState -Check $_
                        bucket     = Get-CheckBucket -Check $_
                    }
                })
            mergeable        = $pr.mergeable
        }
    }

    return $results
}

# Only run when executed directly; dot-sourcing (tests) just loads the functions.
if ($MyInvocation.InvocationName -ne '.') {
    $report = @(Get-CiPrStatusReport -Repo 'Sytone/botnexus')
    if ($report.Count -eq 0) {
        Write-Output '[]'
    } else {
        $report | ConvertTo-Json -Depth 6
    }
}
