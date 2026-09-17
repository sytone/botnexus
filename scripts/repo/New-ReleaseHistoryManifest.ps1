[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [Parameter(Mandatory)][ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string]$ReleasedAt,
    [Parameter(Mandatory)][string]$NotesPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][uri]$ReleaseUrl,
    [Parameter(Mandatory)][uri]$DocumentationUrl,
    [string]$ExistingManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-PublicReleaseUri([uri]$Uri) {
    if ($Uri.Scheme -ne 'https') { return $false }
    if ($Uri.Host -eq 'github.com') {
        return $Uri.AbsolutePath.StartsWith('/sytone/botnexus/', [StringComparison]::OrdinalIgnoreCase)
    }
    if ($Uri.Host -eq 'sytone.github.io') {
        return $Uri.AbsolutePath.StartsWith('/botnexus/', [StringComparison]::OrdinalIgnoreCase)
    }
    return $false
}

function Get-SemVerParts([string]$Value) {
    if ($Value -notmatch '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw "Invalid semantic version '$Value'."
    }
    $prerelease = if ($Matches.ContainsKey('pre')) { [string]$Matches['pre'] } else { '' }
    [pscustomobject]@{
        Major = [uint64]$Matches.major
        Minor = [uint64]$Matches.minor
        Patch = [uint64]$Matches.patch
        Prerelease = $prerelease
    }
}

function Compare-Prerelease([string]$Left, [string]$Right) {
    if ([string]::IsNullOrEmpty($Left) -and [string]::IsNullOrEmpty($Right)) { return 0 }
    if ([string]::IsNullOrEmpty($Left)) { return 1 }
    if ([string]::IsNullOrEmpty($Right)) { return -1 }
    $leftParts = $Left.Split('.')
    $rightParts = $Right.Split('.')
    for ($index = 0; $index -lt [Math]::Min($leftParts.Count, $rightParts.Count); $index++) {
        $leftNumeric = $leftParts[$index] -match '^\d+$'
        $rightNumeric = $rightParts[$index] -match '^\d+$'
        if ($leftNumeric -and $rightNumeric) {
            $comparison = ([uint64]$leftParts[$index]).CompareTo([uint64]$rightParts[$index])
        }
        elseif ($leftNumeric -ne $rightNumeric) {
            $comparison = if ($leftNumeric) { -1 } else { 1 }
        }
        else {
            $comparison = [StringComparer]::Ordinal.Compare($leftParts[$index], $rightParts[$index])
        }
        if ($comparison -ne 0) { return $comparison }
    }
    return $leftParts.Count.CompareTo($rightParts.Count)
}

function Compare-SemVer([string]$Left, [string]$Right) {
    $leftVersion = Get-SemVerParts $Left
    $rightVersion = Get-SemVerParts $Right
    foreach ($property in 'Major','Minor','Patch') {
        $comparison = $leftVersion.$property.CompareTo($rightVersion.$property)
        if ($comparison -ne 0) { return $comparison }
    }
    Compare-Prerelease $leftVersion.Prerelease $rightVersion.Prerelease
}

function Assert-ReleaseEntry([object]$Entry) {
    foreach ($property in 'version','tag','commit','releasedAt','releaseUrl','documentationUrl','categories') {
        if ($null -eq $Entry.PSObject.Properties[$property]) { throw "Release entry is missing required property '$property'." }
    }
    [void](Get-SemVerParts ([string]$Entry.version))
    if ([string]$Entry.tag -ne "v$($Entry.version)") { throw "Tag '$($Entry.tag)' does not match version '$($Entry.version)'." }
    if ([string]$Entry.commit -notmatch '^[0-9a-fA-F]{40}$') { throw "Release '$($Entry.version)' has an invalid resolved commit." }
    if ([string]$Entry.releasedAt -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "Release '$($Entry.version)' has an invalid release date." }
    foreach ($property in 'releaseUrl','documentationUrl') {
        $uri = [uri][string]$Entry.$property
        if (-not (Test-PublicReleaseUri $uri)) { throw "Release '$($Entry.version)' contains a URL that is not public HTTPS: $uri" }
    }
    foreach ($category in @($Entry.categories)) {
        if ([string]::IsNullOrWhiteSpace([string]$category.name)) { throw "Release '$($Entry.version)' contains a category without a name." }
        foreach ($change in @($category.changes)) {
            if ([string]::IsNullOrWhiteSpace([string]$change.summary)) { throw "Release '$($Entry.version)' contains a change without a summary." }
            foreach ($url in @($change.documentationUrls)) {
                if (-not (Test-PublicReleaseUri ([uri][string]$url))) { throw "Release '$($Entry.version)' contains a URL that is not public HTTPS: $url" }
            }
        }
    }
}

if ($Tag -ne "v$Version") { throw "Tag '$Tag' does not match version '$Version'." }
foreach ($uri in $ReleaseUrl, $DocumentationUrl) {
    if (-not (Test-PublicReleaseUri $uri)) { throw "Release metadata URL is not public HTTPS: $uri" }
}
if (-not (Test-Path -LiteralPath $NotesPath -PathType Leaf)) { throw "Release notes file was not found: $NotesPath" }
$noteLines = @(Get-Content -LiteralPath $NotesPath)
foreach ($match in [regex]::Matches(($noteLines -join "`n"), 'https?://[^\s)>]+')) {
    $url = $match.Value
    if (-not (Test-PublicReleaseUri ([uri]$url))) { throw "Release notes contain a URL that is not public HTTPS: $url" }
}

$categories = [Collections.Generic.List[object]]::new()
$currentCategory = $null
foreach ($line in $noteLines) {
    if ($line -match '^###\s+(?<name>.+?)\s*$') {
        $name = $Matches.name -replace '<!--.*?-->','' -replace '[^\p{L}\p{N}/&+ -]',''
        $currentCategory = [pscustomobject][ordered]@{ name = $name.Trim(); changes = [Collections.Generic.List[object]]::new() }
        $categories.Add($currentCategory)
        continue
    }
    if ($null -ne $currentCategory -and $line -match '^\s*-\s+(?<summary>.+?)\s*$') {
        $summary = $Matches.summary
        $urls = [Collections.Generic.List[string]]::new()
        foreach ($match in [regex]::Matches($summary, '\[[^\]]+\]\((?<url>https?://[^)]+)\)|(?<bare>https?://[^\s)>]+)')) {
            $url = if ($match.Groups['url'].Success) { $match.Groups['url'].Value } else { $match.Groups['bare'].Value }
            $uri = [uri]$url
            if (-not (Test-PublicReleaseUri $uri)) { throw "Release notes contain a URL that is not public HTTPS: $url" }
            if ($uri.Host -eq 'sytone.github.io' -and -not $urls.Contains($url)) { $urls.Add($url) }
        }
        $currentCategory.changes.Add([pscustomobject][ordered]@{ summary = $summary; documentationUrls = @($urls) })
    }
}
$categories = @($categories | Where-Object { @($_.changes).Count -gt 0 })
if ($categories.Count -eq 0) { throw 'Release notes contain no categorized changes.' }

$newEntry = [pscustomobject][ordered]@{
    version = $Version
    tag = $Tag
    commit = $Commit.ToLowerInvariant()
    releasedAt = $ReleasedAt
    releaseUrl = $ReleaseUrl.AbsoluteUri
    documentationUrl = $DocumentationUrl.AbsoluteUri
    categories = $categories
}

$releases = [Collections.Generic.List[object]]::new()
if (-not [string]::IsNullOrWhiteSpace($ExistingManifestPath) -and (Test-Path -LiteralPath $ExistingManifestPath -PathType Leaf)) {
    $existing = Get-Content -LiteralPath $ExistingManifestPath -Raw | ConvertFrom-Json -Depth 100
    if ([string]$existing.schemaVersion -ne '1.0.0') { throw "Unsupported release history schema version '$($existing.schemaVersion)'." }
    foreach ($entry in @($existing.releases)) {
        Assert-ReleaseEntry $entry
        $releases.Add($entry)
    }
}
if (@($releases | Where-Object { $_.version -eq $Version }).Count -gt 0) { throw "Release history contains duplicate version '$Version'." }
$releases.Add($newEntry)

$duplicates = @($releases | Group-Object version | Where-Object Count -gt 1)
if ($duplicates.Count -gt 0) { throw "Release history contains duplicate version '$($duplicates[0].Name)'." }
for ($left = 0; $left -lt $releases.Count - 1; $left++) {
    for ($right = $left + 1; $right -lt $releases.Count; $right++) {
        if ((Compare-SemVer $releases[$left].version $releases[$right].version) -lt 0) {
            $temporary = $releases[$left]
            $releases[$left] = $releases[$right]
            $releases[$right] = $temporary
        }
    }
}
foreach ($entry in $releases) { Assert-ReleaseEntry $entry }

$manifest = [pscustomobject][ordered]@{ schemaVersion = '1.0.0'; releases = @($releases) }
$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
$manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
$manifest
