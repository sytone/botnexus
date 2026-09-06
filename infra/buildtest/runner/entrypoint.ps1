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
$runnerTimeoutScript = '/runner/RunnerTimeout.ps1'
$runnerCostScript = '/runner/RunnerCost.ps1'
$runnerBuildScript = '/runner/RunnerBuild.ps1'
. $runnerTimeoutScript
. $runnerCostScript
. $runnerBuildScript

# SELF-IMPOSED DEADLINE (#3305)
#
# The platform replica timeout is a HARD kill: when it expires the replica is destroyed and the
# finally block below -- the only thing that writes result.json and uploads artifacts -- never
# runs. Two measured `full` runs died exactly that way and produced an EMPTY artifact directory,
# leaving no way to tell a hang from a slow suite or to name the project still executing.
#
# So the runner keeps its own clock, set inside the platform budget, and ends the run on a path
# it controls with time left to finalise. The budget is passed in rather than hardcoded so it
# cannot drift from replicaTimeout in main.bicep; the default matches the deployed 1200s.
$replicaTimeoutSeconds = if ($env:REPLICA_TIMEOUT_SECONDS) { [int]$env:REPLICA_TIMEOUT_SECONDS } else { 1200 }
$runDeadlineSeconds = Get-RunnerDeadlineSeconds -BudgetSeconds $replicaTimeoutSeconds
$runStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$timeoutRecord = $null

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

$sourceSnapshot = $null
Invoke-TimedPhase -Phase 'payload-extract' -Body {
    # BEGIN EXACT SOURCE RESTORE
    # The outer transport has exactly four regular files. Validate without extracting first;
    # the verifier module is a trusted sender artifact, not a bootstrap for an old image.
    $stream = [IO.File]::OpenRead($payloadArchive)
    $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
    $tar = [System.Formats.Tar.TarReader]::new($gzip)
    try {
        $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($name in @('repository.bundle','workspace.zip','source-manifest.json','SourceSnapshot.psm1')) { [void]$allowed.Add($name) }
        while ($null -ne ($entry = $tar.GetNextEntry())) {
            if (-not $allowed.Remove($entry.Name) -or $entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile)) { throw 'Unsafe source payload entry.' }
            $destination = [IO.File]::Create((Join-Path $payloadRoot $entry.Name))
            try { $entry.DataStream.CopyTo($destination) } finally { $destination.Dispose() }
        }
        if ($allowed.Count -ne 0) { throw 'Incomplete source payload; operator must deploy compatible sender and runner.' }
    }
    finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
    Import-Module (Join-Path $payloadRoot 'SourceSnapshot.psm1') -Force
    $manifest = Get-Content -LiteralPath (Join-Path $payloadRoot 'source-manifest.json') -Raw | ConvertFrom-Json
    # Do not checkout bundled HEAD: its files may be deleted or replaced in the candidate.
    # Clear inherited index selection for clone only; it belongs to the sender's repository.
    $hadIndex = Test-Path Env:GIT_INDEX_FILE; $savedIndex = $env:GIT_INDEX_FILE
    try {
        Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        git clone --no-checkout (Join-Path $payloadRoot 'repository.bundle') $sourceRoot | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Repository bundle clone failed.' }
    }
    finally {
        if ($hadIndex) { $env:GIT_INDEX_FILE = $savedIndex } else { Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue }
    }
    $script:sourceSnapshot = Restore-SourceSnapshot -Root $sourceRoot -Archive (Join-Path $payloadRoot 'workspace.zip') -Manifest $manifest -RunId $runId
    # END EXACT SOURCE RESTORE

    # The packed payload is no longer needed after the repository is materialized.
    # Reclaim it before restore/build so test fixtures get the maximum ephemeral space.
    Remove-Item -LiteralPath $payloadArchive, $payloadRoot -Recurse -Force
}

Push-Location $sourceRoot
$exitCode = 0
$testResult = $null
try {
    # Source proof was established before any restore/build command; do not manufacture a
    # pre-validation commit. Keep HEAD as history and populate the clone index for diff tools.
    git read-tree HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize runner index.' }

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

    # Release build of the deployment closure (#2914).
    #
    # WHY THIS EXISTS. The gateway boot fixtures - ExtensionBootFixture and
    # NewUserExperienceFixture - must exercise the PRODUCTION boot path, and that path resolves
    # the host from a hardcoded bin/Release location (GatewayCommand.StartAttachedAsync exits 1
    # with "Release build not found" otherwise) while ServeCommand.ResolveExtensionOutputDirectory
    # prefers Release over Debug. The runner above builds Debug, so those fixtures used to compile
    # all 48 src/ projects in Release themselves, from inside a testhost, during the test phase.
    # Measured on run 20260810170439-279d1484: the ExtensionBoot assembly took 228.2s of a 333.6s
    # test phase (68%) for three tests that execute in 89ms.
    #
    # Building Release here does not remove that work, it RELOCATES it to where it is cheaper:
    #   * node reuse and shared compilation are available, whereas a build launched from inside a
    #     testhost must pass /nodeReuse:false /p:UseSharedCompilation=false to avoid leaving build
    #     nodes attached and fighting the testhost for locked, already-loaded dlls;
    #   * it is not competing with 50+ concurrently draining test assemblies for 4 CPUs.
    # The fixtures KEEP their own prebuild call - it becomes an MSBuild up-to-date check over 48
    # projects instead of a compile, and it must still work for anyone running those tests outside
    # this runner where no Release output exists.
    #
    # SkipCli is deliberately NOT passed. Production BuildCommand.BuildSolutionAsync uses
    # /p:SkipTests=true /p:SkipCli=true, but ExtensionBootFixture LAUNCHES BotNexus.Cli.dll, so
    # excluding it here would break the gate. SkipTests is a no-op against src/dirs.proj, which
    # holds no test projects. Do not copy the production argument string wholesale.
    #
    # Skipped for modes that run neither fixture, so a playwright-only or impacted-only run does
    # not pay for a Release compile it will never load.
    $needsReleaseBuild = $mode -in @('full', 'core', 'playwright')
    if ($needsReleaseBuild) {
        # Debug and Release use configuration-specific bin/obj trees. Starting both after the
        # shared restore overlaps their critical paths while keeping independent logs and exit
        # codes. The helper waits for BOTH children before a failure is raised, so no build is
        # orphaned and diagnostics from the successful sibling are retained.
        $buildResults = @(Invoke-ParallelRunnerProcesses -Processes @(
            @{
                Name = 'build'
                FilePath = 'dotnet'
                ArgumentList = @('build', 'dirs.proj', '-c', 'Debug', '--nologo', '--tl:off', '--no-restore')
                LogPath = (Join-Path $artifactsRoot 'build.log')
            },
            @{
                Name = 'build-release'
                FilePath = 'dotnet'
                ArgumentList = @('build', 'src/dirs.proj', '-c', 'Release', '--nologo', '--tl:off', '--no-restore')
                LogPath = (Join-Path $artifactsRoot 'build-release.log')
            }
        ))

        foreach ($result in $buildResults) {
            $status = if ($result.ExitCode -eq 0) { 'ok' } else { 'failed' }
            Write-PhaseTiming -Phase $result.Name -Status $status -Seconds $result.ElapsedSeconds
        }

        $failedBuilds = @($buildResults | Where-Object ExitCode -ne 0)
        if ($failedBuilds.Count -gt 0) {
            $script:exitCode = $failedBuilds[0].ExitCode
            $failureSummary = ($failedBuilds | ForEach-Object { "$($_.Name)=$($_.ExitCode)" }) -join ', '
            throw "Concurrent build failed: $failureSummary."
        }
    }
    else {
        Invoke-TimedPhase -Phase 'build' -Body {
            & dotnet build dirs.proj -c Debug --nologo --tl:off --no-restore 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'build.log')
            if ($LASTEXITCODE -ne 0) { $script:exitCode = $LASTEXITCODE; throw "Build failed with exit code $LASTEXITCODE." }
        }
        Write-PhaseTiming -Phase 'build-release' -Status 'skipped'
    }

    # Timed inline rather than through Invoke-TimedPhase: every branch below assigns $exitCode,
    # and an assignment inside a scriptblock invoked with & would land in a child scope and be
    # silently discarded -- the run would then report success regardless of the test outcome.
    # That is precisely the fail-open class #2851 was raised to eliminate, so the measurement
    # must not reintroduce it.
    #
    # The test phase additionally runs under the runner's OWN deadline (#3305). A blocking
    # `& dotnet test | Tee-Object` pipeline returns only when the child exits, so there is no
    # instant at which the runner could notice it is about to be killed by the platform.
    # Starting the child explicitly and polling it is what makes the finalise-and-upload path
    # in the finally block reachable at all when the suite overruns. Whatever remains of the
    # deadline after restore and build is what the tests get: charging the test phase the wall
    # clock already spent is deliberate, because the reserve exists to protect the upload.
    $testBudgetSeconds = [int][Math]::Max(30, $runDeadlineSeconds - $runStopwatch.Elapsed.TotalSeconds)
    $testLog = Join-Path $artifactsRoot 'test.log'
    $playwrightLog = Join-Path $artifactsRoot 'playwright.log'
    $e2eProject = 'tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj'
    $testStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $run = switch ($mode) {
        'full' {
            Invoke-BoundedProcess -FilePath 'dotnet' -TimeoutSeconds $testBudgetSeconds -LogPath $testLog -ArgumentList @(
                'test', 'tests/dirs.proj', '--nologo', '--tl:off', '-c', 'Debug', '--no-build',
                '--logger', 'trx;LogFilePrefix=runner', '--results-directory', $resultsRoot)
        }
        'core' {
            # Everything except the browser/E2E projects. Those are quarantined while the
            # NotExecuted defect is investigated: on 2026-08-06 a 'full' run reported exit 0
            # with 265 of 280 E2E tests NotExecuted, which is neither passed nor failed and
            # therefore certified a green gate that had silently skipped them. Core must be
            # trustworthy before E2E is folded back in.
            $coreFilter = 'FullyQualifiedName!~BotNexus.Integration.E2E&FullyQualifiedName!~BotNexus.E2E'
            Invoke-BoundedProcess -FilePath 'dotnet' -TimeoutSeconds $testBudgetSeconds -LogPath $testLog -ArgumentList @(
                'test', 'tests/dirs.proj', '--nologo', '--tl:off', '-c', 'Debug', '--no-build',
                '--filter', $coreFilter,
                '--logger', 'trx;LogFilePrefix=runner', '--results-directory', $resultsRoot)
        }
        'strict' {
            $impacted = Invoke-BoundedProcess -FilePath 'pwsh' -TimeoutSeconds $testBudgetSeconds -LogPath $testLog -ArgumentList @(
                '-NoProfile', '-File', './scripts/repo/test-impacted.ps1', '-From', $baseRef, '-NoBuild', '-ResultsDirectory', $resultsRoot)
            if ($impacted.ExitCode -ne 0 -or $impacted.TimedOut) {
                $impacted
            }
            else {
                $remaining = [int][Math]::Max(30, $runDeadlineSeconds - $runStopwatch.Elapsed.TotalSeconds)
                Invoke-BoundedProcess -FilePath 'dotnet' -TimeoutSeconds $remaining -LogPath $playwrightLog -ArgumentList @(
                    'test', $e2eProject, '--nologo', '--tl:off', '-c', 'Debug', '--no-build',
                    '--logger', 'trx;LogFileName=playwright.trx', '--results-directory', $resultsRoot)
            }
        }
        'playwright' {
            Invoke-BoundedProcess -FilePath 'dotnet' -TimeoutSeconds $testBudgetSeconds -LogPath $playwrightLog -ArgumentList @(
                'test', $e2eProject, '--nologo', '--tl:off', '-c', 'Debug', '--no-build',
                '--logger', 'trx;LogFileName=playwright.trx', '--results-directory', $resultsRoot)
        }
        default {
            Invoke-BoundedProcess -FilePath 'pwsh' -TimeoutSeconds $testBudgetSeconds -LogPath $testLog -ArgumentList @(
                '-NoProfile', '-File', './scripts/repo/test-impacted.ps1', '-From', $baseRef, '-NoBuild', '-ResultsDirectory', $resultsRoot)
        }
    }
    $exitCode = $run.ExitCode
    $testStopwatch.Stop()
    Write-PhaseTiming -Phase 'test' -Status $(if ($run.TimedOut) { 'timed-out' } elseif ($exitCode -eq 0) { 'ok' } else { 'failed' }) -Seconds $testStopwatch.Elapsed.TotalSeconds

    if ($run.TimedOut) {
        # ATTRIBUTION (#3305 AC2). Derived from the TRX that DO exist at this instant, which is
        # the entire reason the deadline lands early -- a platform kill leaves nothing to read.
        # Excluded projects are passed through so a filtered run never accuses a project it was
        # never going to execute; a confident wrong attribution is worse than none.
        $partialTrx = @(Get-ChildItem -Path $resultsRoot -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
        $completedAssemblies = @(Get-CompletedTestAssemblies -TrxPaths $partialTrx)
        $excluded = if ($mode -eq 'core') { @('BotNexus.Integration.E2E', 'BotNexus.E2E') } else { @() }
        $unfinished = @(Get-UnfinishedTestProjects -ExpectedProjects (Get-ExpectedTestProjects -TestsRoot (Join-Path $sourceRoot 'tests')) -CompletedAssemblies $completedAssemblies -ExcludedProjects $excluded)
        $timeoutRecord = New-RunnerTimeoutRecord -Phase 'test' -ElapsedSeconds $testStopwatch.Elapsed.TotalSeconds -DeadlineSeconds $testBudgetSeconds -CompletedAssemblies $completedAssemblies -UnfinishedProjects $unfinished
        $exitCode = 1
        throw "Test phase exceeded the runner deadline of $testBudgetSeconds s. $($timeoutRecord.attribution)"
    }

    # Every mode must produce a verifiable test contract before the sender can certify it.
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

    # PER-PROJECT COST ATTRIBUTION (#3314).
    #
    # Emitted on EVERY run -- green, red, or timed out -- rather than only on the timeout path.
    # #3305's attribution answers "which project was still running when we were killed", but
    # the measured `full` run (20260819033413-38a9b933) finished in 13.7 min of its 20 min
    # budget with timeout=null, so that path never fired and the artifacts said nothing at all
    # about where the time went. Answering #3314 required a throwaway TRX parser written
    # outside the repo. This makes the same answer a durable artifact instead.
    #
    # Best-effort throughout, for the same reason as the timing log: a diagnostic must never be
    # able to turn a passing suite red.
    try {
        $costTrx = @(Get-ChildItem -Path $resultsRoot -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
        $projectCosts = @(Get-RunnerProjectCosts -TrxPaths $costTrx)
        $testPhaseSeconds = if ($phaseTimings.Contains('test')) { [double]$phaseTimings['test'].seconds } else { 0.0 }
        Format-RunnerCostReport -Costs $projectCosts -TestPhaseSeconds $testPhaseSeconds -Mode $mode |
            Set-Content -Path (Join-Path $artifactsRoot 'runner-cost.log') -ErrorAction Stop
    }
    catch {
        # Deliberately swallowed: see the AC4 note at the top of this script.
        $projectCosts = @()
    }

    @{
        runId = $runId
        mode = $mode
        baseRef = $baseRef
        exitCode = $exitCode
        completedUtc = [DateTime]::UtcNow.ToString('o')
        tests = $testResult
        sourceSnapshot = $sourceSnapshot
        timings = $phaseTimings
        # #3314: per-project cost travels IN the contract as well as in runner-cost.log, so a
        # caller can attribute the run without re-parsing TRX. Always present, never inferred.
        projectCosts = $projectCosts
        # #3305: a timeout is REPRESENTED here rather than inferred from an absent artifact.
        # $null on an ordinary run, so the field distinguishes "did not time out" from "we
        # never got to write anything" -- which an empty directory could not.
        timeout = $timeoutRecord
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
