Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
    Fails when the runner image the Container Apps Job runs was not built from tracked sources.
.DESCRIPTION
    The runner image tag is content-addressed: `src-<sha256[0:12]>` over every file that lands in
    the image (#2900/#2901). That guarantees identical content always yields the same tag - which
    means the DEPLOYED tag can be compared against the tag the current sources derive to, and any
    difference is a deployment that does not correspond to this commit.

    Why this exists: on 2026-08-21 the job was pointed at an image built from an unmerged branch.
    That image contained `RunnerBuild.ps1`, a file main does not have. It threw on every run before
    build or test executed, so the gate returned `tests: null` for three days and no branch could be
    validated. Nothing reported the mismatch, because nothing was comparing the two.

    The failure mode this prevents is specifically NOT "the image is old". It is "the image is
    running code that this repository cannot show you", which makes every gate result unattributable
    to a commit.
#>

function Get-RunnerContentTag {
    <#
    .SYNOPSIS
        Derives the content-addressed tag for a runner source directory.
    .DESCRIPTION
        Deliberately mirrors Deploy-BuildTestInfrastructure.ps1 rather than importing it: the deploy
        script performs Azure work as a side effect of being run, so a check that dot-sourced it
        could not be executed in CI. The derivation is pinned by DeployTagGuard.Tests.ps1 on the
        deploy side and by this file's own tests here, so a divergence between the two fails.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $RunnerPath
    )

    # Resolve to a canonical absolute path first. The caller may pass a path containing '..'
    # segments (Join-Path $PSScriptRoot '..' '..' ...), and Get-ChildItem returns FULLY RESOLVED
    # FullName values - so the unresolved prefix is longer than the resolved child paths and the
    # Substring below throws "startIndex cannot be larger than length of string". That is a silent
    # failure in practice: the caller catches it and treats provenance as unavailable, which is
    # exactly the "check that cannot fire" this file exists to prevent.
    $resolved = (Resolve-Path -LiteralPath $RunnerPath -ErrorAction Stop).ProviderPath.TrimEnd('\', '/')

    $runnerFiles = Get-ChildItem -Path $resolved -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/]tests[\\/]' } |
        Sort-Object { $_.FullName.Substring($resolved.Length).Replace('\', '/') }

    if (-not $runnerFiles) {
        throw "Refusing to derive a tag: no files under $resolved. An empty set hashes to a constant and would match nothing."
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $buffer = [System.IO.MemoryStream]::new()
        foreach ($file in $runnerFiles) {
            $relative = $file.FullName.Substring($resolved.Length).Replace('\', '/').TrimStart('/')
            # Line endings are normalised: a CRLF/LF difference between a Windows and a Linux
            # operator is not a content change and must not appear as a mismatch.
            $content = (Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop) -replace "`r`n", "`n"
            $bytes = [System.Text.Encoding]::UTF8.GetBytes("$relative`n$content`n")
            $buffer.Write($bytes, 0, $bytes.Length)
        }
        $buffer.Position = 0
        return 'src-' + [BitConverter]::ToString($sha.ComputeHash($buffer)).Replace('-', '').Substring(0, 12).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-DeployedRunnerTag {
    <#
    .SYNOPSIS
        Reads the image tag the Container Apps Job is currently configured to run.
    .OUTPUTS
        The tag portion of the image reference, or $null when the job cannot be read.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $SubscriptionId,
        [Parameter(Mandatory)][string] $ResourceGroup,
        [Parameter(Mandatory)][string] $JobName
    )

    $url = "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.App/jobs/$JobName" +
           '?api-version=2024-03-01'

    $raw = az rest --method get --url $url --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) { return $null }

    $job = $raw | ConvertFrom-Json
    $image = @($job.properties.template.containers)[0].image
    if ([string]::IsNullOrWhiteSpace($image)) { return $null }

    return ($image -split ':')[-1]
}

function Test-RunnerImageMatchesSources {
    <#
    .SYNOPSIS
        Compares the deployed tag with the tag current sources derive to.
    .OUTPUTS
        A record carrying both tags and a verdict: 'match', 'mismatch', or 'unknown'.
    .DESCRIPTION
        'unknown' is a distinct outcome from 'mismatch' and is NOT a failure. A developer with no
        Azure credentials cannot read the job, and turning that into a red check would make the guard
        unusable for everyone who is not an operator - which is how guards get bypassed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RunnerPath,
        [string] $DeployedTag
    )

    $expected = Get-RunnerContentTag -RunnerPath $RunnerPath

    if ([string]::IsNullOrWhiteSpace($DeployedTag)) {
        return [pscustomobject]@{ Expected = $expected; Deployed = $null; Verdict = 'unknown' }
    }

    return [pscustomobject]@{
        Expected = $expected
        Deployed = $DeployedTag
        Verdict  = $(if ($DeployedTag -eq $expected) { 'match' } else { 'mismatch' })
    }
}
