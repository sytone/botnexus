<#
.SYNOPSIS
Tests application discovery, attribution and bounded child execution.
.DESCRIPTION
Preserves existing positive and negative cases without Azure or product gateway execution.
.EXAMPLE
./Test-ApplicationTestExecution.ps1
.OUTPUTS
System.String. Assertion results and a nonzero exit on failure.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ApplicationTestExecution.psm1') -Force

$script:failures = @()
function Assert-True {
    <#
    .SYNOPSIS
    Assert True for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER Condition
    Synthetic Condition input or expected observation.
    .PARAMETER Because
    Synthetic Because input or expected observation.
    .EXAMPLE
    Assert-True -Condition $Condition -Because $Because
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

    param([bool]$Condition, [string]$Because)
    if (-not $Condition) {
        $script:failures += $Because
    }
}
function Assert-Equal {
    <#
    .SYNOPSIS
    Assert Equal for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER Expected
    Synthetic Expected input or expected observation.
    .PARAMETER Actual
    Synthetic Actual input or expected observation.
    .PARAMETER Because
    Synthetic Because input or expected observation.
    .EXAMPLE
    Assert-Equal -Expected $Expected -Actual $Actual -Because $Because
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

    param($Expected, $Actual, [string]$Because)
    if ($Expected -ne $Actual) {
        $script:failures += "$Because Expected '$Expected', got '$Actual'."
    }
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) "rt3305-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Write-TestResultFile {
    <#
    .SYNOPSIS
    Write TestResultFile for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER Path
    Synthetic Path input or expected observation.
    .PARAMETER Assemblies
    Synthetic Assemblies input or expected observation.
    .EXAMPLE
    Write-TestResultFile -Path $Path -Assemblies $Assemblies
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

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

$results = Join-Path $root 'test-results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
Write-TestResultFile -Path (Join-Path $results 'a.trx') -Assemblies @('BotNexus.Gateway.Tests')
Write-TestResultFile -Path (Join-Path $results 'b.trx') -Assemblies @('BotNexus.Architecture.Tests', 'BotNexus.Gateway.Tests')

# 5. Assemblies come from the ROWS, not the filenames. dotnet test writes every project's TRX
#    into one directory with the same prefix, so filenames carry no project identity at all.
$completed = @(Get-CompletedTestAssembly -TrxPaths @((Join-Path $results 'a.trx'), (Join-Path $results 'b.trx')))
Assert-Equal -Expected 2 -Actual $completed.Count -Because "5: expected 2 distinct assemblies, got $($completed -join ',')"
Assert-True -Condition ($completed -contains 'BotNexus.Gateway.Tests') -Because '5: gateway assembly not detected'
Assert-True -Condition ($completed -contains 'BotNexus.Architecture.Tests') -Because '5: architecture assembly not detected'

# 6. THE CENTRAL PROPERTY: a TRX TRUNCATED BY A KILL still attributes. This is the whole point
#    -- a run destroyed mid-write leaves malformed XML, and an [xml] cast would throw and
#    surrender the only evidence the run produced. Text scanning survives it.
$truncated = Join-Path $results 'partial.trx'
Set-Content -Path $truncated -Value '<TestRun><TestDefinitions><UnitTest storage="/work/tests/BotNexus.Cron.Tests/bin/BotNexus.Cron.Tests.dll" /><UnitTe'
$partial = @(Get-CompletedTestAssembly -TrxPaths @($truncated))
Assert-True -Condition ($partial -contains 'BotNexus.Cron.Tests') -Because '6: a truncated TRX yielded no attribution -- the kill path is unreadable'
$xmlThrew = $false
try {
    [xml](Get-Content -LiteralPath $truncated -Raw)
}
catch {
    $xmlThrew = $true
}
Assert-True -Condition $xmlThrew -Because '6: fixture is not actually malformed, so the truncation property is vacuous'

# 7. A missing or empty path is skipped, not fatal. Finalisation runs on the failure path and
#    must never throw there -- throwing would lose the artifacts it exists to save.
Assert-Equal -Expected 0 -Actual @(Get-CompletedTestAssembly -TrxPaths @((Join-Path $results 'nope.trx'))).Count -Because '7: missing TRX was not tolerated.'
Assert-Equal -Expected 0 -Actual @(Get-CompletedTestAssembly -TrxPaths @()).Count -Because '7: empty input was not tolerated.'

# 7b. FALSE-ACCUSATION REGRESSION, measured not imagined. A real green run
#     (20260818002247-be34bc61) emitted this exact shape: the project directory is
#     BotNexus.Agent.Core.Tests but <AssemblyName> renames the dll to BotNexus.AgentCore.Tests,
#     and the whole path is LOWERCASED. Matching on the dll basename alone reported a project
#     that had just passed 438 tests as never having run. The project DIRECTORY must therefore
#     be recorded too.
$renamed = Join-Path $results 'renamed.trx'
Set-Content -Path $renamed -Value '<TestRun><TestDefinitions><UnitTest storage="/work/src/tests/agent/botnexus.agent.core.tests/bin/debug/net10.0/botnexus.agentcore.tests.dll" /></TestDefinitions></TestRun>'
$renamedSet = @(Get-CompletedTestAssembly -TrxPaths @($renamed))
Assert-True -Condition ($renamedSet -contains 'BotNexus.Agent.Core.Tests') -Because "7b: an AssemblyName-renamed project was not attributed to its directory: $($renamedSet -join ',')"
Assert-True -Condition ($renamedSet -contains 'BotNexus.AgentCore.Tests') -Because '7b: the dll name itself was dropped'

# --- Get-ExpectedTestProject --------------------------------------------------------------

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

$expected = @(Get-ExpectedTestProject -TestsRoot $tests)
Assert-Equal -Expected 2 -Actual $expected.Count -Because "8: expected 2 runnable projects, got $($expected -join ',')"
# 8. obj/ is excluded, matching tests/dirs.proj. Those directories are being written by the
#    very run doing the walk (#2666); a generated .csproj under obj is not a test project.
Assert-True -Condition ($expected -notcontains 'Ghost') -Because '8: an obj/ project leaked into the expected set'
# 8b. FALSE-ACCUSATION REGRESSION: a non-test project in tests/ produces no TRX by design, so
#     counting it as expected would name it as unfinished on EVERY timeout.
Assert-True -Condition ($expected -notcontains 'BotNexus.Scenarios.Harness') -Because '8b: a project with no test SDK was treated as expected'
Assert-True -Condition ($expected -notcontains 'BotNexus.Providers.Conformance.Tests') -Because '8b: IsTestProject=false was ignored'

# 9. A missing root returns empty rather than throwing -- same failure-path rule as 7.
Assert-Equal -Expected 0 -Actual @(Get-ExpectedTestProject -TestsRoot (Join-Path $root 'absent')).Count -Because '9: missing root did not return empty.'

# --- Get-UnfinishedTestProject ------------------------------------------------------------

# 10. The projects that produced no results are named. This is the capability the empty
#     artifact directory could not provide at any price.
$unfinished = @(Get-UnfinishedTestProject -ExpectedProjects $expected -CompletedAssemblies @('BotNexus.Gateway.Tests'))
Assert-Equal -Expected 1 -Actual $unfinished.Count -Because "10: expected 1 unfinished project, got $($unfinished -join ',')"
Assert-Equal -Expected 'BotNexus.Integration.E2E.Tests' -Actual $unfinished[0] -Because '10: wrong project attributed.'

# 11. Excluded projects are NOT accused. `core` filters the browser/E2E projects out, so their
#     absence from the results is by design; reporting them would be a false attribution, and
#     a confident wrong answer is worse than none.
$coreUnfinished = @(Get-UnfinishedTestProject -ExpectedProjects $expected -CompletedAssemblies @('BotNexus.Gateway.Tests') -ExcludedProjects @('BotNexus.Integration.E2E', 'BotNexus.E2E'))
Assert-Equal -Expected 0 -Actual $coreUnfinished.Count -Because "11: an excluded project was accused: $($coreUnfinished -join ',')"

# 12. A fully reported run has nothing outstanding -- the negative case, without which 10
#     could pass by always returning a non-empty set.
Assert-Equal -Expected 0 -Actual @(Get-UnfinishedTestProject -ExpectedProjects $expected -CompletedAssemblies @('BotNexus.Gateway.Tests', 'BotNexus.Integration.E2E.Tests')).Count -Because '12: complete run reported outstanding projects.'

# 13. Matching is case-insensitive: assembly casing on a case-sensitive Linux filesystem must
#     not manufacture a phantom unfinished project.
Assert-Equal -Expected 0 -Actual @(Get-UnfinishedTestProject -ExpectedProjects @('BotNexus.Gateway.Tests') -CompletedAssemblies @('botnexus.gateway.tests')).Count -Because '13: casing produced a phantom unfinished project.'

# 13b. A single-project mode must name only that project when it has not emitted a TRX. The
#      playwright lane used the repository-wide expected set and falsely accused every test project
#      when its one E2E process timed out before closing the TRX.
$playwrightExpected = @(Get-ExpectedProjectsForMode -Mode 'playwright' -ExpectedProjects $expected -E2EProject 'BotNexus.Integration.E2E.Tests')
Assert-Equal -Expected 1 -Actual $playwrightExpected.Count -Because "13b: playwright expected set is not singular: $($playwrightExpected -join ',')"
Assert-Equal -Expected 'BotNexus.Integration.E2E.Tests' -Actual $playwrightExpected[0] -Because '13b: playwright mode selected the wrong expected project.'

# 13c. Core still expects the non-E2E set. This negative control stops the mode helper from making
#      every timeout look like playwright merely to satisfy 13b.
$coreExpected = @(Get-ExpectedProjectsForMode -Mode 'core' -ExpectedProjects $expected -E2EProject 'BotNexus.Integration.E2E.Tests')
Assert-Equal -Expected 1 -Actual $coreExpected.Count -Because "13c: core expected set is wrong: $($coreExpected -join ',')"
Assert-Equal -Expected 'BotNexus.Gateway.Tests' -Actual $coreExpected[0] -Because '13c: core retained an excluded E2E project.'

# --- Invoke-BoundedProcess -----------------------------------------------------------------

$pwshPath = (Get-Process -Id $PID).Path

# 19. A process that finishes inside its deadline is NOT reported as timed out, and its exit
#     code is passed through. Without this the bound would fail every run closed.
$log = Join-Path $root 'fast.log'
$fast = Invoke-BoundedProcess -FilePath $pwshPath -ArgumentList @('-NoProfile', '-Command', 'Write-Output hello-3305; exit 0') -LogPath $log -TimeoutSeconds 60 -PollMilliseconds 100
Assert-Equal -Expected $false -Actual $fast.TimedOut -Because '19: a fast process was reported as timed out.'
Assert-Equal -Expected 0 -Actual $fast.ExitCode -Because '19: exit code not passed through.'
Assert-True -Condition ((Get-Content -LiteralPath $log -Raw) -match 'hello-3305') -Because '19: child output was not captured to the log'

# 20. A NON-ZERO exit is passed through as-is and is not confused with a timeout. A genuine
#     test failure and a hang must remain distinguishable -- conflating them is the defect.
$failLog = Join-Path $root 'fail.log'
$failed = Invoke-BoundedProcess -FilePath $pwshPath -ArgumentList @('-NoProfile', '-Command', 'exit 7') -LogPath $failLog -TimeoutSeconds 60 -PollMilliseconds 100
Assert-Equal -Expected 7 -Actual $failed.ExitCode -Because '20: non-zero exit code not passed through.'
Assert-Equal -Expected $false -Actual $failed.TimedOut -Because '20: a failing process was misreported as timed out.'

# 21. THE MECHANISM: a process that overruns is KILLED and reported as timed out, and control
#     RETURNS to the caller -- which is what makes the finalise-and-upload path reachable at
#     all. The platform kill it replaces never returns control to anything.
$hangLog = Join-Path $root 'hang.log'
$hang = Invoke-BoundedProcess -FilePath $pwshPath -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 120') -LogPath $hangLog -TimeoutSeconds 3 -PollMilliseconds 100
Assert-Equal -Expected $true -Actual $hang.TimedOut -Because '21: an overrunning process was not reported as timed out.'
Assert-True -Condition ($hang.ElapsedSeconds -lt 60) -Because "21: the bound did not actually cut the run short ($($hang.ElapsedSeconds)s)"
Assert-True -Condition ($hang.ElapsedSeconds -ge 3) -Because "21: returned before the deadline could have expired ($($hang.ElapsedSeconds)s)"

# 22. The real child process must receive a collector value containing spaces as ONE token.
#     PowerShell's -File binder splits --collect:<value> itself, so use a probe token without
#     the option prefix to isolate the process-launch boundary (the actual runner uses dotnet).
#     Start-Process's string-array API flattens unquoted tokens on Windows.
$probePath = Join-Path $root 'argv-probe.ps1'
Set-Content -LiteralPath $probePath -Value '$args | ConvertTo-Json -Compress' -Encoding utf8
$argvLog = Join-Path $root 'argv.log'
$filter = 'FullyQualifiedName!~BotNexus.Integration.E2E&FullyQualifiedName!~BotNexus.E2E'
$argvRun = Invoke-BoundedProcess -FilePath $pwshPath -ArgumentList @('-NoProfile', '-File', $probePath, 'collector:XPlat Code Coverage', '--filter', $filter) -LogPath $argvLog -TimeoutSeconds 60 -PollMilliseconds 100
Assert-Equal -Expected 0 -Actual $argvRun.ExitCode -Because '22: argv probe did not exit successfully.'
try {
    $observedArguments = @(Get-Content -LiteralPath $argvLog -Raw | ConvertFrom-Json)
    Assert-Equal -Expected 3 -Actual $observedArguments.Count -Because '22: collector or filter was split into multiple arguments.'
    if ($observedArguments.Count -ge 3) {
        Assert-Equal -Expected 'collector:XPlat Code Coverage' -Actual $observedArguments[0] -Because '22: collector name did not survive as one argument.'
        Assert-Equal -Expected '--filter' -Actual $observedArguments[1] -Because '22: filter switch was lost.'
        Assert-Equal -Expected $filter -Actual $observedArguments[2] -Because '22: filter value changed at the child boundary.'
    }
}
catch {
    $script:failures += "22: argv probe did not emit parseable JSON: $($_.Exception.Message)"
}

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

if ($script:failures.Count) {
    Write-Output "FAILED ($($script:failures.Count)):"
    $script:failures | ForEach-Object { Write-Output "  $_" }
    exit 1
}
Write-Output 'Application test execution: PASS'
exit 0
