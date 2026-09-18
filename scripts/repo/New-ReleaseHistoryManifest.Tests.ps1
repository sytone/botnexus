[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'New-ReleaseHistoryManifest.ps1'
$failures = [Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

function Invoke-ManifestGenerator {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Commit,
        [string]$Notes = "## [0.10.0] - 2026-09-17`n`n### ✨ Features`n`n- Add [public capability docs](https://sytone.github.io/botnexus/user-guide/agents)`n`n### 🐛 Bug Fixes`n`n- Fix release selection (#4020)`n",
        [string]$ExistingManifest
    )

    $notesPath = Join-Path $Root 'notes.md'
    $outputPath = Join-Path $Root 'release-history.json'
    Set-Content -LiteralPath $notesPath -Value $Notes -Encoding utf8NoBOM

    $arguments = @{
        Version = $Version
        Tag = "v$Version"
        Commit = $Commit
        ReleasedAt = '2026-09-17'
        NotesPath = $notesPath
        OutputPath = $outputPath
        ReleaseUrl = "https://github.com/sytone/botnexus/releases/tag/v$Version"
        DocumentationUrl = "https://sytone.github.io/botnexus/releases/v$Version/"
    }
    if ($ExistingManifest) { $arguments.ExistingManifestPath = $ExistingManifest }

    & $scriptPath @arguments | Out-Null
    Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json -Depth 50
}

$root = Join-Path ([IO.Path]::GetTempPath()) "release-history-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $commit10 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $manifest = Invoke-ManifestGenerator -Root $root -Version '0.10.0' -Commit $commit10
    Assert-True ($manifest.schemaVersion -eq '1.0.0') 'Manifest schema version must be 1.0.0.'
    Assert-True (@($manifest.releases).Count -eq 1) 'Manifest must contain the generated release.'
    Assert-True ($manifest.releases[0].version -eq '0.10.0') 'Release version must be canonical SemVer without the v prefix.'
    Assert-True ($manifest.releases[0].tag -eq 'v0.10.0') 'Release tag must match the version.'
    Assert-True ($manifest.releases[0].commit -eq $commit10) 'Release commit must preserve the resolved source identity.'
    Assert-True (@($manifest.releases[0].categories).Count -eq 2) 'Canonical note categories must be preserved.'
    Assert-True ($manifest.releases[0].categories[0].name -eq 'Features') 'Category decorations must normalize to a stable name.'
    Assert-True ($manifest.releases[0].categories[0].changes[0].documentationUrls[0] -eq 'https://sytone.github.io/botnexus/user-guide/agents') 'Public capability documentation links must be retained.'

    $existingPath = Join-Path $root 'existing.json'
    $existing = @{
        schemaVersion = '1.0.0'
        releases = @(@{
            version = '0.9.0'
            tag = 'v0.9.0'
            commit = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
            releasedAt = '2026-09-16'
            releaseUrl = 'https://github.com/sytone/botnexus/releases/tag/v0.9.0'
            documentationUrl = 'https://sytone.github.io/botnexus/releases/v0.9.0/'
            categories = @(@{ name = 'Features'; changes = @(@{ summary = 'Earlier release'; documentationUrls = @() }) })
        })
    }
    $existing | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $existingPath -Encoding utf8NoBOM
    $ordered = Invoke-ManifestGenerator -Root $root -Version '0.10.0' -Commit $commit10 -ExistingManifest $existingPath
    Assert-True ((@($ordered.releases.version) -join ',') -eq '0.10.0,0.9.0') 'Releases must use semantic newest-first ordering.'

    try {
        Invoke-ManifestGenerator -Root $root -Version '0.9.0' -Commit $commit10 -ExistingManifest $existingPath | Out-Null
        $failures.Add('Duplicate versions must be rejected.')
    }
    catch {
        Assert-True ($_.Exception.Message -match 'duplicate') 'Duplicate rejection must name the duplicate version.'
    }

    try {
        Invoke-ManifestGenerator -Root $root -Version '0.10.0' -Commit $commit10 -Notes "### Features`n`n- Read [internal notes](http://localhost:5005/private)`n" | Out-Null
        $failures.Add('Internal-only URLs must be rejected.')
    }
    catch {
        Assert-True ($_.Exception.Message -match 'public HTTPS') 'Unsafe-link rejection must describe the public HTTPS contract.'
    }

    try {
        Invoke-ManifestGenerator -Root $root -Version '0.10.0' -Commit $commit10 -Notes "See http://localhost:5005/private.`n`n### Features`n`n- Safe summary`n" | Out-Null
        $failures.Add('Internal-only URLs outside list items must be rejected.')
    }
    catch {
        Assert-True ($_.Exception.Message -match 'public HTTPS') 'Whole-note URL validation must reject unsafe prose links.'
    }

    try {
        & $scriptPath -Version '0.10.0' -Tag 'v0.9.0' -Commit $commit10 -ReleasedAt '2026-09-17' -NotesPath (Join-Path $root 'notes.md') -OutputPath (Join-Path $root 'mismatch.json') -ReleaseUrl 'https://github.com/sytone/botnexus/releases/tag/v0.10.0' -DocumentationUrl 'https://sytone.github.io/botnexus/releases/v0.10.0/' | Out-Null
        $failures.Add('Mismatched release identities must be rejected.')
    }
    catch {
        Assert-True ($_.Exception.Message -match 'Tag.*version') 'Identity mismatch must name the tag/version contract.'
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host 'Release history manifest tests passed.' -ForegroundColor Green
exit 0
