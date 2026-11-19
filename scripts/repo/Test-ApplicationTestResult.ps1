<#
.SYNOPSIS
Tests the application result acceptance contract using synthetic TRX.
.DESCRIPTION
Preserves existing positive and negative cases without Azure or product gateway execution.
.EXAMPLE
./Test-ApplicationTestResult.ps1
.OUTPUTS
System.String. Assertion results and a nonzero exit on failure.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ApplicationTestResult.psm1') -Force

function Assert-Equal($Expected, $Actual, [string]$Message) {
    <#
    .SYNOPSIS
    Assert Equal for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER Expected
    Synthetic Expected input or expected observation.
    .PARAMETER Actual
    Synthetic Actual input or expected observation.
    .PARAMETER Message
    Synthetic Message input or expected observation.
    .EXAMPLE
    Assert-Equal -Expected $Expected -Actual $Actual -Message $Message
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Write-Trx([string]$Path, [int]$Total, [int]$Executed, [int]$Passed, [int]$Failed, [int]$NotExecuted) {
    <#
    .SYNOPSIS
    Write Trx for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER Path
    Synthetic Path input or expected observation.
    .PARAMETER Total
    Synthetic Total input or expected observation.
    .PARAMETER Executed
    Synthetic Executed input or expected observation.
    .PARAMETER Passed
    Synthetic Passed input or expected observation.
    .PARAMETER Failed
    Synthetic Failed input or expected observation.
    .PARAMETER NotExecuted
    Synthetic NotExecuted input or expected observation.
    .EXAMPLE
    Write-Trx -Path $Path -Total $Total -Executed $Executed -Passed $Passed -Failed $Failed -NotExecuted $NotExecuted
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

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
    <#
    .SYNOPSIS
    Write TrxNoResultsNode for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER Path
    Synthetic Path input or expected observation.
    .PARAMETER Total
    Synthetic Total input or expected observation.
    .PARAMETER Executed
    Synthetic Executed input or expected observation.
    .PARAMETER Passed
    Synthetic Passed input or expected observation.
    .EXAMPLE
    Write-TrxNoResultsNode -Path $Path -Total $Total -Executed $Executed -Passed $Passed
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="$Total" executed="$Executed" passed="$Passed" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="$Executed" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -Path $Path
}

function Write-TrxWithResult([string]$Path, [int]$Total, [int]$Executed, [int]$Passed, [string[]]$SkipReasons) {
    <#
    .SYNOPSIS
    Write TrxWithResult for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER Path
    Synthetic Path input or expected observation.
    .PARAMETER Total
    Synthetic Total input or expected observation.
    .PARAMETER Executed
    Synthetic Executed input or expected observation.
    .PARAMETER Passed
    Synthetic Passed input or expected observation.
    .PARAMETER SkipReasons
    Synthetic SkipReasons input or expected observation.
    .EXAMPLE
    Write-TrxWithResult -Path $Path -Total $Total -Executed $Executed -Passed $Passed -SkipReasons $SkipReasons
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

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
    Write-Trx -Path $complete -Total 12 -Executed 12 -Passed 12 -Failed 0 -NotExecuted 0
    $result = Get-RunnerTestResult -TrxPaths @($complete)
    Assert-Equal -Expected 12 -Actual $result.total -Message 'Complete run total.'
    Assert-Equal -Expected 0 -Actual $result.skipped -Message 'Complete run skipped.'
    Assert-Equal -Expected $true -Actual $result.isComplete -Message 'Complete run classification.'

    $skipped = Join-Path $temp 'skipped.trx'
    Write-Trx -Path $skipped -Total 263 -Executed 12 -Passed 12 -Failed 0 -NotExecuted 251
    $result = Get-RunnerTestResult -TrxPaths @($skipped)
    Assert-Equal -Expected 251 -Actual $result.skipped -Message 'Skipped count.'
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'Unexpected skips must be incomplete.'
    # No result row explains these, so they are unexplained rather than declared.
    Assert-Equal -Expected 'fixture-failures' -Actual $result.failureReason -Message 'Unexplained skip classification.'

    # Regression (2026-08-06): a TRX whose summary UNDER-REPORTS its own skips. The real
    # E2E run emitted notExecuted="0" while 265 of 280 results carried outcome="NotExecuted",
    # so the gate reported failed=0/skipped=0 and exited 0 having validated nothing. Only
    # total vs executed exposes it, so that pair must be authoritative.
    $lying = Join-Path $temp 'lying-counters.trx'
    Write-Trx -Path $lying -Total 280 -Executed 15 -Passed 15 -Failed 0 -NotExecuted 0
    $result = Get-RunnerTestResult -TrxPaths @($lying)
    Assert-Equal -Expected 265 -Actual $result.skipped -Message 'Unaccounted tests must be counted as skipped.'
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'A run that executed 15 of 280 must never be complete.'

    # A DECLARED skip is honest and must not fail the gate: the suite legitimately skips
    # "GITHUB_TOKEN not set" and "Windows-only test" in a Linux container, and rejecting
    # those makes the gate unpassable for reasons nobody intends to fix.
    $declared = Join-Path $temp 'declared-skips.trx'
    Write-TrxWithResult -Path $declared -Total 30 -Executed 28 -Passed 28 -SkipReasons @('GITHUB_TOKEN environment variable not set. Skipping integration test.', 'Windows-only test.')
    $result = Get-RunnerTestResult -TrxPaths @($declared)
    Assert-Equal -Expected 0 -Actual $result.fixtureFailures -Message 'Declared skips are not fixture failures.'
    Assert-Equal -Expected $true -Actual $result.isComplete -Message 'Declared skips must not fail the gate.'

    # #4624: an absent developer gateway is not an external credential requirement.
    # Conversation coverage must execute on its candidate fixture, never certify dormant tests.
    $dormant = Join-Path $temp 'dormant-conversation.trx'
    Write-TrxWithResult -Path $dormant -Total 30 -Executed 2 -Passed 2 -SkipReasons (@('Dev gateway not running at localhost:5006') * 28)
    $result = Get-RunnerTestResult -TrxPaths @($dormant)
    Assert-Equal -Expected 28 -Actual $result.fixtureFailures -Message 'All dormant conversation tests must fail certification.'
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'An absent candidate gateway cannot certify CORE.'
    Assert-Equal -Expected 'fixture-failures' -Actual $result.failureReason -Message 'Dormant conversation classification.'

    # A CRASHED FIXTURE is a build failure wearing a skip's clothing. xUnit marks every
    # dependent test NotExecuted, so failed=0 and the run reads green (2026-08-06: 265 E2E
    # tests hidden behind exit 0).
    $fixture = Join-Path $temp 'fixture-crash.trx'
    Write-TrxWithResult -Path $fixture -Total 30 -Executed 28 -Passed 28 -SkipReasons @('Fixture failed: Solution prebuild exit 1.', 'Fixture initialization failed: Solution prebuild exit 1.')
    $result = Get-RunnerTestResult -TrxPaths @($fixture)
    Assert-Equal -Expected 2 -Actual $result.fixtureFailures -Message 'Fixture crashes must be counted.'
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'A crashed fixture must never be complete.'
    Assert-Equal -Expected 'fixture-failures' -Actual $result.failureReason -Message 'Fixture crash classification.'

    $failed = Join-Path $temp 'failed.trx'
    Write-Trx -Path $failed -Total 3 -Executed 3 -Passed 2 -Failed 1 -NotExecuted 0
    $result = Get-RunnerTestResult -TrxPaths @($failed)
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'Failures must be incomplete.'
    Assert-Equal -Expected 'test-failures' -Actual $result.failureReason -Message 'Failure classification.'

    $result = Get-RunnerTestResult -TrxPaths @()
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'Missing result must be incomplete.'
    Assert-Equal -Expected 'missing-test-results' -Actual $result.failureReason -Message 'Missing result classification.'

    $empty = Join-Path $temp 'empty.trx'
    Write-Trx -Path $empty -Total 0 -Executed 0 -Passed 0 -Failed 0 -NotExecuted 0
    $result = Get-RunnerTestResult -TrxPaths @($empty)
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'Zero tests must be incomplete.'
    Assert-Equal -Expected 'no-tests-executed' -Actual $result.failureReason -Message 'Zero test classification.'

    # --- #2851: the blocklist failed open. These are the EXACT messages mined from the 2,214
    # real TRX files on disk that the merged classifier waved through as deliberate skips.

    # AC1/AC2: bare process-failure messages carry no `Fixture failed:` prefix at all.
    foreach ($crash in @(
            'Solution prebuild exit 1.',
            'dotnet pack exit 1.',
            'Fixture init failed: Solution prebuild exit 1.',
            'Fixture init failed: dotnet pack exit 1.',
            'Fixture init failed: GitHub device-code endpoint unreachable from this host. exit code 1',
            'System.InvalidOperationException: the harness never started'
        )) {
        $path = Join-Path $temp ('crash-' + [Guid]::NewGuid().ToString('N') + '.trx')
        Write-TrxWithResult -Path $path -Total 30 -Executed 29 -Passed 29 -SkipReasons @($crash)
        $result = Get-RunnerTestResult -TrxPaths @($path)
        Assert-Equal -Expected 1 -Actual $result.fixtureFailures -Message "Unrecognised skip reason must be a fixture failure: $crash"
        Assert-Equal -Expected $false -Actual $result.isComplete -Message "A build failure must never be complete: $crash"
        Assert-Equal -Expected 'fixture-failures' -Actual $result.failureReason -Message "Build-failure classification: $crash"
    }

    # AC1/AC2 precedence: a crash message that ALSO contains an allow-listed phrase must still
    # fail. Fixture messages routinely quote the condition they were checking, so
    # "Dev gateway not running" appears inside real crash text; if the allow-list were consulted
    # first, quoting a benign phrase would launder a build failure into a deliberate skip.
    $poisoned = Join-Path $temp 'poisoned-reason.trx'
    Write-TrxWithResult -Path $poisoned -Total 30 -Executed 29 -Passed 29 -SkipReasons @('Fixture init failed: Dev gateway not running at localhost:5006. Solution prebuild exit 1.')
    $result = Get-RunnerTestResult -TrxPaths @($poisoned)
    Assert-Equal -Expected 1 -Actual $result.fixtureFailures -Message 'A crash quoting an allow-listed phrase must not be laundered.'
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'Process failure must outrank the allow-list.'

    # AC3: counters claiming executed work that no result row corroborates.
    $ghost = Join-Path $temp 'ghost-rows.trx'
    Write-TrxNoResultsNode -Path $ghost -Total 100 -Executed 100 -Passed 100
    $result = Get-RunnerTestResult -TrxPaths @($ghost)
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'Counters with no result rows must not certify green.'
    Assert-Equal -Expected 'unverifiable-test-results' -Actual $result.failureReason -Message 'Uncorroborated counter classification.'

    # AC3 parity: an empty project legitimately emits zero rows AND zero executed. The green
    # core run of 2026-08-06 contained five such files, so this shape must stay acceptable or
    # the guard breaks a passing gate.
    $emptyProject = Join-Path $temp 'empty-project.trx'
    Write-TrxNoResultsNode -Path $emptyProject -Total 0 -Executed 0 -Passed 0
    $realWork = Join-Path $temp 'real-work.trx'
    Write-Trx -Path $realWork -Total 10 -Executed 10 -Passed 10 -Failed 0 -NotExecuted 0
    $result = Get-RunnerTestResult -TrxPaths @($emptyProject, $realWork)
    Assert-Equal -Expected $true -Actual $result.isComplete -Message 'An empty project with zero executed must not be unverifiable.'

    # AC4: a run cannot execute more tests than it contains.
    $impossible = Join-Path $temp 'impossible.trx'
    Write-Trx -Path $impossible -Total 5 -Executed 500 -Passed 500 -Failed 0 -NotExecuted 0
    $result = Get-RunnerTestResult -TrxPaths @($impossible)
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'executed > total must never be complete.'
    Assert-Equal -Expected 'impossible-test-counters' -Actual $result.failureReason -Message 'Impossible counter classification.'

    # AC5: one passing test satisfies "zero failed" exactly as well as 12,765 do.
    $collapsed = Join-Path $temp 'collapsed.trx'
    Write-Trx -Path $collapsed -Total 1 -Executed 1 -Passed 1 -Failed 0 -NotExecuted 0
    $result = Get-RunnerTestResult -TrxPaths @($collapsed) -MinimumTotal 12000
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'A collapsed run must not pass a mode with a floor.'
    Assert-Equal -Expected 'below-minimum-total' -Actual $result.failureReason -Message 'Collapsed run classification.'
    # And the floor must not fire on a run that meets it.
    $result = Get-RunnerTestResult -TrxPaths @($collapsed) -MinimumTotal 1
    Assert-Equal -Expected $true -Actual $result.isComplete -Message 'A run meeting its floor must still pass.'

    # Retain the historical 38-skip shape: #4624 deliberately revokes its green verdict.
    # Credential/platform skips remain recognized; all 28 dormant gateway cases fail closed.
    $green = Join-Path $temp 'green-parity.trx'
    $greenSkips = @()
    $greenSkips += , 'Dev gateway not running at localhost:5006' * 28
    $greenSkips += , 'GITHUB_TOKEN environment variable not set. Skipping integration test.' * 8
    $greenSkips += 'Windows-only test.'
    $greenSkips += 'Blocked by #383: canvas HTML is not persisted across reconnect.'
    Write-TrxWithResult -Path $green -Total 12802 -Executed 12764 -Passed 12764 -SkipReasons $greenSkips
    $result = Get-RunnerTestResult -TrxPaths @($green) -MinimumTotal 12000
    Assert-Equal -Expected 38 -Actual $result.skipped -Message 'Green-run parity: skipped count.'
    Assert-Equal -Expected 28 -Actual $result.fixtureFailures -Message 'Historical parity: dormant gateway skips are not coverage.'
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'Historical dormant coverage must no longer certify green.'

    # #3639: LocalCliCopilotSetupTests skips on a runner with no egress to github.com. The
    # reason was absent from the allow-list, so run 33208723818 was rejected with
    # fixtureFailures=1 despite failed=0 across all 62 TRX files, blocking PR #3637.
    #
    # The reason string below is VERBATIM from
    # tests/integration/BotNexus.Integration.Cli.Tests/LocalCliCopilotSetupTests.cs:59. Keeping
    # it byte-identical is the point: rewording the guard without extending the pattern must
    # fail here rather than silently reopening the gap in CI.
    $deviceCodeReason = 'GitHub device-code endpoint unreachable from this host.'
    Assert-Equal -Expected $true -Actual (Test-DeliberateSkipReason -Reason $deviceCodeReason) -Message 'The device-code egress skip must be recognised as deliberate.'

    $deviceCode = Join-Path $temp 'device-code.trx'
    Write-TrxWithResult -Path $deviceCode -Total 35 -Executed 34 -Passed 34 -SkipReasons @($deviceCodeReason)
    $result = Get-RunnerTestResult -TrxPaths @($deviceCode)
    Assert-Equal -Expected 0 -Actual $result.fixtureFailures -Message 'A device-code egress skip is not a fixture failure.'
    Assert-Equal -Expected $true -Actual $result.isComplete -Message 'A run whose only skip is the device-code guard stays green.'

    # The full 39-skip decomposition of the rejected run, so this reproduces the exact shape
    # that was red rather than an idealised single-skip case.
    $observed = Join-Path $temp 'run-33208723818.trx'
    $observedSkips = @()
    $observedSkips += , 'Dev gateway not running at localhost:5006' * 28
    $observedSkips += , 'GITHUB_TOKEN environment variable not set. Skipping integration test.' * 8
    $observedSkips += 'Windows-only test.'
    $observedSkips += 'Blocked by #383: canvas HTML is not persisted across reconnect.'
    $observedSkips += $deviceCodeReason
    Write-TrxWithResult -Path $observed -Total 17783 -Executed 17744 -Passed 17744 -SkipReasons $observedSkips
    $result = Get-RunnerTestResult -TrxPaths @($observed) -MinimumTotal 12000
    Assert-Equal -Expected 39 -Actual $result.skipped -Message 'Rejected-run repro: skipped count matches the observed run.'
    Assert-Equal -Expected 28 -Actual $result.fixtureFailures -Message 'Rejected-run repro: device-code recognition cannot excuse dormant gateway coverage.'
    Assert-Equal -Expected $false -Actual $result.isComplete -Message 'Rejected-run repro: dormant conversation tests still fail certification.'

    Write-Output 'RunnerResult.Tests.ps1: PASS'
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
