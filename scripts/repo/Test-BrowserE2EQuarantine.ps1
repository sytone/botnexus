<#
.SYNOPSIS
Tests browser quarantine source policy.
.DESCRIPTION
Uses source inspection and isolated synthetic fixtures only. Does not launch a product gateway, dotnet test host or Azure job.
.EXAMPLE
./Test-BrowserE2EQuarantine.ps1
.OUTPUTS
None. Reports assertion failures with a nonzero exit code.
#>
# Contract for the temporary browser-emulated E2E quarantine (#3601).
#
# Browser suites remain in source and retain explicit diagnostic entry points, but ordinary strict
# validation must use the fail-closed core scope. This prevents noisy browser emulation from
# consuming the authoritative gate while the suite is reviewed.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = @()
<#
.SYNOPSIS
Records a failed boolean assertion.
.DESCRIPTION
Preserves all assertions and continues collecting failures.
.PARAMETER Condition
Actual boolean condition.
.PARAMETER Because
Failure explanation.
.EXAMPLE
Assert-True -Condition $true -Because 'Expected policy.'
.OUTPUTS
None. Reports assertion failures with a nonzero exit code.
#>
function Assert-True {
    param([bool]$Condition, [string]$Because)
    if (-not $Condition) {
        $script:failures += $Because
    }
}

$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
$validatePath = Join-Path $repoRoot 'scripts/repo/Invoke-PreCommitValidation.ps1'
$runnerPath = Join-Path $repoRoot 'infra/buildtest/runner/entrypoint.ps1'
$workflowPath = Join-Path $repoRoot '.github/workflows/ci-build-test.yml'

$validate = Get-Content -LiteralPath $validatePath -Raw
$runner = Get-Content -LiteralPath $runnerPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw

Assert-True -Condition ($validate -match "\[ValidateSet\('strict', 'core'\)\]") -Because '1: strict gate does not constrain RemoteMode to strict/core'
Assert-True -Condition ($validate -match '\[string\]\$RemoteMode\s*=\s*''core''') -Because '1: strict remote validation does not default to core'
Assert-True -Condition ($runner -match "'core'\s*\{") -Because '2: runner lost the explicit core mode'
Assert-True -Condition ($runner -match 'FullyQualifiedName!~BotNexus\.Integration\.E2E&FullyQualifiedName!~BotNexus\.E2E') -Because '2: core mode no longer excludes both browser-E2E namespaces'
Assert-True -Condition ($runner -match "'full'\s*\{" -and $runner -match "'playwright'\s*\{") -Because '3: explicit diagnostic full/playwright modes were removed rather than quarantined'
Assert-True -Condition ($workflow -notmatch '(?m)^\s*e2e-portal-playwright:\s*$') -Because '4: automatic CI still declares the browser-emulation job'
Assert-True -Condition ($workflow -match 'Browser-emulated E2E is quarantined') -Because '5: CI does not state the temporary quarantine/review contract'
Assert-True -Condition ($workflow -match '#3601') -Because '5: quarantine lacks its issue-backed review/re-entry owner'

if ($script:failures.Count) {
    Write-Output "FAILED ($($script:failures.Count)):"
    $script:failures | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output 'Browser E2E quarantine: all checks passed.'
exit 0
