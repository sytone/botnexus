<#
.SYNOPSIS
Collect isolated real candidate UI evidence.
.DESCRIPTION
Builds an evidence image, runs a network-disabled read-only container and validates artifacts after verified owned cleanup. Never launches a host gateway or browser. WhatIf does not build, create output or start containers.
.PARAMETER RepositoryPath
Absolute candidate worktree root.
.PARAMETER ScratchDirectory
Task-owned scratch root outside the repository and .botnexus for immutable source export.
.PARAMETER OutputDirectory
Empty task-owned artifact destination.
.PARAMETER ViewportWidth
Single-capture viewport width.
.PARAMETER ViewportHeight
Single-capture viewport height.
.PARAMETER PromptKey
Supported integration-mock script key.
.PARAMETER PagePath
Candidate route for a single capture.
.PARAMETER Selectors
Required real DOM selectors for a single capture.
.PARAMETER AccessibleNames
Required accessible control names for a single capture.
.PARAMETER CaptureMatrix
Capture the existing active-composer matrix.
.PARAMETER FocusedSidebar
Capture the focused-sidebar sixteen-state matrix; mutually exclusive with CaptureMatrix.
.PARAMETER ContainerRuntime
Explicit Docker or Podman executable selection.
.PARAMETER NuGetPackagesPath
Optional read-only build-time package cache; never mounted into the runtime.
.EXAMPLE
./Invoke-BotNexusUiEvidence.ps1 -RepositoryPath Q:/repos/candidate -OutputDirectory Q:/scratch/evidence -ScratchDirectory Q:/scratch/task -FocusedSidebar
.OUTPUTS
System.IO.FileInfo. Validated evidence.json, or no output under WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$RepositoryPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ScratchDirectory,
    [ValidateRange(320, 7680)][int]$ViewportWidth = 1440,
    [ValidateRange(320, 4320)][int]$ViewportHeight = 1000,
    [ValidatePattern('^[A-Z0-9_/-]+$')][string]$PromptKey = 'SLOW_STREAM',
    [ValidatePattern('^/[A-Za-z0-9_./-]*$')][string]$PagePath = '/chat/evidence-agent',
    [string[]]$Selectors = @('[data-testid="streaming-badge"]', '[data-testid="chat-abort-btn"]'),
    [string[]]$AccessibleNames = @(),
    [switch]$CaptureMatrix,
    [switch]$FocusedSidebar,
    [ValidateSet('docker', 'podman')][string]$ContainerRuntime,
    [string]$NuGetPackagesPath = $env:NUGET_PACKAGES
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BotNexusUiEvidence.psm1') -Force
<#
.SYNOPSIS
Read the current candidate Git commit without a Git subprocess.
.DESCRIPTION
Resolves detached HEAD, worktree refs and packed refs. Fails when the source identity cannot be resolved.
.PARAMETER Root
Candidate worktree root with Git metadata.
.EXAMPLE
Get-RepositoryCommit -Root Q:/repos/candidate
.OUTPUTS
System.String
#>
function Get-RepositoryCommit {
    param([string]$Root)
    $marker = Join-Path $Root '.git'
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        $pointer = [IO.File]::ReadAllText($marker).Trim()
        if ($pointer -notmatch '^gitdir:\s*(.+)$') {
            throw 'Unsupported worktree Git pointer.'
        }
        $target = $Matches[1]
        $gitDirectory = if ([IO.Path]::IsPathRooted($target)) {
            [IO.Path]::GetFullPath($target)
        }
        else {
            [IO.Path]::GetFullPath((Join-Path $Root $target))
        }
    }
    else {
        $gitDirectory = $marker
    }
    $head = [IO.File]::ReadAllText((Join-Path $gitDirectory 'HEAD')).Trim()
    if ($head -match '^[0-9a-f]{40}$') {
        return $head
    }
    if ($head -notmatch '^ref:\s*(.+)$') {
        throw 'Unsupported Git HEAD.'
    }
    $ref = $Matches[1]
    $refPath = Join-Path $gitDirectory $ref
    if (Test-Path -LiteralPath $refPath) {
        return [IO.File]::ReadAllText($refPath).Trim()
    }
    $common = $gitDirectory
    $commonPointer = Join-Path $gitDirectory 'commondir'
    if (Test-Path -LiteralPath $commonPointer) {
        $common = [IO.Path]::GetFullPath((Join-Path $gitDirectory ([IO.File]::ReadAllText($commonPointer).Trim())))
    }
    $commonRef = Join-Path $common $ref
    if (Test-Path -LiteralPath $commonRef) {
        return [IO.File]::ReadAllText($commonRef).Trim()
    }
    $packed = Join-Path $common 'packed-refs'
    if (Test-Path -LiteralPath $packed) {
        foreach ($line in [IO.File]::ReadLines($packed)) {
            if ($line -match "^([0-9a-f]{40})\s+$([regex]::Escape($ref))$") {
                return $Matches[1]
            }
        }
    }
    throw "Cannot resolve Git ref '$ref'."
}

if ($CaptureMatrix -and $FocusedSidebar) {
    throw 'Choose CaptureMatrix or FocusedSidebar, not both.'
}

$repo = [IO.Path]::GetFullPath($RepositoryPath)
if (-not (Test-Path -LiteralPath (Join-Path $repo 'src/dirs.proj')) -or -not (Test-Path -LiteralPath (Join-Path $repo 'Directory.Build.props'))) {
    throw "RepositoryPath is not a BotNexus worktree: $repo"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container)) {
        throw "OutputDirectory is not a directory: $output"
    }
    if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) {
        throw "OutputDirectory must be empty: $output"
    }
}
$runtime = if ($ContainerRuntime) {
    (Get-Command $ContainerRuntime -ErrorAction Stop).Source
}
else {
    Resolve-UiEvidenceContainerRuntime
}
$nugetPackages = if ($NuGetPackagesPath) {
    [IO.Path]::GetFullPath($NuGetPackagesPath)
}
else {
    ''
}
$runId = [guid]::NewGuid().ToString('N')
$image = "botnexus-ui-evidence:$runId"
$container = "botnexus-ui-evidence-$runId"
$volume = "botnexus-ui-evidence-output-$runId"
$scenario = [ordered]@{
    name = if ($FocusedSidebar) {
        'focused-sidebar'
    }
    elseif ($CaptureMatrix) {
        'active-composer-matrix'
    }
    else {
        'active-stream'
    }
    promptKey = $PromptKey
    captureMatrix = [bool]$CaptureMatrix
    pagePath = $PagePath
    viewport = [ordered]@{ width = $ViewportWidth; height = $ViewportHeight }
    selectors = $Selectors
    accessibleNames = $AccessibleNames
} | ConvertTo-Json -Depth 5 -Compress

if (-not $PSCmdlet.ShouldProcess($repo, 'Build and run isolated UI evidence container')) {
    return
}

$sourceCommit = Get-RepositoryCommit -Root $repo
$snapshot = New-UiEvidenceSnapshot -RepositoryPath $repo -SnapshotDirectory (Join-Path $ScratchDirectory $runId) -ExpectedCommit $sourceCommit
try {
    $dockerfile = Join-Path $snapshot.context 'source/skills/botnexus-ui-evidence/container/Dockerfile'
    $buildArguments = @('build', '--file', $dockerfile, '--tag', $image, '--build-arg', "SOURCE_COMMIT=$sourceCommit", '--build-arg', "SOURCE_CONTEXT_DIGEST=$($snapshot.tree)")
    if ($nugetPackages -and (Test-Path -LiteralPath $nugetPackages -PathType Container)) {
        $buildArguments += @('--volume', "${nugetPackages}:/root/.nuget/packages:ro")
    }
    $buildArguments += $snapshot.context
    Invoke-UiEvidenceRuntime $runtime $buildArguments | Out-Null
    $cleanup = Invoke-UiEvidenceContainer -Runtime $runtime -Image $image -ContainerName $container -OutputVolumeName $volume -ScenarioJson $scenario -OutputDirectory $output -FailureDirectory (Join-Path $ScratchDirectory ("failure-$runId"))
    # Refuse links/directories before reading or rewriting the collected manifest.
    $collected = @(Get-ChildItem -LiteralPath $output -Force)
    Test-UiEvidenceInventory -OutputDirectory $output -Allowed @($collected.Name)
    if ($FocusedSidebar) {
        $allowed = @('evidence.json') + @(foreach ($theme in @('light', 'dark')) {
                foreach ($size in @('1440x900', '600x844')) {
                    foreach ($section in @('chat', 'agents')) {
                        foreach ($state in @('populated', 'scrolled')) {
                            "$theme-$size-$section-$state.png"; "$theme-$size-$section-$state.json"
                        }
                    }
                }
            })
        Test-UiEvidenceInventory -OutputDirectory $output -Allowed $allowed
    }
    $manifestPath = Join-Path $output 'evidence.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 20
    if ($cleanup.ContainerRemoved -ne $true -or $cleanup.VolumeRemoved -ne $true) {
        throw 'Owned cleanup was not proven.'
    }
    $manifest.cleanup.containerRemoved = $cleanup.ContainerRemoved
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Test-UiEvidenceOutput -OutputDirectory $output -ExpectedScenario (($scenario | ConvertFrom-Json).name) -ExpectedCommit $sourceCommit -ExpectedTree $snapshot.tree | Out-Null
    Get-Item -LiteralPath $manifestPath
}
finally {
    Invoke-UiEvidenceRuntime $runtime @('rm', '--force', $container) -AllowFailure | Out-Null
    Invoke-UiEvidenceRuntime $runtime @('volume', 'rm', '--force', $volume) -AllowFailure | Out-Null
    Invoke-UiEvidenceRuntime $runtime @('image', 'rm', '--force', $image) -AllowFailure | Out-Null
    if (Get-Variable -Name snapshot -ErrorAction SilentlyContinue) {
        Remove-Item -LiteralPath $snapshot.context -Recurse -Force
    }
}
