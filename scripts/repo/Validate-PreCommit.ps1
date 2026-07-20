[CmdletBinding()]
param([string]$BaseRef = 'origin/main')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (& git rev-parse --show-toplevel).Trim()
$fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
$gitDirectory = (& git -C $repoRoot rev-parse --git-dir).Trim()
if (-not [IO.Path]::IsPathRooted($gitDirectory)) { $gitDirectory = Join-Path $repoRoot $gitDirectory }
$receiptPath = Join-Path $gitDirectory 'botnexus-validation/azure-buildtest.json'

if (Test-Path $receiptPath) {
    try {
        $receipt = Get-Content $receiptPath -Raw | ConvertFrom-Json
        $current = & $fingerprintScript -WorktreePath $repoRoot -BaseRef $BaseRef
        if ($receipt.version -eq 1 -and
            $receipt.fingerprint -eq $current.fingerprint -and
            $receipt.mode -in @('impacted', 'full')) {
            Write-Host "Remote Azure validation receipt matches the exact commit contents ($($receipt.runId)); skipping local build and tests." -ForegroundColor Green
            exit 0
        }
        Write-Host 'Remote validation receipt is stale because the commit contents or base ref changed.' -ForegroundColor Yellow
    }
    catch {
        Write-Warning "Remote validation receipt could not be verified: $($_.Exception.Message)"
    }
}

Write-Host 'No matching remote validation receipt; running local build and Gateway tests.' -ForegroundColor Cyan
dotnet build (Join-Path $repoRoot 'BotNexus.slnx') --nologo --verbosity minimal --tl:off
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'Invoke-GatewayTestsWithFirewall.ps1')
exit $LASTEXITCODE
