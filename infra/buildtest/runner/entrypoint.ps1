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

$env:AZCOPY_AUTO_LOGIN_TYPE = 'MSI'
if ($env:AZURE_CLIENT_ID) { $env:AZCOPY_MSI_CLIENT_ID = $env:AZURE_CLIENT_ID }

Write-Host "Downloading source snapshot for run $runId with managed identity..."
& azcopy copy $env:SOURCE_BLOB_URL $payloadArchive --overwrite=true | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Source download failed with exit code $LASTEXITCODE." }

tar -xzf $payloadArchive -C $payloadRoot
if ($LASTEXITCODE -ne 0) { throw "Payload extraction failed with exit code $LASTEXITCODE." }

git clone (Join-Path $payloadRoot 'repository.bundle') $sourceRoot
if ($LASTEXITCODE -ne 0) { throw "Repository bundle clone failed with exit code $LASTEXITCODE." }
tar -xzf (Join-Path $payloadRoot 'workspace.tar.gz') -C $sourceRoot
if ($LASTEXITCODE -ne 0) { throw "Workspace overlay failed with exit code $LASTEXITCODE." }

# The packed payload is no longer needed after the repository is materialized.
# Reclaim it before restore/build so test fixtures get the maximum ephemeral space.
Remove-Item -LiteralPath $payloadArchive, $payloadRoot -Recurse -Force

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

    & dotnet restore BotNexus.slnx --nologo 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'restore.log')
    if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE; throw "Restore failed with exit code $exitCode." }

    & dotnet tool restore 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'tool-restore.log')
    if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE; throw "Tool restore failed with exit code $exitCode." }

    & dotnet build BotNexus.slnx -c Debug --nologo --tl:off --no-restore 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE; throw "Build failed with exit code $exitCode." }

    $strictResults = $mode -in @('full', 'core', 'strict', 'playwright')
    switch ($mode) {
        'full' {
            & dotnet test BotNexus.slnx --nologo --tl:off -c Debug --no-build --logger "trx;LogFilePrefix=runner" --results-directory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
            $exitCode = $LASTEXITCODE
        }
        'core' {
            # Everything except the browser/E2E projects. Those are quarantined while the
            # NotExecuted defect is investigated: on 2026-08-06 a 'full' run reported exit 0
            # with 265 of 280 E2E tests NotExecuted, which is neither passed nor failed and
            # therefore certified a green gate that had silently skipped them. Core must be
            # trustworthy before E2E is folded back in.
            $coreFilter = 'FullyQualifiedName!~BotNexus.Integration.E2E&FullyQualifiedName!~BotNexus.E2E'
            & dotnet test BotNexus.slnx --nologo --tl:off -c Debug --no-build --filter $coreFilter --logger "trx;LogFilePrefix=runner" --results-directory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
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

    if ($strictResults) {
        . $runnerResultScript
        $trxPaths = @(Get-ChildItem -Path $resultsRoot -Filter '*.trx' -Recurse -File | Select-Object -ExpandProperty FullName)
        $testResult = Get-RunnerTestResult -TrxPaths $trxPaths -RequireZeroSkipped
        $testResult | ConvertTo-Json | Set-Content -Path (Join-Path $artifactsRoot 'test-result.json')
        if (-not $testResult.isComplete) {
            $exitCode = 1
            throw "Strict $mode validation rejected the test result: $($testResult.failureReason) (total=$($testResult.total), passed=$($testResult.passed), failed=$($testResult.failed), skipped=$($testResult.skipped))."
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
    @{
        runId = $runId
        mode = $mode
        baseRef = $baseRef
        exitCode = $exitCode
        completedUtc = [DateTime]::UtcNow.ToString('o')
        tests = $testResult
    } | ConvertTo-Json | Set-Content -Path (Join-Path $artifactsRoot 'result.json')

    Write-Host 'Uploading test artifacts with managed identity...'
    & azcopy copy "$artifactsRoot/*" $env:ARTIFACT_BLOB_URL --recursive=true --overwrite=true | Out-Host
    if ($LASTEXITCODE -ne 0 -and $exitCode -eq 0) { $exitCode = $LASTEXITCODE }
}

exit $exitCode
