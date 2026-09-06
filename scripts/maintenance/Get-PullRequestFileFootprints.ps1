<#
.SYNOPSIS
Collects count-verified REST pull-request file footprints before maintenance admission.
.DESCRIPTION
Buffers the entire requested set and throws on any failed, malformed, duplicate or
incomplete response. No partial success object is emitted. The caller must obtain a
complete open-PR number inventory separately and must stop admission on any error.
.PARAMETER ApiRequest
Offline transport seam: receives one REST endpoint and returns one raw JSON string.
It must throw on transport/HTTP failure. Production uses gh api with the caller's
existing authentication; this script never changes tokens, Git config or remotes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,
    [Parameter(Mandatory)]
    [AllowEmptyCollection()]
    [int[]]$PullRequestNumbers,
    [scriptblock]$ApiRequest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($null -eq $ApiRequest) {
    $ApiRequest = {
        param([string]$Endpoint)
        # Keep stderr out of JSON, and never mistake an HTTP error for an empty page.
        $lines = & gh api --method GET -H 'Accept: application/vnd.github+json' $Endpoint
        if ($LASTEXITCODE -ne 0) { throw "GitHub API request failed (exit $LASTEXITCODE): $Endpoint" }
        return ($lines -join "`n")
    }
}

function Read-ApiJson([string]$Endpoint) {
    $raw = & $ApiRequest $Endpoint
    if ($raw -isnot [string] -or [string]::IsNullOrWhiteSpace($raw)) {
        throw "Missing or invalid JSON response: $Endpoint"
    }
    try { $value = ConvertFrom-Json -InputObject $raw -AsHashtable -NoEnumerate -ErrorAction Stop }
    catch { throw "Invalid JSON response: $Endpoint" }
    return ,$value
}

function Read-PrMetadata([int]$Number) {
    $value = Read-ApiJson "repos/$Repository/pulls/$Number"
    if ($value -isnot [Collections.IDictionary] -or
        -not $value.Contains('number') -or $value.number -ne $Number -or
        -not $value.Contains('changed_files') -or
        ($value.changed_files -isnot [int] -and $value.changed_files -isnot [long]) -or
        $value.changed_files -lt 0 -or
        -not $value.Contains('head') -or $value.head -isnot [Collections.IDictionary] -or
        -not $value.head.Contains('sha') -or $value.head.sha -isnot [string] -or $value.head.sha -notmatch '^[0-9a-fA-F]{40,64}$' -or
        -not $value.Contains('base') -or $value.base -isnot [Collections.IDictionary] -or
        -not $value.base.Contains('sha') -or $value.base.sha -isnot [string] -or $value.base.sha -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw "Invalid PR metadata: $Number"
    }
    return $value
}

if ($PullRequestNumbers.Count -eq 0) { throw 'At least one PR number is required; an empty input is not a verified inventory.' }
$numbers = [Collections.Generic.HashSet[int]]::new()
foreach ($number in $PullRequestNumbers) {
    if ($number -le 0) { throw "Invalid PR number: $number" }
    if (-not $numbers.Add($number)) { throw "Duplicate PR number: $number" }
}

$footprints = [Collections.Generic.List[object]]::new()
$reserved = [Collections.Generic.List[string]]::new()
$union = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($number in $PullRequestNumbers) {
    $before = Read-PrMetadata $number
    $expected = $before.changed_files
    $files = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pages = [Collections.Generic.List[object]]::new()
    $aliases = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $owned = [Collections.Generic.List[string]]::new()
    $ownedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $page = 1
    do {
        $endpoint = "repos/$Repository/pulls/$number/files?per_page=100&page=$page"
        $records = Read-ApiJson $endpoint
        if ($records -isnot [array]) { throw "Expected a JSON array of files: $endpoint" }
        if ($records.Count -gt 100) { throw "Oversized page response: $endpoint" }
        foreach ($record in $records) {
            if ($record -isnot [Collections.IDictionary] -or -not $record.Contains('filename') -or
                $record.filename -isnot [string] -or [string]::IsNullOrWhiteSpace($record.filename)) {
                throw "Invalid filename in page response: $endpoint"
            }
            if (-not $seen.Add($record.filename)) { throw "Duplicate filename/page response for PR $number at page $page" }
            if (-not $record.Contains('status') -or $record.status -isnot [string] -or
                $record.status -cnotin @('added', 'removed', 'modified', 'renamed', 'copied', 'changed', 'unchanged')) {
                throw "Invalid file status in page response: $endpoint"
            }
            $files.Add($record.filename)
            if ($ownedSet.Add($record.filename)) { $owned.Add($record.filename) }
            if ($record.status -ceq 'renamed') {
                if (-not $record.Contains('previous_filename') -or $record.previous_filename -isnot [string] -or
                    [string]::IsNullOrWhiteSpace($record.previous_filename) -or $record.previous_filename -ceq $record.filename) {
                    throw "Invalid rename alias in page response: $endpoint"
                }
                if (-not $aliases.Add($record.previous_filename)) { throw "Duplicate rename alias in page response: $endpoint" }
                # Ownership has two paths; changed_files still counts this record once.
                if ($ownedSet.Add($record.previous_filename)) { $owned.Add($record.previous_filename) }
            }
            elseif ($record.Contains('previous_filename')) {
                throw "Unexpected rename alias for status '$($record.status)': $endpoint"
            }
        }
        $pages.Add([pscustomobject]@{ page = $page; endpoint = $endpoint; actualCount = $records.Count })
        # A short page ends transport enumeration, not the completeness check below.
        if ($records.Count -lt 100) { break }
        if ($files.Count -gt $expected) { throw "PR $number count mismatch: expected $expected, received $($files.Count)" }
        $page++
    } while ($true)

    if ($files.Count -ne $expected) { throw "PR $number count mismatch: expected $expected, received $($files.Count)" }
    $after = Read-PrMetadata $number
    if ($after.changed_files -ne $expected -or $after.head.sha -cne $before.head.sha -or $after.base.sha -cne $before.base.sha) {
        throw "PR $number changed during collection; recollect before admission."
    }
    $footprints.Add([pscustomobject]@{
        number = $number; files = $files.ToArray(); expectedCount = $expected; actualCount = $files.Count
        isComplete = $true; pages = $pages.ToArray(); headSha = $before.head.sha; baseSha = $before.base.sha
        reservedFiles = $owned.ToArray()
        evidence = 'rest-pages+unique-filenames+exact-count+stable-head-base+validated-rename-aliases'
    })
    foreach ($file in $owned) { if ($union.Add($file)) { $reserved.Add($file) } }
}

# One success object, only after every PR has passed. Never catch-and-return [].
[pscustomobject]@{
    repository = $Repository; collectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); isComplete = $true
    pullRequests = $footprints.ToArray(); reservedFiles = $reserved.ToArray()
}
