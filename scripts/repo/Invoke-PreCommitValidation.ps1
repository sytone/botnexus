<#
.SYNOPSIS
Validates a candidate or reuses qualifying exact-content evidence.
.DESCRIPTION
Selects existing local or remote validation. Remote cached success requires v2
execution evidence matching current external operator qualification, not only the
candidate fingerprint. Legacy remote receipts cannot self-upgrade approval.
BOTNEXUS_RUNNER_RELEASE_MANIFEST selects an external profile through the trusted
coordinator, under ordinary existing user-profile permissions; no candidate configuration
discovery is performed. Trusts the coordinator and local shell. Native peer tool restrictions
do not imply OS/shell isolation or operator-only ACL protection. This entry point can launch
validation when no qualifying receipt exists; do not run it on synthetic fixtures
without blocking both delegates.
.PARAMETER BaseRef
Git comparison base.
.PARAMETER WorktreePath
Candidate repository path.
.PARAMETER ValidationMode
Explicit local or remote selection through the existing mode resolver.
.PARAMETER LocalFallback
Legacy alias selecting local validation.
.PARAMETER RemoteMode
Required remote certification mode, core or strict.
.PARAMETER Hook
Runs the existing bounded advisory local hook path.
.PARAMETER ValidationModeEnvironment
Injected mode resolution environment for isolated tests.
.PARAMETER LegacyFallbackEnvironment
Injected legacy selector values for isolated tests.
.PARAMETER AzureValidationScript
Remote validation delegate, invoked only when no qualifying cache exists.
.PARAMETER LocalValidationScript
Local validation delegate selected by local or advisory mode.
.EXAMPLE
./Invoke-PreCommitValidation.ps1 -WorktreePath $candidate -ValidationMode remote
.OUTPUTS
None. Reports progress and exits with validation status.
#>
[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',
    [string]$WorktreePath = (Get-Location).Path,
    [string]$ValidationMode,
    [switch]$LocalFallback,

    # #3601: browser-emulated E2E is temporarily quarantined while its fixture and locator
    # failures are reviewed. Core remains the complete fail-closed non-browser suite; 'strict'
    # remains available only for focused diagnostics. Explicit full/playwright entry points stay
    # in Invoke-AzureBuildTest.ps1 and the runner, but ordinary validation must not select them.
    [ValidateSet('strict', 'core')]
    [string]$RemoteMode = 'core',

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
if ($null -ne $ValidationModeEnvironment) {
    $selectorParameters.EnvironmentValues = $ValidationModeEnvironment
}
if ($null -ne $LegacyFallbackEnvironment) {
    $selectorParameters.LegacyFallbackValues = $LegacyFallbackEnvironment
}
$selectedMode = Resolve-BotNexusValidationMode @selectorParameters
$gateLabel = if ($Hook) {
    'advisory pre-commit gate'
}
else {
    'strict gate'
}
Write-Information -MessageData "Validation mode: $selectedMode ($gateLabel)." -InformationAction Continue

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
        Write-Information -MessageData "Content-addressed validation receipt matches the exact staged candidate; skipping redundant local validation. $($verification.Reason)" -InformationAction Continue
        exit 0
    }

    if ($Hook) {
        # Impacted-only, bounded, and non-blocking on contention. A pre-commit gate that
        # fails when someone else is validating simply trains everyone to use --no-verify.
        Write-Information -MessageData "No qualifying exact-content receipt ($($verification.Reason)); running the bounded impacted-only pre-commit gate." -InformationAction Continue
        & $LocalValidationScript -WorktreePath $repoRoot -BaseRef $BaseRef -Mode hook -SkipOnLockContention
        exit $LASTEXITCODE
    }

    Write-Information -MessageData "No qualifying exact-content receipt ($($verification.Reason)); running globally serialized local validation." -InformationAction Continue
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
        if (($receipt.version -is [int] -or $receipt.version -is [long]) -and $receipt.version -eq 2 -and
            $receipt.fingerprint -eq $current.fingerprint -and
            $receipt.head -eq $current.head -and
            $receipt.baseRef -eq $current.baseRef -and
            $receipt.baseCommit -eq $current.baseCommit -and
            $receipt.tree -eq $current.tree -and
            $receipt.mode -eq $RemoteMode) {
            Import-Module (Join-Path $PSScriptRoot 'RunnerReleaseCompatibility.psm1') -Force
            $bindingParameters = @{
                Binding = $receipt.runnerRelease
                ManifestPath = $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST
                CandidateRoot = $repoRoot
                DeployedImage = $receipt.runnerRelease.image
                Mode = $RemoteMode
                ExecutionConfiguration = $receipt.executionConfiguration
            }
            Assert-RunnerReleaseBinding @bindingParameters | Out-Null
            if ($receipt.sourceSnapshot.version -ne 1 -or $receipt.sourceSnapshot.verified -isnot [bool] -or
                -not $receipt.sourceSnapshot.verified -or $receipt.sourceSnapshot.digest -cne $current.sourceSnapshot.digest -or
                $receipt.sourceSnapshot.runId -cne $receipt.runId) {
                throw 'Cached receipt exact-source proof is invalid.'
            }
            Write-Information -MessageData "Authoritative Azure validation receipt matches the exact candidate ($($receipt.runId)); skipping redundant remote validation." -InformationAction Continue
            exit 0
        }
        Write-Information -MessageData 'Azure validation receipt does not match the required v2 candidate and qualification contract.' -InformationAction Continue
    }
    catch {
        Write-Warning "Azure validation receipt could not be verified: $($_.Exception.Message)"
    }
}

Write-Information -MessageData "No qualifying exact-content receipt; selected remote Azure Container Apps validation ($RemoteMode)." -InformationAction Continue
& $AzureValidationScript -WorktreePath $repoRoot -BaseRef $BaseRef -Mode $RemoteMode
exit $LASTEXITCODE
