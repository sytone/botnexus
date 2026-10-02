[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(320,7680)][int]$ViewportWidth = 1440,
    [ValidateRange(320,4320)][int]$ViewportHeight = 1000,
    [ValidatePattern('^[A-Z0-9_/-]+$')][string]$PromptKey = 'SLOW_STREAM',
    [ValidatePattern('^/[A-Za-z0-9_./-]*$')][string]$PagePath = '/chat/evidence-agent',
    [string[]]$Selectors = @('[data-testid="streaming-badge"]','[data-testid="chat-abort-btn"]'),
    [string[]]$AccessibleNames = @(),
    [ValidateSet('docker','podman')][string]$ContainerRuntime,
    [string]$NuGetPackagesPath = $env:NUGET_PACKAGES
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BotNexusUiEvidence.psm1') -Force
function Get-RepositoryCommit {
    param([string]$Root)
    $marker = Join-Path $Root '.git'
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        $pointer = [IO.File]::ReadAllText($marker).Trim()
        if ($pointer -notmatch '^gitdir:\s*(.+)$') { throw 'Unsupported worktree Git pointer.' }
        $target = $Matches[1]
        $gitDirectory = if ([IO.Path]::IsPathRooted($target)) { [IO.Path]::GetFullPath($target) } else { [IO.Path]::GetFullPath((Join-Path $Root $target)) }
    } else { $gitDirectory = $marker }
    $head = [IO.File]::ReadAllText((Join-Path $gitDirectory 'HEAD')).Trim()
    if ($head -match '^[0-9a-f]{40}$') { return $head }
    if ($head -notmatch '^ref:\s*(.+)$') { throw 'Unsupported Git HEAD.' }
    $ref = $Matches[1]
    $refPath = Join-Path $gitDirectory $ref
    if (Test-Path -LiteralPath $refPath) { return [IO.File]::ReadAllText($refPath).Trim() }
    $common = $gitDirectory
    $commonPointer = Join-Path $gitDirectory 'commondir'
    if (Test-Path -LiteralPath $commonPointer) { $common = [IO.Path]::GetFullPath((Join-Path $gitDirectory ([IO.File]::ReadAllText($commonPointer).Trim()))) }
    $commonRef = Join-Path $common $ref
    if (Test-Path -LiteralPath $commonRef) { return [IO.File]::ReadAllText($commonRef).Trim() }
    $packed = Join-Path $common 'packed-refs'
    if (Test-Path -LiteralPath $packed) {
        foreach ($line in [IO.File]::ReadLines($packed)) { if ($line -match "^([0-9a-f]{40})\s+$([regex]::Escape($ref))$") { return $Matches[1] } }
    }
    throw "Cannot resolve Git ref '$ref'."
}

$repo = [IO.Path]::GetFullPath($RepositoryPath)
if (-not (Test-Path -LiteralPath (Join-Path $repo 'src/dirs.proj')) -or -not (Test-Path -LiteralPath (Join-Path $repo 'Directory.Build.props'))) { throw "RepositoryPath is not a BotNexus worktree: $repo" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container)) { throw "OutputDirectory is not a directory: $output" }
    if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) { throw "OutputDirectory must be empty: $output" }
}
$runtime = if ($ContainerRuntime) { (Get-Command $ContainerRuntime -ErrorAction Stop).Source } else { Resolve-UiEvidenceContainerRuntime }
$nugetPackages = if ($NuGetPackagesPath) { [IO.Path]::GetFullPath($NuGetPackagesPath) } else { '' }
$runId = [guid]::NewGuid().ToString('N')
$image = "botnexus-ui-evidence:$runId"
$container = "botnexus-ui-evidence-$runId"
$volume = "botnexus-ui-evidence-output-$runId"
$scenario = [ordered]@{
    name = 'active-stream'
    promptKey = $PromptKey
    pagePath = $PagePath
    viewport = [ordered]@{ width = $ViewportWidth; height = $ViewportHeight }
    selectors = $Selectors
    accessibleNames = $AccessibleNames
} | ConvertTo-Json -Depth 5 -Compress

try {
    $dockerfile = Join-Path $PSScriptRoot 'container/Dockerfile'
    $buildArguments = @('build','--file',$dockerfile,'--tag',$image,'--build-arg',"SOURCE_COMMIT=$(Get-RepositoryCommit $repo)")
    if ($nugetPackages -and (Test-Path -LiteralPath $nugetPackages -PathType Container)) {
        $buildArguments += @('--volume',"${nugetPackages}:/root/.nuget/packages:ro")
    }
    $buildArguments += $repo
    Invoke-UiEvidenceRuntime $runtime $buildArguments | Out-Null
    Invoke-UiEvidenceContainer -Runtime $runtime -Image $image -ContainerName $container -OutputVolumeName $volume -ScenarioJson $scenario -OutputDirectory $output
    $manifestPath = Join-Path $output 'evidence.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 20
    $manifest.cleanup.containerRemoved = $true
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Test-UiEvidenceOutput -OutputDirectory $output | Out-Null
    Get-Item -LiteralPath $manifestPath
}
finally {
    Invoke-UiEvidenceRuntime $runtime @('rm','--force',$container) -AllowFailure | Out-Null
    Invoke-UiEvidenceRuntime $runtime @('volume','rm','--force',$volume) -AllowFailure | Out-Null
    Invoke-UiEvidenceRuntime $runtime @('image','rm','--force',$image) -AllowFailure | Out-Null
}
