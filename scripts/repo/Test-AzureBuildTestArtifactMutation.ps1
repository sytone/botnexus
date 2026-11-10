<#
.SYNOPSIS
Tests artifact regression sensitivity in disposable mutation copies.
.DESCRIPTION
Never mutates its source checkout. Requires owned scratch and explicit mutation opt-in. Baseline must pass and every applied mutation must fail named assertions; discovery/container errors never count as a killed mutation.
.PARAMETER RunMutation
Explicit opt-in; omitted means discovery-safe no-op.
.PARAMETER ScratchRoot
Owned directory for disposable fixture copies.
.EXAMPLE
./Test-AzureBuildTestArtifactMutation.ps1 -RunMutation -ScratchRoot $ownedScratch
.OUTPUTS
System.Management.Automation.PSCustomObject. Mutation outcome and failed assertion names.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$RunMutation,
    [string]$ScratchRoot = $env:BOTNEXUS_TEST_SCRATCH_ROOT
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $RunMutation) {
    Write-Verbose 'Mutation harness excluded from ordinary fixture collection; use -RunMutation explicitly.'
    return
}
if ([string]::IsNullOrWhiteSpace($ScratchRoot)) {
    throw 'Supply owned ScratchRoot for contained mutation execution.'
}
if (-not $PSCmdlet.ShouldProcess($ScratchRoot, 'Create disposable artifact mutation copies and run synthetic assertions')) {
    return
}
Import-Module Pester -RequiredVersion 5.7.1 -ErrorAction Stop
$module = 'AzureBuildTestArtifacts.psm1'
$script = 'Invoke-AzureBuildTest.ps1'
$test = 'Test-AzureBuildTestArtifacts.ps1'
$mutations = @(
    @{
        Name = 'M1: apply the run-id prefix a second time (restores the #3115 defect)'
        Target = $module
        Old = '    $source = if (Test-Path'
        New = '    $Destination = Join-Path $Destination $RunId' + [Environment]::NewLine + '    $source = if (Test-Path'
    },
    @{
        Name = 'M2: drop the flatten entirely and leave the prefixed tree in place'
        Target = $module
        Old = '    $source = if (Test-Path -LiteralPath $prefixed -PathType Container) { $prefixed } else { $StagingRoot }'
        New = '    $source = $StagingRoot'
    },
    @{
        Name = 'M3: let ConvertTo-CountableArray unroll an empty result back to $null (restores the #3805 .Count throw)'
        Target = $module
        Old = '    return , ([object[]]$items.ToArray())'
        New = '    return ([object[]]$items.ToArray())'
    },
    @{
        Name = 'M4: make failure retention a no-op, so the temp cleanup destroys the diagnosis again (#3805)'
        Target = $module
        Old = '    return Move-AzureBuildTestArtifacts -StagingRoot $StagingRoot -RunId $RunId -Destination $Destination'
        New = '    return $null'
    },
    @{
        Name = 'M5: restore the null-collapsing projectCosts idiom in the script (#3805 root cause)'
        Target = $script
        Old = "    `$projectCosts = ConvertTo-CountableArray (`$(if (`$result -and `$result.PSObject.Properties['projectCosts']) { `$result.projectCosts } else { `$null }))"
        New = "    `$projectCosts = if (`$result -and `$result.PSObject.Properties['projectCosts']) { @(`$result.projectCosts) } else { @() }"
    },
    @{
        Name = 'M6: delete the temp root before retaining artifacts (#3805 AC2 ordering)'
        Target = $script
        Old = '    if (-not $artifactsPlaced) {'
        New = '    if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force }' + [Environment]::NewLine + '    if (-not $artifactsPlaced) {'
    }
)

$root = Join-Path $ScratchRoot ('artifact-mutation-' + [guid]::NewGuid().ToString('N'))
$results = @()
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $baseline = Invoke-Pester -Path (Join-Path $PSScriptRoot $test) -PassThru -Output None
    if ($baseline.Result -ne 'Passed' -or $baseline.FailedCount -or $baseline.PassedCount -eq 0) {
        throw 'Artifact baseline failed; mutation results would not be meaningful.'
    }
    foreach ($mutation in $mutations) {
        $copy = Join-Path $root ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $copy | Out-Null
        foreach ($name in @($module, $script, $test)) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $copy $name)
        }
        $target = Join-Path $copy $mutation.Target
        $original = [IO.File]::ReadAllText($target)
        $oldText = $mutation.Old
        if ($mutation.Name.StartsWith('M5:', [StringComparison]::Ordinal)) {
            $tokens = $null
            $parseErrors = $null
            $syntax = [Management.Automation.Language.Parser]::ParseInput($original, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count) {
                throw 'Mutation input could not be parsed.'
            }
            $assignments = @($syntax.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$projectCosts'
                    }, $true))
            if ($assignments.Count -ne 1) {
                throw 'Expected exactly one projectCosts assignment.'
            }
            $oldText = $assignments[0].Extent.Text
        }
        $mutated = $original.Replace($oldText, $mutation.New)
        if ($mutated -eq $original) {
            throw "Mutation did not apply: $($mutation.Name)"
        }
        [IO.File]::WriteAllText($target, $mutated)
        $result = Invoke-Pester -Path (Join-Path $copy $test) -PassThru -Output None
        $containerErrors = @($result.Containers | Where-Object { $_.ErrorRecord.Count -gt 0 })
        $failedNames = @($result.Failed | ForEach-Object { $_.ExpandedName })
        if ($containerErrors.Count -or $result.FailedCount -eq 0 -or $failedNames.Count -eq 0) {
            throw "Mutation survived or harness failed: $($mutation.Name)"
        }
        $results += [pscustomobject]@{ Mutation = $mutation.Name; Outcome = 'KILLED'; FailedTests = $failedNames; BaselinePassed = $baseline.PassedCount }
    }
    $results
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
