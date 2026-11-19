<#
.SYNOPSIS
Tests application CORE completion, diagnostics and workflow policy.
.DESCRIPTION
Preserves existing positive and negative cases without Azure or product gateway execution.
.EXAMPLE
./Test-CoreTestExecution.ps1
.OUTPUTS
System.String. Assertion results and a nonzero exit on failure.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$runnerPath = Join-Path $repoRoot 'scripts/repo/Invoke-BoundedCoreTests.ps1'
$workflowPath = Join-Path $repoRoot '.github/workflows/ci-build-test.yml'
$contractPath = Join-Path $repoRoot 'scripts/repo/Assert-TestResultContract.ps1'
$diagnosticsPath = Join-Path $repoRoot 'scripts/repo/CoreTestFailureDiagnostics.ps1'
$failures = [Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    <#
    .SYNOPSIS
    Assert True for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER Condition
    Synthetic Condition input or expected observation.
    .PARAMETER Message
    Synthetic Message input or expected observation.
    .EXAMPLE
    Assert-True -Condition $Condition -Message $Message
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

    if (-not $Condition) {
        $failures.Add($Message)
    }
}

Assert-True -Condition (Test-Path -LiteralPath $runnerPath -PathType Leaf) -Message 'The bounded CORE runner script must exist.'
if (Test-Path -LiteralPath $runnerPath -PathType Leaf) {
    $runner = Get-Content -LiteralPath $runnerPath -Raw
    Assert-True -Condition ($runner -match 'Invoke-BoundedProcess') -Message 'The CORE runner must enforce a child-process deadline.'
    Assert-True -Condition ($runner -match 'Get-UnfinishedTestProject') -Message 'The CORE runner must identify projects without terminal results.'
    Assert-True -Condition ($runner -match 'Set-Content -LiteralPath \$CompletionPath') -Message 'The CORE runner must emit a completion receipt.'
    Assert-True -Condition ($runner -match 'exit 124') -Message 'A timed-out CORE runner must use a distinct timeout exit code.'
    Assert-True -Condition ($runner -match '--blame-hang-timeout') -Message 'The CORE runner must request VSTest hang diagnostics below the job deadline.'
    Assert-True -Condition ($runner -match 'Get-CoreTestFailureDiagnostics') -Message 'A failed CORE run must render bounded failure diagnostics.'
}

Assert-True -Condition (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf) -Message 'The bounded CORE failure diagnostic helper must exist.'
if (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf) {
    . $diagnosticsPath
    $tempRoot = Join-Path $repoRoot "tmp/core-test-diagnostics-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    try {
        $trxPath = Join-Path $tempRoot 'failed.trx'
        $trx = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testName="Example.Fails" outcome="Failed">
    <Output><ErrorInfo><Message>Expected value; actual value. ::error::Injected%value password=supersecret</Message></ErrorInfo></Output>
  </UnitTestResult></Results>
</TestRun>
'@
        Set-Content -LiteralPath $trxPath -Value $trx -Encoding utf8
        $logPath = Join-Path $tempRoot 'core-test-process.log'
        Set-Content -LiteralPath $logPath -Value 'unrelated complete process log' -Encoding utf8
        $trxOutput = Get-CoreTestFailureDiagnostics -TrxPaths @($trxPath) -LogPath $logPath -MaxCharacters 1200
        Assert-True -Condition ($trxOutput -match 'Example\.Fails') -Message 'A failed TRX test name must be surfaced.'
        Assert-True -Condition ($trxOutput -match 'Expected value; actual value\.') -Message 'A failed TRX message must be surfaced.'
        Assert-True -Condition ($trxOutput -match '%3A%3Aerror%3A%3AInjected%25value') -Message 'TRX text must escape workflow command syntax and percent signs.'
        Assert-True -Condition ($trxOutput -match 'password=\[REDACTED\]') -Message 'Credential-shaped failure text must be redacted.'
        Assert-True -Condition ($trxOutput -notmatch 'supersecret') -Message 'Failure diagnostics must not expose credential values.'
        Assert-True -Condition ($trxOutput -notmatch 'unrelated complete process log') -Message 'A complete TRX diagnostic must not dump the process log.'

        $fallbackLog = Join-Path $tempRoot 'fallback.log'
        Set-Content -LiteralPath $fallbackLog -Value @('old output', 'tail ::error::Injected%value') -Encoding utf8
        $fallback = Get-CoreTestFailureDiagnostics -TrxPaths @((Join-Path $tempRoot 'missing.trx')) -LogPath $fallbackLog -MaxCharacters 240 -MaxLogLines 2
        Assert-True -Condition ($fallback -match 'tail %3A%3Aerror%3A%3AInjected%25value') -Message 'Fallback output must escape workflow command syntax and percent signs.'
        Assert-True -Condition ($fallback -notmatch 'old output') -Message 'Fallback diagnostics must use only the bounded tail of the log.'

        $truncatedTrxPath = Join-Path $tempRoot 'truncated.trx'
        Set-Content -LiteralPath $truncatedTrxPath -Value '<TestRun><Results><UnitTestResult outcome="Failed"' -Encoding utf8
        $incompleteFallback = Get-CoreTestFailureDiagnostics -TrxPaths @($truncatedTrxPath) -LogPath $fallbackLog -MaxCharacters 240 -MaxLogLines 2
        Assert-True -Condition ($incompleteFallback -match 'tail %3A%3Aerror%3A%3AInjected%25value') -Message 'Malformed TRX must fall back to bounded process-log diagnostics.'

        Set-Content -LiteralPath $fallbackLog -Value @('old output', ('x' * 400)) -Encoding utf8
        $bounded = Get-CoreTestFailureDiagnostics -TrxPaths @((Join-Path $tempRoot 'missing.trx')) -LogPath $fallbackLog -MaxCharacters 240 -MaxLogLines 2
        Assert-True -Condition ($bounded.Length -le 240) -Message 'Failure diagnostics must respect the configured character bound.'
        Assert-True -Condition ($bounded -match 'truncated') -Message 'Oversized failure diagnostics must state that output was truncated.'
        Assert-True -Condition ($bounded -notmatch 'old output') -Message 'Oversized fallback diagnostics must still use only the bounded tail of the log.'
    }
    finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-True -Condition ($workflow -match 'Invoke-BoundedCoreTests\.ps1') -Message 'GitHub CORE must use the bounded runner.'
Assert-True -Condition ($workflow -notmatch 'run: dotnet test tests/dirs\.proj --no-build --filter') -Message 'GitHub CORE must not invoke an unbounded aggregate test host.'
Assert-True -Condition ($workflow -match "CompletionPath.*completion-core\.json") -Message 'GitHub CORE contract validation must require the completion receipt.'
function Test-CoverageOidcContract([string]$WorkflowText) {
    <#
    .SYNOPSIS
    Test CoverageOidcContract for isolated application validation.
    .DESCRIPTION
    Preserves the migrated regression using explicit synthetic values and owned scratch.
    .PARAMETER WorkflowText
    Synthetic WorkflowText input or expected observation.
    .EXAMPLE
    Test-CoverageOidcContract -WorkflowText $WorkflowText
    .OUTPUTS
    System.Object. Fixture observations, or a terminating assertion failure.
    #>

    $fullJob = [regex]::Match($WorkflowText, '(?ms)^  full-tests:\r?\n(?:(?!^  [a-zA-Z0-9_-]+:).)*').Value
    if ([string]::IsNullOrWhiteSpace($fullJob)) {
        return $false
    }
    $outsideJob = $WorkflowText.Replace($fullJob, '')
    if ($outsideJob -match 'id-token\s*:') {
        return $false
    }
    $permissions = [regex]::Match($fullJob, '(?ms)^    permissions:\r?\n(?:(?!^    \S).)*').Value
    $steps = [regex]::Match($fullJob, '(?ms)^    steps:\r?\n.*').Value
    $coverageStep = [regex]::Match($steps, '(?ms)^      - name: Upload coverage\r?\n(?:(?!^      - ).)*').Value
    return ($fullJob -match "(?m)^    if: github\.event_name == 'push'\s*$" -and
        $permissions -match '(?m)^      contents: read\s*$' -and
        $permissions -match '(?m)^      id-token: write\s*$' -and
        ([regex]::Matches($fullJob, 'id-token\s*:').Count -eq 1) -and
        $coverageStep -match '(?m)^        uses: codecov/codecov-action@v4\s*$' -and
        $coverageStep -match '(?m)^          use_oidc: true\s*$' -and
        $coverageStep -match '(?m)^          fail_ci_if_error: true\s*$')
}

Assert-True -Condition (Test-CoverageOidcContract -WorkflowText $workflow) -Message 'Only push-only full-tests may request OIDC, and its Codecov step must authenticate and fail closed.'
Assert-True -Condition (-not (Test-CoverageOidcContract -WorkflowText ("permissions:`n  id-token: write`n" + $workflow))) -Message 'The coverage contract must reject global OIDC permission.'
Assert-True -Condition (-not (Test-CoverageOidcContract -WorkflowText ($workflow -replace '(?m)^  core-tests:', "  core-tests:`n    permissions:`n      id-token: write"))) -Message 'The coverage contract must reject PR-job OIDC permission.'
Assert-True -Condition (-not (Test-CoverageOidcContract -WorkflowText ($workflow -replace 'use_oidc: true', 'use_oidc: false'))) -Message 'The coverage contract must reject unauthenticated coverage.'
Assert-True -Condition (-not (Test-CoverageOidcContract -WorkflowText ($workflow -replace 'fail_ci_if_error: true', 'fail_ci_if_error: false'))) -Message 'The coverage contract must reject masked upload failure.'
Assert-True -Condition (-not (Test-CoverageOidcContract -WorkflowText ($workflow -replace '- name: Upload coverage', '- name: Unrelated step'))) -Message 'The coverage contract must reject inputs outside the named Codecov step.'

$contract = Get-Content -LiteralPath $contractPath -Raw
Assert-True -Condition ($contract -match '\[Parameter\(Mandatory\)\]\[string\] \$CompletionPath') -Message 'The result contract must require a completion receipt.'
Assert-True -Condition ($contract -match 'unfinishedProjects') -Message 'The result contract must reject unfinished expected projects.'
Assert-True -Condition ($contract -match 'completion\.exitCode -ne 0') -Message 'The result contract must reject a non-zero test-process exit.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Output 'CoreTestExecution tests passed.'
exit 0
