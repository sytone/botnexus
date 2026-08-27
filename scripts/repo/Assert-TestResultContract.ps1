#requires -Version 7.0
<#
.SYNOPSIS
    Applies the remote container gate's fail-closed test-result contract to a CI test run.

.DESCRIPTION
    WHY THIS EXISTS (#3602).

    Until this script, CI and the remote container gate ran the SAME tests but judged them by
    DIFFERENT standards, and only one of those standards is trustworthy.

    CI trusted the `dotnet test` exit code. That exit code answers exactly one question -- "did
    any test that ran report a failure?" -- and is silent on the question that actually matters:
    "did the tests run at all?" A run that discovers nothing, skips everything on a crashed
    fixture, or emits no TRX exits 0 and paints the check green.

    That is not hypothetical. On 2026-08-06 the E2E project emitted notExecuted=0 while carrying
    265 result rows with outcome="NotExecuted": 15 of 280 tests actually executed and the gate
    exited 0. The container grew `RunnerResult.ps1` in response (#2851), which fails CLOSED on
    unrecognised skip reasons, uncorroborated counters, arithmetically impossible counters and a
    collapsed total. CI never got that treatment, so a PR could be certified green by a standard
    the repo had already concluded was inadequate.

    This script closes that gap by DOT-SOURCING the container's contract rather than
    reimplementing it. That is the entire point: `entrypoint.ps1` already warns that three
    spellings of one scope is how they drift apart, and the core filter has to be maintained
    identically in three places today. A second implementation of the result contract would
    reproduce that problem in a place where the consequence is a silently weaker gate. One
    definition, two callers.

.PARAMETER ResultsDirectory
    Directory containing the TRX files emitted by `dotnet test --logger trx`.

.PARAMETER MinimumTotal
    Floor for the total test count. A collapsed run is invisible to a pass/fail check -- one
    passing test satisfies "zero failed" exactly as well as 17,000 do -- so a filter typo or a
    project that silently stopped being discovered would otherwise certify green. Set well below
    observed counts so ordinary suite growth never trips it.

.PARAMETER SummaryPath
    Optional path to write the contract verdict as JSON, for artifact upload.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [int] $MinimumTotal = 0,
    [string] $SummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The contract itself lives with the runner it was written for. Dot-sourced, never copied.
$contractScript = Join-Path $PSScriptRoot '../../infra/buildtest/runner/RunnerResult.ps1'
if (-not (Test-Path -LiteralPath $contractScript -PathType Leaf)) {
    throw "Test-result contract not found at $contractScript. CI cannot validate a run it cannot judge."
}
. $contractScript

if (-not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) {
    # An absent results directory is itself a finding: the test step either never ran or never
    # emitted TRX. Failing here is the fail-closed behaviour this script exists to provide.
    Write-Host "::error::No test results directory at $ResultsDirectory -- the test run produced nothing to validate."
    exit 1
}

$trxPaths = @(
    Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
)

$result = Get-RunnerTestResult -TrxPaths $trxPaths -RequireZeroSkipped -MinimumTotal $MinimumTotal

$summary = [ordered]@{
    total           = $result.total
    executed        = $result.executed
    passed          = $result.passed
    failed          = $result.failed
    skipped         = $result.skipped
    fixtureFailures = $result.fixtureFailures
    isComplete      = $result.isComplete
    failureReason   = $result.failureReason
    minimumTotal    = $MinimumTotal
    trxFileCount    = $trxPaths.Count
}

if ($SummaryPath) {
    try {
        $parent = Split-Path -Parent $SummaryPath
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $SummaryPath
    }
    catch {
        # A diagnostic must never be able to change the verdict it is describing.
        Write-Host "::warning::Could not write contract summary to ${SummaryPath}: $($_.Exception.Message)"
    }
}

$line = "total=$($result.total) executed=$($result.executed) passed=$($result.passed) " +
        "failed=$($result.failed) skipped=$($result.skipped) " +
        "fixtureFailures=$($result.fixtureFailures) trxFiles=$($trxPaths.Count)"

if ($result.isComplete) {
    Write-Host "Test-result contract satisfied: $line"
    exit 0
}

Write-Host "::error::Test-result contract REJECTED this run: $($result.failureReason). $line (minimumTotal=$MinimumTotal)"
exit 1
