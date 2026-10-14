[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('docker','podman')][string]$ContainerRuntime = 'podman',
    [string]$NuGetPackagesPath = $env:NUGET_PACKAGES,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BotNexusUiEvidence.psm1') -Force
$repo = [IO.Path]::GetFullPath($RepositoryPath)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw 'OutputDirectory must not already exist.' }
if (-not (Test-Path (Join-Path $repo 'src/dirs.proj'))) { throw 'RepositoryPath must contain the candidate source.' }
if (-not $NuGetPackagesPath -or -not (Test-Path $NuGetPackagesPath)) { throw 'A populated NuGetPackagesPath is required.' }
$id = [guid]::NewGuid().ToString('N')
$image = "botnexus-ui-evidence:$id"
$container = "botnexus-ui-evidence-$id"
$volume = "botnexus-ui-evidence-output-$id"
$runtime = (Get-Command $ContainerRuntime -ErrorAction Stop).Source
$commit = $SourceCommit
if (-not $commit) {
    $commit = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit.' }
}
$scenario = @{ name = 'conversation-menu'; viewport = @{ width = 1280; height = 800 }; pagePath = '/chat/evidence-agent' } | ConvertTo-Json -Compress
try {
    Write-Output "Building menu evidence $id"
    $build = Invoke-UiEvidenceRuntime $runtime @('build','--layers','--file',(Join-Path $PSScriptRoot 'container/Dockerfile'),'--tag',$image,'--build-arg',"SOURCE_COMMIT=$commit",'--volume',"${NuGetPackagesPath}:/root/.nuget/packages:ro",$repo)
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $build.Output | Set-Content (Join-Path $output 'build.log')
    Invoke-UiEvidenceRuntime $runtime @('volume','create',$volume) | Out-Null
    $args = New-UiEvidenceContainerArguments -Image $image -ContainerName $container -RepositoryPath '/source-built-into-image' -OutputVolumeName $volume -ScenarioJson $scenario
    Invoke-UiEvidenceRuntime $runtime $args | Out-Null
    $run = Invoke-UiEvidenceRuntime $runtime @('start','--attach',$container) -AllowFailure
    $run.Output | Set-Content (Join-Path $output 'browser.log')
    Write-Output $run.Output
    Invoke-UiEvidenceRuntime $runtime @('cp',"${container}:/evidence-output/.",$output) | Out-Null
    if ($run.ExitCode -ne 0) { throw "Menu container could not complete: $($run.ExitCode)." }
} finally {
    $removed = Invoke-UiEvidenceRuntime $runtime @('rm','--force',$container) -AllowFailure
    $volumeRemoved = Invoke-UiEvidenceRuntime $runtime @('volume','rm','--force',$volume) -AllowFailure
    Invoke-UiEvidenceRuntime $runtime @('image','rm','--force',$image) -AllowFailure | Out-Null
    $manifestPath = Join-Path $output 'evidence.json'
    if (Test-Path $manifestPath) {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -Depth 30
        $manifest.cleanup.containerRemoved = $removed.ExitCode -eq 0 -and $volumeRemoved.ExitCode -eq 0
        $manifest | ConvertTo-Json -Depth 30 | Set-Content $manifestPath
    }
}
$failed = @($manifest.assertions | Where-Object { -not $_.passed })
foreach ($capture in $manifest.captures) {
    $hash = (Get-FileHash (Join-Path $output $capture.path) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $capture.sha256) { throw "Screenshot hash mismatch: $($capture.path)" }
}
foreach ($property in @('browserClosed','gatewayStopped','temporaryDataRemoved','containerRemoved')) {
    if ($manifest.cleanup.$property -ne $true) { throw "Cleanup not verified: $property" }
}
Write-Output "Menu assertions: $(@($manifest.assertions).Count), failed: $($failed.Count), screenshots: $(@($manifest.captures).Count)"
if ($failed.Count -gt 0) { throw 'Conversation menu browser assertions failed; retained evidence.json and screenshots.' }
