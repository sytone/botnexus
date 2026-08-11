# Mutation harness for the #2785 freshness guards.
#
# A green suite proves nothing on its own: the whole point of #2785 is that a gate can report
# success about work it did not do. So each guard is reverted to its PRE-FIX behaviour and the
# suite must redden BY NAME. A mutation that leaves the suite green means that test is vacuous
# and does not defend the clause it claims to.
#
# Usage: pwsh -NoProfile -File ./ValidationFreshness.Mutation.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'ValidationFreshness.psm1'
$testPath = Join-Path $PSScriptRoot 'ValidationFreshness.Tests.ps1'
$original = Get-Content -LiteralPath $modulePath -Raw

# Each mutation restores one pre-#2785 behaviour and names the tests that MUST redden.
$mutations = @(
    @{
        Name     = 'M1: stale assemblies are treated as fresh (the --no-build defect)'
        Find     = "        `$state = if (`$assembly.LastWriteTimeUtc -lt `$reference) { 'stale' } else { 'fresh' }"
        Replace  = "        `$state = 'fresh'"
        Expected = @(
            'classifies an assembly compiled BEFORE the validated commit as stale',
            'reports IsFresh false and names the offender when any assembly predates the commit'
        )
    },
    @{
        Name     = 'M2: an absent assembly is treated as fresh instead of failing closed'
        Find     = "                    State            = 'missing'"
        Replace  = "                    State            = 'fresh'"
        Expected = @(
            'classifies an ABSENT assembly as missing, never fresh - absence fails closed',
            'evaluates freshness per configuration, so a Debug build cannot certify a Release run',
            'fails closed on a project that was never built at all'
        )
    },
    @{
        Name     = 'M3: base ref resolves to the tip, not the merge-base (the two-dot diff defect)'
        Find     = "    `$mergeBase = `"`$mergeBase`".Trim()"
        Replace  = "    `$mergeBase = `$baseCommit"
        Expected = @(
            'resolves the merge-base, not the base tip, when the base has moved on',
            'computes a change set containing ONLY the branch file when diffed from the merge-base'
        )
    },
    @{
        Name     = 'M4: the base ref is never fetched (the stale-cache defect)'
        Find     = "    if (`$isRemoteTracking -and -not `$NoFetch) {"
        Replace  = "    if (`$false) {"
        Expected = @(
            'fetches the remote-tracking base ref so the impacted set is not computed from a cache'
        )
    }
)

$failures = @()
try {
    foreach ($m in $mutations) {
        if ($original -notmatch [regex]::Escape($m.Find)) {
            throw "Mutation '$($m.Name)' does not apply: anchor not found in the module. Update the harness."
        }
        Set-Content -LiteralPath $modulePath -Value ($original.Replace($m.Find, $m.Replace)) -NoNewline -Encoding utf8

        $result = Invoke-Pester -Path $testPath -PassThru -Output None
        $reddened = @($result.Tests | Where-Object { $_.Result -eq 'Failed' } | ForEach-Object { $_.Name })

        Write-Host ""
        Write-Host $m.Name -ForegroundColor Cyan
        foreach ($expected in $m.Expected) {
            if ($reddened -contains $expected) {
                Write-Host "  [OK]   reddened: $expected" -ForegroundColor Green
            }
            else {
                Write-Host "  [VACUOUS] stayed green: $expected" -ForegroundColor Red
                $failures += "$($m.Name) -> $expected"
            }
        }
        Write-Host "  (total failed under this mutation: $($reddened.Count))" -ForegroundColor DarkGray
    }
}
finally {
    Set-Content -LiteralPath $modulePath -Value $original -NoNewline -Encoding utf8
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "MUTATION CHECK FAILED - $($failures.Count) assertion(s) are vacuous:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "MUTATION CHECK PASSED - every #2785 guard is defended by a test that reddens by name." -ForegroundColor Green
exit 0
