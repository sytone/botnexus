Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
Find the first supported container CLI.
.DESCRIPTION
Checks Docker then Podman without starting a runtime.
.PARAMETER CommandResolver
Injectable executable resolver for offline tests.
.EXAMPLE
Resolve-UiEvidenceContainerRuntime
.OUTPUTS
System.String
#>
function Resolve-UiEvidenceContainerRuntime {
    [CmdletBinding()]
    param([scriptblock]$CommandResolver = { param($Name) Get-Command $Name -ErrorAction SilentlyContinue })
    foreach ($name in @('docker', 'podman')) {
        if (& $CommandResolver $name) {
            return $name
        }
    }
    throw 'Docker or Podman is required; neither executable was found.'
}

<#
.SYNOPSIS
Compose isolated runtime arguments.
.DESCRIPTION
Returns arguments without invoking a CLI or mounting host source. RepositoryPath is a compatibility identity only.
.PARAMETER Image
Task-owned built candidate image.
.PARAMETER ContainerName
Unique task-owned container name.
.PARAMETER RepositoryPath
Repository identity, never mounted into the runtime.
.PARAMETER OutputVolumeName
Unique task-owned output volume name.
.PARAMETER ScenarioJson
Serialized supported scenario description.
.EXAMPLE
Get-UiEvidenceContainerArgument -Image image -ContainerName owned -RepositoryPath /source -OutputVolumeName output -ScenarioJson "{}"
.OUTPUTS
System.Array
#>
function Get-UiEvidenceContainerArgument {
    [OutputType([System.Array])]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Image,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$RepositoryPath,
        [Parameter(Mandatory)][string]$OutputVolumeName,
        [Parameter(Mandatory)][string]$ScenarioJson
    )
    if ([string]::IsNullOrWhiteSpace($RepositoryPath)) {
        throw 'Repository identity is required but never mounted.'
    }
    @(
        'create', '--name', $ContainerName, '--network=none', '--read-only',
        '--tmpfs', '/tmp:rw,noexec,nosuid,size=512m',
        '--tmpfs', '/evidence:rw,nosuid,size=512m',
        '--mount', "type=volume,source=$OutputVolumeName,target=/evidence-output",
        '--env', "BOTNEXUS_EVIDENCE_SCENARIO=$ScenarioJson",
        $Image
    )
}

<#
.SYNOPSIS
Invoke the container CLI and capture its result.
.DESCRIPTION
Checks the native exit status. AllowFailure returns a nonzero result for explicit handling by the caller.
.PARAMETER Runtime
Docker or Podman executable path.
.PARAMETER Arguments
Native CLI argument array; never a shell expression.
.PARAMETER AllowFailure
Return nonzero exit codes instead of throwing.
.EXAMPLE
Invoke-UiEvidenceRuntime -Runtime docker -Arguments @("version")
.OUTPUTS
System.Management.Automation.PSCustomObject
#>
function Invoke-UiEvidenceRuntime {
    param([string]$Runtime, [string[]]$Arguments, [switch]$AllowFailure)
    $output = & $Runtime @Arguments 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "$Runtime $($Arguments[0]) exited $code. $($output -join [Environment]::NewLine)"
    }
    [pscustomobject]@{ ExitCode = $code; Output = ($output -join [Environment]::NewLine) }
}

<#
.SYNOPSIS
Run and remove one exclusively owned evidence container.
.DESCRIPTION
Creates a named output volume, attaches to the bounded container, copies evidence, then verifies exact container and volume absence even after failure.
.PARAMETER Runtime
Docker or Podman executable path.
.PARAMETER Image
Task-owned built candidate image.
.PARAMETER ContainerName
Unique task-owned container name.
.PARAMETER OutputVolumeName
Unique task-owned output volume name.
.PARAMETER ScenarioJson
Serialized supported scenario description.
.PARAMETER OutputDirectory
Directory containing only this invocation artifacts.
.PARAMETER RuntimeInvoker
Injectable CLI boundary for synthetic fixtures.
.EXAMPLE
Invoke-UiEvidenceContainer -Runtime docker -Image image -ContainerName owned -OutputVolumeName output -ScenarioJson "{}"
.OUTPUTS
System.Management.Automation.PSCustomObject
#>
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
        & $RuntimeInvoker $Runtime @('volume', 'create', $OutputVolumeName) | Out-Null
        $containerArguments = Get-UiEvidenceContainerArgument -Image $Image -ContainerName $ContainerName -RepositoryPath '/source-built-into-image' -OutputVolumeName $OutputVolumeName -ScenarioJson $ScenarioJson
        & $RuntimeInvoker $Runtime $containerArguments | Out-Null
        $started = & $RuntimeInvoker $Runtime @('start', '--attach', $ContainerName) -AllowFailure
        if ($started.ExitCode -ne 0) {
            throw "Evidence container exited $($started.ExitCode). $($started.Output)"
        }
        if ($OutputDirectory) {
            [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
            & $RuntimeInvoker $Runtime @('cp', "${ContainerName}:/evidence-output/.", $OutputDirectory) | Out-Null
        }
    }
    finally {
        $removed = & $RuntimeInvoker $Runtime @('rm', '--force', $ContainerName) -AllowFailure
        $inventory = & $RuntimeInvoker $Runtime @('container', 'ls', '--all', '--filter', "name=^${ContainerName}$", '--format', '{{.Names}}') -AllowFailure
        $volumeRemoved = & $RuntimeInvoker $Runtime @('volume', 'rm', '--force', $OutputVolumeName) -AllowFailure
        $volumes = & $RuntimeInvoker $Runtime @('volume', 'ls', '--filter', "name=^${OutputVolumeName}$", '--format', '{{.Name}}') -AllowFailure
        if ($removed.ExitCode -ne 0 -or $inventory.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($inventory.Output)) {
            throw 'Owned evidence container cleanup absence was not proven.'
        }
        if ($volumeRemoved.ExitCode -ne 0 -or $volumes.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($volumes.Output)) {
            throw 'Owned evidence output volume cleanup absence was not proven.'
        }
    }
    [pscustomobject]@{ ContainerRemoved = $true; VolumeRemoved = $true }
}

<#
.SYNOPSIS
Validate collected evidence before acceptance.
.DESCRIPTION
Reads the manifest and artifacts. Missing state, hashes, assertions, scenario inventory or cleanup proof fails closed. Does not launch any runtime.
.PARAMETER OutputDirectory
Directory containing only this invocation artifacts.
.PARAMETER ExpectedScenario
Requested scenario identity; rejects downgraded or substituted manifests when supplied by the host.
.PARAMETER ExpectedTree
Independently computed pre-build source context digest.
.PARAMETER ExpectedCommit
Exact requested candidate commit; rejects substituted image evidence.
.EXAMPLE
Test-UiEvidenceOutput -OutputDirectory ./evidence
.OUTPUTS
System.Boolean
#>
function Test-UiEvidenceOutput {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutputDirectory, [string]$ExpectedScenario, [string]$ExpectedCommit, [string]$ExpectedTree)
    $outputRoot = Get-Item -LiteralPath $OutputDirectory -Force
    if (($outputRoot.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Output root is a link.'
    }
    foreach ($entry in @(Get-ChildItem -LiteralPath $OutputDirectory -Force)) {
        if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Output contains a directory or link.'
        }
    }
    $manifestPath = Join-Path $OutputDirectory 'evidence.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'evidence.json is missing.'
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 20
    }
    catch {
        throw "evidence.json is corrupt: $($_.Exception.Message)"
    }
    foreach ($property in @('schemaVersion', 'source', 'scenario', 'viewport', 'screenshot', 'assertions', 'accessibility', 'timestamps', 'cleanup')) {
        if ($null -eq $manifest.$property) {
            throw "evidence.json is missing '$property'."
        }
    }
    if ($ExpectedTree -and $manifest.source.tree -cne $ExpectedTree) {
        throw 'Source context digest mismatch.'
    }
    if ($ExpectedScenario -and $manifest.scenario.name -cne $ExpectedScenario) {
        throw 'Requested evidence scenario mismatch.'
    }
    if ($ExpectedCommit -and $manifest.source.commit -cne $ExpectedCommit) {
        throw 'Requested source commit mismatch.'
    }
    if ($manifest.schemaVersion -ne '1.0') {
        throw 'Unsupported evidence schemaVersion.'
    }
    if ($manifest.source.commit -notmatch '^[0-9a-f]{40}$' -or $manifest.source.tree -notmatch '^[0-9a-f]{64}$') {
        throw 'Source commit/tree is invalid.'
    }
    if ($manifest.screenshot.path -notmatch '^[A-Za-z0-9._-]+\.png$') {
        throw 'Screenshot path is unsafe.'
    }
    $screenshotPath = Join-Path $OutputDirectory $manifest.screenshot.path
    if (-not (Test-Path -LiteralPath $screenshotPath -PathType Leaf) -or (Get-Item -LiteralPath $screenshotPath).Length -eq 0) {
        throw 'Screenshot is missing or empty.'
    }
    $actualHash = (Get-FileHash -LiteralPath $screenshotPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $manifest.screenshot.sha256) {
        throw 'Screenshot SHA-256 does not match evidence.json.'
    }
    if ($manifest.scenario.name -eq 'focused-sidebar') {
        Test-UiEvidenceSidebar -Manifest $manifest -OutputDirectory $OutputDirectory
    }
    elseif ($manifest.scenario.name -notin @('active-stream', 'active-composer-matrix')) {
        throw 'Unsupported evidence scenario.'
    }
    elseif ($manifest.PSObject.Properties.Name -contains 'captures' -and $null -ne $manifest.captures) {
        $expectedCaptures = @('desktop-idle', 'mobile-idle', 'mobile-active', 'active-pre-token', 'active-tool-gap', 'focused-active', 'reduced-motion', 'returned-idle', 'animated-active-low', 'animated-active-high', 'preference-disabled-active')
        $captureNames = @($manifest.captures | ForEach-Object { $_.name })
        if ($captureNames.Count -ne $expectedCaptures.Count -or @($captureNames | Select-Object -Unique).Count -ne $expectedCaptures.Count) {
            throw 'Evidence capture matrix must contain each required state exactly once.'
        }
        foreach ($name in $expectedCaptures) {
            if ($captureNames -notcontains $name) {
                throw "Evidence capture matrix is missing '$name'."
            }
        }
        if (@($manifest.assertions | Where-Object { $_.target -eq 'animation:bottom-edge-opacity-changes' -and $_.passed }).Count -eq 0) {
            throw 'Evidence is missing the computed animation bottom-edge opacity change assertion.'
        }
        foreach ($capture in $manifest.captures) {
            if ($capture.screenshot.path -notmatch '^[A-Za-z0-9._-]+\.png$') {
                throw "Screenshot path is unsafe for '$($capture.name)'."
            }
            $capturePath = Join-Path $OutputDirectory $capture.screenshot.path
            if (-not (Test-Path -LiteralPath $capturePath -PathType Leaf) -or (Get-Item -LiteralPath $capturePath).Length -eq 0) {
                throw "Screenshot is missing or empty for '$($capture.name)'."
            }
            $captureHash = (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($captureHash -ne $capture.screenshot.sha256) {
                throw "Screenshot SHA-256 does not match evidence.json for '$($capture.name)'."
            }
            if (@($capture.assertions).Count -eq 0 -or @($capture.assertions | Where-Object { $_.passed -isnot [bool] -or $_.passed -ne $true }).Count -gt 0) {
                throw "Evidence assertions are missing or failed for '$($capture.name)'."
            }
            if (@($capture.assertions | Where-Object { $_.kind -eq 'browserState' }).Count -eq 0) {
                throw "Browser state assertions are missing for '$($capture.name)'."
            }
            if ($capture.name -in @('mobile-idle', 'mobile-active')) {
                $statusTarget = if ($capture.name -eq 'mobile-active') {
                    'stable-status'
                }
                else {
                    'stable-status-absent'
                }
                $requiredTargets = @("$($capture.name):route", "$($capture.name):composer-selector", "$($capture.name):aria-busy", "$($capture.name):$statusTarget")
                foreach ($target in $requiredTargets) {
                    if (@($capture.assertions | Where-Object { $_.target -eq $target -and $_.passed }).Count -eq 0) {
                        throw "Mobile capture '$($capture.name)' is missing passing assertion '$target'."
                    }
                }
            }
            if ($capture.name -in @('animated-active-low', 'animated-active-high')) {
                $target = "$($capture.name):bottom-edge"
                if (@($capture.assertions | Where-Object { $_.target -eq $target -and $_.passed }).Count -eq 0) {
                    throw "Animation capture '$($capture.name)' is missing computed bottom-edge assertion."
                }
            }
            if ($capture.name -eq 'preference-disabled-active') {
                if (@($capture.assertions | Where-Object { $_.target -eq 'preference-disabled-active:static-bottom-edge' -and $_.passed }).Count -eq 0) {
                    throw "Preference-disabled capture is missing the static bottom-edge assertion."
                }
            }
        }
    }
    if (@($manifest.assertions).Count -eq 0 -or @($manifest.assertions | Where-Object { $_.passed -isnot [bool] -or $_.passed -ne $true }).Count -gt 0) {
        throw 'Evidence assertions are missing or failed.'
    }
    if ($manifest.scenario.name -ne 'focused-sidebar') {
        foreach ($control in @('chat-steer-btn', 'chat-redirect-btn', 'chat-followup-btn', 'chat-abort-btn')) {
            $name = $manifest.accessibility.activeRunControls.$control
            if ([string]::IsNullOrWhiteSpace($name)) {
                throw "Accessible name is missing for '$control'."
            }
        }
    }
    foreach ($cleanupProperty in @('gatewayStopped', 'browserClosed', 'temporaryDataRemoved', 'containerRemoved')) {
        if ($manifest.cleanup.$cleanupProperty -isnot [bool] -or $manifest.cleanup.$cleanupProperty -ne $true) {
            throw "Evidence cleanup '$cleanupProperty' was not proven."
        }
    }
    return $true
}


<#
.SYNOPSIS
Validate the focused sidebar capture contract.
.DESCRIPTION
Requires all sixteen theme, viewport and section captures with matching metadata, hashes and explicit input and stationary geometry assertions.
.PARAMETER Manifest
Parsed manifest from this invocation.
.PARAMETER OutputDirectory
Directory containing only this invocation artifacts.
.EXAMPLE
Test-UiEvidenceSidebar -Manifest $manifest -OutputDirectory ./evidence
.OUTPUTS
None
#>
function Test-UiEvidenceSidebar {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Manifest, [Parameter(Mandatory)][string]$OutputDirectory)
    $expected = @(foreach ($theme in @('light', 'dark')) {
            foreach ($size in @('1440x900', '600x844')) {
                foreach ($section in @('chat', 'agents')) {
                    foreach ($state in @('populated', 'scrolled')) {
                        "$theme-$size-$section-$state"
                    }
                }
            }
        })
    $allowed = @('evidence.json') + @($expected | ForEach-Object { "$_.png"; "$_.json" })
    Test-UiEvidenceInventory -OutputDirectory $OutputDirectory -Allowed $allowed
    $names = @($Manifest.captures | ForEach-Object { $_.name })
    if ($names.Count -ne 16 -or @($names | Select-Object -Unique).Count -ne 16) {
        throw 'Sidebar capture matrix must contain 16 unique captures.'
    }
    foreach ($name in $expected) {
        if ($names -notcontains $name) {
            throw "Sidebar capture missing '$name'."
        }
        $capture = @($Manifest.captures | Where-Object { $_.name -ceq $name })[0]
        $parts = $name.Split('-')
        $size = $parts[1].Split('x')
        if ($capture.viewport.width -ne [int]$size[0] -or $capture.viewport.height -ne [int]$size[1]) {
            throw 'Sidebar viewport mismatch.'
        }
        foreach ($artifact in @($capture.screenshot, $capture.metadata)) {
            if ($artifact.path -cnotmatch '^[A-Za-z0-9._-]+\.(png|json)$' -or $artifact.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw 'Unsafe sidebar artifact or hash.'
            }
            $path = Join-Path $OutputDirectory $artifact.path
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
                throw 'Sidebar artifact missing or empty.'
            }
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $artifact.sha256) {
                throw 'Sidebar artifact SHA-256 mismatch.'
            }
        }
        if ($capture.screenshot.path -cne "$name.png" -or $capture.metadata.path -cne "$name.json") {
            throw 'Sidebar artifact name mismatch.'
        }
        $imageBytes = [IO.File]::ReadAllBytes((Join-Path $OutputDirectory $capture.screenshot.path))
        if ($imageBytes.Length -lt 8 -or [Convert]::ToHexString($imageBytes[0..7]) -cne '89504E470D0A1A0A') {
            throw 'Sidebar screenshot is not PNG content.'
        }
        $metadata = Get-Content -LiteralPath (Join-Path $OutputDirectory $capture.metadata.path) -Raw | ConvertFrom-Json -Depth 20
        $owner = if ($parts[2] -eq 'chat') {
            '.focused-chat-scroll'
        }
        else {
            '[data-testid="focused-section-content"]'
        }
        if ($metadata.theme -cne $parts[0] -or $metadata.section -cne $parts[2] -or $metadata.viewport.width -ne [int]$size[0] -or $metadata.viewport.height -ne [int]$size[1] -or $metadata.scrollOwner.selector -cne $owner) {
            throw 'Sidebar metadata contract mismatch.'
        }
        $geometry = $metadata.scrollOwner.geometry
        if ($geometry.clientHeight -le 100 -or $geometry.scrollHeight -le $geometry.clientHeight + 20) {
            throw 'Sidebar did not overflow.'
        }
        if (($parts[3] -eq 'populated' -and $geometry.scrollTop -ne 0) -or ($parts[3] -eq 'scrolled' -and $geometry.scrollTop -le 0)) {
            throw 'Sidebar scroll position mismatch.'
        }
        $targets = @('overflow', 'fixed-picker', 'fixed-document', 'outer-owner', 'populated', 'theme', 'desktop-route', 'no-browser-errors')
        if ($parts[2] -eq 'chat') {
            $targets += @('fixed-controls', 'peer-groups', 'custom-member', 'built-in-menus')
        }
        if ($parts[3] -eq 'scrolled') {
            $targets += @('wheel-moved', 'keyboard-moved', 'keyboard-focus')
        }
        if (@($capture.assertions | Where-Object { $_.passed -isnot [bool] -or $_.passed -ne $true }).Count -gt 0) {
            throw 'Sidebar assertions failed.'
        }
        foreach ($target in $targets) {
            if (@($capture.assertions | Where-Object { $_.kind -ceq 'browserState' -and $_.target -ceq "${name}:$target" -and $_.passed -is [bool] -and $_.passed }).Count -ne 1) {
                throw "Sidebar assertion missing or duplicated: ${name}:$target"
            }
        }
    }
}



<#
.SYNOPSIS
Reject unexpected or non-regular collected output.
.DESCRIPTION
Checks the directory itself and every immediate entry without traversing links. Exact case-sensitive inventory is required. Directories, reparse points, symbolic links and extra files fail closed.
.PARAMETER OutputDirectory
Task-owned collected output directory.
.PARAMETER Allowed
Exact case-sensitive regular filenames allowed by the scenario.
.EXAMPLE
Test-UiEvidenceInventory -OutputDirectory ./evidence -Allowed @('evidence.json', 'portal.png')
.OUTPUTS
None
#>
function Test-UiEvidenceInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string[]]$Allowed)
    $root = Get-Item -LiteralPath $OutputDirectory -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Output root is a link or reparse point.'
    }
    $entries = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
    if ($entries.Count -ne $Allowed.Count) {
        throw 'Unexpected output inventory count.'
    }
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $entry.LinkType) {
            throw 'Output contains a directory, link or reparse point.'
        }
        if ($Allowed -cnotcontains $entry.Name) {
            throw 'Unexpected output inventory entry.'
        }
    }
}

<#
.SYNOPSIS
Prepare an immutable committed evidence source context.
.DESCRIPTION
Exports approved tracked Git blobs using the repository snapshot helper. Rejects dirty/untracked candidates, secret paths and links. Uses a new task-owned scratch directory outside the checkout and .botnexus. No container or product runtime starts.
.PARAMETER RepositoryPath
Clean committed candidate worktree.
.PARAMETER SnapshotDirectory
New absolute task-owned scratch context directory outside the repository and .botnexus.
.PARAMETER ExpectedCommit
Exact approved candidate commit.
.EXAMPLE
New-UiEvidenceSnapshot -RepositoryPath Q:/repos/candidate -SnapshotDirectory Q:/scratch/task/context -ExpectedCommit ('a' * 40)
.OUTPUTS
System.Management.Automation.PSCustomObject
#>
function New-UiEvidenceSnapshot {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$RepositoryPath, [Parameter(Mandatory)][string]$SnapshotDirectory, [Parameter(Mandatory)][string]$ExpectedCommit)
    if (-not $PSCmdlet.ShouldProcess($SnapshotDirectory, 'Export approved committed source snapshot')) {
        return
    }
    $packet = & python -B (Join-Path $PSScriptRoot 'snapshot.py') --repository $RepositoryPath --destination $SnapshotDirectory --expected-commit $ExpectedCommit
    if ($LASTEXITCODE -ne 0) {
        throw 'Clean committed source snapshot preparation failed.'
    }
    $snapshot = $packet | ConvertFrom-Json
    if ($snapshot.commit -cne $ExpectedCommit -or $snapshot.tree -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Source snapshot identity mismatch.'
    }
    return $snapshot
}
