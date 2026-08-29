Set-StrictMode -Version Latest

# The set of DELIBERATE skip reasons is small, stable, and authored by us; the set of ways a
# fixture crash can be worded is unbounded and drifts every time somebody rephrases a message.
# #2841 shipped a BLOCKLIST and it failed open within two days: `Fixture init failed:` slipped
# past a pattern that spelled out `initialization`, and bare `Solution prebuild exit 1.` matched
# nothing at all. Measured against the 2,214 real TRX files on disk that blocklist missed 210
# genuine build-failure skips. So the check is inverted here: a reason must be RECOGNISED as
# deliberate to earn a pass, and anything unrecognised is treated as a failure.
#
# Every entry below was mined from real TRX output rather than imagined, so the list covers the
# suite as it actually behaves. Adding a new deliberate skip now requires adding it here, which
# is the point: a skip nobody declared is a skip nobody vouched for.
$script:DeliberateSkipPatterns = @(
    'Dev gateway not running at'
    'GITHUB_TOKEN environment variable not set'
    # LocalCliCopilotSetupTests drives the real GitHub device-code endpoint. A runner with no
    # egress to github.com skips it deliberately, but the reason was never allow-listed, so an
    # otherwise fully-green core run (failed=0 across 62 TRX files) was rejected as a fixture
    # failure -- see #3639, which blocked PR #3637 on a two-file diff that cannot reach it.
    'device-code endpoint unreachable'
    '^Windows-only test\.'
    '^Blocked by #\d+'
    '^E2E_[A-Z0-9_]+ (not set|!=)'
    'Compaction tests require real slash-command processing'
    'e2e loopback is TBD'
    'Message stream not tall enough to be scrollable'
    'Sessions REST API (unavailable|not available)'
    'Sessions nav not found'
    'No tool messages rendered'
)

# Phrasings that name a build or process failure. These are checked BEFORE the allow-list so a
# crash message can never be waved through by happening to contain an allow-listed substring.
$script:ProcessFailurePattern = 'exit \d+|exit code|Exception|Fixture (failed|init|initialization)|One or more errors occurred|could not be (created|constructed)|Class fixture|Collection fixture'

function Test-DeliberateSkipReason {
    [CmdletBinding()]
    param([string] $Reason)

    if ([string]::IsNullOrWhiteSpace($Reason)) { return $false }
    if ($Reason -match $script:ProcessFailurePattern) { return $false }
    foreach ($pattern in $script:DeliberateSkipPatterns) {
        if ($Reason -match $pattern) { return $true }
    }
    return $false
}

function Get-RunnerTestResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]] $TrxPaths,

        [switch] $RequireZeroSkipped,

        # A collapsed run is indistinguishable from a green one if only the pass/fail ratio is
        # checked: one passing test satisfies "0 failed" exactly as well as 12,765 do. The caller
        # knows how big the suite it asked for should be, so it supplies the floor.
        [int] $MinimumTotal = 0
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

    $impossibleCounters = $false
    $unverifiable = $false
    $reasonedSkips = 0

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

        $fileTotal = [int]$counters.total
        $fileExecuted = [int]$counters.executed

        # Arithmetically impossible: a run cannot execute more tests than it contains. Accepting
        # it lets a forged counter block inflate `executed` past `total` and satisfy every
        # downstream check by construction.
        if ($fileExecuted -gt $fileTotal) { $impossibleCounters = $true }

        $summary.total += $fileTotal
        $summary.executed += $fileExecuted
        $summary.passed += [int]$counters.passed
        $summary.failed += [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
        $summary.skipped += [int]$counters.notExecuted + [int]$counters.notRunnable + [int]$counters.inconclusive

        # Counters are the file's own claim about itself. Corroborate them against the rows:
        # 160 of the 2,214 real TRX files carry no Results node, so a counter block asserting
        # work that left no trace is a shape that occurs in practice, not just in theory.
        # Zero rows with zero executed is honest (an empty project emits exactly that), so the
        # guard fires only when a file claims to have RUN something it cannot show.
        $resultsNode = $trx.TestRun.PSObject.Properties['Results']
        $rows = @()
        if ($resultsNode -and $trx.TestRun.Results) {
            $rows = @($trx.TestRun.Results.UnitTestResult) | Where-Object { $_ }
        }
        if ($fileExecuted -gt 0 -and $rows.Count -eq 0) { $unverifiable = $true }

        foreach ($unit in $rows) {
            if ($unit.GetAttribute('outcome') -ne 'NotExecuted') { continue }
            $reason = $unit.Output.ErrorInfo.Message
            if (Test-DeliberateSkipReason -Reason $reason) { $reasonedSkips++ }
            else { $summary.fixtureFailures++ }
        }
    }

    # A TRX summary can under-report its own skips: on 2026-08-06 the E2E project emitted
    # notExecuted=0 while the file carried 265 results with outcome="NotExecuted", so the
    # gate reported failed=0/skipped=0 and exited 0 having actually run 15 of 280 tests.
    # total vs executed is the only counter pair that exposes that, so it is authoritative:
    # a test that was neither executed nor deliberately skipped has NOT been validated, and
    # certifying it green is worse than reporting a failure.
    $unaccounted = $summary.total - $summary.executed - $summary.skipped
    if ($unaccounted -gt 0) { $summary.skipped += $unaccounted }

    # Skips that no result row explained: the TRX asserted them in its counters but never
    # said why. Unexplained is not the same as acceptable.
    $unexplained = $summary.skipped - $reasonedSkips - $summary.fixtureFailures
    if ($unexplained -gt 0) { $summary.fixtureFailures += $unexplained }

    if ($summary.total -eq 0 -or $summary.executed -eq 0) {
        $summary.failureReason = 'no-tests-executed'
    }
    elseif ($impossibleCounters) {
        $summary.failureReason = 'impossible-test-counters'
    }
    elseif ($unverifiable) {
        $summary.failureReason = 'unverifiable-test-results'
    }
    elseif ($summary.failed -gt 0) {
        $summary.failureReason = 'test-failures'
    }
    elseif ($summary.fixtureFailures -gt 0) {
        $summary.failureReason = 'fixture-failures'
    }
    elseif ($MinimumTotal -gt 0 -and $summary.total -lt $MinimumTotal) {
        $summary.failureReason = 'below-minimum-total'
    }
    else {
        $summary.isComplete = $true
    }

    [pscustomobject]$summary
}
