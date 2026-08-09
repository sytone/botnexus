$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($name in @('SOURCE_BLOB_URL', 'ARTIFACT_BLOB_URL')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Required environment variable $name is missing."
    }
}

$runId = if ($env:RUN_ID) { $env:RUN_ID } else { [Guid]::NewGuid().ToString('N') }
$mode = if ($env:TEST_MODE) { $env:TEST_MODE } else { 'impacted' }
$baseRef = if ($env:BASE_REF) { $env:BASE_REF } else { 'origin/main' }
$workRoot = Join-Path '/work' $runId
$payloadArchive = Join-Path $workRoot 'payload.tar.gz'
$payloadRoot = Join-Path $workRoot 'payload'
$sourceRoot = Join-Path $workRoot 'src'
$artifactsRoot = Join-Path $workRoot 'artifacts'
$resultsRoot = Join-Path $artifactsRoot 'test-results'
$runnerResultScript = '/runner/RunnerResult.ps1'
New-Item -ItemType Directory -Path $payloadRoot, $artifactsRoot, $resultsRoot -Force | Out-Null

# PHASE TIMING (#2889)
#
# The gate is the dominant cost in the development cycle, but until this existed the only
# derivable number was total wall clock: a 12-minute run could have been 3 minutes of restore
# plus 9 of test, or 7 plus 5, and those two worlds call for completely different fixes. Every
# optimisation proposal was therefore an argument from structure rather than from measurement.
#
# Written to an ARTIFACT, not merely Write-Host. Runner stdout is not among the uploaded
# artifacts and `az containerapp job logs show` hangs on this environment, so a diagnostic that
# only reaches the console is one nobody can read -- the inotify probe below learned that the
# expensive way, producing zero evidence either way on its first run.
#
# MUST NOT be able to fail an otherwise-green run (#2889 AC4). Measurement is not the subject of
# the gate: a timing bug turning a passing suite red would be strictly worse than having no
# timings at all. Every write is therefore best-effort and swallows its own errors.
$timingLog = Join-Path $artifactsRoot 'runner-timing.log'
$phaseTimings = [ordered]@{}

function Write-PhaseTiming {
    param(
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][string]$Status,
        [double]$Seconds = 0
    )
    try {
        $phaseTimings[$Phase] = [ordered]@{ status = $Status; seconds = [Math]::Round($Seconds, 2) }
        $line = if ($Status -eq 'skipped') {
            "{0,-18} skipped" -f $Phase
        }
        else {
            "{0,-18} {1,8:N2}s  {2}" -f $Phase, $Seconds, $Status
        }
        $line | Add-Content -Path $timingLog -ErrorAction Stop
    }
    catch {
        # Deliberately swallowed: see the AC4 note above.
    }
}

function Invoke-TimedPhase {
    param(
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][scriptblock]$Body
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $sw.Stop()
        Write-PhaseTiming -Phase $Phase -Status 'ok' -Seconds $sw.Elapsed.TotalSeconds
    }
    catch {
        # A failing phase is still a measured phase -- knowing that a run died 40 seconds into
        # restore rather than 9 minutes into test is exactly the attribution this exists for.
        $sw.Stop()
        Write-PhaseTiming -Phase $Phase -Status 'failed' -Seconds $sw.Elapsed.TotalSeconds
        throw
    }
}

try {
    "runner phase timings for run $runId (mode=$mode) $(Get-Date -Format o)" | Set-Content -Path $timingLog -ErrorAction Stop
}
catch {
    # Deliberately swallowed: see the AC4 note above.
}

$env:AZCOPY_AUTO_LOGIN_TYPE = 'MSI'
if ($env:AZURE_CLIENT_ID) { $env:AZCOPY_MSI_CLIENT_ID = $env:AZURE_CLIENT_ID }

Write-Host "Downloading source snapshot for run $runId with managed identity..."
Invoke-TimedPhase -Phase 'source-download' -Body {
    & azcopy copy $env:SOURCE_BLOB_URL $payloadArchive --overwrite=true | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Source download failed with exit code $LASTEXITCODE." }
}

Invoke-TimedPhase -Phase 'payload-extract' -Body {
    tar -xzf $payloadArchive -C $payloadRoot
    if ($LASTEXITCODE -ne 0) { throw "Payload extraction failed with exit code $LASTEXITCODE." }

    git clone (Join-Path $payloadRoot 'repository.bundle') $sourceRoot
    if ($LASTEXITCODE -ne 0) { throw "Repository bundle clone failed with exit code $LASTEXITCODE." }
    tar -xzf (Join-Path $payloadRoot 'workspace.tar.gz') -C $sourceRoot
    if ($LASTEXITCODE -ne 0) { throw "Workspace overlay failed with exit code $LASTEXITCODE." }

    # The packed payload is no longer needed after the repository is materialized.
    # Reclaim it before restore/build so test fixtures get the maximum ephemeral space.
    Remove-Item -LiteralPath $payloadArchive, $payloadRoot -Recurse -Force
}

Push-Location $sourceRoot
$exitCode = 0
$testResult = $null
try {
    git config user.name 'BotNexus Azure Build Runner'
    git config user.email 'build-runner@botnexus.invalid'
    git add --all
    git commit --allow-empty -m 'build runner snapshot' | Out-Host

    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_PACKAGES = '/cache/nuget'
    $env:PLAYWRIGHT_BROWSERS_PATH = '/ms-playwright'

    # Attempt to raise the inotify INSTANCE ceiling before any test host starts (#2825).
    #
    # MEASURED OUTCOME: this DOES NOT WORK on Azure Container Apps. /proc/sys is mounted
    # read-only, the container has no CAP_SYS_ADMIN, and Container Apps jobs expose no sysctl
    # surface, so both writes fail and the run continues on the default. The block is retained
    # deliberately: it documents what was tried, it costs nothing, and it will take effect on any
    # runtime that does permit the write. Do not read a green run as evidence that it applied --
    # check runner-env.log, which records the failure explicitly.
    # The exhaustion finding itself is independently confirmed by probe: sixteen test classes boot
    # a WebApplicationFactory<Program>, and every host registers
    # AddJsonFile(reloadOnChange: true) -- one inotify instance each. Instances are counted
    # per-USER, not per-process, so a full-suite run accumulates them across every concurrently
    # live test host. The container default (128) is exhausted partway through, and the failure is
    # SILENT: FileSystemWatcher cannot allocate, so the reload token simply never fires and the
    # test reports "expected the reload pipeline to notify IOptionsMonitor" as though the product
    # were broken.
    #
    # Measured, not assumed: a probe run in this image fired reliably at low watcher counts and
    # stopped firing once enough reloading configuration roots were held open, while the same
    # probe passes standalone -- which is exactly why three earlier mechanism hypotheses
    # (overlayfs, the atomic inode swap, xUnit collection parallelism) each looked plausible and
    # each tested clean in isolation.
    #
    # Best-effort: /proc/sys is read-only here, so this always lands in the catch on Container
    # Apps and must not fail the run -- it simply leaves the previous behaviour in place. The
    # real remedy is to reduce watcher DEMAND (test hosts mostly do not need reloadOnChange at
    # all) rather than to raise supply, which is tracked separately.
    #
    # The outcome is written to an ARTIFACT, not merely Write-Host. Runner stdout is not among the
    # uploaded artifacts and `az containerapp job logs show` hangs on this environment, so a
    # diagnostic that only reaches the console is one nobody can read: the first run of this block
    # produced zero evidence either way and could not be confirmed to have executed at all.
    $runnerEnvLog = Join-Path $artifactsRoot 'runner-env.log'
    "runner image env probe $(Get-Date -Format o)" | Set-Content -Path $runnerEnvLog
    "node: $(try { (& node --version) 2>&1 } catch { '<missing>' })" | Add-Content -Path $runnerEnvLog

    foreach ($limit in @(
        @{ Path = '/proc/sys/fs/inotify/max_user_instances'; Value = '8192' },
        @{ Path = '/proc/sys/fs/inotify/max_user_watches'; Value = '524288' })) {
        try {
            $before = (Get-Content $limit.Path -ErrorAction Stop).Trim()
            Set-Content -Path $limit.Path -Value $limit.Value -ErrorAction Stop
            $after = (Get-Content $limit.Path -ErrorAction Stop).Trim()
            $line = "inotify: $($limit.Path) $before -> $after"
        }
        catch {
            $line = "inotify: could not raise $($limit.Path) ($($_.Exception.Message.Split([Environment]::NewLine)[0])). Continuing with the default."
        }
        Write-Host $line
        $line | Add-Content -Path $runnerEnvLog
    }

    Invoke-TimedPhase -Phase 'restore' -Body {
        & dotnet restore dirs.proj --nologo 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'restore.log')
        if ($LASTEXITCODE -ne 0) { $script:exitCode = $LASTEXITCODE; throw "Restore failed with exit code $LASTEXITCODE." }
    }

    Invoke-TimedPhase -Phase 'tool-restore' -Body {
        & dotnet tool restore 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'tool-restore.log')
        if ($LASTEXITCODE -ne 0) { $script:exitCode = $LASTEXITCODE; throw "Tool restore failed with exit code $LASTEXITCODE." }
    }

    Invoke-TimedPhase -Phase 'build' -Body {
        & dotnet build dirs.proj -c Debug --nologo --tl:off --no-restore 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'build.log')
        if ($LASTEXITCODE -ne 0) { $script:exitCode = $LASTEXITCODE; throw "Build failed with exit code $LASTEXITCODE." }
    }

    $strictResults = $mode -in @('full', 'core', 'strict', 'playwright')

    # Timed inline rather than through Invoke-TimedPhase: every branch below assigns $exitCode,
    # and an assignment inside a scriptblock invoked with & would land in a child scope and be
    # silently discarded -- the run would then report success regardless of the test outcome.
    # That is precisely the fail-open class #2851 was raised to eliminate, so the measurement
    # must not reintroduce it.
    $testStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    switch ($mode) {
        'full' {
            & dotnet test tests/dirs.proj --nologo --tl:off -c Debug --no-build --logger "trx;LogFilePrefix=runner" --results-directory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
            $exitCode = $LASTEXITCODE
        }
        'core' {
            # Everything except the browser/E2E projects. Those are quarantined while the
            # NotExecuted defect is investigated: on 2026-08-06 a 'full' run reported exit 0
            # with 265 of 280 E2E tests NotExecuted, which is neither passed nor failed and
            # therefore certified a green gate that had silently skipped them. Core must be
            # trustworthy before E2E is folded back in.
            $coreFilter = 'FullyQualifiedName!~BotNexus.Integration.E2E&FullyQualifiedName!~BotNexus.E2E'
            & dotnet test tests/dirs.proj --nologo --tl:off -c Debug --no-build --filter $coreFilter --logger "trx;LogFilePrefix=runner" --results-directory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
            $exitCode = $LASTEXITCODE
        }
        'strict' {
            & pwsh -NoProfile -File ./scripts/repo/test-impacted.ps1 -From $baseRef -NoBuild -ResultsDirectory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
            $exitCode = $LASTEXITCODE
            if ($exitCode -eq 0) {
                & dotnet test tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj --nologo --tl:off -c Debug --no-build --logger "trx;LogFileName=playwright.trx" --results-directory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'playwright.log')
                $exitCode = $LASTEXITCODE
            }
        }
        'playwright' {
            & dotnet test tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj --nologo --tl:off -c Debug --no-build --logger "trx;LogFileName=playwright.trx" --results-directory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'playwright.log')
            $exitCode = $LASTEXITCODE
        }
        default {
            & pwsh -NoProfile -File ./scripts/repo/test-impacted.ps1 -From $baseRef -NoBuild -ResultsDirectory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
            $exitCode = $LASTEXITCODE
        }
    }
    $testStopwatch.Stop()
    Write-PhaseTiming -Phase 'test' -Status $(if ($exitCode -eq 0) { 'ok' } else { 'failed' }) -Seconds $testStopwatch.Elapsed.TotalSeconds

    if ($strictResults) {
        . $runnerResultScript
        $trxPaths = @(Get-ChildItem -Path $resultsRoot -Filter '*.trx' -Recurse -File | Select-Object -ExpandProperty FullName)

        # A collapsed run is invisible to a pass/fail check: one passing test satisfies
        # "zero failed" exactly as well as 12,765 do, so a filter typo or a project that
        # silently stopped being discovered would certify green. The floors are set well
        # below the observed counts (core measured 12,802 on 2026-08-06) so ordinary suite
        # growth and churn never trip them, but a collapse cannot hide.
        $minimumTotals = @{ full = 12000; core = 12000; strict = 0; playwright = 0 }
        $minimumTotal = if ($minimumTotals.ContainsKey($mode)) { $minimumTotals[$mode] } else { 0 }

        $testResult = Get-RunnerTestResult -TrxPaths $trxPaths -RequireZeroSkipped -MinimumTotal $minimumTotal
        $testResult | ConvertTo-Json | Set-Content -Path (Join-Path $artifactsRoot 'test-result.json')
        if (-not $testResult.isComplete) {
            $exitCode = 1
            throw "Strict $mode validation rejected the test result: $($testResult.failureReason) (total=$($testResult.total), passed=$($testResult.passed), failed=$($testResult.failed), skipped=$($testResult.skipped), fixtureFailures=$($testResult.fixtureFailures), minimumTotal=$minimumTotal)."
        }
    }
}
catch {
    if ($exitCode -eq 0) { $exitCode = 1 }
    $_ | Out-String | Set-Content -Path (Join-Path $artifactsRoot 'runner-error.log')
    Write-Error $_
}
finally {
    Pop-Location

    # AC5: a phase that did not run for this mode is recorded as explicitly skipped rather than
    # left absent or reported as zero seconds. A missing phase and a zero-cost phase are very
    # different findings, and silently collapsing them to "0.00s" would invent a measurement
    # that was never taken.
    foreach ($expected in @('source-download', 'payload-extract', 'restore', 'tool-restore', 'build', 'test')) {
        if (-not $phaseTimings.Contains($expected)) {
            Write-PhaseTiming -Phase $expected -Status 'skipped'
        }
    }

    @{
        runId = $runId
        mode = $mode
        baseRef = $baseRef
        exitCode = $exitCode
        completedUtc = [DateTime]::UtcNow.ToString('o')
        tests = $testResult
        timings = $phaseTimings
    } | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $artifactsRoot 'result.json')

    Write-Host 'Uploading test artifacts with managed identity...'
    $uploadStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    & azcopy copy "$artifactsRoot/*" $env:ARTIFACT_BLOB_URL --recursive=true --overwrite=true | Out-Host
    $uploadStopwatch.Stop()
    $uploadExitCode = $LASTEXITCODE
    if ($uploadExitCode -ne 0 -and $exitCode -eq 0) { $exitCode = $uploadExitCode }

    # The upload cannot record its own duration in the payload it is uploading, so the timing
    # line is appended afterwards and the log re-copied on its own. This second copy is a single
    # small file and is best-effort: failing to record how long the upload took must never turn a
    # green run red, and the artifacts it would have described are already safely uploaded above.
    try {
        Write-PhaseTiming -Phase 'artifact-upload' -Status $(if ($uploadExitCode -eq 0) { 'ok' } else { 'failed' }) -Seconds $uploadStopwatch.Elapsed.TotalSeconds
        & azcopy copy $timingLog $env:ARTIFACT_BLOB_URL --overwrite=true | Out-Null
    }
    catch {
        # Deliberately swallowed: see the AC4 note at the top of this script.
    }
}

exit $exitCode
