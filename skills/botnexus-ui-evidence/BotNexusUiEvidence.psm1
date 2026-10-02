Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-UiEvidenceContainerRuntime {
    [CmdletBinding()]
    param([scriptblock]$CommandResolver = { param($Name) Get-Command $Name -ErrorAction SilentlyContinue })
    foreach ($name in @('docker', 'podman')) {
        if (& $CommandResolver $name) { return $name }
    }
    throw 'Docker or Podman is required; neither executable was found.'
}

function New-UiEvidenceContainerArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Image,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$RepositoryPath,
        [Parameter(Mandatory)][string]$OutputVolumeName,
        [Parameter(Mandatory)][string]$ScenarioJson
    )
    @(
        'create', '--name', $ContainerName, '--network=none', '--read-only',
        '--tmpfs', '/tmp:rw,noexec,nosuid,size=512m',
        '--tmpfs', '/evidence:rw,nosuid,size=512m',
        '--mount', "type=volume,source=$OutputVolumeName,target=/evidence-output",
        '--env', "BOTNEXUS_EVIDENCE_SCENARIO=$ScenarioJson",
        $Image
    )
}

function Invoke-UiEvidenceRuntime {
    param([string]$Runtime,[string[]]$Arguments,[switch]$AllowFailure)
    $output = & $Runtime @Arguments 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "$Runtime $($Arguments[0]) exited $code. $($output -join [Environment]::NewLine)"
    }
    [pscustomobject]@{ ExitCode = $code; Output = ($output -join [Environment]::NewLine) }
}

function Invoke-UiEvidenceContainer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$Image,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$OutputVolumeName,
        [Parameter(Mandatory)][string]$ScenarioJson,
        [string]$OutputDirectory,
        [scriptblock]$RuntimeInvoker = ${function:Invoke-UiEvidenceRuntime}
    )
    try {
        & $RuntimeInvoker $Runtime @('volume','create',$OutputVolumeName) | Out-Null
        $args = New-UiEvidenceContainerArguments -Image $Image -ContainerName $ContainerName -RepositoryPath '/source-built-into-image' -OutputVolumeName $OutputVolumeName -ScenarioJson $ScenarioJson
        & $RuntimeInvoker $Runtime $args | Out-Null
        $started = & $RuntimeInvoker $Runtime @('start','--attach',$ContainerName) -AllowFailure
        if ($started.ExitCode -ne 0) { throw "Evidence container exited $($started.ExitCode). $($started.Output)" }
        if ($OutputDirectory) {
            [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
            & $RuntimeInvoker $Runtime @('cp',"${ContainerName}:/evidence-output/.",$OutputDirectory) | Out-Null
        }
    }
    finally {
        & $RuntimeInvoker $Runtime @('rm','--force',$ContainerName) -AllowFailure | Out-Null
        & $RuntimeInvoker $Runtime @('volume','rm','--force',$OutputVolumeName) -AllowFailure | Out-Null
    }
}

function Test-UiEvidenceOutput {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutputDirectory)
    $manifestPath = Join-Path $OutputDirectory 'evidence.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'evidence.json is missing.' }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 20 }
    catch { throw "evidence.json is corrupt: $($_.Exception.Message)" }
    foreach ($property in @('schemaVersion','source','scenario','viewport','screenshot','assertions','accessibility','timestamps','cleanup')) {
        if ($null -eq $manifest.$property) { throw "evidence.json is missing '$property'." }
    }
    if ($manifest.schemaVersion -ne '1.0') { throw 'Unsupported evidence schemaVersion.' }
    if ($manifest.source.commit -notmatch '^[0-9a-f]{40}$' -or $manifest.source.tree -notmatch '^[0-9a-f]{64}$') { throw 'Source commit/tree is invalid.' }
    if ($manifest.screenshot.path -notmatch '^[A-Za-z0-9._-]+\.png$') { throw 'Screenshot path is unsafe.' }
    $screenshotPath = Join-Path $OutputDirectory $manifest.screenshot.path
    if (-not (Test-Path -LiteralPath $screenshotPath -PathType Leaf) -or (Get-Item -LiteralPath $screenshotPath).Length -eq 0) { throw 'Screenshot is missing or empty.' }
    $actualHash = (Get-FileHash -LiteralPath $screenshotPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $manifest.screenshot.sha256) { throw 'Screenshot SHA-256 does not match evidence.json.' }
    if (@($manifest.assertions).Count -eq 0 -or @($manifest.assertions | Where-Object { -not $_.passed }).Count -gt 0) { throw 'Evidence assertions are missing or failed.' }
    foreach ($control in @('chat-steer-btn','chat-redirect-btn','chat-followup-btn','chat-abort-btn')) {
        $name = $manifest.accessibility.activeRunControls.$control
        if ([string]::IsNullOrWhiteSpace($name)) { throw "Accessible name is missing for '$control'." }
    }
    foreach ($cleanupProperty in @('gatewayStopped','browserClosed','temporaryDataRemoved','containerRemoved')) {
        if ($manifest.cleanup.$cleanupProperty -ne $true) { throw "Evidence cleanup '$cleanupProperty' was not proven." }
    }
    return $true
}

Export-ModuleMember -Function Resolve-UiEvidenceContainerRuntime,New-UiEvidenceContainerArguments,Invoke-UiEvidenceRuntime,Invoke-UiEvidenceContainer,Test-UiEvidenceOutput
