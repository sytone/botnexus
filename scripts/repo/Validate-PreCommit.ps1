[CmdletBinding()]
param(
    [string]$BaseRef = 'origin/main',
    [string]$WorktreePath = (Get-Location).Path,
    [switch]$LocalFallback,
    [string]$AzureValidationScript = (Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'),
    [string]$LocalValidationScript = (Join-Path $PSScriptRoot 'Invoke-LocalValidation.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $LocalFallback -and $env:BOTNEXUS_VALIDATION_LOCAL_FALLBACK -eq '1') { $LocalFallback = $true }

$repoRoot = (& git -C $WorktreePath rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "WorktreePath is not inside a git repository: $WorktreePath"
}

$fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
$gitDirectory = (& git -C $repoRoot rev-parse --absolute-git-dir).Trim()
$receiptPath = Join-Path $gitDirectory 'botnexus-validation/azure-buildtest.json'

if (-not $LocalFallback -and (Test-Path $receiptPath)) {
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
            Write-Host "Authoritative Azure validation receipt matches the exact candidate ($($receipt.runId)); skipping redundant validation." -ForegroundColor Green
            exit 0
        }
        Write-Host 'Azure validation receipt does not match the exact candidate tree and base commit.' -ForegroundColor Yellow
    }
    catch {
        Write-Warning "Azure validation receipt could not be verified: $($_.Exception.Message)"
    }
}

if ($LocalFallback) {
    Write-Warning 'Explicit local validation fallback selected. Validation is globally serialized on this host.'
    & $LocalValidationScript -WorktreePath $repoRoot -BaseRef $BaseRef -Mode strict
    exit $LASTEXITCODE
}

Write-Host 'No qualifying exact-content receipt; Azure Container Apps validation is authoritative.' -ForegroundColor Cyan
Write-Host 'If Azure is unavailable, rerun with -LocalFallback to use the explicit globally serialized local gate.' -ForegroundColor DarkGray
& $AzureValidationScript -WorktreePath $repoRoot -BaseRef $BaseRef -Mode strict
exit $LASTEXITCODE
