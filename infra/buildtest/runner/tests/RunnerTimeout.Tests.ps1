# Tests for the runner's self-imposed deadline and per-project attribution (#3305).
#
# WHY THIS EXISTS: `-Mode full` reached the 20-minute platform replica ceiling twice on one
# worktree and was killed by the platform, so the entrypoint's finally block never ran and the
# artifact directory was EMPTY. No result.json, no TRX, no way to name the project that was
# still executing. These tests pin the pure logic that replaces that outcome: a deadline that
# lands inside the platform budget, and attribution derived from whatever TRX exist at that
# moment.
#
# No Azure, no container, no test run required: every function under test is pure, apart from
# Invoke-BoundedProcess which is exercised against a real short-lived pwsh child.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..' 'RunnerTimeout.ps1')

$script:failures = @()
function Assert-True {
    param([bool]$Condition, [string]$Because)
    if (-not $Condition) { $script:failures += $Because }
}
function Assert-Equal {
    param($Expected, $Actual, [string]$Because)
    if ($Expected -ne $Actual) { $script:failures += "$Because Expected '$Expected', got '$Actual'." }
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) "rt3305-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null

function New-Trx {
    param([string]$Path, [string[]]$Assemblies)
    $rows = ($Assemblies | ForEach-Object {
        "    <UnitTest name=`"t`" storage=`"/work/src/tests/$_/bin/Debug/net10.0/$_.dll`" />"
    }) -join "`n"
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <TestDefinitions>
$rows
  </TestDefinitions>
</TestRun>
"@ | Set-Content -Path $Path
}

# --- Get-RunnerDeadlineSeconds -------------------------------------------------------------

# 1. The deadline is strictly INSIDE the platform budget. Landing on it reproduces the defect:
#    the platform destroys the replica before the upload can complete.
$d = Get-RunnerDeadlineSeconds -BudgetSeconds 1200
Assert-True ($d -lt 1200) "1: deadline $d is not inside the 1200s platform budget"
Assert-Equal 1110 $d '1: default reserve is not 90s.'

# 2. The reserve is honoured as given, so retuning the platform budget cannot silently change
#    how much finalisation time the runner keeps.
Assert-Equal 900 (Get-RunnerDeadlineSeconds -BudgetSeconds 1200 -ReserveSeconds 300) '2: explicit reserve ignored.'

# 3. NON-VACUITY / floor: an absurd budget must still yield a POSITIVE deadline. A negative
#    deadline would abort the run before a single test executed while reporting a timeout,
#    which is a worse lie than the original empty directory.
$tiny = Get-RunnerDeadlineSeconds -BudgetSeconds 10
Assert-True ($tiny -gt 0) "3: tiny budget produced a non-positive deadline ($tiny)"
Assert-Equal 30 $tiny '3: floor is not applied.'

# 4. The deadline is SENSITIVE to the budget. A constant would pass tests 1-3 vacuously.
Assert-True ((Get-RunnerDeadlineSeconds -BudgetSeconds 2400) -ne (Get-RunnerDeadlineSeconds -BudgetSeconds 1200)) `
    '4: deadline does not vary with the budget -- the derivation is constant'

# --- Get-CompletedTestAssemblies -----------------------------------------------------------

$results = Join-Path $root 'test-results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
New-Trx -Path (Join-Path $results 'a.trx') -Assemblies @('BotNexus.Gateway.Tests')
New-Trx -Path (Join-Path $results 'b.trx') -Assemblies @('BotNexus.Architecture.Tests', 'BotNexus.Gateway.Tests')

# 5. Assemblies come from the ROWS, not the filenames. dotnet test writes every project's TRX
#    into one directory with the same prefix, so filenames carry no project identity at all.
$completed = @(Get-CompletedTestAssemblies -TrxPaths @((Join-Path $results 'a.trx'), (Join-Path $results 'b.trx')))
Assert-Equal 2 $completed.Count "5: expected 2 distinct assemblies, got $($completed -join ',')"
Assert-True ($completed -contains 'BotNexus.Gateway.Tests') '5: gateway assembly not detected'
Assert-True ($completed -contains 'BotNexus.Architecture.Tests') '5: architecture assembly not detected'

# 6. THE CENTRAL PROPERTY: a TRX TRUNCATED BY A KILL still attributes. This is the whole point
#    -- a run destroyed mid-write leaves malformed XML, and an [xml] cast would throw and
#    surrender the only evidence the run produced. Text scanning survives it.
$truncated = Join-Path $results 'partial.trx'
Set-Content -Path $truncated -Value '<TestRun><TestDefinitions><UnitTest storage="/work/tests/BotNexus.Cron.Tests/bin/BotNexus.Cron.Tests.dll" /><UnitTe'
$partial = @(Get-CompletedTestAssemblies -TrxPaths @($truncated))
Assert-True ($partial -contains 'BotNexus.Cron.Tests') '6: a truncated TRX yielded no attribution -- the kill path is unreadable'
$xmlThrew = $false
try { [xml](Get-Content -LiteralPath $truncated -Raw) } catch { $xmlThrew = $true }
Assert-True $xmlThrew '6: fixture is not actually malformed, so the truncation property is vacuous'

# 7. A missing or empty path is skipped, not fatal. Finalisation runs on the failure path and
#    must never throw there -- throwing would lose the artifacts it exists to save.
Assert-Equal 0 @(Get-CompletedTestAssemblies -TrxPaths @((Join-Path $results 'nope.trx'))).Count '7: missing TRX was not tolerated.'
Assert-Equal 0 @(Get-CompletedTestAssemblies -TrxPaths @()).Count '7: empty input was not tolerated.'

# 7b. FALSE-ACCUSATION REGRESSION, measured not imagined. A real green run
#     (20260818002247-be34bc61) emitted this exact shape: the project directory is
#     BotNexus.Agent.Core.Tests but <AssemblyName> renames the dll to BotNexus.AgentCore.Tests,
#     and the whole path is LOWERCASED. Matching on the dll basename alone reported a project
#     that had just passed 438 tests as never having run. The project DIRECTORY must therefore
#     be recorded too.
$renamed = Join-Path $results 'renamed.trx'
Set-Content -Path $renamed -Value '<TestRun><TestDefinitions><UnitTest storage="/work/src/tests/agent/botnexus.agent.core.tests/bin/debug/net10.0/botnexus.agentcore.tests.dll" /></TestDefinitions></TestRun>'
$renamedSet = @(Get-CompletedTestAssemblies -TrxPaths @($renamed))
Assert-True ($renamedSet -contains 'BotNexus.Agent.Core.Tests') `
    "7b: an AssemblyName-renamed project was not attributed to its directory: $($renamedSet -join ',')"
Assert-True ($renamedSet -contains 'BotNexus.AgentCore.Tests') '7b: the dll name itself was dropped'

# --- Get-ExpectedTestProjects --------------------------------------------------------------

$tests = Join-Path $root 'tests'
foreach ($p in @('gateway/BotNexus.Gateway.Tests', 'e2e/BotNexus.Integration.E2E.Tests', 'gateway/BotNexus.Gateway.Tests/obj/Debug', 'harness/BotNexus.Scenarios.Harness', 'conformance/BotNexus.Providers.Conformance.Tests')) {
    New-Item -ItemType Directory -Path (Join-Path $tests $p) -Force | Out-Null
}
$testSdk = '<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>'
Set-Content -Path (Join-Path $tests 'gateway/BotNexus.Gateway.Tests/BotNexus.Gateway.Tests.csproj') -Value $testSdk
Set-Content -Path (Join-Path $tests 'e2e/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj') -Value $testSdk
Set-Content -Path (Join-Path $tests 'gateway/BotNexus.Gateway.Tests/obj/Debug/Ghost.csproj') -Value $testSdk
# Real shapes measured in tests/: a shared harness with no test SDK, and a support library
# that explicitly opts out. dotnet test never runs either, so neither may be accused.
Set-Content -Path (Join-Path $tests 'harness/BotNexus.Scenarios.Harness/BotNexus.Scenarios.Harness.csproj') -Value '<Project><ItemGroup><PackageReference Include="xunit" /></ItemGroup></Project>'
Set-Content -Path (Join-Path $tests 'conformance/BotNexus.Providers.Conformance.Tests/BotNexus.Providers.Conformance.Tests.csproj') -Value '<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>'

$expected = @(Get-ExpectedTestProjects -TestsRoot $tests)
Assert-Equal 2 $expected.Count "8: expected 2 runnable projects, got $($expected -join ',')"
# 8. obj/ is excluded, matching tests/dirs.proj. Those directories are being written by the
#    very run doing the walk (#2666); a generated .csproj under obj is not a test project.
Assert-True ($expected -notcontains 'Ghost') '8: an obj/ project leaked into the expected set'
# 8b. FALSE-ACCUSATION REGRESSION: a non-test project in tests/ produces no TRX by design, so
#     counting it as expected would name it as unfinished on EVERY timeout.
Assert-True ($expected -notcontains 'BotNexus.Scenarios.Harness') '8b: a project with no test SDK was treated as expected'
Assert-True ($expected -notcontains 'BotNexus.Providers.Conformance.Tests') '8b: IsTestProject=false was ignored'

# 9. A missing root returns empty rather than throwing -- same failure-path rule as 7.
Assert-Equal 0 @(Get-ExpectedTestProjects -TestsRoot (Join-Path $root 'absent')).Count '9: missing root did not return empty.'

# --- Get-UnfinishedTestProjects ------------------------------------------------------------

# 10. The projects that produced no results are named. This is the capability the empty
#     artifact directory could not provide at any price.
$unfinished = @(Get-UnfinishedTestProjects -ExpectedProjects $expected -CompletedAssemblies @('BotNexus.Gateway.Tests'))
Assert-Equal 1 $unfinished.Count "10: expected 1 unfinished project, got $($unfinished -join ',')"
Assert-Equal 'BotNexus.Integration.E2E.Tests' $unfinished[0] '10: wrong project attributed.'

# 11. Excluded projects are NOT accused. `core` filters the browser/E2E projects out, so their
#     absence from the results is by design; reporting them would be a false attribution, and
#     a confident wrong answer is worse than none.
$coreUnfinished = @(Get-UnfinishedTestProjects -ExpectedProjects $expected -CompletedAssemblies @('BotNexus.Gateway.Tests') -ExcludedProjects @('BotNexus.Integration.E2E', 'BotNexus.E2E'))
Assert-Equal 0 $coreUnfinished.Count "11: an excluded project was accused: $($coreUnfinished -join ',')"

# 12. A fully reported run has nothing outstanding -- the negative case, without which 10
#     could pass by always returning a non-empty set.
Assert-Equal 0 @(Get-UnfinishedTestProjects -ExpectedProjects $expected -CompletedAssemblies @('BotNexus.Gateway.Tests', 'BotNexus.Integration.E2E.Tests')).Count '12: complete run reported outstanding projects.'

# 13. Matching is case-insensitive: assembly casing on a case-sensitive Linux filesystem must
#     not manufacture a phantom unfinished project.
Assert-Equal 0 @(Get-UnfinishedTestProjects -ExpectedProjects @('BotNexus.Gateway.Tests') -CompletedAssemblies @('botnexus.gateway.tests')).Count '13: casing produced a phantom unfinished project.'

# --- New-RunnerTimeoutRecord ---------------------------------------------------------------

$record = New-RunnerTimeoutRecord -Phase 'test' -ElapsedSeconds 1110.4 -DeadlineSeconds 1110 -UnfinishedProjects @('BotNexus.Integration.E2E.Tests') -CompletedAssemblies @('BotNexus.Gateway.Tests')

# 14. The record is STRUCTURED and carries the timeout flag, so the client can read the outcome
#     out of result.json instead of inferring it from an absence (#3244's shape).
Assert-Equal $true $record.timedOut '14: timedOut flag not set.'
Assert-Equal 'test' $record.phase '15: phase not recorded.'
Assert-Equal 1110 $record.deadlineSeconds '16: deadline not recorded.'

# 17. AC2: the message NAMES the outstanding project.
Assert-True ($record.attribution -match 'BotNexus\.Integration\.E2E\.Tests') `
    "17: attribution does not name the outstanding project: $($record.attribution)"

# 18. HONESTY: when nothing was outstanding the record says so rather than inventing a culprit.
#     "Everything reported and it still overran" is a real and different finding.
$noneOutstanding = New-RunnerTimeoutRecord -Phase 'test' -ElapsedSeconds 1110 -DeadlineSeconds 1110
Assert-True ($noneOutstanding.attribution -match 'No test project was outstanding') `
    "18: an empty outstanding set did not produce an explicit statement: $($noneOutstanding.attribution)"
Assert-Equal 0 @($noneOutstanding.unfinishedProjects).Count '18: empty set was not preserved.'

# --- Invoke-BoundedProcess -----------------------------------------------------------------

$pwshPath = (Get-Process -Id $PID).Path

# 19. A process that finishes inside its deadline is NOT reported as timed out, and its exit
#     code is passed through. Without this the bound would fail every run closed.
$log = Join-Path $root 'fast.log'
$fast = Invoke-BoundedProcess -FilePath $pwshPath -ArgumentList @('-NoProfile', '-Command', 'Write-Host hello-3305; exit 0') -LogPath $log -TimeoutSeconds 60 -PollMilliseconds 100
Assert-Equal $false $fast.TimedOut '19: a fast process was reported as timed out.'
Assert-Equal 0 $fast.ExitCode '19: exit code not passed through.'
Assert-True ((Get-Content -LiteralPath $log -Raw) -match 'hello-3305') '19: child output was not captured to the log'

# 20. A NON-ZERO exit is passed through as-is and is not confused with a timeout. A genuine
#     test failure and a hang must remain distinguishable -- conflating them is the defect.
$failLog = Join-Path $root 'fail.log'
$failed = Invoke-BoundedProcess -FilePath $pwshPath -ArgumentList @('-NoProfile', '-Command', 'exit 7') -LogPath $failLog -TimeoutSeconds 60 -PollMilliseconds 100
Assert-Equal 7 $failed.ExitCode '20: non-zero exit code not passed through.'
Assert-Equal $false $failed.TimedOut '20: a failing process was misreported as timed out.'

# 21. THE MECHANISM: a process that overruns is KILLED and reported as timed out, and control
#     RETURNS to the caller -- which is what makes the finalise-and-upload path reachable at
#     all. The platform kill it replaces never returns control to anything.
$hangLog = Join-Path $root 'hang.log'
$hang = Invoke-BoundedProcess -FilePath $pwshPath -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 120') -LogPath $hangLog -TimeoutSeconds 3 -PollMilliseconds 100
Assert-Equal $true $hang.TimedOut '21: an overrunning process was not reported as timed out.'
Assert-True ($hang.ElapsedSeconds -lt 60) "21: the bound did not actually cut the run short ($($hang.ElapsedSeconds)s)"
Assert-True ($hang.ElapsedSeconds -ge 3) "21: returned before the deadline could have expired ($($hang.ElapsedSeconds)s)"

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

if ($script:failures.Count) {
    Write-Host "FAILED ($($script:failures.Count)):" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'RunnerTimeout.Tests.ps1: PASS' -ForegroundColor Green
exit 0
