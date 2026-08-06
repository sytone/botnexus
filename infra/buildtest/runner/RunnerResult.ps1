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
        fixtureFailures = 0
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
    # A summary-only skip carries no per-test reason at all, so nothing distinguishes it
    # from a crashed fixture. Absent evidence, treat it as unproven rather than honest:
    # only a stated reason earns a pass.
    $unaccounted = $summary.total - $summary.executed - $summary.skipped
    if ($unaccounted -gt 0) { $summary.skipped += $unaccounted }
    $reasonedSkips = 0

    # A test that did not run is only acceptable if it SAID WHY. xUnit reports both cases
    # as NotExecuted: a deliberate Skip ("GITHUB_TOKEN not set", "Windows-only test") and a
    # crashed fixture, which marks every dependent test NotExecuted with a message like
    # "Fixture failed: Solution prebuild exit 1". The second is a BUILD FAILURE wearing a
    # skip's clothing - on 2026-08-06 it hid 265 unrun E2E tests behind failed=0 and exit 0.
    # So classify by reason, not by count: fixture/collection failures are failures.
    $fixtureFailurePattern = 'Fixture (failed|initialization failed)|One or more errors occurred|could not be (created|constructed)|Class fixture|Collection fixture'
    foreach ($path in $TrxPaths) {
        if (-not (Test-Path $path)) { continue }
        $document = [xml](Get-Content -Path $path -Raw)
        $unitResults = $document.TestRun.PSObject.Properties['Results']
        if (-not $unitResults -or -not $document.TestRun.Results) { continue }
        foreach ($unit in @($document.TestRun.Results.UnitTestResult)) {
            if (-not $unit -or $unit.GetAttribute('outcome') -ne 'NotExecuted') { continue }
            $reason = $unit.Output.ErrorInfo.Message
            if ([string]::IsNullOrWhiteSpace($reason) -or $reason -match $fixtureFailurePattern) {
                $summary.fixtureFailures++
            }
            else { $reasonedSkips++ }
        }
    }

    # Skips that no result row explained: the TRX asserted them in its counters but never
    # said why. Unexplained is not the same as acceptable.
    $unexplained = $summary.skipped - $reasonedSkips - $summary.fixtureFailures
    if ($unexplained -gt 0) { $summary.fixtureFailures += $unexplained }

    if ($summary.total -eq 0 -or $summary.executed -eq 0) {
        $summary.failureReason = 'no-tests-executed'
    }
    elseif ($summary.failed -gt 0) {
        $summary.failureReason = 'test-failures'
    }
    elseif ($summary.fixtureFailures -gt 0) {
        $summary.failureReason = 'fixture-failures'
    }
    else {
        $summary.isComplete = $true
    }

    [pscustomobject]$summary
}
