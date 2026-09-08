$ErrorActionPreference = 'Stop'
# Mutation proof for issues #3115 (AC4) and #3805 (AC3): reintroducing an original defect must
# redden the regression tests BY NAME. A green suite that survives the original defect proves
# nothing.
#
# #3805 mutates BOTH files, so each mutation names its own target - the null-collapse defect lives
# in the module helper, while the artifact-destroying cleanup lives in the script.
$module = Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.psm1'
$script = Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'
$test = Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.Tests.ps1'

$mutations = @(
    @{
        Name = 'M1: apply the run-id prefix a second time (restores the #3115 defect)'
        Target = $module
        Old  = '    $source = if (Test-Path'
        New  = '    $Destination = Join-Path $Destination $RunId' + [Environment]::NewLine + '    $source = if (Test-Path'
    },
    @{
        Name = 'M2: drop the flatten entirely and leave the prefixed tree in place'
        Target = $module
        Old  = '    $source = if (Test-Path -LiteralPath $prefixed -PathType Container) { $prefixed } else { $StagingRoot }'
        New  = '    $source = $StagingRoot'
    },
    @{
        Name = 'M3: let ConvertTo-CountableArray unroll an empty result back to $null (restores the #3805 .Count throw)'
        Target = $module
        Old  = '    return , ([object[]]$items.ToArray())'
        New  = '    return ([object[]]$items.ToArray())'
    },
    @{
        Name = 'M4: make failure retention a no-op, so the temp cleanup destroys the diagnosis again (#3805)'
        Target = $module
        Old  = '    return Move-AzureBuildTestArtifacts -StagingRoot $StagingRoot -RunId $RunId -Destination $Destination'
        New  = '    return $null'
    },
    @{
        Name = 'M5: restore the null-collapsing projectCosts idiom in the script (#3805 root cause)'
        Target = $script
        Old  = "    `$projectCosts = ConvertTo-CountableArray (`$(if (`$result -and `$result.PSObject.Properties['projectCosts']) { `$result.projectCosts } else { `$null }))"
        New  = "    `$projectCosts = if (`$result -and `$result.PSObject.Properties['projectCosts']) { @(`$result.projectCosts) } else { @() }"
    },
    @{
        Name = 'M6: delete the temp root before retaining artifacts (#3805 AC2 ordering)'
        Target = $script
        Old  = '    if (-not $artifactsPlaced) {'
        New  = '    if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force }' + [Environment]::NewLine + '    if (-not $artifactsPlaced) {'
    }
)

$results = @()
foreach ($m in $mutations) {
    $target = $m.Target
    $orig = Get-Content $target -Raw
    $mutated = $orig.Replace($m.Old, $m.New)
    if ($mutated -eq $orig) {
        $results += [pscustomobject]@{ Mutation = $m.Name; Outcome = 'MUTATION DID NOT APPLY - INVALID' }
        continue
    }
    Set-Content -Path $target -Value $mutated -NoNewline
    try {
        $r = Invoke-Pester -Path $test -PassThru -Output None
        $outcome = if ($r.FailedCount -gt 0) { "KILLED (failed=$($r.FailedCount))" } else { 'SURVIVED - GAP' }
        $failed = @($r.Failed | ForEach-Object { $_.ExpandedName })
        $results += [pscustomobject]@{ Mutation = $m.Name; Outcome = $outcome; FailedTests = ($failed -join ' | ') }
    }
    finally {
        Set-Content -Path $target -Value $orig -NoNewline
        if ((Get-Content $target -Raw) -ne $orig) { throw "RESTORE FAILED for $($m.Name)" }
    }
}

$results | Format-List
"restored-clean-module: $((Get-Content $module -Raw).Length -gt 0)"
"survivors: $(@($results | Where-Object { $_.Outcome -ne '' -and $_.Outcome -notlike 'KILLED*' }).Count)"
