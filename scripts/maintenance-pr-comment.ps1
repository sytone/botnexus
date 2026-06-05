#!/usr/bin/env pwsh
# maintenance-pr-comment.ps1
#
# Creates or patches the Autonomous Maintenance review comment on a PR.
# Finds the comment by marker, renders the fixed template, diffs against current
# body, and only patches if something changed. Appends a change history entry on update.
#
# Usage:
#   pwsh -NoProfile -File scripts/maintenance-pr-comment.ps1 `
#     -PR 123 `
#     -ConventionalCommit 'pass' `
#     -Coverage 'Happy + sad paths present -- 5 new tests' `
#     -SpecCompleteness 'Closes #456 -- all AC met' `
#     -MergeConflict 'clean' `
#     -CommentResponses @('None') `
#     -Notes @('Ready to merge')
#
# ConventionalCommit: pass | fail | <explanation string>
# MergeConflict:      clean | resolved | conflicting | <explanation string>
# CommentResponses:   string[] -- use @('None') if none
# Notes:              string[] -- free-form observations, use @('None') if none
# Outputs JSON: { action, commentId, pr }  where action = created | updated | skipped

param(
    [Parameter(Mandatory)][int]   $PR,
    [Parameter(Mandatory)][string]$ConventionalCommit,  # pass | fail | explanation
    [Parameter(Mandatory)][string]$Coverage,            # one-line summary
    [Parameter(Mandatory)][string]$SpecCompleteness,    # one-line summary
    [Parameter(Mandatory)][string]$MergeConflict,       # clean | resolved | conflicting | explanation
    [Parameter(Mandatory)][array] $CommentResponses,    # string[] -- use @('None') if none
    [Parameter(Mandatory)][array] $Notes,               # string[] -- use @('None') if none
    [string]$Repo = 'Sytone/botnexus'
)

$ErrorActionPreference = 'Stop'

$marker = "<!-- farnsworth:maintenance-review-$PR -->"
$nowUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm UTC')

# ---------------------------------------------------------------------------
# 1. Render status icons
# ---------------------------------------------------------------------------
function Get-Icon([string]$val) {
    switch ($val.ToLower()) {
        'pass'        { return 'pass' }
        'fail'        { return 'FAIL' }
        'clean'       { return 'clean' }
        'resolved'    { return 'resolved this cycle' }
        'conflicting' { return 'CONFLICTING' }
        default       { return $val }
    }
}

$commitIcon   = Get-Icon $ConventionalCommit
$conflictIcon = Get-Icon $MergeConflict

# ---------------------------------------------------------------------------
# 2. Render bullet lists
# ---------------------------------------------------------------------------
function Format-Bullets([array]$items) {
    ($items | ForEach-Object { "- $_" }) -join "`n"
}

$responseBullets = Format-Bullets $CommentResponses
$notesBullets    = Format-Bullets $Notes

# ---------------------------------------------------------------------------
# 3. Render full template
# ---------------------------------------------------------------------------
function New-Body([string]$historyBlock) {
    @"
$marker
## PR Review -- #$PR

| Area | Status |
|------|--------|
| Conventional commit title | $commitIcon |
| Test coverage | $Coverage |
| Spec completeness | $SpecCompleteness |
| Merge conflicts | $conflictIcon |

**Responses to comments:**
$responseBullets

**Notes:**
$notesBullets

---
$historyBlock
---
*Farnsworth (autonomous maintenance) -- [BotNexus](https://github.com/Sytone/$($Repo.Split('/')[1])) -- Last updated: $nowUtc*
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
    $histLines = @()
    if ($existingBody -match '(?s)---\n((?:- .+\n?)+)---') {
        $histLines = ($Matches[1].Trim() -split "`n") | Where-Object { $_ -match '^- ' }
    }
    $kept         = @($histLines | Select-Object -First 9)
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
    $body = $body -replace '\*Farnsworth \(autonomous maintenance\).+\*', ''
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
    gh pr comment $PR --repo $Repo --body $newBody | Out-Null
    $newId = (gh api "repos/$Repo/issues/$PR/comments" --paginate 2>$null |
        ConvertFrom-Json | Where-Object { $_.body -match [regex]::Escape($marker) } |
        Select-Object -Last 1).id
    @{ action = 'created'; commentId = $newId; pr = $PR } | ConvertTo-Json -Compress
}
