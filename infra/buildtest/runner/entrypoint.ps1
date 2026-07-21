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

    & dotnet restore BotNexus.slnx --nologo 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'restore.log')
    if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE; throw "Restore failed with exit code $exitCode." }

    & dotnet tool restore 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'tool-restore.log')
    if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE; throw "Tool restore failed with exit code $exitCode." }

    & dotnet build BotNexus.slnx -c Debug --nologo --tl:off --no-restore 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE; throw "Build failed with exit code $exitCode." }

    $strictResults = $mode -in @('full', 'strict', 'playwright')
    switch ($mode) {
        'full' {
            & dotnet test BotNexus.slnx --nologo --tl:off -c Debug --no-build --logger "trx;LogFilePrefix=runner" --results-directory $resultsRoot 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
            $exitCode = $LASTEXITCODE
        }
        'strict' {
            & pwsh -NoProfile -File ./scripts/repo/test-impacted.ps1 -From $baseRef -NoBuild 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
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
            & pwsh -NoProfile -File ./scripts/repo/test-impacted.ps1 -From $baseRef -NoBuild 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
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
