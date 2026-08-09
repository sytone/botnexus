[CmdletBinding()]
param(
    [string]$SubscriptionId = $env:BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID,
    [string]$ResourceGroup = $env:BOTNEXUS_BUILDTEST_RESOURCE_GROUP,
    [string]$Location = $(if ($env:BOTNEXUS_BUILDTEST_LOCATION) { $env:BOTNEXUS_BUILDTEST_LOCATION } else { 'westus2' }),
    # DELIBERATELY EMPTY. The tag is DERIVED from the content of infra/buildtest/runner/, not
    # chosen by a human (#2900).
    #
    # A hand-maintained version number here was wrong in three independent ways at once:
    #   1. It drifted. This line read '0.1.11' while the deployed job ran '0.1.15'.
    #   2. ACR tags are MUTABLE, so picking the "next" number by reading only this line
    #      silently OVERWROTE the existing 0.1.12 image on 2026-08-09.
    #   3. Nothing tied the number to the content, so an unchanged runner could be republished
    #      under a new tag, and a changed runner could be published under an old one.
    #
    # A content hash fixes all three by construction: identical content always produces the same
    # tag (so a rebuild is a no-op rather than an overwrite), different content always produces a
    # different tag (so a change can never reuse an existing tag), and there is no number left to
    # drift. Override only to pin an explicit historical tag for a rollback.
    #
    # Historical tag record, kept for rollback reference now that the sequence has ended:
    #   0.1.8 node; 0.1.9 ABANDONED polling-watcher experiment that REGRESSED results, do not use;
    #   0.1.10 node+inotify; 0.1.11 +runner-env artifact; 0.1.12-0.1.15 incremental runner fixes;
    #   0.1.16 +runner-timing (#2889); 0.1.17 +build-release phase (#2914).
    [string]$RunnerImageTag
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

# Derive the tag from runner content unless the caller pinned one explicitly (#2900).
#
# Hash every file that lands in the image, ordered deterministically so the same content always
# yields the same tag regardless of filesystem enumeration order. Line endings are normalised
# because a CRLF/LF difference between a Windows and a Linux operator is not a content change and
# must not mint a new tag.
$tagWasDerived = $false
if ([string]::IsNullOrWhiteSpace($RunnerImageTag)) {
    $runnerFiles = Get-ChildItem -Path $runnerPath -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/]tests[\\/]' } |
        Sort-Object { $_.FullName.Substring($runnerPath.Length).Replace('\', '/') }

    if (-not $runnerFiles) {
        throw "Refusing to derive an image tag: no files found under $runnerPath. An empty candidate set would hash to a constant and pin every future build to one tag."
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $buffer = [System.IO.MemoryStream]::new()
        foreach ($file in $runnerFiles) {
            $relative = $file.FullName.Substring($runnerPath.Length).Replace('\', '/').TrimStart('/')
            $content = (Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop) -replace "`r`n", "`n"
            $bytes = [System.Text.Encoding]::UTF8.GetBytes("$relative`n$content`n")
            $buffer.Write($bytes, 0, $bytes.Length)
        }
        $buffer.Position = 0
        $RunnerImageTag = 'src-' + [BitConverter]::ToString($sha.ComputeHash($buffer)).Replace('-', '').Substring(0, 12).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }

    $tagWasDerived = $true
    Write-Host "Runner image tag derived from content: $RunnerImageTag ($($runnerFiles.Count) files)"
}
else {
    Write-Host "Runner image tag pinned explicitly: $RunnerImageTag"
}

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

# FAIL CLOSED ON TAG COLLISION (#2900).
#
# ACR tags are mutable: `az acr build` against an existing tag overwrites it with no warning and
# exit 0. That destroyed the historical 0.1.12 image on 2026-08-09.
#
# A derived tag that already exists means the content is byte-identical to what is already
# published, so the build is a genuine no-op and is skipped rather than repeated.
#
# An EXPLICIT tag that already exists is the dangerous case and is refused outright: the operator
# named a tag that holds different content, which is exactly the overwrite this guard exists to
# prevent. Deleting or reusing it has to be a deliberate, separate act.
$existingTags = @(az acr repository show-tags --subscription $SubscriptionId --name $acrName --repository botnexus-buildtest-runner -o tsv 2>$null)
$tagExists = $existingTags -contains $RunnerImageTag

if ($tagExists -and -not $tagWasDerived) {
    throw "Refusing to overwrite the existing image tag '$RunnerImageTag'. ACR tags are mutable, so publishing over it would destroy the image that tag currently points at. Omit -RunnerImageTag to derive a content-addressed tag, or delete the tag deliberately first."
}

if ($tagExists) {
    Write-Host "Runner image $RunnerImageTag already published and content is unchanged; skipping build."
}
else {
    az acr build `
        --subscription $SubscriptionId `
        --registry $acrName `
        --image "botnexus-buildtest-runner:$RunnerImageTag" `
        --file (Join-Path $runnerPath 'Dockerfile') `
        $runnerPath
    if ($LASTEXITCODE -ne 0) { throw 'Runner image build failed.' }
}

az deployment group create `
    --subscription $SubscriptionId `
    --resource-group $ResourceGroup `
    --name buildtest-platform `
    --template-file $templatePath `
    --parameters location=$Location operatorObjectId=$operatorObjectId suffix=$suffix runnerImageTag=$RunnerImageTag `
    --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Build/test infrastructure deployment failed.' }
