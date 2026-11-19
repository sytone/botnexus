<#
.SYNOPSIS
Validates application CORE completion and test-result evidence.
.DESCRIPTION
Loads the application-owned result checker. Requires clean process termination, complete expected projects and sound TRX counters and rows.
.PARAMETER ResultsDirectory
Directory containing application TRX results.
.PARAMETER CompletionPath
Bounded execution completion JSON.
.PARAMETER MinimumTotal
Minimum aggregate count required for the selected scope.
.PARAMETER SummaryPath
Optional JSON verdict destination.
.EXAMPLE
./Assert-TestResultContract.ps1 -ResultsDirectory ./artifacts/tests -CompletionPath ./artifacts/completion.json -MinimumTotal 12000
.OUTPUTS
None. Exits zero only when evidence satisfies the contract.
#>
#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [Parameter(Mandatory)][string] $CompletionPath,
    [int] $MinimumTotal = 0,
    [string] $SummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Application-owned TRX policy no longer imports container runtime source.
$contractScript = Join-Path $PSScriptRoot 'ApplicationTestResult.psm1'
if (-not (Test-Path -LiteralPath $contractScript -PathType Leaf)) {
    throw "Test-result contract not found at $contractScript. CI cannot validate a run it cannot judge."
}
Import-Module $contractScript -Force

if (-not (Test-Path -LiteralPath $CompletionPath -PathType Leaf)) {
    Write-Output "::error::No CORE completion receipt at $CompletionPath -- the test process did not prove it exited."
    exit 1
}
try {
    $completion = Get-Content -LiteralPath $CompletionPath -Raw | ConvertFrom-Json
}
catch {
    Write-Output "::error::CORE completion receipt is invalid: $($_.Exception.Message)"
    exit 1
}
$unfinishedProjects = @($completion.unfinishedProjects)
if ($completion.processExited -ne $true -or $completion.timedOut -eq $true -or $completion.exitCode -ne 0 -or $unfinishedProjects.Count -gt 0) {
    $unfinishedText = if ($unfinishedProjects.Count -gt 0) {
        $unfinishedProjects -join ', '
    }
    else {
        '<none identified>'
    }
    Write-Output "::error::CORE execution did not terminate cleanly: processExited=$($completion.processExited) timedOut=$($completion.timedOut) exitCode=$($completion.exitCode) unfinishedProjects=$unfinishedText."
    exit 1
}

if (-not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) {
    # An absent results directory is itself a finding: the test step either never ran or never
    # emitted TRX. Failing here is the fail-closed behaviour this script exists to provide.
    Write-Output "::error::No test results directory at $ResultsDirectory -- the test run produced nothing to validate."
    exit 1
}

$trxPaths = @(
    Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
)

$result = Get-RunnerTestResult -TrxPaths $trxPaths -MinimumTotal $MinimumTotal

$summary = [ordered]@{
    total = $result.total
    executed = $result.executed
    passed = $result.passed
    failed = $result.failed
    skipped = $result.skipped
    fixtureFailures = $result.fixtureFailures
    isComplete = $result.isComplete
    failureReason = $result.failureReason
    minimumTotal = $MinimumTotal
    trxFileCount = $trxPaths.Count
}

if ($SummaryPath) {
    try {
        $parent = Split-Path -Parent $SummaryPath
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $SummaryPath
    }
    catch {
        # A diagnostic must never be able to change the verdict it is describing.
        Write-Output "::warning::Could not write contract summary to ${SummaryPath}: $($_.Exception.Message)"
    }
}

$line = "total=$($result.total) executed=$($result.executed) passed=$($result.passed) " +
"failed=$($result.failed) skipped=$($result.skipped) " +
"fixtureFailures=$($result.fixtureFailures) trxFiles=$($trxPaths.Count)"

if ($result.isComplete) {
    Write-Output "Test-result contract satisfied: $line"
    exit 0
}

Write-Output "::error::Test-result contract REJECTED this run: $($result.failureReason). $line (minimumTotal=$MinimumTotal)"
exit 1
