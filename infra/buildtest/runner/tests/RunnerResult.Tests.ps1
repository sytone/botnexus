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
    Assert-Equal 'unexpected-skips' $result.failureReason 'Unexpected skip classification.'

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
