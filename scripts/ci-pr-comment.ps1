#!/usr/bin/env pwsh
# ci-pr-comment.ps1
#
# Creates or patches the CI Health Check comment on a PR.
# Finds the comment by marker, renders the fixed template, diffs against current
# body, and only patches if something changed. Appends a change history entry on update.
#
# Usage:
#   pwsh -NoProfile -File scripts/ci-pr-comment.ps1 `
#     -PR 123 `
#     -CheckRows @(
#         [pscustomobject]@{ name='impacted-tests'; status='pass' },
#         [pscustomobject]@{ name='CodeQL'; status='pass' }
#     ) `
#     -BehindBy 0 `
#     -Mergeable 'MERGEABLE' `
#     -Actions @('No action required -- all checks passing') `
#     -Blockers @('None')
#
# Status values for CheckRows: pass | fail | pending | skipped
# Outputs JSON: { action, commentId, pr }  where action = created | updated | skipped

param(
    [Parameter(Mandatory)][int]   $PR,
    [Parameter(Mandatory)][array] $CheckRows,   # array of [pscustomobject]@{ name; status }
    [Parameter(Mandatory)][int]   $BehindBy,
    [Parameter(Mandatory)][string]$Mergeable,   # MERGEABLE | CONFLICTING | UNKNOWN
    [Parameter(Mandatory)][array] $Actions,     # string[] -- at least one entry
    [Parameter(Mandatory)][array] $Blockers,    # string[] -- use @('None') if none
    [string]$Branch = '',
    [string]$Repo   = 'Sytone/botnexus'
)

$ErrorActionPreference = 'Stop'

$marker  = "<!-- farnsworth:ci-monitor-$PR -->"
$nowUtc  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm UTC')

# ---------------------------------------------------------------------------
# 1. Render check table rows
# ---------------------------------------------------------------------------
$statusIcon = @{
    'pass'    = 'pass'
    'fail'    = 'FAIL'
    'pending' = 'pending'
    'skipped' = 'skipped'
}

# Known checks in display order; any extras from CheckRows appended at end
$knownOrder = @(
    'impacted-tests',
    'CodeQL',
    'Analyze (csharp)',
    'Code Pattern Checks',
    'Dependency Security Audit',
    'Secret Scanning (TruffleHog)'
)

$checkMap = @{}
foreach ($row in $CheckRows) { $checkMap[$row.name] = $row.status }

# Build ordered list: known first, then any unknown checks
$orderedNames = $knownOrder + ($checkMap.Keys | Where-Object { $_ -notin $knownOrder } | Sort-Object)

$tableRows = $orderedNames | ForEach-Object {
    $n = $_
    $s = if ($checkMap.ContainsKey($n)) { $statusIcon[$checkMap[$n]] ?? $checkMap[$n] } else { 'skipped' }
    "| $n | $s |"
}
$tableBody = $tableRows -join "`n"

# ---------------------------------------------------------------------------
# 2. Render bullet lists
# ---------------------------------------------------------------------------
function Format-Bullets([array]$items) {
    ($items | ForEach-Object { "- $_" }) -join "`n"
}

$actionBullets  = Format-Bullets $Actions
$blockerBullets = Format-Bullets $Blockers

# ---------------------------------------------------------------------------
# 3. Render full template (history block placeholder filled below)
# ---------------------------------------------------------------------------
function New-Body([string]$historyBlock) {
    @"
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
$historyBlock
---
*Farnsworth (automated CI monitor) -- [BotNexus](https://github.com/Sytone/$($Repo.Split('/')[1])) -- Last updated: $nowUtc*
"@
}

# ---------------------------------------------------------------------------
# 4. Find existing comment
# ---------------------------------------------------------------------------
$existingId   = $null
$existingBody = $null

$comments = gh api "repos/$Repo/issues/$PR/comments" --paginate 2>$null | ConvertFrom-Json
foreach ($c in $comments) {
    if ($c.body -match [regex]::Escape($marker)) {
        $existingId   = $c.id
        $existingBody = $c.body
        break
    }
}

# ---------------------------------------------------------------------------
# 5. Build history block
# ---------------------------------------------------------------------------
$newEntry = "- $nowUtc"

if ($existingBody) {
    # Extract existing history lines (between the two --- dividers that wrap the history block)
    $histLines = @()
    if ($existingBody -match '(?s)---\n((?:- .+\n?)+)---') {
        $histLines = ($Matches[1].Trim() -split "`n") | Where-Object { $_ -match '^- ' }
    }
    # Keep last 9 entries, prepend new one (newest first)
    $kept        = @($histLines | Select-Object -First 9)
    $historyBlock = (@($newEntry) + $kept) -join "`n"
} else {
    $historyBlock = $newEntry
}

$newBody = New-Body $historyBlock

# ---------------------------------------------------------------------------
# 6. Diff -- skip update if substantive content is identical
#    Strip both the history block AND the footer timestamp before comparing.
#    A timestamp-only change is not worth patching.
# ---------------------------------------------------------------------------
function Strip-Volatile([string]$body) {
    # Remove the history block (lines between the two --- dividers)
    $body = $body -replace '(?s)(---\n)((?:- .+\n?)+)(---)', '${1}${3}'
    # Remove the footer timestamp line
    $body = $body -replace '\*Farnsworth \(automated CI monitor\).+\*', ''
    $body
}

if ($existingBody -and ((Strip-Volatile $existingBody) -eq (Strip-Volatile $newBody))) {
    @{ action = 'skipped'; commentId = $existingId; pr = $PR } | ConvertTo-Json -Compress
    exit 0
}

# ---------------------------------------------------------------------------
# 7. Create or patch
# ---------------------------------------------------------------------------
if ($existingId) {
    gh api "repos/$Repo/issues/comments/$existingId" -X PATCH -f body=$newBody | Out-Null
    @{ action = 'updated'; commentId = $existingId; pr = $PR } | ConvertTo-Json -Compress
} else {
    $result = gh pr comment $PR --repo $Repo --body $newBody | Out-Null
    # Fetch the new comment id
    $newId = (gh api "repos/$Repo/issues/$PR/comments" --paginate 2>$null |
        ConvertFrom-Json | Where-Object { $_.body -match [regex]::Escape($marker) } |
        Select-Object -Last 1).id
    @{ action = 'created'; commentId = $newId; pr = $PR } | ConvertTo-Json -Compress
}
