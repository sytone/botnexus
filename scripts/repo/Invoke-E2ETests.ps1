<#
.SYNOPSIS
    Runs the BotNexus end-to-end (Playwright) test project and fails a vacuously
    green run.

.DESCRIPTION
    Issue #2739. The E2E collection fixture prebuilds the solution, and when that
    prebuild lost a race against a concurrently running test host every class in
    the collection degraded into a silent Skip. `dotnet test` still printed
    "Passed!" and exited 0 off 265 skips, so the gate reported success having
    verified essentially nothing.

    The fixture race itself is fixed in NewUserExperienceFixture
    (EnsureSolutionBuiltAsync serialises the prebuild behind a machine-wide
    mutex) and asserted by FixtureHealthTests.FixtureInitializationSucceeded.
    This wrapper is the belt-and-braces half: whatever the cause, a run whose
    skip count meets or exceeds its passed count is not evidence of anything and
    is reported as a FAILURE, not as "Passed!".
#>
[CmdletBinding()]
param(
    [string]$Project = 'tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj',
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [string]$Filter
)

$ErrorActionPreference = 'Stop'

$dotnetArgs = @('test', $Project, '--nologo', '--tl:off', '-c', $Configuration)
if ($NoBuild) { $dotnetArgs += '--no-build' }
if ($Filter) { $dotnetArgs += @('--filter', $Filter) }

Write-Host "dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan

$output = & dotnet @dotnetArgs 2>&1 | ForEach-Object {
    $line = $_.ToString()
    Write-Host $line
    $line
}
$testExit = $LASTEXITCODE

# `dotnet test` summary line, e.g.
#   Failed! - Failed: 54, Passed: 198, Skipped: 28, Total: 280, Duration: 12m
$passed = 0
$failed = 0
$skipped = 0
foreach ($line in $output) {
    $m = [regex]::Match($line, 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)')
    if ($m.Success) {
        $failed += [int]$m.Groups[1].Value
        $passed += [int]$m.Groups[2].Value
        $skipped += [int]$m.Groups[3].Value
    }
}

Write-Host ''
Write-Host "E2E totals: passed=$passed failed=$failed skipped=$skipped (dotnet test exit $testExit)" -ForegroundColor Cyan

# Surface the actual skip REASONS. A skip caused by a missing Playwright browser
# and a skip caused by a failed fixture read identically in the summary line, and
# conflating them is how #2739 stayed invisible.
$reasons = $output | Select-String -Pattern 'Fixture initialization failed|Solution prebuild exit|Skipped\s+\S+' |
    Select-Object -First 20
if ($reasons) {
    Write-Host 'Skip/diagnostic lines:' -ForegroundColor DarkYellow
    $reasons | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
}

if ($testExit -ne 0) {
    Write-Host 'E2E suite FAILED (dotnet test reported failures).' -ForegroundColor Red
    exit $testExit
}

if ($passed -eq 0) {
    Write-Host 'E2E suite FAILED: zero tests passed. A run that verified nothing is not a green run (issue #2739).' -ForegroundColor Red
    exit 1
}

if ($skipped -ge $passed) {
    Write-Host "E2E suite FAILED: skipped ($skipped) >= passed ($passed). This is the vacuous-green signature of issue #2739 - the harness degraded into skips instead of testing." -ForegroundColor Red
    exit 1
}

Write-Host "E2E suite passed with a non-trivial result: $passed passed, $skipped skipped." -ForegroundColor Green
exit 0
