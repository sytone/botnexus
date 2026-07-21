[CmdletBinding()]
param(
    [string]$SubscriptionId = $env:BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID,
    [string]$ResourceGroup = $env:BOTNEXUS_BUILDTEST_RESOURCE_GROUP,
    [string]$Location = $(if ($env:BOTNEXUS_BUILDTEST_LOCATION) { $env:BOTNEXUS_BUILDTEST_LOCATION } else { 'westus2' }),
    [string]$RunnerImageTag = '0.1.7'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SubscriptionId) -or [string]::IsNullOrWhiteSpace($ResourceGroup)) {
    throw 'Set BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID and BOTNEXUS_BUILDTEST_RESOURCE_GROUP, or pass -SubscriptionId and -ResourceGroup.'
}

$operatorObjectId = az ad signed-in-user show --query id -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($operatorObjectId)) {
    throw 'Unable to resolve the signed-in Azure user object ID.'
}
$operatorObjectId = $operatorObjectId.Trim()
$suffix = ($SubscriptionId -replace '-', '').Substring(($SubscriptionId -replace '-', '').Length - 8).ToLowerInvariant()
$templatePath = Join-Path $PSScriptRoot 'main.bicep'

az provider register --subscription $SubscriptionId --namespace Microsoft.App --wait
if ($LASTEXITCODE -ne 0) { throw 'Microsoft.App provider registration failed.' }

$acrName = "bnxbt${suffix}acr"
$runnerPath = Join-Path $PSScriptRoot 'runner'

# The Container Apps Job cannot reference a tag that does not exist. Provision the shared
# registry first, publish the runner, and only then deploy/update the job template.
if (-not (az acr show --subscription $SubscriptionId --resource-group $ResourceGroup --name $acrName --query name -o tsv 2>$null)) {
    az acr create `
        --subscription $SubscriptionId `
        --resource-group $ResourceGroup `
        --name $acrName `
        --location $Location `
        --sku Premium `
        --admin-enabled false `
        --public-network-enabled true `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Runner registry provisioning failed.' }
}

az acr build `
    --subscription $SubscriptionId `
    --registry $acrName `
    --image "botnexus-buildtest-runner:$RunnerImageTag" `
    --file (Join-Path $runnerPath 'Dockerfile') `
    $runnerPath
if ($LASTEXITCODE -ne 0) { throw 'Runner image build failed.' }

az deployment group create `
    --subscription $SubscriptionId `
    --resource-group $ResourceGroup `
    --name buildtest-platform `
    --template-file $templatePath `
    --parameters location=$Location operatorObjectId=$operatorObjectId suffix=$suffix runnerImageTag=$RunnerImageTag `
    --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Build/test infrastructure deployment failed.' }
