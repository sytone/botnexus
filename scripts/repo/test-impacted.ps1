<#
.SYNOPSIS
    Runs only the test projects affected by changes since a given git ref.

.DESCRIPTION
    Uses the MSBuild project dependency graph to determine which test projects
    transitively depend on changed source projects. Only those tests are run,
    plus architecture and scenario tests which always run as a safety net.

    This provides Test Impact Analysis (TIA) without requiring prior coverage
    data — it works on fresh worktrees and build agents from the first run.

.PARAMETER BaseBranch
    The git ref to diff against. Defaults to 'origin/main'.

.PARAMETER Configuration
    Build configuration. Defaults to 'Debug'.

.PARAMETER All
    If set, skips impact analysis and runs the full test suite (same as test.ps1).

.PARAMETER DryRun
    If set, prints which test projects would run without executing them.

.EXAMPLE
    # Run only tests affected by changes on the current branch
    .\scripts\repo\test-impacted.ps1

.EXAMPLE
    # Diff against a specific commit
    .\scripts\repo\test-impacted.ps1 -BaseBranch "HEAD~3"

.EXAMPLE
    # See what would run without executing
    .\scripts\repo\test-impacted.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$BaseBranch = 'origin/main',
    [string]$Configuration = 'Debug',
    [switch]$All,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$slnxPath = Join-Path $repoRoot 'BotNexus.slnx'

# Projects that always run regardless of what changed (cross-cutting safety net)
$alwaysRunPatterns = @(
    'BotNexus.Architecture.Tests'
    'BotNexus.Scenarios.Tests'
)

if ($All) {
    Write-Host "Running full test suite (--All specified)" -ForegroundColor Cyan
    dotnet test $slnxPath --nologo --tl:off -c $Configuration
    exit $LASTEXITCODE
}

# --- Step 1: Determine changed files ---
Write-Host "Comparing against: $BaseBranch" -ForegroundColor Cyan

$changedFiles = git -C $repoRoot diff --name-only $BaseBranch -- 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "git diff failed — falling back to full test suite"
    dotnet test $slnxPath --nologo --tl:off -c $Configuration
    exit $LASTEXITCODE
}

# Include uncommitted changes
$uncommitted = git -C $repoRoot diff --name-only 2>$null
$staged = git -C $repoRoot diff --name-only --cached 2>$null
$changedFiles = @($changedFiles) + @($uncommitted) + @($staged) | Sort-Object -Unique | Where-Object { $_ }
$changedFiles = @($changedFiles)

if ($changedFiles.Count -eq 0) {
    Write-Host "No changes detected — nothing to test." -ForegroundColor Green
    exit 0
}

Write-Host "Changed files: $($changedFiles.Count)" -ForegroundColor Cyan

# --- Step 2: Identify changed source projects ---
# Parse the slnx to get all project paths
[xml]$slnx = Get-Content $slnxPath -Raw
$allProjects = @()
$projectNodes = $slnx.SelectNodes('//Project[@Path]')
foreach ($node in $projectNodes) {
    $allProjects += ($node.Path -replace '\\', '/')
}

# Find source projects whose directory contains a changed file
$changedSourceProjects = @()
foreach ($proj in $allProjects) {
    if ($proj -match '^tests/') { continue }
    $projDir = ($proj | Split-Path -Parent) -replace '\\', '/'
    foreach ($file in $changedFiles) {
        $normalizedFile = $file -replace '\\', '/'
        if ($normalizedFile.StartsWith("$projDir/")) {
            $changedSourceProjects += $proj
            break
        }
    }
}

# Also detect changes to test projects themselves (new tests, refactored tests)
$changedTestProjects = @()
foreach ($proj in $allProjects) {
    if ($proj -notmatch '^tests/') { continue }
    $projDir = ($proj | Split-Path -Parent) -replace '\\', '/'
    foreach ($file in $changedFiles) {
        $normalizedFile = $file -replace '\\', '/'
        if ($normalizedFile.StartsWith("$projDir/")) {
            $changedTestProjects += $proj
            break
        }
    }
}

Write-Host "Changed source projects: $($changedSourceProjects.Count)" -ForegroundColor Cyan
foreach ($p in $changedSourceProjects) { Write-Host "  - $p" -ForegroundColor DarkGray }

# --- Step 3: Build dependency map (test project → source project references) ---
# Walk ProjectReference chains to find which test projects depend on changed source projects

function Get-TransitiveDependencies {
    param([string]$CsprojPath, [hashtable]$Cache)

    $fullPath = Join-Path $repoRoot ($CsprojPath -replace '/', [IO.Path]::DirectorySeparatorChar)
    if ($Cache.ContainsKey($fullPath)) { return ,$Cache[$fullPath] }

    $deps = [System.Collections.Generic.HashSet[string]]::new()
    $Cache[$fullPath] = $deps

    if (-not (Test-Path $fullPath)) { return ,$deps }

    $content = Get-Content $fullPath -Raw
    $regex = [regex]'<ProjectReference\s+Include="([^"]+)"'
    $refMatches = $regex.Matches($content)

    foreach ($m in $refMatches) {
        $refRelative = $m.Groups[1].Value
        $refAbsolute = [IO.Path]::GetFullPath(
            (Join-Path (Split-Path $fullPath) $refRelative)
        )
        # Normalize to repo-relative with forward slashes
        $repoRelative = $refAbsolute.Substring($repoRoot.Length + 1) -replace '\\', '/'
        [void]$deps.Add($repoRelative)

        # Recurse into transitive deps
        $transitive = Get-TransitiveDependencies -CsprojPath $repoRelative -Cache $Cache
        if ($null -ne $transitive) {
            foreach ($t in $transitive) { [void]$deps.Add($t) }
        }
    }

    return ,$deps
}

$depCache = @{}
$impactedTestProjects = [System.Collections.Generic.HashSet[string]]::new()

# All test projects from the solution (exclude harness/helper libraries)
$testProjects = $allProjects | Where-Object { $_ -match '^tests/' -and $_ -match '\.Tests\.csproj$' }

foreach ($testProj in $testProjects) {
    # Check if this test project directly changed
    if ($changedTestProjects -contains $testProj) {
        [void]$impactedTestProjects.Add($testProj)
        continue
    }

    # Check if any transitive dependency is a changed source project
    $deps = Get-TransitiveDependencies -CsprojPath $testProj -Cache $depCache
    if ($null -ne $deps) {
        foreach ($changedSrc in $changedSourceProjects) {
            if ($deps.Contains($changedSrc)) {
                [void]$impactedTestProjects.Add($testProj)
                break
            }
        }
    }
}

# --- Step 4: Always include safety-net projects ---
foreach ($testProj in $testProjects) {
    foreach ($pattern in $alwaysRunPatterns) {
        if ($testProj -match [regex]::Escape($pattern)) {
            [void]$impactedTestProjects.Add($testProj)
            break
        }
    }
}

# --- Step 5: Handle shared infrastructure changes ---
# If Directory.Build.props, Directory.Packages.props, or global.json changed, run everything
$infrastructureFiles = @(
    'Directory.Build.props'
    'Directory.Packages.props'
    'global.json'
    'tests/Directory.Build.props'
    'tests/Directory.Build.targets'
)
$infraChanged = $changedFiles | Where-Object { ($_ -replace '\\', '/') -in $infrastructureFiles }
if ($infraChanged) {
    Write-Host "Build infrastructure changed — running full test suite" -ForegroundColor Yellow
    if ($DryRun) {
        Write-Host "[DRY RUN] Would run: dotnet test $slnxPath" -ForegroundColor Yellow
        exit 0
    }
    dotnet test $slnxPath --nologo --tl:off -c $Configuration
    exit $LASTEXITCODE
}

# --- Step 6: Run impacted tests ---
$sortedProjects = $impactedTestProjects | Sort-Object

if ($sortedProjects.Count -eq 0) {
    Write-Host "No test projects impacted by changes." -ForegroundColor Green
    exit 0
}

Write-Host "`nTest projects to run ($($sortedProjects.Count) of $($testProjects.Count) total):" -ForegroundColor Cyan
foreach ($p in $sortedProjects) {
    $isSafetyNet = $alwaysRunPatterns | Where-Object { $p -match [regex]::Escape($_) }
    $label = if ($isSafetyNet) { " (always-run)" } else { "" }
    Write-Host "  - $p$label" -ForegroundColor DarkGray
}

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would run $($sortedProjects.Count) test projects." -ForegroundColor Yellow
    exit 0
}

Write-Host ""
$failed = $false
foreach ($proj in $sortedProjects) {
    $projFullPath = Join-Path $repoRoot ($proj -replace '/', [IO.Path]::DirectorySeparatorChar)
    Write-Host "Testing: $proj" -ForegroundColor White
    dotnet test $projFullPath --nologo --tl:off -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

if ($failed) {
    Write-Host "`nSome tests failed." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "`nAll impacted tests passed." -ForegroundColor Green
    exit 0
}
