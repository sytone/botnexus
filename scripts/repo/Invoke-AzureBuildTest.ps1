<#
.SYNOPSIS
    Runs build and test validation for a worktree in the BotNexus Azure Container Apps Job.

.DESCRIPTION
    Captures committed, staged, unstaged, and untracked worktree state without pushing it.
    The snapshot is uploaded with the signed-in Azure CLI identity. The remote job downloads
    it and uploads results using its user-assigned managed identity. No keys, SAS tokens, or
    connection strings are used.
#>
[CmdletBinding()]
param(
    [ValidateSet('strict', 'impacted', 'full', 'playwright')]
    [string]$Mode = 'strict',
    [string]$WorktreePath = (Get-Location).Path,
    [string]$SubscriptionId = $env:BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID,
    [string]$ResourceGroup = $env:BOTNEXUS_BUILDTEST_RESOURCE_GROUP,
    [string]$StorageAccount = $env:BOTNEXUS_BUILDTEST_STORAGE_ACCOUNT,
    [string]$JobName = $env:BOTNEXUS_BUILDTEST_JOB_NAME,
    [string]$BaseRef = 'origin/main',
    [string]$OutputPath,
    [switch]$KeepRemoteArtifacts,
    [switch]$NoWait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredSettings = @{
    SubscriptionId = $SubscriptionId
    ResourceGroup = $ResourceGroup
    StorageAccount = $StorageAccount
    JobName = $JobName
}
$missingSettings = @($requiredSettings.GetEnumerator() | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Value) } | ForEach-Object Key)
if ($missingSettings.Count -gt 0) {
    throw "Missing Azure build/test settings: $($missingSettings -join ', '). Set BOTNEXUS_BUILDTEST_SUBSCRIPTION_ID, BOTNEXUS_BUILDTEST_RESOURCE_GROUP, BOTNEXUS_BUILDTEST_STORAGE_ACCOUNT, and BOTNEXUS_BUILDTEST_JOB_NAME, or pass the corresponding parameters."
}

function Invoke-AzJson {
    param([string[]]$Arguments)
    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments -join ' ') failed:`n$($output -join [Environment]::NewLine)" }
    return ($output -join [Environment]::NewLine) | ConvertFrom-Json
}

$repoRoot = (& git -C $WorktreePath rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "WorktreePath is not inside a git repository: $WorktreePath"
}
$repoRoot = $repoRoot.Trim()
$fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
$fingerprint = & $fingerprintScript -WorktreePath $repoRoot -BaseRef $BaseRef
$runId = "{0}-{1}" -f ([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts/azure-buildtest/$runId"
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "botnexus-buildtest-$runId"
$workspaceArchive = Join-Path $tempRoot 'workspace.tar.gz'
$bundlePath = Join-Path $tempRoot 'repository.bundle'
$payloadArchive = Join-Path $tempRoot 'payload.tar.gz'
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    $account = Invoke-AzJson @('account', 'show', '--subscription', $SubscriptionId, '-o', 'json')
    Write-Host "Using Azure identity $($account.user.name) in subscription $($account.name)." -ForegroundColor Cyan

    & git -C $repoRoot bundle create $bundlePath --all
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create repository bundle.' }

    $archiveFileList = Join-Path $tempRoot 'workspace-files.txt'
    $trackedFiles = @(& git -C $repoRoot ls-files --cached --others --exclude-standard | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($LASTEXITCODE -ne 0) { throw 'Failed to enumerate worktree files.' }
    if ($trackedFiles.Count -eq 0) { throw 'Worktree overlay contains no files.' }
    # Use LF explicitly: Windows PowerShell's Set-Content emits CRLF, which GNU tar treats as
    # part of each pathname when this script runs under Git's Unix toolchain.
    [IO.File]::WriteAllText($archiveFileList, (($trackedFiles -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

    Push-Location $repoRoot
    try {
        # Resolve tar.exe explicitly. Git's /usr/bin/tar interprets a Windows drive-letter
        # archive path as a remote host specification ("C:"), while bsdtar handles it.
        $tarCommand = if ($IsWindows) {
            Join-Path $env:SystemRoot 'System32/tar.exe'
        }
        else {
            (Get-Command tar -CommandType Application | Select-Object -First 1).Source
        }
        & $tarCommand -T $archiveFileList -czf $workspaceArchive
        if ($LASTEXITCODE -ne 0) { throw 'Failed to create worktree overlay archive.' }
    }
    finally { Pop-Location }

    Push-Location $tempRoot
    try {
        tar -czf $payloadArchive 'repository.bundle' 'workspace.tar.gz'
        if ($LASTEXITCODE -ne 0) { throw 'Failed to create source payload.' }
    }
    finally { Pop-Location }

    $sourceBlob = "$runId/source.tar.gz"
    & az storage blob upload --subscription $SubscriptionId --account-name $StorageAccount --container-name sources --name $sourceBlob --file $payloadArchive --auth-mode login --overwrite true --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Source upload failed.' }

    if ($account.environmentName -ne 'AzureCloud') { throw "Unsupported Azure environment: $($account.environmentName)" }
    $storageSuffix = 'core.windows.net'
    $sourceUrl = "https://$StorageAccount.blob.$storageSuffix/sources/$sourceBlob"
    $artifactUrl = "https://$StorageAccount.blob.$storageSuffix/artifacts/$runId"

    $jobUrl = "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.App/jobs/$JobName"
    $job = Invoke-AzJson @('rest', '--method', 'get', '--url', "${jobUrl}?api-version=2024-03-01")
    $template = $job.properties.template
    $container = $template.containers[0]
    $managedIdentityClientId = ($container.env | Where-Object name -eq 'AZURE_CLIENT_ID' | Select-Object -First 1).value
    if ([string]::IsNullOrWhiteSpace($managedIdentityClientId)) { throw 'The job template does not expose its managed-identity client ID.' }
    $container.env = @(
        @{ name = 'AZURE_CLIENT_ID'; value = $managedIdentityClientId }
        @{ name = 'RUN_ID'; value = $runId }
        @{ name = 'SOURCE_BLOB_URL'; value = $sourceUrl }
        @{ name = 'ARTIFACT_BLOB_URL'; value = $artifactUrl }
        @{ name = 'TEST_MODE'; value = $Mode }
        @{ name = 'BASE_REF'; value = $BaseRef }
    )
    $startBody = @{ containers = $template.containers } | ConvertTo-Json -Depth 30 -Compress
    $bodyPath = Join-Path $tempRoot 'start.json'
    Set-Content -Path $bodyPath -Value $startBody -Encoding utf8NoBOM

    $execution = Invoke-AzJson @('rest', '--method', 'post', '--url', "${jobUrl}/start?api-version=2024-03-01", '--body', "@$bodyPath")
    $executionName = ($execution.name ?? $execution.id.Split('/')[-1])
    Write-Host "Started Azure build/test execution $executionName (run $runId)." -ForegroundColor Cyan

    if ($NoWait) {
        [pscustomobject]@{ RunId = $runId; ExecutionName = $executionName; SourceBlob = $sourceBlob; ArtifactPrefix = $runId }
        return
    }

    $executionUrl = "${jobUrl}/executions/${executionName}?api-version=2024-03-01"
    do {
        Start-Sleep -Seconds 15
        $status = Invoke-AzJson @('rest', '--method', 'get', '--url', $executionUrl)
        Write-Host "Execution status: $($status.properties.status)" -ForegroundColor DarkGray
    } while ($status.properties.status -in @('Running', 'Processing', 'Unknown'))

    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    & az storage blob download-batch --subscription $SubscriptionId --account-name $StorageAccount --source artifacts --destination $OutputPath --pattern "$runId/*" --auth-mode login --overwrite true --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Artifact download failed.' }

    $resultFile = Get-ChildItem -Path $OutputPath -Filter result.json -Recurse | Select-Object -First 1
    $result = if ($resultFile) { Get-Content $resultFile.FullName -Raw | ConvertFrom-Json } else { $null }
    $playwrightArtifact = Get-ChildItem -Path $OutputPath -Filter playwright.log -Recurse | Select-Object -First 1
    $requiredArtifactsPresent = $Mode -ne 'strict' -or $null -ne $playwrightArtifact

    if ($status.properties.status -eq 'Succeeded' -and $null -ne $result -and $result.exitCode -eq 0 -and $requiredArtifactsPresent) {
        $gitDirectory = (& git -C $repoRoot rev-parse --git-dir).Trim()
        if (-not [IO.Path]::IsPathRooted($gitDirectory)) { $gitDirectory = Join-Path $repoRoot $gitDirectory }
        $receiptDirectory = Join-Path $gitDirectory 'botnexus-validation'
        New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
        @{
            version = 1
            fingerprint = $fingerprint.fingerprint
            head = $fingerprint.head
            baseRef = $fingerprint.baseRef
            baseCommit = $fingerprint.baseCommit
            tree = $fingerprint.tree
            mode = $Mode
            runId = $runId
            executionName = $executionName
            completedUtc = $result.completedUtc
        } | ConvertTo-Json | Set-Content -Path (Join-Path $receiptDirectory 'azure-buildtest.json') -Encoding utf8NoBOM
    }

    if (-not $KeepRemoteArtifacts) {
        & az storage blob delete --subscription $SubscriptionId --account-name $StorageAccount --container-name sources --name $sourceBlob --auth-mode login --only-show-errors | Out-Null
        & az storage blob delete-batch --subscription $SubscriptionId --account-name $StorageAccount --source artifacts --pattern "$runId/*" --auth-mode login --only-show-errors | Out-Null
    }

    if ($status.properties.status -ne 'Succeeded' -or $null -eq $result -or $result.exitCode -ne 0 -or -not $requiredArtifactsPresent) {
        $artifactFailure = if ($requiredArtifactsPresent) { '' } else { ' The strict Playwright artifact is missing; the deployed runner does not prove strict mode.' }
        throw "Azure validation failed. Execution status: $($status.properties.status).$artifactFailure Artifacts: $OutputPath"
    }

    Write-Host "Azure validation passed. Artifacts: $OutputPath" -ForegroundColor Green
}
finally {
    if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force }
}
