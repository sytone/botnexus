[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',
    [string]$WorktreePath = (Get-Location).Path,
    [string]$ValidationMode,
    [switch]$LocalFallback,

    # #2825: remote validation defaults to 'full' - the whole solution, not the impacted
    # subset. The impacted-test narrowing exists to spare a developer workstation's CPU;
    # that constraint does not apply to an ephemeral container, and strict was measured to
    # exercise ~4,700 of 13,088 tests while reporting zeroed counters.
    [ValidateSet('strict', 'full')]
    [string]$RemoteMode = 'full',

    # Advisory pre-commit scope (#2331): impacted projects only, bounded per step, and a
    # clean skip when another validation holds the global lock. The authoritative gate is
    # unchanged and still runs at pre-push and in CI.
    [switch]$Hook,

    [System.Collections.IDictionary]$ValidationModeEnvironment,
    [System.Collections.IDictionary]$LegacyFallbackEnvironment,
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
if ($null -ne $LegacyFallbackEnvironment) { $selectorParameters.LegacyFallbackValues = $LegacyFallbackEnvironment }
$selectedMode = Resolve-BotNexusValidationMode @selectorParameters
$gateLabel = if ($Hook) { 'advisory pre-commit gate' } else { 'strict gate' }
Write-Host "Validation mode: $selectedMode ($gateLabel)." -ForegroundColor Cyan

$repoRoot = (& git -C $WorktreePath rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "WorktreePath is not inside a git repository: $WorktreePath"
}

# Content-addressed receipt fast path (issue #2143): if the exact staged candidate has
# already passed the current required strict policy, skip redundant build/test. Any
# missing, malformed, failed, stale, expired, or mismatched receipt fails closed by
# running the normal gate below. This applies to the advisory hook too - it is the
# common case where strict validation has just been run by hand.
if ($selectedMode -eq 'local' -or $Hook) {
    Import-Module (Join-Path $PSScriptRoot 'ValidationReceipt.psm1') -Force
    $verification = Test-BotNexusValidationReceipt -WorktreePath $repoRoot -BaseRef $BaseRef -RequiredScopes @('strict')
    if ($verification.Match) {
        Write-Host "Content-addressed validation receipt matches the exact staged candidate; skipping redundant local validation. $($verification.Reason)" -ForegroundColor Green
        exit 0
    }

    if ($Hook) {
        # Impacted-only, bounded, and non-blocking on contention. A pre-commit gate that
        # fails when someone else is validating simply trains everyone to use --no-verify.
        Write-Host "No qualifying exact-content receipt ($($verification.Reason)); running the bounded impacted-only pre-commit gate." -ForegroundColor Yellow
        & $LocalValidationScript -WorktreePath $repoRoot -BaseRef $BaseRef -Mode hook -SkipOnLockContention
        exit $LASTEXITCODE
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
            $receipt.mode -eq $RemoteMode) {
            Write-Host "Authoritative Azure validation receipt matches the exact candidate ($($receipt.runId)); skipping redundant remote validation." -ForegroundColor Green
            exit 0
        }
        Write-Host 'Azure validation receipt does not match the exact candidate tree and base commit.' -ForegroundColor Yellow
    }
    catch {
        Write-Warning "Azure validation receipt could not be verified: $($_.Exception.Message)"
    }
}

Write-Host "No qualifying exact-content receipt; selected remote Azure Container Apps validation ($RemoteMode)." -ForegroundColor Cyan
& $AzureValidationScript -WorktreePath $repoRoot -BaseRef $BaseRef -Mode $RemoteMode
exit $LASTEXITCODE
