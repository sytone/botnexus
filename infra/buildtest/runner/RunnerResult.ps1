Set-StrictMode -Version Latest

function Get-RunnerTestResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]] $TrxPaths,

        [switch] $RequireZeroSkipped
    )

    $summary = [ordered]@{
        total = 0
        executed = 0
        passed = 0
        failed = 0
        skipped = 0
        isComplete = $false
        failureReason = $null
    }

    if ($TrxPaths.Count -eq 0) {
        $summary.failureReason = 'missing-test-results'
        return [pscustomobject]$summary
    }

    foreach ($path in $TrxPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $summary.failureReason = 'missing-test-results'
            return [pscustomobject]$summary
        }

        [xml]$trx = Get-Content -LiteralPath $path -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($null -eq $counters) {
            $summary.failureReason = 'invalid-test-results'
            return [pscustomobject]$summary
        }

        $summary.total += [int]$counters.total
        $summary.executed += [int]$counters.executed
        $summary.passed += [int]$counters.passed
        $summary.failed += [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
        $summary.skipped += [int]$counters.notExecuted + [int]$counters.notRunnable + [int]$counters.inconclusive
    }

    # A TRX summary can under-report its own skips: on 2026-08-06 the E2E project emitted
    # notExecuted=0 while the file carried 265 results with outcome="NotExecuted", so the
    # gate reported failed=0/skipped=0 and exited 0 having actually run 15 of 280 tests.
    # total vs executed is the only counter pair that exposes that, so it is authoritative:
    # a test that was neither executed nor deliberately skipped has NOT been validated, and
    # certifying it green is worse than reporting a failure.
    $unaccounted = $summary.total - $summary.executed - $summary.skipped
    if ($unaccounted -gt 0) { $summary.skipped += $unaccounted }

    if ($summary.total -eq 0 -or $summary.executed -eq 0) {
        $summary.failureReason = 'no-tests-executed'
    }
    elseif ($summary.failed -gt 0) {
        $summary.failureReason = 'test-failures'
    }
    elseif ($summary.executed -lt $summary.total -and -not $RequireZeroSkipped) {
        $summary.failureReason = 'tests-not-executed'
    }
    elseif ($RequireZeroSkipped -and $summary.skipped -gt 0) {
        $summary.failureReason = 'unexpected-skips'
    }
    else {
        $summary.isComplete = $true
    }

    [pscustomobject]$summary
}
