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
    if (-not $Condition) { $failures.Add($Message) }
}

Assert-True (Test-Path -LiteralPath $runnerPath -PathType Leaf) 'The bounded CORE runner script must exist.'
if (Test-Path -LiteralPath $runnerPath -PathType Leaf) {
    $runner = Get-Content -LiteralPath $runnerPath -Raw
    Assert-True ($runner -match 'Invoke-BoundedProcess') 'The CORE runner must enforce a child-process deadline.'
    Assert-True ($runner -match 'Get-UnfinishedTestProjects') 'The CORE runner must identify projects without terminal results.'
    Assert-True ($runner -match 'Set-Content -LiteralPath \$CompletionPath') 'The CORE runner must emit a completion receipt.'
    Assert-True ($runner -match 'exit 124') 'A timed-out CORE runner must use a distinct timeout exit code.'
    Assert-True ($runner -match '--blame-hang-timeout') 'The CORE runner must request VSTest hang diagnostics below the job deadline.'
    Assert-True ($runner -match 'Get-CoreTestFailureDiagnostics') 'A failed CORE run must render bounded failure diagnostics.'
}

Assert-True (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf) 'The bounded CORE failure diagnostic helper must exist.'
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
        Assert-True ($trxOutput -match 'Example\.Fails') 'A failed TRX test name must be surfaced.'
        Assert-True ($trxOutput -match 'Expected value; actual value\.') 'A failed TRX message must be surfaced.'
        Assert-True ($trxOutput -match '%3A%3Aerror%3A%3AInjected%25value') 'TRX text must escape workflow command syntax and percent signs.'
        Assert-True ($trxOutput -match 'password=\[REDACTED\]') 'Credential-shaped failure text must be redacted.'
        Assert-True ($trxOutput -notmatch 'supersecret') 'Failure diagnostics must not expose credential values.'
        Assert-True ($trxOutput -notmatch 'unrelated complete process log') 'A complete TRX diagnostic must not dump the process log.'

        $fallbackLog = Join-Path $tempRoot 'fallback.log'
        Set-Content -LiteralPath $fallbackLog -Value @('old output', 'tail ::error::Injected%value') -Encoding utf8
        $fallback = Get-CoreTestFailureDiagnostics -TrxPaths @((Join-Path $tempRoot 'missing.trx')) -LogPath $fallbackLog -MaxCharacters 240 -MaxLogLines 2
        Assert-True ($fallback -match 'tail %3A%3Aerror%3A%3AInjected%25value') 'Fallback output must escape workflow command syntax and percent signs.'
        Assert-True ($fallback -notmatch 'old output') 'Fallback diagnostics must use only the bounded tail of the log.'

        $truncatedTrxPath = Join-Path $tempRoot 'truncated.trx'
        Set-Content -LiteralPath $truncatedTrxPath -Value '<TestRun><Results><UnitTestResult outcome="Failed"' -Encoding utf8
        $incompleteFallback = Get-CoreTestFailureDiagnostics -TrxPaths @($truncatedTrxPath) -LogPath $fallbackLog -MaxCharacters 240 -MaxLogLines 2
        Assert-True ($incompleteFallback -match 'tail %3A%3Aerror%3A%3AInjected%25value') 'Malformed TRX must fall back to bounded process-log diagnostics.'

        Set-Content -LiteralPath $fallbackLog -Value @('old output', ('x' * 400)) -Encoding utf8
        $bounded = Get-CoreTestFailureDiagnostics -TrxPaths @((Join-Path $tempRoot 'missing.trx')) -LogPath $fallbackLog -MaxCharacters 240 -MaxLogLines 2
        Assert-True ($bounded.Length -le 240) 'Failure diagnostics must respect the configured character bound.'
        Assert-True ($bounded -match 'truncated') 'Oversized failure diagnostics must state that output was truncated.'
        Assert-True ($bounded -notmatch 'old output') 'Oversized fallback diagnostics must still use only the bounded tail of the log.'
    }
    finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-True ($workflow -match 'Invoke-BoundedCoreTests\.ps1') 'GitHub CORE must use the bounded runner.'
Assert-True ($workflow -notmatch 'run: dotnet test tests/dirs\.proj --no-build --filter') 'GitHub CORE must not invoke an unbounded aggregate test host.'
Assert-True ($workflow -match "CompletionPath.*completion-core\.json") 'GitHub CORE contract validation must require the completion receipt.'

$contract = Get-Content -LiteralPath $contractPath -Raw
Assert-True ($contract -match '\[Parameter\(Mandatory\)\]\[string\] \$CompletionPath') 'The result contract must require a completion receipt.'
Assert-True ($contract -match 'unfinishedProjects') 'The result contract must reject unfinished expected projects.'
Assert-True ($contract -match 'completion\.exitCode -ne 0') 'The result contract must reject a non-zero test-process exit.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host 'CoreTestExecution tests passed.' -ForegroundColor Green
exit 0
