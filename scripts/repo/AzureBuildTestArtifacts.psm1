<#
.SYNOPSIS
    Artifact placement helpers for Invoke-AzureBuildTest.ps1.

.DESCRIPTION
    Issue #3115: `az storage blob download-batch` reproduces the full blob NAME under the
    destination directory. The artifact blobs are already named `<runId>/result.json`, so
    downloading them straight into the run directory produced
    `artifacts/azure-buildtest/<runId>/<runId>/result.json` while the script advertised the
    outer directory on success. A missing `result.json` at the advertised path is also the
    symptom of a truncated download, so the defect made a complete green run read as a lost one.

    The fix downloads into a staging root and then flattens the run-id prefix into the single
    run directory the caller was given, so the printed path and the real artifact path are the
    same variable by construction and cannot drift apart again.
#>

Set-StrictMode -Version Latest

function Move-AzureBuildTestArtifacts {
    <#
    .SYNOPSIS
        Flattens downloaded artifacts out of the run-id blob prefix into the run directory.

    .DESCRIPTION
        Moves everything under <StagingRoot>/<RunId>/ into <Destination>, preserving nested
        directories such as `test-results/` so `test-results/*.trx` remain reachable under the
        same run directory. If the blob prefix is ever dropped upstream the staging root is
        moved verbatim, so this stays correct rather than silently emptying the run directory.

        Returns the destination path: the ONE directory that both contains result.json and is
        reported to the caller.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    $prefixed = Join-Path $StagingRoot $RunId
    $source = if (Test-Path -LiteralPath $prefixed -PathType Container) { $prefixed } else { $StagingRoot }
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { return $Destination }

    foreach ($item in @(Get-ChildItem -LiteralPath $source -Force)) {
        $target = Join-Path $Destination $item.Name
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Move-Item -LiteralPath $item.FullName -Destination $target -Force
    }

    if ($source -eq $prefixed) { Remove-Item -LiteralPath $prefixed -Recurse -Force -ErrorAction SilentlyContinue }

    return $Destination
}

Export-ModuleMember -Function Move-AzureBuildTestArtifacts

