# Tests for per-project COST attribution (#3314).
#
# WHY THIS EXISTS: #3305 made a TIMED-OUT run attributable -- it names the projects that had
# not reported when the deadline expired. That answers "which project was still running", but
# only on the timeout path, and only as a SET with no cost attached.
#
# The #3314 measurement showed why that is not enough. `-Mode full` on unmodified main
# (run 20260819033413-38a9b933) completed in 13.7 min of its 20 min budget with timeout=null:
# it did NOT overrun, so #3305's attribution never fired and produced nothing. Answering the
# actual question the issue asked -- where does the `core`/`full` delta go -- required
# hand-writing a throwaway TRX scanner outside the repo. A diagnostic that has to be
# reinvented ad hoc every time is not a diagnostic; the measured numbers below
# (botnexus.integration.e2e.tests: 325.8s of a 472.7s test phase) came from exactly that
# throwaway and are not reproducible by anyone else without rewriting it.
#
# So cost attribution is made a FIRST-CLASS, ALWAYS-ON artifact rather than a timeout-only
# side effect: every run emits per-project wall seconds and counts, so the next person asking
# "what is expensive" reads a file instead of writing a parser.
#
# Pure functions only: no Azure, no container, no test run.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..' 'RunnerCost.ps1')

$script:failures = @()
function Assert-True {
    param([bool]$Condition, [string]$Because)
    if (-not $Condition) { $script:failures += $Because }
}
function Assert-Equal {
    param($Expected, $Actual, [string]$Because)
    if ($Expected -ne $Actual) { $script:failures += "$Because Expected '$Expected', got '$Actual'." }
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) "rc3314-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null

# Fixture shaped like the REAL artifacts measured on 20260819033413-38a9b933: lowercased
# storage paths, a <Times> element carrying start/finish, and a <Counters> element.
function New-CostTrx {
    param(
        [string]$Path,
        [string]$Storage,
        [string]$Start,
        [string]$Finish,
        [int]$Total = 1,
        [int]$Passed = 1,
        [int]$Failed = 0
    )
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="$Start" queuing="$Start" start="$Start" finish="$Finish" />
  <TestDefinitions>
    <UnitTest name="t" storage="$Storage" />
  </TestDefinitions>
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Total" passed="$Passed" failed="$Failed" notExecuted="0" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -Path $Path
}

$results = Join-Path $root 'test-results'
New-Item -ItemType Directory -Path $results -Force | Out-Null

New-CostTrx -Path (Join-Path $results 'slow.trx') `
    -Storage '/work/src/tests/integration/botnexus.integration.e2e.tests/bin/debug/net10.0/botnexus.integration.e2e.tests.dll' `
    -Start '2026-08-19T03:40:00.0000000+00:00' -Finish '2026-08-19T03:45:00.0000000+00:00' -Total 283 -Passed 281 -Failed 2
New-CostTrx -Path (Join-Path $results 'fast.trx') `
    -Storage '/work/src/tests/gateway/botnexus.gateway.tests/bin/debug/net10.0/botnexus.gateway.tests.dll' `
    -Start '2026-08-19T03:40:00.0000000+00:00' -Finish '2026-08-19T03:40:10.0000000+00:00' -Total 5587 -Passed 5587

$trx = @(Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -ExpandProperty FullName)

# --- Get-RunnerProjectCosts ----------------------------------------------------------------

$costs = @(Get-RunnerProjectCosts -TrxPaths $trx)

# 1. Every assembly that produced results is reported. Attribution is the whole point.
Assert-Equal 2 $costs.Count "1: expected 2 attributed projects, got $($costs.Count)"

# 2. THE CENTRAL PROPERTY: cost is attributed to the PROJECT and ordered most-expensive first,
#    so the answer to "what is expensive" is the first row rather than a sort the reader has to
#    perform. An unordered list is what the throwaway scanner produced and it is why the number
#    had to be re-derived by hand.
Assert-Equal 'botnexus.integration.e2e.tests' $costs[0].project '2: costs are not ordered most-expensive first.'
Assert-Equal 300 $costs[0].seconds '2: wall seconds not derived from the TRX Times element.'
Assert-Equal 10 $costs[1].seconds '2: the cheap project was mis-costed.'

# 3. Counts travel WITH the cost. 300s for 283 tests and 10s for 5,587 tests are opposite
#    findings, and a seconds-only report cannot tell them apart -- which is precisely the
#    distinction that identified E2E rather than the gateway suite as the cost driver.
Assert-Equal 283 $costs[0].total '3: test total not attributed.'
Assert-Equal 2 $costs[0].failed '3: failed count not attributed.'
Assert-Equal 5587 $costs[1].total '3: the cheap project lost its count.'

# 4. NON-VACUITY: the seconds must be DERIVED, not constant. Two projects with identical
#    fixtures but different Times must not report the same cost.
Assert-True ($costs[0].seconds -ne $costs[1].seconds) '4: cost is constant across differing runs -- the derivation is vacuous.'

# 5. SAD PATH -- a TRX truncated by a kill has no </TestRun> and frequently no <Counters> at
#    all. It must still attribute what it can rather than throwing: finalisation runs on the
#    failure path, and throwing there destroys the only evidence the run produced. This is the
#    same property #3305 pinned for the unfinished-project set.
$truncated = Join-Path $results 'partial.trx'
Set-Content -Path $truncated -Value '<TestRun><Times start="2026-08-19T03:40:00.0000000+00:00" finish="2026-08-19T03:41:00.0000000+00:00" /><TestDefinitions><UnitTest storage="/work/tests/BotNexus.Cron.Tests/bin/BotNexus.Cron.Tests.dll" /><UnitTe'
$xmlThrew = $false
try { [xml](Get-Content -LiteralPath $truncated -Raw) } catch { $xmlThrew = $true }
Assert-True $xmlThrew '5: fixture is not actually malformed, so the truncation property is vacuous.'
$partialCosts = @(Get-RunnerProjectCosts -TrxPaths @($truncated))
Assert-Equal 1 $partialCosts.Count '5: a truncated TRX yielded no cost attribution.'
Assert-Equal 60 $partialCosts[0].seconds '5: a truncated TRX lost its measured duration.'
Assert-Equal 0 $partialCosts[0].total '5: a missing Counters element did not degrade to zero.'

# 6. SAD PATH -- missing, empty, and unreadable inputs are tolerated, never fatal.
Assert-Equal 0 @(Get-RunnerProjectCosts -TrxPaths @((Join-Path $results 'nope.trx'))).Count '6: a missing TRX was not tolerated.'
Assert-Equal 0 @(Get-RunnerProjectCosts -TrxPaths @()).Count '6: empty input was not tolerated.'

# 7. SAD PATH -- a TRX with no Times element cannot be costed. It must report zero seconds
#    rather than inventing a duration; a fabricated measurement is worse than an absent one.
$noTimes = Join-Path $results 'notimes.trx'
Set-Content -Path $noTimes -Value '<TestRun><TestDefinitions><UnitTest storage="/work/tests/BotNexus.Odd.Tests/bin/BotNexus.Odd.Tests.dll" /></TestDefinitions></TestRun>'
$oddCosts = @(Get-RunnerProjectCosts -TrxPaths @($noTimes))
Assert-Equal 1 $oddCosts.Count '7: a TRX without Times was dropped entirely rather than reported at zero.'
Assert-Equal 0 $oddCosts[0].seconds '7: a duration was invented for a TRX with no Times element.'

# 8. Two TRX for the SAME project are SUMMED, not overwritten. dotnet test can emit more than
#    one result file per assembly, and taking only the last would under-report the cost of
#    exactly the project most likely to be the driver.
$multiA = Join-Path $results 'multi-a.trx'
$multiB = Join-Path $results 'multi-b.trx'
New-CostTrx -Path $multiA -Storage '/work/tests/BotNexus.Dup.Tests/bin/BotNexus.Dup.Tests.dll' -Start '2026-08-19T03:40:00.0000000+00:00' -Finish '2026-08-19T03:40:20.0000000+00:00' -Total 5 -Passed 5
New-CostTrx -Path $multiB -Storage '/work/tests/BotNexus.Dup.Tests/bin/BotNexus.Dup.Tests.dll' -Start '2026-08-19T03:41:00.0000000+00:00' -Finish '2026-08-19T03:41:30.0000000+00:00' -Total 7 -Passed 7
$dup = @(Get-RunnerProjectCosts -TrxPaths @($multiA, $multiB))
Assert-Equal 1 $dup.Count '8: the same project was reported twice instead of being aggregated.'
Assert-Equal 50 $dup[0].seconds '8: durations for one project were not summed.'
Assert-Equal 12 $dup[0].total '8: counts for one project were not summed.'

# --- Format-RunnerCostReport ---------------------------------------------------------------

$report = Format-RunnerCostReport -Costs $costs -TestPhaseSeconds 472.74 -Mode 'full'

# 9. The report NAMES the dominant project. The whole failure of the #3314 investigation was
#    that nothing on disk said "E2E costs 325.8s"; a report that omits the name repeats it.
Assert-True ($report -match 'botnexus\.integration\.e2e\.tests') "9: the report does not name the dominant project."

# 10. The report states the SHARE of the test phase, because 300s is only alarming relative to
#     the phase that contains it. 300 of 472.74 is 63%.
Assert-True ($report -match '63(\.\d+)?%') "10: the report does not express cost as a share of the test phase: $report"

# 10b. CONCURRENCY DISCLOSURE, found by replaying the real artifacts rather than the fixtures.
#      `dotnet test` runs assemblies in PARALLEL, so the per-project shares overlap and sum to
#      well over 100% -- the real run 20260819033413-38a9b933 attributes 65.2% + 49.1% + 48.2%
#      to its top three alone. A reader who takes these as exclusive slices of the phase will
#      conclude the arithmetic is broken, or worse, that removing the top project would save
#      65% of the phase. The report must say so itself; a caveat that lives only in a commit
#      message is a caveat nobody reads.
Assert-True ($report -match 'concurrent') "10b: the report does not disclose that projects overlap: $report"

# 11. The mode is recorded. A cost profile is meaningless without knowing which test set
#     produced it -- core and full are different runs and would otherwise be indistinguishable.
Assert-True ($report -match 'full') '11: the report does not record the mode.'

# 12. SAD PATH -- an empty cost set produces an explicit statement rather than an empty file or
#     a divide-by-zero. "No project reported" is a real finding (it is what a run killed before
#     any TRX landed looks like) and must be stated, not implied by silence.
$emptyReport = Format-RunnerCostReport -Costs @() -TestPhaseSeconds 0 -Mode 'full'
Assert-True (-not [string]::IsNullOrWhiteSpace($emptyReport)) '12: an empty cost set produced no report at all.'
Assert-True ($emptyReport -match 'No per-project cost') "12: an empty cost set did not state so explicitly: $emptyReport"

# 13. SAD PATH -- a zero test-phase duration must not divide by zero or emit a bogus share.
#     Asserted POSITIVELY against the 'n/a' the guard produces, not merely negatively against
#     the words 'Infinity|NaN': mutation testing removed the guard and the negative-only form
#     SURVIVED, because PowerShell renders a division by zero as the glyph 8734 rather than
#     the word. A sad-path assertion that cannot fail is worse than none, since it certifies
#     the guard while ignoring it.
$zeroPhase = Format-RunnerCostReport -Costs $costs -TestPhaseSeconds 0 -Mode 'full'
Assert-True (-not [string]::IsNullOrWhiteSpace($zeroPhase)) '13: a zero phase duration produced no report.'
Assert-True ($zeroPhase -match 'n/a') "13: a zero phase duration did not degrade the share to 'n/a': $zeroPhase"
# Scoped to the DATA ROWS: the fixed header legitimately contains the literal '100%'.
$zeroRows = @($zeroPhase -split "`n" | Where-Object { $_ -match 'botnexus\.' })
Assert-True ($zeroRows.Count -gt 0) '13: the zero-phase report emitted no data rows to check.'
Assert-Equal 0 @($zeroRows | Where-Object { $_ -match '%' }).Count "13: a share was computed against a zero-length phase: $zeroRows"
Assert-True ($zeroPhase -notmatch 'Infinity|NaN|\u221E') "13: a zero phase duration produced a bogus share: $zeroPhase"

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

if ($script:failures.Count) {
    Write-Host "FAILED ($($script:failures.Count)):" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'RunnerCost.Tests.ps1: PASS' -ForegroundColor Green
exit 0
