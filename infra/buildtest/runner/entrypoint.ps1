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
New-Item -ItemType Directory -Path $payloadRoot, $artifactsRoot -Force | Out-Null

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

Push-Location $sourceRoot
$exitCode = 0
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

    switch ($mode) {
        'full' {
            & pwsh -NoProfile -File ./scripts/repo/test-impacted.ps1 -All -NoBuild 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
        }
        'strict' {
            & pwsh -NoProfile -File ./scripts/repo/test-impacted.ps1 -From $baseRef -NoBuild 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
            $impactedExitCode = $LASTEXITCODE
            if ($impactedExitCode -ne 0) {
                $exitCode = $impactedExitCode
            }
            else {
                & dotnet test tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj --nologo --tl:off -c Debug --no-build 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'playwright.log')
            }
        }
        'playwright' {
            & dotnet test tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj --nologo --tl:off -c Debug --no-build 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'playwright.log')
        }
        default {
            & pwsh -NoProfile -File ./scripts/repo/test-impacted.ps1 -From $baseRef -NoBuild 2>&1 | Tee-Object -FilePath (Join-Path $artifactsRoot 'test.log')
        }
    }
    $exitCode = $LASTEXITCODE
    if ($mode -eq 'strict' -and $exitCode -eq 0 -and -not (Test-Path (Join-Path $artifactsRoot 'playwright.log'))) {
        $exitCode = 1
        'Strict validation completed without running Playwright.' | Set-Content -Path (Join-Path $artifactsRoot 'playwright.log')
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
    } | ConvertTo-Json | Set-Content -Path (Join-Path $artifactsRoot 'result.json')

    Write-Host 'Uploading test artifacts with managed identity...'
    & azcopy copy "$artifactsRoot/*" $env:ARTIFACT_BLOB_URL --recursive=true --overwrite=true | Out-Host
    if ($LASTEXITCODE -ne 0 -and $exitCode -eq 0) { $exitCode = $LASTEXITCODE }
}

exit $exitCode
