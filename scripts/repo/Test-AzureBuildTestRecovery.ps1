<#
.SYNOPSIS
Runs offline receipt-recovery regressions.
.DESCRIPTION
Uses synthetic Git, provider and runner evidence only. Never invokes Azure, dotnet or a gateway.
.EXAMPLE
./Test-AzureBuildTestRecovery.ps1
.OUTPUTS
System.String. Regression case results.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$module = Join-Path $PSScriptRoot 'AzureBuildTestRecovery.psm1'
if (-not (Test-Path -LiteralPath $module)) {
    throw 'RED: supported existing-run recovery module is missing.'
}
Import-Module $module -Force
$root = Join-Path ([IO.Path]::GetTempPath()) "recovery-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    & git -C $root init --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'Fixture Git init failed.'
    }
    Set-Content -LiteralPath (Join-Path $root 'candidate.txt') -Value 'candidate'
    & git -C $root add .
    & git -C $root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) {
        throw 'Fixture Git commit failed.'
    }
    $fingerprint = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $root -BaseRef HEAD
    $context = [pscustomobject]@{
        version = 1
        runId = 'offline-run'
        mode = 'core'
        executionName = 'offline-execution'
        jobUrl = 'https://management.azure.com/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture'
        fingerprint = $fingerprint
        subscriptionId = 'fixture'
        resourceGroup = 'fixture'
        storageAccount = 'fixturestorage'
        jobName = 'fixture'
        expectedPreviousReceipt = 'absent'
    }
    $provider = [pscustomobject]@{
        name = $context.executionName
        id = "$(([uri]$context.jobUrl).AbsolutePath)/executions/$($context.executionName)"
        properties = [pscustomobject]@{
            status = 'Succeeded'
            startTime = '2026-10-28T01:00:00Z'
            endTime = '2026-10-28T01:10:00Z'
            template = [pscustomobject]@{ containers = @([pscustomobject]@{ env = @(
                            [pscustomobject]@{ name = 'RUN_ID'; value = $context.runId }
                            [pscustomobject]@{ name = 'TEST_MODE'; value = 'core' }
                            [pscustomobject]@{ name = 'BASE_REF'; value = 'HEAD' }
                        )
                    })
            }
        }
    }
    $result = [pscustomobject]@{
        runId = $context.runId
        mode = 'core'
        exitCode = 0
        completedUtc = [DateTime]::UtcNow.ToString('o')
        sourceSnapshot = [pscustomobject]@{ version = 1; verified = $true; digest = $fingerprint.sourceSnapshot.digest; runId = $context.runId }
        tests = [pscustomobject]@{ total = 12000; executed = 12000; passed = 12000; failed = 0; skipped = 0; fixtureFailures = 0; isComplete = $true; failureReason = $null }
    }
    $receiptPath = Join-Path $root '.git/botnexus-validation/azure-buildtest.json'
    $goodResult = $result | ConvertTo-Json -Depth 10
    $goodProvider = $provider | ConvertTo-Json -Depth 10
    $cases = @('running', 'failed-provider', 'wrong-execution', 'wrong-run', 'wrong-mode', 'string-exit', 'missing-tests', 'incomplete', 'fixture-failed', 'string-count', 'wrong-digest', 'missing-proof', 'invalid-date')
    foreach ($case in $cases) {
        $result = $goodResult | ConvertFrom-Json
        $provider = $goodProvider | ConvertFrom-Json
        switch ($case) {
            'running' {
                $provider.properties.status = 'Running'
            }
            'failed-provider' {
                $provider.properties.status = 'Failed'
            }
            'wrong-execution' {
                $provider.name = 'other'
            }
            'wrong-run' {
                $provider.properties.template.containers[0].env[0].value = 'other'
            }
            'wrong-mode' {
                $provider.properties.template.containers[0].env[1].value = 'full'
            }
            'string-exit' {
                $result.exitCode = '0'
            }
            'missing-tests' {
                $result.tests = $null
            }
            'incomplete' {
                $result.tests.isComplete = $false
            }
            'fixture-failed' {
                $result.tests.fixtureFailures = 1
            }
            'string-count' {
                $result.tests.total = '12000'
            }
            'wrong-digest' {
                $result.sourceSnapshot.digest = 'wrong'
            }
            'missing-proof' {
                $result.sourceSnapshot = $null
            }
            'invalid-date' {
                $result.completedUtc = 'invalid'
            }
        }
        $rejected = $false
        try {
            Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root | Out-Null
        }
        catch {
            $message = $_.Exception.Message
            $reason = switch ($case) {
                { $_ -in @('running', 'failed-provider', 'wrong-execution') } {
                    'Provider does not prove terminal success*'
                }
                { $_ -in @('wrong-run', 'wrong-mode') } {
                    'Provider execution run/mode identity mismatch*'
                }
                'string-exit' {
                    'Runner exit code is not numeric success*'
                }
                'missing-tests' {
                    'Validation did not report successful tests*'
                }
                { $_ -in @('incomplete', 'fixture-failed') } {
                    'Incomplete or inconsistent test result*'
                }
                'string-count' {
                    'Invalid test counters*'
                }
                'wrong-digest' {
                    'Source proof does not match*'
                }
                'missing-proof' {
                    'Missing exact-source proof*'
                }
                'invalid-date' {
                    'Missing or malformed runner completion time*'
                }
            }
            if ($message -notlike $reason) {
                throw "Wrong rejection for ${case}: $message"
            }
            $rejected = $true
        }
        if (-not $rejected -or (Test-Path -LiteralPath $receiptPath)) {
            throw "Accepted invalid evidence: $case"
        }
        "PASS $case"
    }
    $result = $goodResult | ConvertFrom-Json
    $provider = $goodProvider | ConvertFrom-Json
    Set-Content -LiteralPath (Join-Path $root 'candidate.txt') -Value 'changed'
    $rejected = $false
    try {
        Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root | Out-Null
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected -or (Test-Path -LiteralPath $receiptPath)) {
        throw 'Changed candidate accepted.'
    }
    & git -C $root checkout -- candidate.txt
    'PASS changed-candidate'
    Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root | Out-Null
    $before = [IO.File]::ReadAllText($receiptPath)
    Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root | Out-Null
    if ([IO.File]::ReadAllText($receiptPath) -cne $before) {
        throw 'Idempotent recovery changed receipt.'
    }
    'PASS interrupted-wait recovery and idempotency'
    $result.completedUtc = [DateTime]::UtcNow.AddSeconds(1).ToString('o')
    $rejected = $false
    try {
        Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root | Out-Null
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected -or [IO.File]::ReadAllText($receiptPath) -cne $before) {
        throw 'Conflicting replacement accepted.'
    }
    'PASS conflicting-replacement'
    $result = $goodResult | ConvertFrom-Json
    $contextPath = Join-Path $root '.git/botnexus-validation/runs/offline-run.json'
    Write-AzureBuildTestEvidence -Path $contextPath -Evidence $context | Out-Null
    $providerPath = Join-Path $root '.git/provider.json'
    $resultPath = Join-Path $root '.git/result.json'
    Set-Content -LiteralPath $providerPath -Value $goodProvider
    Set-Content -LiteralPath $resultPath -Value $goodResult
    $azureFixture = @{ interrupted = $true; calls = [Collections.Generic.List[string]]::new(); root = $root; runId = $context.runId }
    Set-Item -Path Function:az -Value ({
            $arguments = @($args)
            $azureFixture.calls.Add($arguments -join ' ')
            $fixtureProvider = Join-Path $azureFixture.root '.git/provider.json'
            $fixtureResult = Join-Path $azureFixture.root '.git/result.json'
            $global:LASTEXITCODE = 0
            if ($arguments[0] -eq 'rest' -and $arguments[2] -eq 'get') {
                Get-Content -LiteralPath $fixtureProvider -Raw
                return
            }
            if (($arguments[0..2] -join ' ') -eq 'storage blob download-batch') {
                $destination = $arguments[[Array]::IndexOf($arguments, '--destination') + 1]
                $download = Join-Path $destination $azureFixture.runId
                [IO.Directory]::CreateDirectory($download) | Out-Null
                Copy-Item -LiteralPath $fixtureResult -Destination (Join-Path $download 'result.json')
                if ($azureFixture.interrupted) {
                    throw 'Synthetic interruption after download.'
                }
                return
            }
            throw 'OFFLINE FENCE: unexpected Azure operation.'
        }.GetNewClosure())
    try {
        Remove-Item -LiteralPath $receiptPath
        $rejected = $false
        try {
            & (Join-Path $PSScriptRoot 'Resume-AzureBuildTest.ps1') -ContextPath $contextPath -WorktreePath $root -OutputPath (Join-Path $root '.git/download')
        }
        catch {
            $rejected = $_.Exception.Message -like '*Synthetic interruption*'
            if (-not $rejected) {
                throw
            }
        }
        if (-not $rejected -or (Test-Path -LiteralPath $receiptPath)) {
            throw 'Interrupted download certified success.'
        }
        $azureFixture.interrupted = $false
        & (Join-Path $PSScriptRoot 'Resume-AzureBuildTest.ps1') -ContextPath $contextPath -WorktreePath $root -OutputPath (Join-Path $root '.git/download') | Out-Null
        if ([IO.File]::ReadAllText($receiptPath) -cne $before) {
            throw 'Command recovery differs from original receipt.'
        }
        if ($azureFixture.calls.Count -ne 4 -or ($azureFixture.calls -match 'upload|start|delete').Count -ne 0) {
            throw 'Recovery started or uploaded another execution.'
        }
        'PASS command interruption after download, persisted context and no start/upload'
        foreach ($invalidArtifact in @('missing', 'malformed')) {
            if (Test-Path -LiteralPath $receiptPath) {
                Remove-Item -LiteralPath $receiptPath
            }
            if ($invalidArtifact -eq 'missing') {
                Remove-Item -LiteralPath $resultPath
            }
            elseif ($invalidArtifact -eq 'malformed') {
                Set-Content -LiteralPath $resultPath -Value '{invalid'
            }

            $rejected = $false
            try {
                & (Join-Path $PSScriptRoot 'Resume-AzureBuildTest.ps1') -ContextPath $contextPath -WorktreePath $root -OutputPath (Join-Path $root '.git/download') | Out-Null
            }
            catch {
                $rejected = $true
            }

            if (-not $rejected -or (Test-Path -LiteralPath $receiptPath)) {
                throw "Invalid artifact accepted: $invalidArtifact"
            }
            "PASS command $invalidArtifact artifact"
        }
    }
    finally {
        Remove-Item Function:az
    }
    Set-Content -LiteralPath $resultPath -Value $goodResult
    Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root | Out-Null
    # Concurrent same-directory no-overwrite publication: identical evidence converges,
    # conflicting evidence leaves the winner untouched. Thread jobs cannot invoke Azure.
    $atomicPath = Join-Path $root '.git/concurrent.json'
    $workers = @(1..4 | ForEach-Object {
            $pipeline = [powershell]::Create()
            $null = $pipeline.AddScript('param($modulePath, $path) Import-Module $modulePath -Force; Write-AzureBuildTestEvidence -Path $path -Evidence ([ordered]@{ value = "same" })').AddArgument($module).AddArgument($atomicPath)
            [pscustomobject]@{ pipeline = $pipeline; handle = $pipeline.BeginInvoke() }
        })
    try {
        foreach ($worker in $workers) {
            $worker.pipeline.EndInvoke($worker.handle) | Out-Null
            if ($worker.pipeline.HadErrors) {
                throw 'Concurrent identical writer failed.'
            }
        }
        if ((Get-Content -LiteralPath $atomicPath -Raw | ConvertFrom-Json).value -cne 'same') {
            throw 'Concurrent receipt corrupted.'
        }
        'PASS four concurrent identical writers'
    }
    finally {
        foreach ($worker in $workers) {
            $worker.pipeline.Dispose()
        }
    }
    $rejected = $false
    try {
        Write-AzureBuildTestEvidence -Path $atomicPath -Evidence ([ordered]@{ value = 'different' }) | Out-Null
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected -or (Get-Content -LiteralPath $atomicPath -Raw | ConvertFrom-Json).value -cne 'same') {
        throw 'Concurrent winner replaced.'
    }
    'PASS concurrent winner conflict refusal'
    # The real strict consumer must exit before its Azure script can be invoked.
    & (Join-Path $PSScriptRoot 'Validate-PreCommit.ps1') -WorktreePath $root -BaseRef HEAD -ValidationMode remote -AzureValidationScript (Join-Path $root 'MUST-NOT-RUN.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Strict consumer did not reuse receipt.'
    }
    'PASS strict-consumer reuse'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
