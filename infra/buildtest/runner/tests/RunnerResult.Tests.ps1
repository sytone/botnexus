$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..' 'RunnerResult.ps1')

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
}

function Write-Trx([string]$Path, [int]$Total, [int]$Executed, [int]$Passed, [int]$Failed, [int]$NotExecuted) {
    # Emits $Passed passing result rows so the fixture matches the shape real `dotnet test`
    # output actually has. A counter block with no rows behind it is the forged shape #2851
    # added a guard for, so a synthetic TRX must not use it to mean "an ordinary run".
    $rows = (1..[Math]::Max($Passed, 0) | Where-Object { $Passed -gt 0 } | ForEach-Object {
        "    <UnitTestResult testName=`"p$_`" outcome=`"Passed`" />"
    }) -join "`n"
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
$rows
  </Results>
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Executed" passed="$Passed" failed="$Failed" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="$NotExecuted" disconnected="0" warning="0" completed="$Executed" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -Path $Path
}

function Write-TrxNoResultsNode([string]$Path, [int]$Total, [int]$Executed, [int]$Passed) {
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Executed" passed="$Passed" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="$Executed" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -Path $Path
}

function Write-TrxWithResults([string]$Path, [int]$Total, [int]$Executed, [int]$Passed, [string[]]$SkipReasons) {
    $results = ($SkipReasons | ForEach-Object {
        $m = [Security.SecurityElement]::Escape($_)
        "    <UnitTestResult testName=`"t$([Guid]::NewGuid().ToString('N'))`" outcome=`"NotExecuted`"><Output><ErrorInfo><Message>$m</Message></ErrorInfo></Output></UnitTestResult>"
    }) -join "`n"
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
$results
  </Results>
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Executed" passed="$Passed" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="$Executed" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -Path $Path
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('runner-result-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $complete = Join-Path $temp 'complete.trx'
    Write-Trx $complete 12 12 12 0 0
    $result = Get-RunnerTestResult -TrxPaths @($complete) -RequireZeroSkipped
    Assert-Equal 12 $result.total 'Complete run total.'
    Assert-Equal 0 $result.skipped 'Complete run skipped.'
    Assert-Equal $true $result.isComplete 'Complete run classification.'

    $skipped = Join-Path $temp 'skipped.trx'
    Write-Trx $skipped 263 12 12 0 251
    $result = Get-RunnerTestResult -TrxPaths @($skipped) -RequireZeroSkipped
    Assert-Equal 251 $result.skipped 'Skipped count.'
    Assert-Equal $false $result.isComplete 'Unexpected skips must be incomplete.'
    # No result row explains these, so they are unexplained rather than declared.
    Assert-Equal 'fixture-failures' $result.failureReason 'Unexplained skip classification.'

    # Regression (2026-08-06): a TRX whose summary UNDER-REPORTS its own skips. The real
    # E2E run emitted notExecuted="0" while 265 of 280 results carried outcome="NotExecuted",
    # so the gate reported failed=0/skipped=0 and exited 0 having validated nothing. Only
    # total vs executed exposes it, so that pair must be authoritative.
    $lying = Join-Path $temp 'lying-counters.trx'
    Write-Trx $lying 280 15 15 0 0
    $result = Get-RunnerTestResult -TrxPaths @($lying) -RequireZeroSkipped
    Assert-Equal 265 $result.skipped 'Unaccounted tests must be counted as skipped.'
    Assert-Equal $false $result.isComplete 'A run that executed 15 of 280 must never be complete.'

    # A DECLARED skip is honest and must not fail the gate: the suite legitimately skips
    # "GITHUB_TOKEN not set" and "Windows-only test" in a Linux container, and rejecting
    # those makes the gate unpassable for reasons nobody intends to fix.
    $declared = Join-Path $temp 'declared-skips.trx'
    Write-TrxWithResults $declared 30 28 28 @('GITHUB_TOKEN environment variable not set. Skipping integration test.', 'Windows-only test.')
    $result = Get-RunnerTestResult -TrxPaths @($declared) -RequireZeroSkipped
    Assert-Equal 0 $result.fixtureFailures 'Declared skips are not fixture failures.'
    Assert-Equal $true $result.isComplete 'Declared skips must not fail the gate.'

    # A CRASHED FIXTURE is a build failure wearing a skip's clothing. xUnit marks every
    # dependent test NotExecuted, so failed=0 and the run reads green (2026-08-06: 265 E2E
    # tests hidden behind exit 0).
    $fixture = Join-Path $temp 'fixture-crash.trx'
    Write-TrxWithResults $fixture 30 28 28 @('Fixture failed: Solution prebuild exit 1.', 'Fixture initialization failed: Solution prebuild exit 1.')
    $result = Get-RunnerTestResult -TrxPaths @($fixture) -RequireZeroSkipped
    Assert-Equal 2 $result.fixtureFailures 'Fixture crashes must be counted.'
    Assert-Equal $false $result.isComplete 'A crashed fixture must never be complete.'
    Assert-Equal 'fixture-failures' $result.failureReason 'Fixture crash classification.'

    $failed = Join-Path $temp 'failed.trx'
    Write-Trx $failed 3 3 2 1 0
    $result = Get-RunnerTestResult -TrxPaths @($failed) -RequireZeroSkipped
    Assert-Equal $false $result.isComplete 'Failures must be incomplete.'
    Assert-Equal 'test-failures' $result.failureReason 'Failure classification.'

    $result = Get-RunnerTestResult -TrxPaths @() -RequireZeroSkipped
    Assert-Equal $false $result.isComplete 'Missing result must be incomplete.'
    Assert-Equal 'missing-test-results' $result.failureReason 'Missing result classification.'

    $empty = Join-Path $temp 'empty.trx'
    Write-Trx $empty 0 0 0 0 0
    $result = Get-RunnerTestResult -TrxPaths @($empty) -RequireZeroSkipped
    Assert-Equal $false $result.isComplete 'Zero tests must be incomplete.'
    Assert-Equal 'no-tests-executed' $result.failureReason 'Zero test classification.'

    # --- #2851: the blocklist failed open. These are the EXACT messages mined from the 2,214
    # real TRX files on disk that the merged classifier waved through as deliberate skips.

    # AC1/AC2: bare process-failure messages carry no `Fixture failed:` prefix at all.
    foreach ($crash in @(
        'Solution prebuild exit 1.',
        'dotnet pack exit 1.',
        'Fixture init failed: Solution prebuild exit 1.',
        'Fixture init failed: dotnet pack exit 1.',
        'System.InvalidOperationException: the harness never started'
    )) {
        $path = Join-Path $temp ('crash-' + [Guid]::NewGuid().ToString('N') + '.trx')
        Write-TrxWithResults $path 30 29 29 @($crash)
        $result = Get-RunnerTestResult -TrxPaths @($path) -RequireZeroSkipped
        Assert-Equal 1 $result.fixtureFailures "Unrecognised skip reason must be a fixture failure: $crash"
        Assert-Equal $false $result.isComplete "A build failure must never be complete: $crash"
        Assert-Equal 'fixture-failures' $result.failureReason "Build-failure classification: $crash"
    }

    # AC1/AC2 precedence: a crash message that ALSO contains an allow-listed phrase must still
    # fail. Fixture messages routinely quote the condition they were checking, so
    # "Dev gateway not running" appears inside real crash text; if the allow-list were consulted
    # first, quoting a benign phrase would launder a build failure into a deliberate skip.
    $poisoned = Join-Path $temp 'poisoned-reason.trx'
    Write-TrxWithResults $poisoned 30 29 29 @('Fixture init failed: Dev gateway not running at localhost:5006. Solution prebuild exit 1.')
    $result = Get-RunnerTestResult -TrxPaths @($poisoned) -RequireZeroSkipped
    Assert-Equal 1 $result.fixtureFailures 'A crash quoting an allow-listed phrase must not be laundered.'
    Assert-Equal $false $result.isComplete 'Process failure must outrank the allow-list.'

    # AC3: counters claiming executed work that no result row corroborates.
    $ghost = Join-Path $temp 'ghost-rows.trx'
    Write-TrxNoResultsNode $ghost 100 100 100
    $result = Get-RunnerTestResult -TrxPaths @($ghost) -RequireZeroSkipped
    Assert-Equal $false $result.isComplete 'Counters with no result rows must not certify green.'
    Assert-Equal 'unverifiable-test-results' $result.failureReason 'Uncorroborated counter classification.'

    # AC3 parity: an empty project legitimately emits zero rows AND zero executed. The green
    # core run of 2026-08-06 contained five such files, so this shape must stay acceptable or
    # the guard breaks a passing gate.
    $emptyProject = Join-Path $temp 'empty-project.trx'
    Write-TrxNoResultsNode $emptyProject 0 0 0
    $realWork = Join-Path $temp 'real-work.trx'
    Write-Trx $realWork 10 10 10 0 0
    $result = Get-RunnerTestResult -TrxPaths @($emptyProject, $realWork) -RequireZeroSkipped
    Assert-Equal $true $result.isComplete 'An empty project with zero executed must not be unverifiable.'

    # AC4: a run cannot execute more tests than it contains.
    $impossible = Join-Path $temp 'impossible.trx'
    Write-Trx $impossible 5 500 500 0 0
    $result = Get-RunnerTestResult -TrxPaths @($impossible) -RequireZeroSkipped
    Assert-Equal $false $result.isComplete 'executed > total must never be complete.'
    Assert-Equal 'impossible-test-counters' $result.failureReason 'Impossible counter classification.'

    # AC5: one passing test satisfies "zero failed" exactly as well as 12,765 do.
    $collapsed = Join-Path $temp 'collapsed.trx'
    Write-Trx $collapsed 1 1 1 0 0
    $result = Get-RunnerTestResult -TrxPaths @($collapsed) -RequireZeroSkipped -MinimumTotal 12000
    Assert-Equal $false $result.isComplete 'A collapsed run must not pass a mode with a floor.'
    Assert-Equal 'below-minimum-total' $result.failureReason 'Collapsed run classification.'
    # And the floor must not fire on a run that meets it.
    $result = Get-RunnerTestResult -TrxPaths @($collapsed) -RequireZeroSkipped -MinimumTotal 1
    Assert-Equal $true $result.isComplete 'A run meeting its floor must still pass.'

    # AC6 (parity): every deliberate skip reason observed in the last green core run must still
    # be recognised. These four messages account for all 38 skips in run 20260806211507-8a39b1d4.
    $green = Join-Path $temp 'green-parity.trx'
    $greenSkips = @()
    $greenSkips += ,'Dev gateway not running at localhost:5006' * 28
    $greenSkips += ,'GITHUB_TOKEN environment variable not set. Skipping integration test.' * 8
    $greenSkips += 'Windows-only test.'
    $greenSkips += 'Blocked by #383: canvas HTML is not persisted across reconnect.'
    Write-TrxWithResults $green 12802 12764 12764 $greenSkips
    $result = Get-RunnerTestResult -TrxPaths @($green) -RequireZeroSkipped -MinimumTotal 12000
    Assert-Equal 38 $result.skipped 'Green-run parity: skipped count.'
    Assert-Equal 0 $result.fixtureFailures 'Green-run parity: no skip may be reclassified as a crash.'
    Assert-Equal $true $result.isComplete 'Green-run parity: the last green core run must stay green.'

    Write-Host 'RunnerResult.Tests.ps1: PASS'
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
