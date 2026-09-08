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

function ConvertTo-CountableArray {
    <#
    .SYNOPSIS
        Returns a real array for a value that may be $null, a scalar, or a collection.

    .DESCRIPTION
        Issue #3805. `$x = if ($cond) { @($json.prop) } else { @() }` looks like it always
        yields an array, but PowerShell UNROLLS the output of the if-statement's pipeline: when
        `$json.prop` is JSON `null`, `@($null)` is a one-element array whose single element is
        $null, and unrolling that on assignment hands the variable back a bare $null. Under
        `Set-StrictMode -Version Latest` the next `.Count` then throws
        "The property 'Count' cannot be found on this object".

        That is exactly the state a BUILD failure produces - the test phase is skipped, so
        `result.json` carries `"tests": null` and `"projectCosts": null` - which is why the
        failure-reporting path was the one place the defect could fire, and why it destroyed the
        diagnosis instead of printing it.

        Nulls are dropped rather than counted: a null element is the absence of a datum, and a
        report that says "1 project cost" and then formats $null is worse than saying none.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()]$Value)

    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($item in @($Value)) {
        if ($null -ne $item) { $items.Add($item) }
    }

    # The unary comma prevents the pipeline from unrolling an empty result back to $null -
    # i.e. it stops this helper from reintroducing the very defect it exists to fix.
    return , ([object[]]$items.ToArray())
}

function Save-AzureBuildTestFailureArtifacts {
    <#
    .SYNOPSIS
        Lands already-downloaded artifacts on disk before the temp staging root is destroyed.

    .DESCRIPTION
        Issue #3805. The download lands in a staging directory under the run's temp root, which
        the script's `finally` removes unconditionally. Any throw between the download and the
        normal flatten therefore deleted `result.json` and `build.log` - the two files that carry
        the diagnosis - while the remote blobs had also already been deleted unless
        `-KeepRemoteArtifacts` was passed. The evidence then existed at no location the caller
        could reach, and the run had to be repeated in full.

        A failing gate is the case where artifacts matter MOST, so retention runs on the failure
        path too, through the same `Move-AzureBuildTestArtifacts` seam rather than a second
        placement rule that could drift from it.

        Returns the destination when something was retained, or $null when there was nothing to
        retain - so the caller can print a path it has actually verified rather than assert one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $StagingRoot -PathType Container)) { return $null }
    $downloaded = ConvertTo-CountableArray (Get-ChildItem -LiteralPath $StagingRoot -Force -Recurse -File -ErrorAction SilentlyContinue)
    if ($downloaded.Count -eq 0) { return $null }

    return Move-AzureBuildTestArtifacts -StagingRoot $StagingRoot -RunId $RunId -Destination $Destination
}

Export-ModuleMember -Function Move-AzureBuildTestArtifacts, ConvertTo-CountableArray, Save-AzureBuildTestFailureArtifacts

