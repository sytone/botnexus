<#
.SYNOPSIS
Runs the bounded application CORE suite and records completion.
.DESCRIPTION
Starts the application test traversal with browser quarantine, retains logs and requires every expected project to report.
.PARAMETER ResultsDirectory
Owned directory for TRX and process logs.
.PARAMETER CompletionPath
JSON completion record destination.
.PARAMETER TimeoutSeconds
Whole child-process deadline in seconds.
.PARAMETER HangTimeoutMinutes
VSTest hang detection timeout in minutes.
.PARAMETER CollectCoverage
Collects XPlat coverage when selected.
.EXAMPLE
./Invoke-BoundedCoreTests.ps1 -ResultsDirectory ./artifacts/tests -CompletionPath ./artifacts/completion.json
.OUTPUTS
None. Exits with the test or completeness status.
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [Parameter(Mandatory)][string] $CompletionPath,
    [int] $TimeoutSeconds = 2100,
    [int] $HangTimeoutMinutes = 10,
    [switch] $CollectCoverage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
Import-Module (Join-Path $PSScriptRoot 'ApplicationTestExecution.psm1') -Force
. (Join-Path $PSScriptRoot 'CoreTestFailureDiagnostics.ps1')

New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$completionParent = Split-Path -Parent $CompletionPath
if ($completionParent) {
    New-Item -ItemType Directory -Path $completionParent -Force | Out-Null
}

$logPath = Join-Path $ResultsDirectory 'core-test-process.log'
$coreFilter = 'FullyQualifiedName!~BotNexus.Integration.E2E&FullyQualifiedName!~BotNexus.E2E'
$arguments = @(
    'test', 'tests/dirs.proj', '--no-build', '--nologo', '--tl:off',
    '--filter', $coreFilter,
    '--logger', 'trx;LogFilePrefix=ci', '--results-directory', $ResultsDirectory,
    '--blame-hang', '--blame-hang-dump-type', 'none',
    '--blame-hang-timeout', "$HangTimeoutMinutes`m"
)
if ($CollectCoverage) {
    $arguments += '--collect:XPlat Code Coverage'
}

$run = Invoke-BoundedProcess -FilePath 'dotnet' -ArgumentList $arguments -LogPath $logPath -TimeoutSeconds $TimeoutSeconds

$trxPaths = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName)
$completed = @(Get-CompletedTestAssembly -TrxPaths $trxPaths)
$expected = @(Get-ExpectedProjectsForMode -Mode 'core' -ExpectedProjects @(Get-ExpectedTestProject -TestsRoot (Join-Path $repoRoot 'tests')) -E2EProject 'BotNexus.Integration.E2E.Tests')
$unfinished = @(Get-UnfinishedTestProject -ExpectedProjects $expected -CompletedAssemblies $completed)

$receipt = [ordered]@{
    version = 1
    processExited = -not $run.TimedOut
    timedOut = $run.TimedOut
    exitCode = $run.ExitCode
    elapsedSeconds = [Math]::Round($run.ElapsedSeconds, 2)
    deadlineSeconds = $TimeoutSeconds
    hangTimeoutMinutes = $HangTimeoutMinutes
    expectedProjectCount = $expected.Count
    completedAssemblies = $completed
    unfinishedProjects = $unfinished
    logPath = [IO.Path]::GetFileName($logPath)
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $CompletionPath -Encoding utf8NoBOM

if ($run.TimedOut) {
    $attribution = if ($unfinished.Count -gt 0) {
        "Outstanding test projects: $($unfinished -join ', ')."
    }
    else {
        'Every expected project emitted result rows, but the aggregate dotnet test process remained alive. Inspect core-test-process.log and blame-hang sequence files for the surviving test host.'
    }
    Write-Output "::error::CORE test process exceeded ${TimeoutSeconds}s. $attribution"
    exit 124
}

if ($run.ExitCode -ne 0) {
    Write-Output "::error::CORE test process exited with code $($run.ExitCode). Failure details follow."
    $diagnostics = Get-CoreTestFailureDiagnostics -TrxPaths $trxPaths -LogPath $logPath
    Write-Output 'CORE test failure diagnostics (bounded):'
    foreach ($line in ($diagnostics -split '\r?\n')) {
        Write-Output $line
    }
    exit $run.ExitCode
}

if ($unfinished.Count -gt 0) {
    Write-Output "::error::CORE test process exited without terminal results from: $($unfinished -join ', ')."
    exit 1
}

Write-Output "CORE test process completed: $($expected.Count) expected projects reported; elapsed=$([Math]::Round($run.ElapsedSeconds, 1))s."
exit 0
