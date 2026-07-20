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
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$slnxPath = Join-Path $repoRoot 'BotNexus.slnx'
$firewallHelper = Join-Path $PSScriptRoot 'Ensure-TesthostFirewallRules.ps1'

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

# Enumerate every test project in the solution (used for -All and safety-net).
function Get-AllSolutionTestProjects {
    [xml]$slnxDoc = Get-Content $slnxPath -Raw
    $projects = @()
    foreach ($node in $slnxDoc.SelectNodes('//Project[@Path]')) {
        $path = $node.Path -replace '\\', '/'
        if ($path -match '\.Tests\.csproj$') {
            $projects += (Join-Path $repoRoot ($path -replace '/', [IO.Path]::DirectorySeparatorChar))
        }
    }
    return $projects
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
Write-Host "Analyzing affected projects (comparing against: $From)..." -ForegroundColor Cyan

$outputDir = Join-Path $repoRoot '.affected'
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$affectedProjects = @()

# Check committed changes against base ref
$affectedOutput = dotnet affected --from $From -f text --output-dir $outputDir --output-name affected-branch 2>&1
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
# Find all test projects in the solution matching safety-net patterns
[xml]$slnx = Get-Content $slnxPath -Raw
$allTestProjects = @()
$projectNodes = $slnx.SelectNodes('//Project[@Path]')
foreach ($node in $projectNodes) {
    $path = $node.Path -replace '\\', '/'
    if ($path -match '\.Tests\.csproj$') {
        $allTestProjects += (Join-Path $repoRoot ($path -replace '/', [IO.Path]::DirectorySeparatorChar))
    }
}

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
try {
    Invoke-FirewallAction -Projects $projectsToTest -Action Ensure -LeasePath $leasePath
    foreach ($proj in $projectsToTest) {
        $name = [IO.Path]::GetFileNameWithoutExtension($proj)
        Write-Host "Testing: $name" -ForegroundColor White
        dotnet test $proj --nologo --tl:off -c $Configuration $buildFlag
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
