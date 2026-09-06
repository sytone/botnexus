<#
.SYNOPSIS
    Returns CI status for all open PRs in Sytone/botnexus as a JSON array.

.DESCRIPTION
    Queries GitHub for all open PRs, their check statuses, and how far behind
    their independently captured current target branch they are. Outputs a JSON array suitable for consumption by maintenance
    automation scripts.

    Check entries arrive in two different shapes depending on which GitHub API
    surface produced them:
      * CheckRun     - carries 'conclusion' (SUCCESS/FAILURE/CANCELLED/TIMED_OUT/
                       SKIPPED/NEUTRAL/ACTION_REQUIRED/STALE) plus 'status'
                       (COMPLETED/IN_PROGRESS/QUEUED).
      * StatusContext - carries 'state' (SUCCESS/FAILURE/PENDING/ERROR).
    Get-NormalizedCheckState folds both into a single vocabulary so a cancelled
    or timed-out run can never be mistaken for a pass.

    A PR whose ONLY failing check is the Sensitive File Guard, with every other
    check successful, is not broken - it is waiting on a human. The guard demands
    an admin/maintain/write maintainer comment, which this automation is
    deliberately incapable of posting (see Get-AwaitingAckDetail). Such a PR is
    classified 'awaiting-ack' so the maintenance loop reports it instead of
    routing it into the CI-repair path where there is nothing to repair.

.OUTPUTS
    JSON array of objects with: number, title, branch, ciStatus, behindBy,
    failingChecks, pendingChecks, stuckChecks, checkConclusions, mergeable,
    headSha, awaitingAck, aheadBy, freshnessStatus, freshnessDiagnostic,
    targetRef, targetSha. Unknown freshness has null counts. A complete board
    exits 0 (including unknown freshness); a collection failure exits 1.

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

# The literal name of the maintainer-acknowledgement guard. The rollup reports
# either the bare job name or the workflow-qualified 'Security: <job>' form,
# so both are recognised.
$script:SensitiveFileGuardJobName = 'Sensitive File Guard'

<#
.SYNOPSIS
    True when a check entry is the Sensitive File Guard.
#>
function Test-SensitiveFileGuardCheck {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()][object]$Check)

    if ($null -eq $Check) { return $false }
    $name = ([string]$Check.name).Trim()
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }

    return ($name -eq $script:SensitiveFileGuardJobName) -or
           ($name.EndsWith(": $($script:SensitiveFileGuardJobName)"))
}

<#
.SYNOPSIS
    True when the Sensitive File Guard is the ONLY thing standing between this PR
    and a green rollup.
.DESCRIPTION
    Requires at least one failing guard entry, no non-guard failure, and every
    other check in the 'ok' bucket. A pending, stuck or uninterpretable sibling
    check means the outcome is not yet known, so the PR keeps its existing
    classification rather than claiming a state it cannot prove.
#>
function Test-AwaitingMaintainerAck {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Checks)

    if ($null -eq $Checks -or $Checks.Count -eq 0) { return $false }

    $sawFailingGuard = $false
    foreach ($check in $Checks) {
        $bucket = Get-CheckBucket -Check $check
        $isGuard = Test-SensitiveFileGuardCheck -Check $check

        if ($isGuard -and $bucket -eq 'failing') { $sawFailingGuard = $true; continue }
        if ($bucket -ne 'ok') { return $false }
    }

    return $sawFailingGuard
}

<#
.SYNOPSIS
    Builds the awaiting-ack payload: the exact command a maintainer must post and
    the head SHA the approval is bound to.
.DESCRIPTION
    SECURITY - DELIBERATE NON-CAPABILITY: this function FORMATS the command for a
    human to post. It does not post it, and nothing in this script may. The guard
    requires admin/maintain/write; agent-farnsworth[bot] posting its own approval
    would defeat the control outright, exactly as authoring content as the
    maintainer would. This script therefore contains no comment-write call of any
    kind, and ci-pr-status.Tests.ps1 asserts that by inspecting the source.

    'notificationKey' exists so a surfacing loop can notify once per head SHA:
    the approval is SHA-bound, so a new push legitimately warrants a new notice,
    while the same SHA must not be re-reported every cycle.
#>
function Get-AwaitingAckDetail {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Number,
        [Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$HeadSha
    )

    $sha = ([string]$HeadSha).Trim()
    if ([string]::IsNullOrWhiteSpace($sha)) { $sha = 'unknown' }

    return [pscustomobject]@{
        reason          = 'sensitive-file-guard'
        headSha         = $sha
        ackCommand      = "/allow-security-sensitive-change $sha"
        ackRequiredFrom = 'a maintainer with admin/maintain/write; automation must not self-approve'
        notificationKey = "awaiting-ack:${Number}:${sha}"
    }
}

<#
.SYNOPSIS
    Derives the overall ciStatus for a PR from its check entries.
.DESCRIPTION
    Precedence: awaiting-ack > failing > stuck > pending > unknown-check >
    passing. An empty check set is 'unknown'. A check whose state cannot be
    interpreted is also 'unknown' rather than being silently swallowed into
    'passing'.

    'awaiting-ack' outranks 'failing' only in the narrow case where the guard is
    the sole failure; any other genuine failure keeps the PR 'failing' so the new
    status can never mask a real defect.
#>
function Get-PrCiStatus {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Checks)

    if ($null -eq $Checks -or $Checks.Count -eq 0) { return 'unknown' }

    if (Test-AwaitingMaintainerAck -Checks $Checks) { return 'awaiting-ack' }

    $buckets = @($Checks | ForEach-Object { Get-CheckBucket -Check $_ })

    if ($buckets -contains 'failing') { return 'failing' }
    if ($buckets -contains 'stuck') { return 'stuck' }
    if ($buckets -contains 'pending') { return 'pending' }
    if ($buckets -contains 'unknown') { return 'unknown' }
    return 'passing'
}

<#
.SYNOPSIS
    Captures the current target ref independently, then compares immutable SHAs.
.DESCRIPTION
    A PR object's base OID can lag the target branch. Never use it as freshness
    evidence. Unknown transport/schema results retain null counts, not zero.
#>
function Get-PrFreshness {
    [CmdletBinding()]
    param([string]$Repo, [AllowNull()][object]$Pr)

    $result = [ordered]@{
        freshnessStatus = 'unknown'
        targetRef = [string]$Pr.baseRefName
        targetSha = $null
        behindBy = $null
        aheadBy = $null
        freshnessDiagnostic = $null
    }
    $stage = 'target-ref'
    try {
        if ([string]::IsNullOrWhiteSpace($result.targetRef)) { throw 'Missing target branch name.' }
        if ([string]$Pr.headRefOid -notmatch '\A[0-9a-fA-F]{40}\z') { throw 'Missing or invalid captured PR head SHA.' }
        $refPath = [Uri]::EscapeDataString($result.targetRef)
        $raw = gh api "repos/$Repo/git/ref/heads/$refPath" 2>$null
        if ($LASTEXITCODE -ne 0) { throw "GitHub target-ref request failed (exit $LASTEXITCODE)." }
        $target = ($raw -join [Environment]::NewLine) | ConvertFrom-Json -NoEnumerate
        if ($target -is [array] -or $target -isnot [pscustomobject] -or $target.object.type -ne 'commit' -or
            [string]$target.object.sha -notmatch '\A[0-9a-fA-F]{40}\z') {
            throw 'Target-ref response lacks a valid commit SHA.'
        }
        $result.targetSha = [string]$target.object.sha
        $stage = 'compare'
        $raw = gh api "repos/$Repo/compare/$($result.targetSha)...$($Pr.headRefOid)" 2>$null
        if ($LASTEXITCODE -ne 0) { throw "GitHub compare request failed (exit $LASTEXITCODE)." }
        $comparison = ($raw -join [Environment]::NewLine) | ConvertFrom-Json -NoEnumerate
        if ($comparison -is [array] -or $comparison -isnot [pscustomobject]) { throw 'Compare response must be a JSON object.' }
        foreach ($field in @('behind_by', 'ahead_by')) {
            $value = $comparison.$field
            if (($value -isnot [int] -and $value -isnot [long]) -or $value -lt 0) {
                throw "Compare response lacks a nonnegative integer $field."
            }
        }
        $result.behindBy = $comparison.behind_by
        $result.aheadBy = $comparison.ahead_by
        $result.freshnessStatus = 'verified'
    } catch {
        $result.freshnessDiagnostic = "${stage}: $($_.Exception.Message)"
    }
    return [pscustomobject]$result
}

function Get-CiPrStatusReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Repo)

    # --limit is MANDATORY. gh defaults 'pr list' to 30 items and emits no
    # page-boundary signal, so omitting it truncates the board silently and
    # indistinguishably from "there are exactly 30 open PRs" (#3773). 500 is an
    # order of magnitude above the observed board (31) and above the sibling
    # convention of 100 used by Get-PrFailureCause.ps1 / Invoke-IssueClaim.ps1,
    # which is deliberate: this is the instrument the whole maintenance loop
    # reads its open-PR count from, and that count gates dispatch.
    $raw = gh pr list --repo $Repo --state open --limit 500 --json number,title,headRefName,headRefOid,baseRefName,mergeable
    if ($LASTEXITCODE -ne 0) { throw "GitHub PR list failed (exit $LASTEXITCODE)." }
    $prs = ($raw -join [Environment]::NewLine) | ConvertFrom-Json -NoEnumerate
    if ($prs -isnot [array]) { throw 'GitHub PR list response must be a JSON array.' }
    if ($prs.Count -eq 0) { return @() }

    $results = @()
    foreach ($pr in $prs) {
        $checks = gh pr checks $pr.number --repo $Repo --json name,state 2>$null | ConvertFrom-Json
        if (-not $checks) { $checks = @() }
        $checks = @($checks)

        $freshness = Get-PrFreshness -Repo $Repo -Pr $pr

        $failing = @($checks | Where-Object { (Get-CheckBucket -Check $_) -eq 'failing' })
        $pending = @($checks | Where-Object { (Get-CheckBucket -Check $_) -eq 'pending' })
        $stuck = @($checks | Where-Object { (Get-CheckBucket -Check $_) -eq 'stuck' })

        $ciStatus = Get-PrCiStatus -Checks $checks
        $awaitingAck = $null
        if ($ciStatus -eq 'awaiting-ack') {
            $awaitingAck = Get-AwaitingAckDetail -Number ([int]$pr.number) -HeadSha ([string]$pr.headRefOid)
        }

        $results += [pscustomobject]@{
            number           = $pr.number
            title            = $pr.title
            branch           = $pr.headRefName
            headSha          = [string]$pr.headRefOid
            ciStatus         = $ciStatus
            awaitingAck      = $awaitingAck
            freshnessStatus  = $freshness.freshnessStatus
            freshnessDiagnostic = $freshness.freshnessDiagnostic
            targetRef        = $freshness.targetRef
            targetSha        = $freshness.targetSha
            behindBy         = $freshness.behindBy
            aheadBy          = $freshness.aheadBy
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
    try {
        $report = @(Get-CiPrStatusReport -Repo 'Sytone/botnexus')
        ConvertTo-Json -InputObject $report -Depth 6
        # A complete board can contain unknown freshness; incidental gh exit
        # codes must not override this deliberate reporting contract.
        exit 0
    } catch {
        [Console]::Error.WriteLine("CI PR status collection failed: $($_.Exception.Message)")
        exit 1
    }
}
