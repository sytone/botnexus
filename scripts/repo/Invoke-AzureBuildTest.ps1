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
    [ValidateSet('strict', 'impacted', 'full', 'core', 'playwright')]
    [string]$Mode = 'strict',

    # Must track replicaTimeout in infra/buildtest/main.bicep. Kept as a parameter so the
    # reported budget cannot silently drift from the deployed one when either is retuned.
    [int]$ReplicaTimeoutMinutes = 20,
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
Import-Module (Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.psm1') -Force
$fingerprintScript = Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1'
$fingerprint = & $fingerprintScript -WorktreePath $repoRoot -BaseRef $BaseRef
$runId = "{0}-{1}" -f ([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts/azure-buildtest/$runId"
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "botnexus-buildtest-$runId"
# #3805: the download lands under $tempRoot, which the finally block removes unconditionally.
# These two are declared out here so the finally can tell "artifacts were never downloaded"
# from "artifacts were downloaded and a later step threw before they were placed" - the second
# case is the one that used to delete the only copy of the diagnosis.
$downloadStaging = Join-Path $tempRoot 'artifacts'
$artifactsPlaced = $false
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

    # #3516: warn when the deployed runner image was not built from these sources.
    #
    # On 2026-08-21 the job was pointed at an image built from an unmerged branch. It contained a
    # file main does not have, threw before build or test ran, and reported `tests: null` for three
    # days - no branch could be validated, and nothing said why. The tag is content-addressed
    # (#2900), so the comparison is exact and free: we already have the job here.
    #
    # WARN, never throw. A mismatch is legitimate while an infra change is being rolled out, and a
    # hard failure would make the gate unusable during exactly the window an operator needs it. What
    # matters is that a gate result can no longer be silently unattributable to a commit.
    try {
        . (Join-Path $PSScriptRoot '..' '..' 'infra' 'buildtest' 'RunnerImageProvenance.ps1')
        $provenance = Test-RunnerImageMatchesSources `
            -RunnerPath (Join-Path $PSScriptRoot '..' '..' 'infra' 'buildtest' 'runner') `
            -DeployedTag ($container.image -split ':')[-1]

        if ($provenance.Verdict -eq 'mismatch') {
            Write-Warning (
                "Runner image provenance MISMATCH: the job runs '$($provenance.Deployed)' but these " +
                "sources derive '$($provenance.Expected)'. The gate is executing runner code that is " +
                "not in this worktree, so a failure here may not correspond to your change. " +
                'Redeploy with infra/buildtest/Deploy-BuildTestInfrastructure.ps1 to realign.')
        }
    }
    catch {
        # Provenance is a diagnostic, not the subject of the gate. A check that could fail an
        # otherwise-valid run would be worse than no check.
        Write-Verbose "Runner image provenance check skipped: $($_.Exception.Message)"
    }

    $managedIdentityClientId = ($container.env | Where-Object name -eq 'AZURE_CLIENT_ID' | Select-Object -First 1).value
    if ([string]::IsNullOrWhiteSpace($managedIdentityClientId)) { throw 'The job template does not expose its managed-identity client ID.' }
    $container.env = @(
        @{ name = 'AZURE_CLIENT_ID'; value = $managedIdentityClientId }
        @{ name = 'RUN_ID'; value = $runId }
        @{ name = 'SOURCE_BLOB_URL'; value = $sourceUrl }
        @{ name = 'ARTIFACT_BLOB_URL'; value = $artifactUrl }
        @{ name = 'TEST_MODE'; value = $Mode }
        @{ name = 'BASE_REF'; value = $BaseRef }
        # #3305: the runner sets its own deadline strictly inside this budget so it ends the
        # run on a path it controls and still has time to upload artifacts. Passed in rather
        # than hardcoded in the image so the two numbers cannot drift apart.
        @{ name = 'REPLICA_TIMEOUT_SECONDS'; value = [string]($ReplicaTimeoutMinutes * 60) }
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
    $watchStarted = [DateTime]::UtcNow
    do {
        Start-Sleep -Seconds 15
        $status = Invoke-AzJson @('rest', '--method', 'get', '--url', $executionUrl)
        Write-Host "Execution status: $($status.properties.status)" -ForegroundColor DarkGray
    } while ($status.properties.status -in @('Running', 'Processing', 'Unknown'))
    $elapsed = ([DateTime]::UtcNow - $watchStarted).TotalMinutes

    # The replica timeout is a deliberately realistic budget, not a safety ceiling, so a run
    # that approaches it is a signal rather than noise. Report the margin every time and say
    # so loudly on a breach: a hung lane and a failing test must not read the same, and the
    # threshold should be raised only when honest run time has genuinely grown.
    $budgetMinutes = $ReplicaTimeoutMinutes
    $timedOut = $elapsed -ge ($budgetMinutes - 0.5)
    $margin = [Math]::Round($budgetMinutes - $elapsed, 1)
    if ($timedOut) {
        Write-Warning ("TIMEOUT BUDGET BREACHED: ran {0:N1} min against a {1} min budget. Either the run genuinely hung or the budget is now too small - investigate before raising it." -f $elapsed, $budgetMinutes)
    }
    else {
        Write-Host ("Run took {0:N1} min of a {1} min budget ({2} min margin)." -f $elapsed, $budgetMinutes, $margin) -ForegroundColor DarkGray
    }

    # #3115: download-batch recreates the full blob NAME under --destination, and the blobs are
    # already named "<runId>/...". Downloading straight into $OutputPath therefore applied the run
    # id twice. Stage the download, then flatten the prefix so $OutputPath - the single variable
    # the success and failure messages both report - is the directory that holds result.json.
    New-Item -ItemType Directory -Path $downloadStaging -Force | Out-Null
    & az storage blob download-batch --subscription $SubscriptionId --account-name $StorageAccount --source artifacts --destination $downloadStaging --pattern "$runId/*" --auth-mode login --overwrite true --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Artifact download failed.' }
    $OutputPath = Move-AzureBuildTestArtifacts -StagingRoot $downloadStaging -RunId $runId -Destination $OutputPath
    $artifactsPlaced = $true

    # Deliberately NOT recursive: the contract must be at the advertised path or the run is not
    # provably green (#3115). A nested copy is a regression, not an acceptable location.
    $resultFile = Get-ChildItem -Path $OutputPath -Filter result.json -File | Select-Object -First 1
    $result = if ($resultFile) { Get-Content $resultFile.FullName -Raw | ConvertFrom-Json } else { $null }

    # #3305: a run that overran its budget now says so IN the contract, and names the projects
    # that had not reported. Before this the only evidence was an empty directory, which cannot
    # distinguish a hang from a slow suite and attributes the cost to nothing at all.
    $runnerTimeout = if ($result -and $result.PSObject.Properties['timeout']) { $result.timeout } else { $null }
    if ($runnerTimeout) {
        Write-Warning ("Runner deadline expired after {0:N0}s of a {1}s test budget. {2}" -f $runnerTimeout.elapsedSeconds, $runnerTimeout.deadlineSeconds, $runnerTimeout.attribution)
    }
    # #3314: per-project cost is reported on EVERY run, not only on a timeout. The measured
    # `full` run finished inside its budget with timeout=null, so the #3305 attribution never
    # fired and nothing on disk said where the time went. Surfacing the top costs here means
    # the answer is in the operator's console rather than in a parser they have to write.
    #
    # #3788: the OUTER @(...) is load-bearing. An `if` used as an expression yields its branch's
    # pipeline output, and both `@()` and `@(<empty array>)` are an EMPTY pipeline - which assigns
    # $null, discarding the inner array subexpression. A build-failed run emits
    # `"projectCosts": []`, so without this wrapper $projectCosts was $null and the .Count below
    # threw "The property 'Count' cannot be found on this object" under Set-StrictMode, replacing
    # the verdict this script exists to report with what looked like a tooling breakage.
    # #3805: read through ConvertTo-CountableArray. `@($result.projectCosts)` is unrolled back to
    # a bare $null when the property is JSON null, and `.Count` on $null throws under StrictMode -
    # which is precisely the shape a BUILD failure produces (test phase skipped), so the failure
    # path was the only path that could hit it and the secondary error masked the real cause.
    $projectCosts = ConvertTo-CountableArray ($(if ($result -and $result.PSObject.Properties['projectCosts']) { $result.projectCosts } else { $null }))
    if ($projectCosts.Count -gt 0) {
        $topCosts = ($projectCosts | Select-Object -First 3 | ForEach-Object { "{0} {1:N1}s" -f $_.project, $_.seconds }) -join '; '
        Write-Host "Most expensive projects: $topCosts (full table: runner-cost.log)." -ForegroundColor DarkGray
    }

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
        $timeoutNote = if ($runnerTimeout) {
            (' The runner stopped the test phase at its own {0}s deadline, inside the {1} min replica budget, so artifacts were still uploaded. {2}' -f $runnerTimeout.deadlineSeconds, $budgetMinutes, $runnerTimeout.attribution)
        }
        elseif ($timedOut) {
            (' The run reached the {0} min replica timeout, so it was killed rather than completing - treat this as a hang, not a test failure.' -f $budgetMinutes)
        }
        else { '' }
        # #3805: name the test outcome as well as the execution status. A build failure reports
        # `tests: null`, and "Execution status: Failed" alone gave the caller no way to tell a
        # compile break from a red suite without re-reading result.json themselves.
        $testSummary = if ($null -eq $result) { ' No result contract was produced.' }
        elseif (-not $result.PSObject.Properties['tests'] -or $null -eq $result.tests) { ' The test phase did not report (tests: null) - read build.log; this is normally a build failure, not a test failure.' }
        else { '' }
        throw "Azure validation failed. Execution status: $($status.properties.status).$artifactFailure$timeoutNote$testSummary Artifacts: $OutputPath"
    }

    Write-Host "Azure validation passed. Artifacts: $OutputPath" -ForegroundColor Green
}
finally {
    # #3805: a failing gate is the case where the artifacts matter MOST. If anything threw between
    # the download and the flatten, the only copy of result.json and build.log was inside $tempRoot
    # and this cleanup deleted it - while the remote blobs had already been deleted too unless
    # -KeepRemoteArtifacts was passed. Retain first, then clean up, and print the path we verified
    # rather than one we assert.
    if (-not $artifactsPlaced) {
        try {
            $rescued = Save-AzureBuildTestFailureArtifacts -StagingRoot $downloadStaging -RunId $runId -Destination $OutputPath
            if ($rescued) { Write-Warning "Run did not complete cleanly. Downloaded artifacts were retained at: $rescued" }
        }
        catch {
            # Retention is a diagnostic. It must never replace the original failure with its own.
            Write-Warning "Could not retain downloaded artifacts: $($_.Exception.Message)"
        }
    }

    if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force }
}
