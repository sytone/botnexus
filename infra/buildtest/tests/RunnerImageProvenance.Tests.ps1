Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Tests for RunnerImageProvenance.ps1 (#3516).
#
# The defect these pin: the Container Apps Job ran an image built from an unmerged branch for three
# days. It contained a file main does not have, threw before build or test executed, and reported
# `tests: null`. No branch could be validated and nothing said why - because nothing compared the
# deployed image against the sources.

. (Join-Path $PSScriptRoot '..' 'RunnerImageProvenance.ps1')

$script:failures = 0
function Assert-True($condition, $message) {
    if (-not $condition) { Write-Host "FAIL: $message" -ForegroundColor Red; $script:failures++ }
    else { Write-Host "pass: $message" -ForegroundColor DarkGray }
}
function Assert-Equal($expected, $actual, $message) {
    Assert-True ($expected -eq $actual) "$message (expected '$expected', got '$actual')"
}

$root = Join-Path ([IO.Path]::GetTempPath()) "runner-provenance-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null

try {
    $runner = Join-Path $root 'runner'
    New-Item -ItemType Directory -Path $runner -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $runner 'entrypoint.ps1') -Value "Write-Host 'a'`n"
    Set-Content -LiteralPath (Join-Path $runner 'Dockerfile') -Value "FROM scratch`n"

    # 1. Determinism - the whole design rests on identical content yielding an identical tag.
    $first = Get-RunnerContentTag -RunnerPath $runner
    $second = Get-RunnerContentTag -RunnerPath $runner
    Assert-Equal $first $second '1: the same content derives the same tag'
    Assert-True ($first -match '^src-[0-9a-f]{12}$') '2: the tag has the src-<sha12> shape'

    # 3. A content change must change the tag, or a modified runner could deploy under an old tag -
    #    which is exactly how an image and its sources drift apart.
    Set-Content -LiteralPath (Join-Path $runner 'entrypoint.ps1') -Value "Write-Host 'b'`n"
    $changed = Get-RunnerContentTag -RunnerPath $runner
    Assert-True ($changed -ne $first) '3: changed content derives a different tag'

    # 4. A NEW FILE must change the tag. This is the case that actually bit: the branch image added
    #    RunnerBuild.ps1, and a derivation that ignored new files would have matched anyway.
    Set-Content -LiteralPath (Join-Path $runner 'RunnerBuild.ps1') -Value "Write-Host 'new'`n"
    $withNewFile = Get-RunnerContentTag -RunnerPath $runner
    Assert-True ($withNewFile -ne $changed) '4: an ADDED file derives a different tag'

    # 5. Tests are excluded, matching the deploy script - they do not land in the image.
    $testsDir = Join-Path $runner 'tests'
    New-Item -ItemType Directory -Path $testsDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $testsDir 'Some.Tests.ps1') -Value "Write-Host 'test'`n"
    Assert-Equal $withNewFile (Get-RunnerContentTag -RunnerPath $runner) '5: files under tests/ do not affect the tag'

    # 6. Line endings are not content. A Windows operator and a Linux operator must derive the same
    #    tag from the same file, or the guard cries wolf on every cross-platform checkout.
    $crlf = Join-Path $root 'crlf'
    New-Item -ItemType Directory -Path $crlf -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $crlf 'entrypoint.ps1'), "Write-Host 'a'`r`n")
    [IO.File]::WriteAllText((Join-Path $crlf 'Dockerfile'), "FROM scratch`r`n")
    $lf = Join-Path $root 'lf'
    New-Item -ItemType Directory -Path $lf -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $lf 'entrypoint.ps1'), "Write-Host 'a'`n")
    [IO.File]::WriteAllText((Join-Path $lf 'Dockerfile'), "FROM scratch`n")
    Assert-Equal (Get-RunnerContentTag -RunnerPath $lf) (Get-RunnerContentTag -RunnerPath $crlf) `
        '6: CRLF and LF derive the same tag'

    # 7. An empty candidate set must throw rather than hash to a constant, which would silently match
    #    every deployment forever.
    $empty = Join-Path $root 'empty'
    New-Item -ItemType Directory -Path $empty -Force | Out-Null
    $threw = $false
    try { Get-RunnerContentTag -RunnerPath $empty | Out-Null } catch { $threw = $true }
    Assert-True $threw '7: an empty runner directory throws rather than returning a constant'

    # 7b. A path containing '..' segments must work. The real caller passes
    #     `Join-Path $PSScriptRoot '..' '..' 'infra' 'buildtest' 'runner'`, and an unresolved prefix
    #     is LONGER than the resolved FullName values Get-ChildItem returns - so the substring threw
    #     and the caller's catch swallowed it. The guard silently reported nothing on every run until
    #     this case existed.
    $dotted = Join-Path (Join-Path $runner '..') 'runner'
    $dottedTag = Get-RunnerContentTag -RunnerPath $dotted
    Assert-Equal (Get-RunnerContentTag -RunnerPath $runner) $dottedTag `
        "7b: a path with '..' segments derives the same tag as its resolved form"

    # 8-10. The verdict surface.
    $expected = Get-RunnerContentTag -RunnerPath $lf
    Assert-Equal 'match' (Test-RunnerImageMatchesSources -RunnerPath $lf -DeployedTag $expected).Verdict `
        '8: a deployed tag equal to the derived tag is a match'
    Assert-Equal 'mismatch' (Test-RunnerImageMatchesSources -RunnerPath $lf -DeployedTag 'src-4eafca6de104').Verdict `
        '9: the real 2026-08-21 branch tag is reported as a mismatch'
    Assert-Equal 'unknown' (Test-RunnerImageMatchesSources -RunnerPath $lf -DeployedTag $null).Verdict `
        '10: an unreadable job is unknown, NOT a mismatch - a developer without Azure credentials must not see a red check'
}
finally {
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) failure(s)" -ForegroundColor Red
    exit 1
}
Write-Host 'all runner-image provenance checks passed' -ForegroundColor Green
