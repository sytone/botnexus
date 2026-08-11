<#
.SYNOPSIS
    Runs only the test projects affected by changes since a given git ref.

.DESCRIPTION
    Uses dotnet-affected (which leverages MSBuild's ProjectGraph) to determine
    which projects are transitively affected by changes. Only affected test
    projects are run, plus architecture and scenario tests as a safety net.

    This provides Test Impact Analysis (TIA) without requiring prior coverage
    data — it works on fresh worktrees and build agents from the first run.

    Requires: dotnet tool restore (installs dotnet-affected from dotnet-tools.json)

.PARAMETER From
    The git ref to diff against. Defaults to 'origin/main'.

.PARAMETER Configuration
    Build configuration. Defaults to 'Debug'.

.PARAMETER All
    If set, skips impact analysis and runs the full test suite (same as test.ps1).

.PARAMETER NoBuild
    If set, passes --no-build to dotnet test (use when already built).

.PARAMETER DryRun
    If set, prints which test projects would run without executing them.

.EXAMPLE
    # Run only tests affected by changes on the current branch
    .\scripts\repo\test-impacted.ps1

.EXAMPLE
    # Diff against a specific commit
    .\scripts\repo\test-impacted.ps1 -From "HEAD~3"

.EXAMPLE
    # See what would run without executing
    .\scripts\repo\test-impacted.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$From = 'origin/main',
    [string]$Configuration = 'Debug',
    [switch]$All,
    [switch]$NoBuild,
    [switch]$DryRun,

    # #2825: when set, each project emits a TRX here so the remote runner can parse real
    # counters. Omitted for local runs, which read pass/fail from the exit code alone.
    [string]$ResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$slnxPath = Join-Path $repoRoot 'dirs.proj'
$firewallHelper = Join-Path $PSScriptRoot 'Ensure-TesthostFirewallRules.ps1'

# #2785: freshness guards for the two cached inputs this script consumes without ever
# having checked them - the compiled test assemblies (run with --no-build) and the base
# ref (used to compute the impacted set).
Import-Module (Join-Path $PSScriptRoot 'ValidationFreshness.psm1') -Force

function Invoke-FirewallAction {
    param(
        [string[]]$Projects,
        [ValidateSet('Ensure', 'Cleanup')]
        [string]$Action,
        [string]$LeasePath
    )

    try {
        & $firewallHelper -ProjectPath $Projects -Configuration $Configuration -Action $Action -LeasePath $LeasePath
    }
    catch {
        Write-Warning "Testhost firewall $($Action.ToLowerInvariant()) skipped: $($_.Exception.Message)"
    }
}

function Invoke-FullTestSuite {
    param([string[]]$Projects)

    $arguments = @('test', $slnxPath, '--nologo', '--tl:off', '-c', $Configuration)
    if ($NoBuild) { $arguments += '--no-build' }
    $leasePath = Join-Path ([IO.Path]::GetTempPath()) ("botnexus-fw-lease-{0}" -f [guid]::NewGuid().ToString('N'))
    $exitCode = 1
    try {
        Invoke-FirewallAction -Projects $Projects -Action Ensure -LeasePath $leasePath
        & dotnet @arguments | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        Invoke-FirewallAction -Projects $Projects -Action Cleanup -LeasePath $leasePath
    }
    return $exitCode
}

# Projects that always run regardless of what changed (cross-cutting safety net)
$alwaysRunPatterns = @(
    '\.Architecture\.Tests'
    '\.Scenarios\.Tests'
)

# Enumerate every test project (used for -All and safety-net).
# #2842: discovered from disk to match tests/dirs.proj rather than parsing BotNexus.slnx,
# which was a second hand-maintained spelling of the same set.
function Get-AllSolutionTestProjects {
    return @(Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Filter '*.Tests.csproj' -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Select-Object -ExpandProperty FullName |
        Sort-Object)
}

if ($All) {
    Write-Host "Running full test suite (--All specified)" -ForegroundColor Cyan
    $allProjects = Get-AllSolutionTestProjects
    $exitCode = Invoke-FullTestSuite -Projects $allProjects
    exit $exitCode
}

# --- Step 1: Ensure dotnet-affected is available ---
Write-Host "Restoring dotnet tools..." -ForegroundColor DarkGray
dotnet tool restore --nologo 2>&1 | Out-Null

# --- Step 2: Run dotnet-affected to get affected project list ---
# Run for committed changes (branch diff) AND uncommitted changes (working dir)
#
# #2785 AC4/AC5: `$From` is a CACHE. It defaulted to 'origin/main' and nothing ever fetched
# it, so the impacted set was derived from whatever that ref last pointed at in this
# checkout - observed 7 commits stale. Worse, `--from <tip>` is a two-dot diff, so every
# commit that landed on the base after the branch forked enters the change set: a true
# 10-file diff was reported as 26 files across projects the branch never touched. The
# opposite direction is the dangerous one - a stale base can OMIT projects a change
# genuinely impacts, and those tests then never run.
#
# Resolving to the merge-base makes the change set independent of later base commits by
# construction, and the fetch means it is not computed from a cache. A fetch failure is not
# fatal (offline / no auth is a legitimate state) but it is reported, never silent.
$baseResolution = Resolve-BotNexusValidationBaseRef -RepoRoot $repoRoot -BaseRef $From
if (-not $baseResolution.Fetched -and $null -ne $baseResolution.FetchError) {
    Write-Warning "Could not refresh '$From' before computing the impacted set; using the cached ref. $($baseResolution.FetchError)"
}
if ($baseResolution.IsStale) {
    Write-Host "Base ref '$From' is $($baseResolution.BehindCount) commit(s) ahead of HEAD's fork point; diffing from the merge-base $($baseResolution.MergeBase.Substring(0, 8)) so unrelated base commits cannot enter the impacted set." -ForegroundColor Yellow
}
$diffFrom = $baseResolution.MergeBase

Write-Host "Analyzing affected projects (comparing against: $From @ merge-base $($diffFrom.Substring(0, 8)))..." -ForegroundColor Cyan

$outputDir = Join-Path $repoRoot '.affected'
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$affectedProjects = @()

# Check committed changes against base ref
$affectedOutput = dotnet affected --from $diffFrom -f text --output-dir $outputDir --output-name affected-branch 2>&1
$branchExitCode = $LASTEXITCODE

if ($branchExitCode -eq 0) {
    $branchFile = Join-Path $outputDir 'affected-branch.txt'
    if (Test-Path $branchFile) {
        $affectedProjects += @(Get-Content $branchFile | Where-Object { $_ })
    }
}
elseif ($branchExitCode -ne 166) {
    Write-Warning "dotnet-affected failed (exit $branchExitCode) — falling back to full test suite"
    Write-Host ($affectedOutput -join "`n") -ForegroundColor DarkGray
    $allProjects = Get-AllSolutionTestProjects
    $exitCode = Invoke-FullTestSuite -Projects $allProjects
    exit $exitCode
}

# Also check uncommitted/staged changes (HEAD vs working directory)
$uncommittedOutput = dotnet affected -f text --output-dir $outputDir --output-name affected-local 2>&1
$localExitCode = $LASTEXITCODE

if ($localExitCode -eq 0) {
    $localFile = Join-Path $outputDir 'affected-local.txt'
    if (Test-Path $localFile) {
        $affectedProjects += @(Get-Content $localFile | Where-Object { $_ })
    }
}

# Deduplicate
$affectedProjects = @($affectedProjects | Sort-Object -Unique)

if ($affectedProjects.Count -eq 0) {
    Write-Host "No projects affected by changes." -ForegroundColor Green
    Write-Host "Running safety-net tests only..." -ForegroundColor Cyan
}

# Filter to only test projects
$affectedTestProjects = @($affectedProjects | Where-Object { $_ -match '\.Tests[/\\]' -or $_ -match '\.Tests\.csproj$' })

# --- Step 4: Always include safety-net projects ---
# Find all test projects matching safety-net patterns (#2842: reuse the single discovery
# function rather than re-parsing the graph a second way).
$allTestProjects = @(Get-AllSolutionTestProjects)

$safetyNetProjects = @()
foreach ($proj in $allTestProjects) {
    foreach ($pattern in $alwaysRunPatterns) {
        if ($proj -match $pattern) {
            $safetyNetProjects += $proj
            break
        }
    }
}

# Merge affected test projects with safety-net (deduplicated)
$projectsToTest = @($affectedTestProjects + $safetyNetProjects | Sort-Object -Unique)

# --- Step 5: Report and execute ---
if ($projectsToTest.Count -eq 0) {
    Write-Host "No test projects to run." -ForegroundColor Green
    exit 0
}

Write-Host "`nTest projects to run ($($projectsToTest.Count) of $($allTestProjects.Count) total):" -ForegroundColor Cyan
foreach ($p in $projectsToTest) {
    $name = [IO.Path]::GetFileNameWithoutExtension($p)
    $isSafetyNet = $alwaysRunPatterns | Where-Object { $p -match $_ }
    $label = if ($isSafetyNet) { " (always-run)" } else { "" }
    Write-Host "  - $name$label" -ForegroundColor DarkGray
}

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would run $($projectsToTest.Count) test projects." -ForegroundColor Yellow
    exit 0
}

# Run tests
Write-Host ""
$buildFlag = if ($NoBuild) { '--no-build' } else { '--no-restore' }
$leasePath = Join-Path ([IO.Path]::GetTempPath()) ("botnexus-fw-lease-{0}" -f [guid]::NewGuid().ToString('N'))
$failed = $false

# #2785 AC1/AC2/AC6: refuse to run an assembly older than the commit under validation.
# `dotnet test --no-build` does NOT fail when the assembly on disk predates the source;
# it silently runs the stale .dll and reports a verdict about code it never compiled.
# Observed live: 564 tests / 3 failed from a 15-minute-old assembly where a forced-clean
# run of the same commit gave 591 / 0. The false-green direction is the severe one.
# Deliberately OUTSIDE the try below: bailing here must not trigger firewall cleanup for a
# lease that was never acquired.
if ($NoBuild) {
    $commitTimeRaw = & git -C $repoRoot show -s --format=%cI HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($commitTimeRaw)) {
        $commitTimeUtc = [DateTimeOffset]::Parse("$commitTimeRaw".Trim()).UtcDateTime
        $freshness = Assert-BotNexusTestAssemblyFreshness -ProjectPath $projectsToTest -Configuration $Configuration -ReferenceTimeUtc $commitTimeUtc
        if (-not $freshness.IsFresh) {
            Write-Host ""
            Write-Host $freshness.Message -ForegroundColor Red
            exit 1
        }
        Write-Host $freshness.Message -ForegroundColor DarkGray
    }
    else {
        Write-Warning 'Could not read the HEAD commit timestamp; skipping the #2785 stale-assembly guard.'
    }
}

try {
    Invoke-FirewallAction -Projects $projectsToTest -Action Ensure -LeasePath $leasePath
    foreach ($proj in $projectsToTest) {
        $name = [IO.Path]::GetFileNameWithoutExtension($proj)
        Write-Host "Testing: $name" -ForegroundColor White
        # #2825: emit a TRX per project when a results directory is supplied. Without a
        # logger the runner's Get-RunnerTestResult finds no TRX and reports zeroed counters
        # with failureReason 'missing-test-results' even for a suite that visibly ran and
        # passed - a gate that cannot report what it executed is not a gate.
        $loggerArgs = @()
        if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
            $loggerArgs = @('--logger', "trx;LogFilePrefix=$name", '--results-directory', $ResultsDirectory)
        }
        dotnet test $proj --nologo --tl:off -c $Configuration $buildFlag @loggerArgs
        if ($LASTEXITCODE -ne 0) { $failed = $true }
    }
}
finally {
    Invoke-FirewallAction -Projects $projectsToTest -Action Cleanup -LeasePath $leasePath
}

# Cleanup
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }

if ($failed) {
    Write-Host "`nSome tests failed." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "`nAll impacted tests passed." -ForegroundColor Green
    exit 0
}
