# Contract for the temporary browser-emulated E2E quarantine (#3601).
#
# Browser suites remain in source and retain explicit diagnostic entry points, but ordinary strict
# validation must use the fail-closed core scope. This prevents noisy browser emulation from
# consuming the authoritative gate while the suite is reviewed.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = @()
function Assert-True {
    param([bool]$Condition, [string]$Because)
    if (-not $Condition) { $script:failures += $Because }
}

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$validatePath = Join-Path $repoRoot 'scripts/repo/Validate-PreCommit.ps1'
$runnerPath = Join-Path $repoRoot 'infra/buildtest/runner/entrypoint.ps1'
$workflowPath = Join-Path $repoRoot '.github/workflows/ci-build-test.yml'

$validate = Get-Content -LiteralPath $validatePath -Raw
$runner = Get-Content -LiteralPath $runnerPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw

Assert-True ($validate -match "\[ValidateSet\('strict', 'core'\)\]") '1: strict gate does not constrain RemoteMode to strict/core'
Assert-True ($validate -match '\[string\]\$RemoteMode\s*=\s*''core''') '1: strict remote validation does not default to core'
Assert-True ($runner -match "'core'\s*\{") '2: runner lost the explicit core mode'
Assert-True ($runner -match 'FullyQualifiedName!~BotNexus\.Integration\.E2E&FullyQualifiedName!~BotNexus\.E2E') '2: core mode no longer excludes both browser-E2E namespaces'
Assert-True ($runner -match "'full'\s*\{" -and $runner -match "'playwright'\s*\{") '3: explicit diagnostic full/playwright modes were removed rather than quarantined'
Assert-True ($workflow -notmatch '(?m)^\s*e2e-portal-playwright:\s*$') '4: automatic CI still declares the browser-emulation job'
Assert-True ($workflow -match 'Browser-emulated E2E is quarantined') '5: CI does not state the temporary quarantine/review contract'
Assert-True ($workflow -match '#3601') '5: quarantine lacks its issue-backed review/re-entry owner'

if ($script:failures.Count) {
    Write-Host "FAILED ($($script:failures.Count)):" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Browser E2E quarantine: all checks passed.' -ForegroundColor Green
exit 0
