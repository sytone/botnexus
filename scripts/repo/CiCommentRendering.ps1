<#
.SYNOPSIS
    Pure rendering helpers for the CI Health Check PR comment.
.DESCRIPTION
    Extracted from scripts/ci-pr-comment.ps1 so the body can be rendered and
    asserted without any GitHub network access.

    The check rows handed to these functions come from the GitHub
    'statusCheckRollup', whose entries are a union of two shapes: a CheckRun
    carries 'name', while a StatusContext carries 'context' and no 'name' at
    all. Issue #2997: using the raw 'name' as a hashtable key threw
    "Index operation failed; the array index evaluated to null" on the first
    StatusContext entry, aborting the whole comment. Because the maintenance
    loop treats commenting as best-effort, the operator saw no comment and no
    error -- a silent failure. Every display key therefore flows through
    Get-CheckDisplayKey and nothing else.
#>

<#
.SYNOPSIS
    Returns the single display key for a check row, or $null when it has none.
.DESCRIPTION
    This is the ONLY place a row's display key is computed (issue #2997 AC4).
    Both the status map and the ordered name list call it, so the two cannot
    disagree about which rows exist. Prefers 'name' (CheckRun), falls back to
    'context' (StatusContext), and returns $null when neither is usable so the
    caller can skip the row instead of throwing on a null hashtable key.
#>
function Get-CheckDisplayKey {
    param([Parameter()] $Row)

    if ($null -eq $Row) { return $null }

    $name = $Row.PSObject.Properties['name'] ? $Row.name : $null
    if ($name -is [string] -and -not [string]::IsNullOrWhiteSpace($name)) { return $name }

    $context = $Row.PSObject.Properties['context'] ? $Row.context : $null
    if ($context -is [string] -and -not [string]::IsNullOrWhiteSpace($context)) { return $context }

    return $null
}

<#
.SYNOPSIS
    Builds the markdown table body (rows only) for the CI Health Check comment.
.DESCRIPTION
    Known checks are rendered first in a fixed order; any additional rows are
    appended alphabetically. Rows with no usable display key are skipped rather
    than aborting the render.
#>
function New-CiCheckTableBody {
    param(
        [Parameter()][array]$CheckRows,
        [Parameter()][string[]]$KnownOrder = @(
            'core-tests',
            'CodeQL',
            'Analyze (csharp)',
            'Code Pattern Checks',
            'Dependency Security Audit',
            'Secret Scanning (TruffleHog)'
        )
    )

    $statusIcon = @{
        'pass'    = 'pass'
        'fail'    = 'FAIL'
        'pending' = 'pending'
        'skipped' = 'skipped'
    }

    $checkMap = @{}
    foreach ($row in $CheckRows) {
        $key = Get-CheckDisplayKey -Row $row
        if (-not $key) { continue }
        $checkMap[$key] = $row.status
    }

    $orderedNames = @($KnownOrder) + @($checkMap.Keys | Where-Object { $_ -notin $KnownOrder } | Sort-Object)

    $tableRows = $orderedNames | ForEach-Object {
        $n = $_
        $s = 'skipped'
        if ($checkMap.ContainsKey($n)) {
            $raw = $checkMap[$n]
            # A row may carry a null/blank status; a null hashtable index throws.
            if ($raw -is [string] -and -not [string]::IsNullOrWhiteSpace($raw)) {
                $s = $statusIcon.ContainsKey($raw) ? $statusIcon[$raw] : $raw
            } else {
                $s = 'unknown'
            }
        }
        "| $n | $s |"
    }

    return ($tableRows -join "`n")
}

<#
.SYNOPSIS
    Renders the complete CI Health Check comment body.
#>
function New-CiHealthCheckBody {
    param(
        [Parameter(Mandatory)][int]$PR,
        [Parameter()][array]$CheckRows,
        [Parameter()][int]$BehindBy = 0,
        [Parameter()][string]$Mergeable = 'UNKNOWN',
        [Parameter()][array]$Actions = @(),
        [Parameter()][array]$Blockers = @(),
        [Parameter()][string]$Branch = '',
        [Parameter()][string]$Repo = 'Sytone/botnexus',
        [Parameter()][string]$HistoryBlock = '',
        [Parameter()][string]$NowUtc = ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm UTC'))
    )

    $marker = "<!-- farnsworth:ci-monitor-$PR -->"
    $tableBody = New-CiCheckTableBody -CheckRows $CheckRows
    $actionBullets = (($Actions | ForEach-Object { "- $_" }) -join "`n")
    $blockerBullets = (($Blockers | ForEach-Object { "- $_" }) -join "`n")
    $repoName = $Repo.Split('/')[1]

    return @"
$marker
## CI Health Check -- PR #$PR

| Check | Status |
|-------|--------|
$tableBody

**Branch:** ``$Branch`` | **Behind main:** $BehindBy commits | **Mergeable:** $Mergeable

**Actions taken:**
$actionBullets

**Blockers for Jon:**
$blockerBullets

---
$HistoryBlock
---
*Farnsworth (automated CI monitor) -- [BotNexus](https://github.com/Sytone/$repoName) -- Last updated: $NowUtc*
"@
}
