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
    #   0.1.16 +runner-timing (#2889); 0.1.17 +build-release phase (#2914). 0.1.17 was the LAST
    #   hand-picked tag; everything after it is content-addressed as src-<sha256[0:12]>.
    [string]$RunnerImageTag,

    # Bounded wall-clock ceiling for the deployment poll loop (#3118). A deployment that has not
    # reached a terminal state by then throws rather than looping forever -- an unattended BCDR
    # rebuild must fail loudly, not stall silently the way the synchronous CLI call did.
    [int]$DeploymentTimeoutMinutes = 45
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
    # FORCE UTF-8 ON THE CLI'S OWN STDOUT (#3314).
    #
    # MEASURED, TWICE, DETERMINISTICALLY: `az acr build` streams the remote build log through
    # colorama, which writes to a Windows console encoded cp1252 by default. The apt output of
    # this very Dockerfile contains U+2192 ('->'), so the CLI dies mid-stream with
    # `UnicodeEncodeError: 'charmap' codec can't encode character '\u2192'` and exits non-zero.
    #
    # THE BUILD ITSELF SUCCEEDS. The failure is purely in printing the log, but it lands after
    # `az acr build` has already queued the run, so the throw below reports "Runner image build
    # failed" for an image that is being published normally. That is the worst shape of false
    # alarm: it stops the deployment while the thing it claims failed is fine, and the operator
    # cannot tell without going to the registry by hand -- which is exactly what #3314 had to do
    # before the runner could be deployed at all.
    #
    # PYTHONIOENCODING is the documented Python-level control and is scoped to this call.
    $previousIoEncoding = $env:PYTHONIOENCODING
    $env:PYTHONIOENCODING = 'utf-8'
    try {
        az acr build `
            --subscription $SubscriptionId `
            --registry $acrName `
            --image "botnexus-buildtest-runner:$RunnerImageTag" `
            --file (Join-Path $runnerPath 'Dockerfile') `
            $runnerPath
        $buildExitCode = $LASTEXITCODE
    }
    finally {
        $env:PYTHONIOENCODING = $previousIoEncoding
    }

    # VERIFY AGAINST THE REGISTRY, NOT THE EXIT CODE. The registry is the authority on whether
    # the image exists; the exit code additionally reports console-encoding faults in the log
    # stream. Re-checking the tag turns a cosmetic streaming failure into a no-op instead of a
    # blocked deployment, while a genuinely failed build still throws because the tag is absent.
    if ($buildExitCode -ne 0) {
        $tagsAfterBuild = @(az acr repository show-tags --subscription $SubscriptionId --name $acrName --repository botnexus-buildtest-runner -o tsv 2>$null)
        if ($tagsAfterBuild -contains $RunnerImageTag) {
            Write-Warning "az acr build exited $buildExitCode, but tag '$RunnerImageTag' is present in the registry, so the image published and only the log stream failed. Continuing."
        }
        else {
            throw "Runner image build failed (az acr build exited $buildExitCode and tag '$RunnerImageTag' is not present in the registry)."
        }
    }
}

# SUBMIT ASYNCHRONOUSLY, THEN POLL (#3118).
#
# A synchronous `az deployment group create` against this subscription HANGS INDEFINITELY and --
# critically -- had submitted nothing when it did: no deployment recorded, no resource created.
# That made an unattended BCDR rebuild impossible, because the only recovery was a human noticing
# the stall and interrupting the CLI. The mechanism was never diagnosed; what IS established is
# that `--no-wait` plus explicit polling returns immediately and completes normally.
#
# So: never remove `--no-wait` from a deployment submission here. The wait is ours to own, with a
# bounded ceiling and ARM's own error detail surfaced on failure.
function Invoke-BuildTestDeployment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DeploymentName,
        [Parameter(Mandatory)][string]$TemplateFile,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Parameters,
        [int]$TimeoutMinutes = 45,
        [int]$PollSeconds = 15
    )

    az deployment group create `
        --subscription $SubscriptionId `
        --resource-group $ResourceGroup `
        --name $DeploymentName `
        --template-file $TemplateFile `
        --parameters @Parameters `
        --no-wait `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "Submission of deployment '$DeploymentName' failed." }

    Write-Host "Deployment '$DeploymentName' submitted; polling for terminal state (timeout ${TimeoutMinutes}m)..."

    $terminal = @('Succeeded', 'Failed', 'Canceled')
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $state = $null

    while ($true) {
        $state = az deployment group show `
            --subscription $SubscriptionId `
            --resource-group $ResourceGroup `
            --name $DeploymentName `
            --query properties.provisioningState -o tsv 2>$null
        if ($state) { $state = "$state".Trim() }

        if ($state -and $terminal -contains $state) { break }

        if ((Get-Date) -ge $deadline) {
            $observed = if ($state) { $state } else { 'unknown' }
            throw "Deployment '$DeploymentName' did not reach a terminal state within $TimeoutMinutes minutes (last observed state: '$observed'). Inspect it with: az deployment group show --subscription $SubscriptionId -g $ResourceGroup --name $DeploymentName"
        }

        Start-Sleep -Seconds $PollSeconds
    }

    if ($state -ne 'Succeeded') {
        # Surface ARM's own error payload. A bare non-zero exit tells the operator that something
        # failed but not what, which is exactly the information a BCDR rebuild needs at 3am.
        $detail = az deployment group show `
            --subscription $SubscriptionId `
            --resource-group $ResourceGroup `
            --name $DeploymentName `
            --query properties.error -o json 2>$null
        if ([string]::IsNullOrWhiteSpace($detail)) { $detail = '(no properties.error returned by ARM)' }
        throw "Deployment '$DeploymentName' finished in state '$state'. ARM error detail: $detail"
    }

    Write-Host "Deployment '$DeploymentName' succeeded."
}

Invoke-BuildTestDeployment `
    -DeploymentName 'buildtest-platform' `
    -TemplateFile $templatePath `
    -Parameters @("location=$Location", "operatorObjectId=$operatorObjectId", "suffix=$suffix", "runnerImageTag=$RunnerImageTag") `
    -TimeoutMinutes $DeploymentTimeoutMinutes

