$ErrorActionPreference = 'Stop'
# Mutation proof for issue #3115 (AC4): reintroducing the double run-id prefix must redden the
# regression tests BY NAME. A green suite that survives the original defect proves nothing.
$target = Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.psm1'
$test = Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.Tests.ps1'
$orig = Get-Content $target -Raw

$mutations = @(
    @{
        Name = 'M1: apply the run-id prefix a second time (restores the #3115 defect)'
        Old  = '    $source = if (Test-Path'
        New  = '    $Destination = Join-Path $Destination $RunId' + [Environment]::NewLine + '    $source = if (Test-Path'
    },
    @{
        Name = 'M2: drop the flatten entirely and leave the prefixed tree in place'
        Old  = '    $source = if (Test-Path -LiteralPath $prefixed -PathType Container) { $prefixed } else { $StagingRoot }'
        New  = '    $source = $StagingRoot'
    }
)

$results = @()
foreach ($m in $mutations) {
    $mutated = $orig.Replace($m.Old, $m.New)
    if ($mutated -eq $orig) {
        $results += [pscustomobject]@{ Mutation = $m.Name; Outcome = 'MUTATION DID NOT APPLY - INVALID' }
        continue
    }
    Set-Content -Path $target -Value $mutated -NoNewline
    $r = Invoke-Pester -Path $test -PassThru -Output None
    $outcome = if ($r.FailedCount -gt 0) { "KILLED (failed=$($r.FailedCount))" } else { 'SURVIVED - GAP' }
    $failed = @($r.Failed | ForEach-Object { $_.ExpandedName })
    $results += [pscustomobject]@{ Mutation = $m.Name; Outcome = $outcome; FailedTests = ($failed -join ' | ') }
    Set-Content -Path $target -Value $orig -NoNewline
    if ((Get-Content $target -Raw) -ne $orig) { throw "RESTORE FAILED for $($m.Name)" }
}

$results | Format-List
"restored-clean: $((Get-Content $target -Raw) -eq $orig)"
