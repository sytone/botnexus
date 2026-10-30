<#
.SYNOPSIS
Recovers the receipt for an existing Azure build/test execution.
.DESCRIPTION
Reads the persisted start context, queries the exact Azure execution and downloads its
canonical artifact prefix. Never uploads source, starts an execution or deletes remote
artifacts. Running, failed, malformed or changed-source runs fail without a receipt.
Use the context emitted by Invoke-AzureBuildTest.ps1; older runs without it cannot recover.
.PARAMETER ContextPath
Path to the run context under the worktree Git directory, printed by the launcher.
.PARAMETER WorktreePath
Unchanged candidate worktree. Defaults to the current directory.
.PARAMETER OutputPath
Optional download destination. Defaults to artifacts/azure-buildtest/<runId>.
.EXAMPLE
./Resume-AzureBuildTest.ps1 -ContextPath $context -WorktreePath $worktree
.OUTPUTS
System.String. Recovered strict-consumer receipt path.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ContextPath,
    [string]$WorktreePath = (Get-Location).Path,
    [string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AzureBuildTestRecovery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.psm1') -Force
$context = Get-Content -LiteralPath $ContextPath -Raw | ConvertFrom-Json
# Do not follow arbitrary URLs from a damaged local context with Azure credentials.
$expectedJobUrl = "https://management.azure.com/subscriptions/$($context.subscriptionId)/resourceGroups/$($context.resourceGroup)/providers/Microsoft.App/jobs/$($context.jobName)"
foreach ($identity in @($context.subscriptionId, $context.resourceGroup, $context.jobName, $context.executionName, $context.runId)) {
    if ($identity -isnot [string] -or $identity -cnotmatch '^[A-Za-z0-9_.()-]+$') {
        throw 'Invalid persisted Azure resource identity.'
    }
}
if ($context.jobUrl -cne $expectedJobUrl -or $context.storageAccount -cnotmatch '^[a-z0-9]{3,24}$') {
    throw 'Invalid persisted Azure job or storage account.'
}
$executionUrl = "$expectedJobUrl/executions/$($context.executionName)?api-version=2024-03-01"
$providerJson = & az rest --method get --url $executionUrl --output json
if ($LASTEXITCODE -ne 0) {
    throw 'Could not query the existing Azure execution.'
}
$provider = ($providerJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($provider.properties.status -cne 'Succeeded') {
    throw "Existing execution is not successful: $($provider.properties.status). No new execution was started."
}
if (-not $OutputPath) {
    $OutputPath = Join-Path $WorktreePath "artifacts/azure-buildtest/$($context.runId)"
}
$staging = Join-Path ([IO.Path]::GetTempPath()) "recovery-download-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    & az storage blob download-batch --subscription $context.subscriptionId --account-name $context.storageAccount --source artifacts --destination $staging --pattern "$($context.runId)/*" --auth-mode login --overwrite true --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw 'Existing-run artifact download failed; no receipt was written.'
    }
    # Require the canonical freshly downloaded file, never an old result in OutputPath.
    $runArtifacts = Join-Path $staging $context.runId
    $result = Get-Content -LiteralPath (Join-Path $runArtifacts 'result.json') -Raw | ConvertFrom-Json
    Assert-AzureBuildTestArtifact -Mode $context.mode -Path $runArtifacts
    $OutputPath = Move-AzureBuildTestArtifacts -StagingRoot $staging -RunId $context.runId -Destination $OutputPath
    Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $WorktreePath -ArtifactPath $OutputPath
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
