$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot 'CiCommentRendering.ps1'
$test = Join-Path $PSScriptRoot 'CiCommentRendering.Tests.ps1'
$orig = Get-Content $target -Raw

$mutations = @(
    @{
        Name = 'M1: remove the null-key guard (restores the #2997 defect)'
        Old  = @"
        `$key = Get-CheckDisplayKey -Row `$row
        if (-not `$key) { continue }
        `$checkMap[`$key] = `$row.status
"@
        New  = @"
        `$checkMap[`$row.name] = `$row.status
"@
    },
    @{
        Name = 'M2: drop the context fallback in Get-CheckDisplayKey'
        Old  = @"
    `$context = `$Row.PSObject.Properties['context'] ? `$Row.context : `$null
    if (`$context -is [string] -and -not [string]::IsNullOrWhiteSpace(`$context)) { return `$context }
"@
        New  = ''
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
    $onDisk = Get-Content $target -Raw
    if ($onDisk -eq $orig) {
        $results += [pscustomobject]@{ Mutation = $m.Name; Outcome = 'MUTATION DID NOT APPLY ON DISK - INVALID' }
        Set-Content -Path $target -Value $orig -NoNewline
        continue
    }
    $r = Invoke-Pester -Path $test -PassThru -Output None
    $outcome = if ($r.FailedCount -gt 0) { "KILLED (failed=$($r.FailedCount))" } else { 'SURVIVED - GAP' }
    $failed = @($r.Failed | ForEach-Object { $_.ExpandedName })
    $results += [pscustomobject]@{ Mutation = $m.Name; Outcome = $outcome; FailedTests = ($failed -join ' | ') }
    Set-Content -Path $target -Value $orig -NoNewline
    $restored = (Get-Content $target -Raw) -eq $orig
    if (-not $restored) { throw "RESTORE FAILED for $($m.Name)" }
}

$results | Format-List
"restored-clean: $((Get-Content $target -Raw) -eq $orig)"
