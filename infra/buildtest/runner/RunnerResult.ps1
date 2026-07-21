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

    if ($summary.total -eq 0 -or $summary.executed -eq 0) {
        $summary.failureReason = 'no-tests-executed'
    }
    elseif ($summary.failed -gt 0) {
        $summary.failureReason = 'test-failures'
    }
    elseif ($RequireZeroSkipped -and $summary.skipped -gt 0) {
        $summary.failureReason = 'unexpected-skips'
    }
    else {
        $summary.isComplete = $true
    }

    [pscustomobject]$summary
}
