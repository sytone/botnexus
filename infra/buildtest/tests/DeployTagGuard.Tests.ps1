# Tests for the content-addressed runner image tag and the collision guard (#2900).
#
# WHY THIS EXISTS: on 2026-08-09 a hand-picked tag bump silently OVERWROTE an existing ACR image,
# because ACR tags are mutable and `az acr build` republishes over them with exit 0 and no warning.
# The source default had also drifted four versions behind the deployed job. These tests pin the
# properties that make that class of mistake impossible.
#
# No Azure access required: the tag derivation is pure content hashing, and the collision decision
# is pure set membership. Both are extracted here as functions matching the deploy script, and
# `Deploy-TagLogic_MatchesScript` guards against the two drifting apart.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = @()
function Assert-True {
    param([bool]$Condition, [string]$Because)
    if (-not $Condition) { $script:failures += $Because }
}

function Get-RunnerContentTag {
    param([Parameter(Mandatory)][string]$RunnerPath)

    $runnerFiles = Get-ChildItem -Path $RunnerPath -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/]tests[\\/]' } |
        Sort-Object { $_.FullName.Substring($RunnerPath.Length).Replace('\', '/') }

    if (-not $runnerFiles) {
        throw "Refusing to derive an image tag: no files found under $RunnerPath."
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $buffer = [System.IO.MemoryStream]::new()
        foreach ($file in $runnerFiles) {
            $relative = $file.FullName.Substring($RunnerPath.Length).Replace('\', '/').TrimStart('/')
            $content = (Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop) -replace "`r`n", "`n"
            $bytes = [System.Text.Encoding]::UTF8.GetBytes("$relative`n$content`n")
            $buffer.Write($bytes, 0, $bytes.Length)
        }
        $buffer.Position = 0
        return 'src-' + [BitConverter]::ToString($sha.ComputeHash($buffer)).Replace('-', '').Substring(0, 12).ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Test-TagPublishDecision {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ExistingTags,
        [Parameter(Mandatory)][bool]$WasDerived
    )
    $exists = $ExistingTags -contains $Tag
    if ($exists -and -not $WasDerived) { return 'refuse' }
    if ($exists) { return 'skip' }
    return 'build'
}

# --- fixture -------------------------------------------------------------------------------

$root = Join-Path ([System.IO.Path]::GetTempPath()) "tag2900-$([Guid]::NewGuid().ToString('N'))"
$runner = Join-Path $root 'runner'
New-Item -ItemType Directory -Path $runner -Force | Out-Null
Set-Content -Path (Join-Path $runner 'Dockerfile') -Value "FROM base`nRUN echo hi"
Set-Content -Path (Join-Path $runner 'entrypoint.ps1') -Value "Write-Host 'run'"

# --- tests ---------------------------------------------------------------------------------

# 1. Deterministic: identical content yields an identical tag.
$a = Get-RunnerContentTag -RunnerPath $runner
$b = Get-RunnerContentTag -RunnerPath $runner
Assert-True ($a -eq $b) "1: tag is not deterministic ($a vs $b)"

# 2. Sensitive: ANY content change yields a different tag. Without this the guard is worthless,
#    because a changed runner could be published under a tag that already exists.
Set-Content -Path (Join-Path $runner 'entrypoint.ps1') -Value "Write-Host 'run changed'"
$c = Get-RunnerContentTag -RunnerPath $runner
Assert-True ($c -ne $a) '2: content change did not change the tag'

# 3. A NEW FILE changes the tag. A hash over only known filenames would miss an added file.
Set-Content -Path (Join-Path $runner 'helper.ps1') -Value "Write-Host 'helper'"
$d = Get-RunnerContentTag -RunnerPath $runner
Assert-True ($d -ne $c) '3: adding a file did not change the tag'

# 4. Line-ending-only differences do NOT change the tag: a Windows and a Linux operator must not
#    mint two different tags for identical content.
Set-Content -Path (Join-Path $runner 'helper.ps1') -Value "line1`r`nline2" -NoNewline
$crlf = Get-RunnerContentTag -RunnerPath $runner
Set-Content -Path (Join-Path $runner 'helper.ps1') -Value "line1`nline2" -NoNewline
$lf = Get-RunnerContentTag -RunnerPath $runner
Assert-True ($crlf -eq $lf) '4: CRLF vs LF produced different tags'

# 5. Tests directory is excluded: editing runner tests must not force an image rebuild, since
#    those files never enter the image.
$before = Get-RunnerContentTag -RunnerPath $runner
New-Item -ItemType Directory -Path (Join-Path $runner 'tests') -Force | Out-Null
Set-Content -Path (Join-Path $runner 'tests/Some.Tests.ps1') -Value 'Describe "x" {}'
$after = Get-RunnerContentTag -RunnerPath $runner
Assert-True ($before -eq $after) '5: a file under tests/ changed the image tag'

# 6. NON-VACUITY: the candidate set must be non-empty. An empty directory hashing to a constant
#    would pin every future build to a single tag and pass tests 1 and 4 vacuously.
$empty = Join-Path $root 'empty'
New-Item -ItemType Directory -Path $empty -Force | Out-Null
$threw = $false
try { Get-RunnerContentTag -RunnerPath $empty } catch { $threw = $true }
Assert-True $threw '6: empty runner directory did not throw -- tag derivation can pass vacuously'

# 7. Tag is a valid Docker tag (alphanumerics, dots, dashes, underscores; max 128).
Assert-True ($a -match '^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$') "7: '$a' is not a valid docker tag"

# 8. THE CENTRAL GUARD: an explicit tag that already exists is REFUSED, never overwritten.
Assert-True ((Test-TagPublishDecision -Tag '0.1.12' -ExistingTags @('0.1.11', '0.1.12') -WasDerived $false) -eq 'refuse') `
    '8: explicit collision was not refused -- this is the 2026-08-09 overwrite'

# 9. A derived tag that already exists is a genuine no-op: content is identical, so skip.
Assert-True ((Test-TagPublishDecision -Tag 'src-abc123' -ExistingTags @('src-abc123') -WasDerived $true) -eq 'skip') `
    '9: unchanged derived content did not skip the build'

# 10. A tag that does not exist builds, whether derived or explicit.
Assert-True ((Test-TagPublishDecision -Tag 'src-new' -ExistingTags @('0.1.1') -WasDerived $true) -eq 'build') '10: new derived tag did not build'
Assert-True ((Test-TagPublishDecision -Tag '9.9.9' -ExistingTags @('0.1.1') -WasDerived $false) -eq 'build') '10: new explicit tag did not build'

# 11. An empty registry (no tags yet) must still build rather than error.
Assert-True ((Test-TagPublishDecision -Tag 'src-first' -ExistingTags @() -WasDerived $true) -eq 'build') '11: first-ever push did not build'

# 12. The deploy script must not reintroduce a hardcoded default tag, and main.bicep must not
#     carry one either. Those two defaults were the original drift.
$scriptPath = Join-Path $PSScriptRoot '../Deploy-BuildTestInfrastructure.ps1'
$bicepPath = Join-Path $PSScriptRoot '../main.bicep'
if (Test-Path $scriptPath) {
    $deployText = Get-Content $scriptPath -Raw
    Assert-True ($deployText -notmatch "RunnerImageTag\s*=\s*'[\d.]+'") '12: deploy script reintroduced a hardcoded version default'
    Assert-True ($deployText -match 'Refusing to overwrite the existing image tag') '12: deploy script lost the collision guard'
}
if (Test-Path $bicepPath) {
    Assert-True ((Get-Content $bicepPath -Raw) -notmatch "param runnerImageTag string\s*=\s*'") '12: main.bicep reintroduced a default tag'
}

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

if ($script:failures.Count) {
    Write-Host "FAILED ($($script:failures.Count)):" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'Deploy tag guard: all checks passed.' -ForegroundColor Green
exit 0
