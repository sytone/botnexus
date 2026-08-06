$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..' 'RunnerResult.ps1')

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
}

function Write-Trx([string]$Path, [int]$Total, [int]$Executed, [int]$Passed, [int]$Failed, [int]$NotExecuted) {
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Executed" passed="$Passed" failed="$Failed" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="$NotExecuted" disconnected="0" warning="0" completed="$Executed" inProgress="0" pending="0" />
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

    Write-Host 'RunnerResult.Tests.ps1: PASS'
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
