[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',
    [string]$WorktreePath = (Get-Location).Path,
    [string]$ValidationMode,
    [switch]$LocalFallback,
    [System.Collections.IDictionary]$ValidationModeEnvironment,
    [string]$AzureValidationScript = (Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'),
    [string]$LocalValidationScript = (Join-Path $PSScriptRoot 'Invoke-LocalValidation.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-ValidationMode.ps1')
$selectorParameters = @{
    RequestedMode = $ValidationMode
    LocalFallback = $LocalFallback
}
if ($null -ne $ValidationModeEnvironment) { $selectorParameters.EnvironmentValues = $ValidationModeEnvironment }
$selectedMode = Resolve-BotNexusValidationMode @selectorParameters
Write-Host "Validation mode: $selectedMode (strict gate)." -ForegroundColor Cyan

$repoRoot = (& git -C $WorktreePath rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "WorktreePath is not inside a git repository: $WorktreePath"
}

if ($selectedMode -eq 'local') {
    # Content-addressed receipt fast path (issue #2143): if the exact staged candidate has
    # already passed the current required strict policy, skip redundant build/test. Any
    # missing, malformed, failed, stale, expired, or mismatched receipt fails closed by
    # running the normal local gate below.
    Import-Module (Join-Path $PSScriptRoot 'ValidationReceipt.psm1') -Force
    $verification = Test-BotNexusValidationReceipt -WorktreePath $repoRoot -BaseRef $BaseRef -RequiredScopes @('strict')
    if ($verification.Match) {
        Write-Host "Content-addressed validation receipt matches the exact staged candidate; skipping redundant local validation. $($verification.Reason)" -ForegroundColor Green
        exit 0
    }
    Write-Host "No qualifying exact-content receipt ($($verification.Reason)); running globally serialized local validation." -ForegroundColor Yellow
    & $LocalValidationScript -WorktreePath $repoRoot -BaseRef $BaseRef -Mode strict
    exit $LASTEXITCODE
}

$fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
$gitDirectory = (& git -C $repoRoot rev-parse --absolute-git-dir).Trim()
$receiptPath = Join-Path $gitDirectory 'botnexus-validation/azure-buildtest.json'
if (Test-Path $receiptPath) {
    try {
        $receipt = Get-Content $receiptPath -Raw | ConvertFrom-Json
        $current = & $fingerprintScript -WorktreePath $repoRoot -BaseRef $BaseRef
        if ($receipt.version -eq 1 -and
            $receipt.fingerprint -eq $current.fingerprint -and
            $receipt.head -eq $current.head -and
            $receipt.baseRef -eq $current.baseRef -and
            $receipt.baseCommit -eq $current.baseCommit -and
            $receipt.tree -eq $current.tree -and
            $receipt.mode -eq 'strict') {
            Write-Host "Authoritative Azure validation receipt matches the exact candidate ($($receipt.runId)); skipping redundant remote validation." -ForegroundColor Green
            exit 0
        }
        Write-Host 'Azure validation receipt does not match the exact candidate tree and base commit.' -ForegroundColor Yellow
    }
    catch {
        Write-Warning "Azure validation receipt could not be verified: $($_.Exception.Message)"
    }
}

Write-Host 'No qualifying exact-content receipt; selected remote Azure Container Apps validation.' -ForegroundColor Cyan
& $AzureValidationScript -WorktreePath $repoRoot -BaseRef $BaseRef -Mode strict
exit $LASTEXITCODE
