[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$runnerPath = Join-Path $repoRoot 'scripts/repo/Invoke-BoundedCoreTests.ps1'
$workflowPath = Join-Path $repoRoot '.github/workflows/ci-build-test.yml'
$contractPath = Join-Path $repoRoot 'scripts/repo/Assert-TestResultContract.ps1'
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
