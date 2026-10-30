<#
.SYNOPSIS
Tests reviewed Azure receipt finalization defects without remote execution.
.DESCRIPTION
Uses isolated Git and synthetic Azure responses. Tests ARM identity, replacement lifecycle
and fresh strict artifacts. Never starts Azure, dotnet, gateways or product test hosts.
.EXAMPLE
./Test-AzureBuildTestFinalization.ps1
Runs offline finalization regressions. Run this explicit path separately on Linux;
CORE does not automatically execute this PowerShell fixture.
.OUTPUTS
System.String. Regression results.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AzureBuildTestRecovery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.psm1') -Force
function Assert-Rejection {
    <#
    .SYNOPSIS
    Requires an action to fail for its intended reason.
    .DESCRIPTION
    Rejects success or a different exception, preventing shared earlier failures from
    turning unrelated negative cases green. Positive controls run separately.
    .PARAMETER Pattern
    Wildcard matching the expected exception message.
    .PARAMETER Action
    Isolated production operation under test.
    .EXAMPLE
    Assert-Rejection -Pattern '*predecessor conflict*' -Action $operation
    .OUTPUTS
    None. Throws if the expected rejection is not observed.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Pattern, [Parameter(Mandatory)][scriptblock]$Action)
    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -notlike $Pattern) {
            throw "Unexpected rejection: $($_.Exception.Message); expected $Pattern"
        }
        return
    }
    throw "Expected rejection was not observed: $Pattern"
}
$root = Join-Path ([IO.Path]::GetTempPath()) "finalization-$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    & git -C $root init --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'Fixture Git init failed.'
    }
    Set-Content -LiteralPath (Join-Path $root 'candidate.txt') -Value 'candidate'
    & git -C $root add .
    & git -C $root -c user.name=Fixture -c user.email=fixture@example.com commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) {
        throw 'Fixture Git commit failed.'
    }
    $fingerprint = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $root -BaseRef HEAD
    $context = [pscustomobject]@{
        version = 1
        runId = 'review-run'
        mode = 'core'
        executionName = 'review-execution'
        jobUrl = 'https://management.azure.com/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture'
        subscriptionId = 'fixture'
        resourceGroup = 'fixture'
        jobName = 'fixture'
        storageAccount = 'fixturestorage'
        fingerprint = $fingerprint
        expectedPreviousReceipt = 'absent'
    }
    $provider = [pscustomobject]@{
        id = '/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture/executions/review-execution'
        name = 'review-execution'
        properties = [pscustomobject]@{
            status = 'Succeeded'
            startTime = '2026-10-28T01:00:00Z'
            endTime = '2026-10-28T01:10:00Z'
            template = [pscustomobject]@{ containers = @([pscustomobject]@{
                        name = 'runner'
                        image = 'fixture.invalid/runner:fixture'
                        env = @(
                            [pscustomobject]@{ name = 'RUN_ID'; value = 'review-run' }
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
        completedUtc = '2026-10-28T01:10:00Z'
        sourceSnapshot = [pscustomobject]@{ version = 1; verified = $true; digest = $fingerprint.sourceSnapshot.digest; runId = $context.runId }
        tests = [pscustomobject]@{ total = 12000; executed = 12000; passed = 12000; failed = 0; skipped = 0; fixtureFailures = 0; isComplete = $true; failureReason = $null }
    }
    $launcher = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'))
    $startPosition = $launcher.IndexOf("$" + "execution = Invoke-AzJson @('rest', '--method', 'post'")
    $persistPosition = $launcher.IndexOf('Write-AzureBuildTestEvidence -Path (Join-Path $gitDirectory "botnexus-validation/runs/$runId-start.json")')
    if ($persistPosition -lt 0 -or $startPosition -lt $persistPosition) {
        throw 'Predecessor context is not persisted before execution start.'
    }
    if ($launcher -notmatch 'expectedPreviousReceipt = \$expectedPreviousReceipt') {
        throw 'Final recovery context lacks predecessor token.'
    }
    'PASS launcher persists predecessor before execution start'
    $receiptPath = Join-Path $root '.git/botnexus-validation/azure-buildtest.json'
    Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root | Out-Null
    'PASS production ARM positive control'
    $before = [IO.File]::ReadAllText($receiptPath)
    $firstContext = $context | ConvertTo-Json -Depth 20
    $firstProvider = $provider | ConvertTo-Json -Depth 20
    $firstResult = $result | ConvertTo-Json -Depth 20
    $secondContext = $firstContext | ConvertFrom-Json
    $secondProvider = $firstProvider | ConvertFrom-Json
    $secondResult = $firstResult | ConvertFrom-Json
    $secondContext.expectedPreviousReceipt = Get-AzureBuildTestReceiptToken -Path $receiptPath
    $secondContext.runId = 'second-run'
    $secondContext.executionName = 'second-execution'
    $secondProvider.name = $secondContext.executionName
    $secondProvider.id = '/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture/executions/second-execution'
    $secondProvider.properties.template.containers[0].env[0].value = $secondContext.runId
    $secondResult.runId = $secondContext.runId
    $secondResult.sourceSnapshot.runId = $secondContext.runId
    Complete-AzureBuildTestRecovery -Context $secondContext -Provider $secondProvider -Result $secondResult -WorktreePath $root | Out-Null
    $secondBytes = [IO.File]::ReadAllText($receiptPath)
    if ($secondBytes -ceq $before) {
        throw 'Second run did not replace expected old receipt.'
    }
    Complete-AzureBuildTestRecovery -Context $secondContext -Provider $secondProvider -Result $secondResult -WorktreePath $root | Out-Null
    if ([IO.File]::ReadAllText($receiptPath) -cne $secondBytes) {
        throw 'Repeated same run changed receipt.'
    }
    'PASS normal second run replaces old receipt and repeated same writer is idempotent'
    Assert-Rejection -Pattern '*Receipt predecessor conflict*' -Action {
        Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root
    }
    if ([IO.File]::ReadAllText($receiptPath) -cne $secondBytes) {
        throw 'Late stale writer overwrote winner.'
    }
    'PASS late stale writer preserves intervening distinct winner'
    $archive = Join-Path (Split-Path -Parent $receiptPath) ('archive/' + $secondContext.expectedPreviousReceipt.Substring(7) + '.json')
    if ([IO.File]::ReadAllText($archive) -cne $before) {
        throw 'Prior evidence was not archived byte-for-byte.'
    }
    'PASS predecessor archived by exact byte hash'

    # A changed candidate starts a new execution normally; the old singleton is expected.
    $context = $secondContext | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $provider = $secondProvider | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $result = $secondResult | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $context.expectedPreviousReceipt = Get-AzureBuildTestReceiptToken -Path $receiptPath
    Set-Content -LiteralPath (Join-Path $root 'candidate.txt') -Value 'changed candidate'
    $context.fingerprint = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $root -BaseRef HEAD
    $result.sourceSnapshot.digest = $context.fingerprint.sourceSnapshot.digest
    $context.runId = 'changed-run'
    $context.executionName = 'changed-execution'
    $provider.name = $context.executionName
    $provider.id = '/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture/executions/changed-execution'
    $provider.properties.template.containers[0].env[0].value = $context.runId
    $result.runId = $context.runId
    $result.sourceSnapshot.runId = $context.runId
    Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root | Out-Null
    'PASS changed-candidate second run replaces expected old receipt'

    $goodContext = $context | ConvertTo-Json -Depth 20
    $goodProvider = $provider | ConvertTo-Json -Depth 20
    $goodResult = $result | ConvertTo-Json -Depth 20
    $installed = [IO.File]::ReadAllText($receiptPath)
    foreach ($case in @('wrong-arm', 'url-id', 'missing-id', 'wrong-subscription', 'wrong-job', 'result-run', 'result-mode', 'zero', 'arithmetic', 'failed', 'bad-total', 'bad-executed', 'bad-passed', 'bad-failed', 'bad-skipped', 'bad-fixtureFailures')) {
        $context = $goodContext | ConvertFrom-Json
        $provider = $goodProvider | ConvertFrom-Json
        $result = $goodResult | ConvertFrom-Json
        $pattern = '*Source proof does not match*'
        switch ($case) {
            'wrong-arm' {
                $provider.id += '-wrong'; $pattern = '*Provider does not prove terminal success*'
            }
            'url-id' {
                $provider.id = 'https://management.azure.com' + $provider.id; $pattern = '*Provider does not prove terminal success*'
            }
            'missing-id' {
                $provider.id = $null; $pattern = '*Provider does not prove terminal success*'
            }
            'wrong-subscription' {
                $provider.id = $provider.id.Replace('/subscriptions/fixture/', '/subscriptions/other/'); $pattern = '*Provider does not prove terminal success*'
            }
            'wrong-job' {
                $provider.id = $provider.id.Replace('/jobs/fixture/', '/jobs/other/'); $pattern = '*Provider does not prove terminal success*'
            }
            'result-run' {
                $result.runId = 'wrong'
            }
            'result-mode' {
                $result.mode = 'full'
            }
            'zero' {
                $result.tests.total = 0; $pattern = '*Incomplete or inconsistent test result*'
            }
            'arithmetic' {
                $result.tests.executed = 12001; $pattern = '*Incomplete or inconsistent test result*'
            }
            'failed' {
                $result.tests.failed = 1; $pattern = '*Incomplete or inconsistent test result*'
            }
            default {
                $field = $case.Substring(4); $result.tests.$field = '0'; $pattern = '*Invalid test counters*'
            }
        }
        Assert-Rejection -Pattern $pattern -Action {
            Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root
        }
        if ([IO.File]::ReadAllText($receiptPath) -cne $installed) {
            throw "Invalid case changed installed receipt: $case"
        }
        "PASS reason-specific $case"
    }
    $context = $goodContext | ConvertFrom-Json
    $provider = $goodProvider | ConvertFrom-Json
    $result = $goodResult | ConvertFrom-Json
    $head = (& git -C $root rev-parse HEAD).Trim()
    & git -C $root update-ref refs/heads/comparison $head
    if ($LASTEXITCODE -ne 0) {
        throw 'Fixture base ref creation failed.'
    }
    $context.fingerprint = & (Join-Path $PSScriptRoot 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $root -BaseRef refs/heads/comparison
    $tree = (& git -C $root rev-parse 'HEAD^{tree}').Trim()
    $newBase = (& git -C $root -c user.name=Fixture -c user.email=fixture@example.com commit-tree $tree -p $head -m comparison).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Fixture comparison commit failed.'
    }
    & git -C $root update-ref refs/heads/comparison $newBase
    if ($LASTEXITCODE -ne 0) {
        throw 'Fixture base movement failed.'
    }
    Assert-Rejection -Pattern '*Source changed before receipt*' -Action {
        Complete-AzureBuildTestRecovery -Context $context -Provider $provider -Result $result -WorktreePath $root
    }
    'PASS base-only movement rejects unchanged source bytes'

    # Exercise actual Resume GET/download with production-shaped ARM response. A stale local
    # strict artifact must not substitute for evidence omitted from the fresh prefix.
    $context = $goodContext | ConvertFrom-Json
    $provider = $goodProvider | ConvertFrom-Json
    $result = $goodResult | ConvertFrom-Json
    $context.mode = 'strict'
    $context.expectedPreviousReceipt = Get-AzureBuildTestReceiptToken -Path $receiptPath
    $provider.properties.template.containers[0].env[1].value = 'strict'
    $result.mode = 'strict'
    $output = Join-Path $root '.git/download'
    [IO.Directory]::CreateDirectory($output) | Out-Null
    Set-Content -LiteralPath (Join-Path $output 'playwright.log') -Value 'stale'
    $contextPath = Join-Path $root '.git/strict-context.json'
    Write-AzureBuildTestEvidence -Path $contextPath -Evidence $context | Out-Null
    $azure = @{ provider = ($provider | ConvertTo-Json -Depth 20); result = ($result | ConvertTo-Json -Depth 20); fresh = $false; runId = $context.runId; getExit = 0; downloadExit = 0; calls = [Collections.Generic.List[string]]::new() }
    Set-Item Function:az -Value ({
            $arguments = @($args)
            $azure.calls.Add($arguments -join ' ')
            if ($arguments[0] -eq 'rest' -and $arguments[2] -eq 'get') {
                $global:LASTEXITCODE = $azure.getExit
                $azure.provider
                return
            }
            if (($arguments[0..2] -join ' ') -eq 'storage blob download-batch') {
                $global:LASTEXITCODE = $azure.downloadExit
                $destination = $arguments[[Array]::IndexOf($arguments, '--destination') + 1]
                $freshPath = Join-Path $destination $azure.runId
                [IO.Directory]::CreateDirectory($freshPath) | Out-Null
                Set-Content -LiteralPath (Join-Path $freshPath 'result.json') -Value $azure.result
                if ($azure.fresh) {
                    Set-Content -LiteralPath (Join-Path $freshPath 'playwright.log') -Value 'fresh'
                }
                return
            }
            throw 'OFFLINE FENCE: unexpected Azure operation.'
        }.GetNewClosure())
    try {
        Assert-Rejection -Pattern '*Strict Playwright artifact is missing from fresh canonical download*' -Action {
            & (Join-Path $PSScriptRoot 'Resume-AzureBuildTest.ps1') -ContextPath $contextPath -WorktreePath $root -OutputPath $output
        }
        if ([IO.File]::ReadAllText($receiptPath) -cne $installed) {
            throw 'Stale strict artifact changed receipt.'
        }
        'PASS command rejects stale strict artifact before merge'
        $azure.getExit = 1
        Assert-Rejection -Pattern '*Could not query the existing Azure execution*' -Action {
            & (Join-Path $PSScriptRoot 'Resume-AzureBuildTest.ps1') -ContextPath $contextPath -WorktreePath $root -OutputPath $output
        }
        $azure.getExit = 0
        $azure.downloadExit = 1
        Assert-Rejection -Pattern '*Existing-run artifact download failed*' -Action {
            & (Join-Path $PSScriptRoot 'Resume-AzureBuildTest.ps1') -ContextPath $contextPath -WorktreePath $root -OutputPath $output
        }
        $azure.downloadExit = 0
        $azure.fresh = $true
        & (Join-Path $PSScriptRoot 'Resume-AzureBuildTest.ps1') -ContextPath $contextPath -WorktreePath $root -OutputPath $output | Out-Null
        $strictBytes = [IO.File]::ReadAllText($receiptPath)
        & (Join-Path $PSScriptRoot 'Resume-AzureBuildTest.ps1') -ContextPath $contextPath -WorktreePath $root -OutputPath $output | Out-Null
        if ([IO.File]::ReadAllText($receiptPath) -cne $strictBytes) {
            throw 'Repeated strict recovery changed receipt.'
        }
        if (@($azure.calls | Where-Object { $_ -match 'upload|start|delete' }).Count -ne 0) {
            throw 'Recovery wrote to Azure.'
        }
        'PASS GET/download errors fail closed and fresh strict recovery replaces old receipt idempotently'
    }
    finally {
        Remove-Item Function:az
    }

    $racePath = Join-Path $root '.git/race.json'
    Set-AzureBuildTestReceipt -Path $racePath -Evidence ([ordered]@{ value = 'prior' }) -ExpectedPrevious absent | Out-Null
    $predecessor = Get-AzureBuildTestReceiptToken -Path $racePath
    $module = Join-Path $PSScriptRoot 'AzureBuildTestRecovery.psm1'
    foreach ($raceKind in @('distinct', 'identical')) {
        $raceValues = if ($raceKind -eq 'distinct') {
            @('alpha', 'beta')
        }
        else {
            @('same', 'same')
        }
        $predecessor = Get-AzureBuildTestReceiptToken -Path $racePath
        $gatePath = Join-Path $root ".git/race-gate-$raceKind"
        $workers = @($raceValues | ForEach-Object {
                $childSource = @'
$ErrorActionPreference = 'Stop'
Import-Module '__MODULE__' -Force
$clock = [Diagnostics.Stopwatch]::StartNew()
while (-not [IO.File]::Exists('__GATE__')) {
    if ($clock.Elapsed.TotalSeconds -gt 10) { throw 'Fixture race barrier timed out.' }
    Start-Sleep -Milliseconds 10
}
try {
    Set-AzureBuildTestReceipt -Path '__PATH__' -Evidence ([ordered]@{ value = '__VALUE__' }) -ExpectedPrevious '__TOKEN__' | Out-Null
    'won:__VALUE__'
}
catch {
    if ($_.Exception.Message -notlike '*Receipt predecessor conflict*') { throw }
    'lost:__VALUE__'
}
'@
                $childSource = $childSource.Replace('__MODULE__', $module.Replace("'", "''")).Replace('__GATE__', $gatePath.Replace("'", "''")).Replace('__PATH__', $racePath.Replace("'", "''")).Replace('__TOKEN__', $predecessor).Replace('__VALUE__', $_)
                $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
                $start.UseShellExecute = $false
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                $start.ArgumentList.Add('-NoProfile')
                $start.ArgumentList.Add('-EncodedCommand')
                $start.ArgumentList.Add([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childSource)))
                $child = [Diagnostics.Process]::Start($start)
                [pscustomobject]@{ process = $child; stdout = $child.StandardOutput.ReadToEndAsync(); stderr = $child.StandardError.ReadToEndAsync() }
            })
        try {
            [IO.File]::WriteAllText($gatePath, 'release')
            $outcomes = @($workers | ForEach-Object {
                    if (-not $_.process.WaitForExit(15000)) {
                        $_.process.Kill($true); throw 'Receipt race child timed out.'
                    }
                    $errors = $_.stderr.GetAwaiter().GetResult()
                    if ($_.process.ExitCode -ne 0) {
                        throw "Distinct concurrent writer failed: $errors"
                    }
                    $_.stdout.GetAwaiter().GetResult().Trim()
                })
            $expectedWins = if ($raceKind -eq 'distinct') {
                1
            }
            else {
                2
            }
            if (@($outcomes | Where-Object { $_ -like 'won:*' }).Count -ne $expectedWins -or @($outcomes | Where-Object { $_ -like 'lost:*' }).Count -ne (2 - $expectedWins)) {
                throw 'Concurrent writers did not preserve the expected winner count.'
            }
            $winner = (Get-Content -LiteralPath $racePath -Raw | ConvertFrom-Json).value
            Set-AzureBuildTestReceipt -Path $racePath -Evidence ([ordered]@{ value = $winner }) -ExpectedPrevious $predecessor | Out-Null
            "PASS cross-process $raceKind writers expected winner count and winner repeat idempotent"
        }
        finally {
            foreach ($worker in $workers) {
                $worker.process.Dispose()
            }
        }
    }
    $lock = [IO.File]::Open("$racePath.lock", [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Assert-Rejection -Pattern '*Receipt exclusive lock acquisition timed out*' -Action {
            Set-AzureBuildTestReceipt -Path $racePath -Evidence ([ordered]@{ value = 'blocked' }) -ExpectedPrevious $predecessor -LockTimeoutMilliseconds 50
        }
        'PASS bounded exclusive lock failure'
    }
    finally {
        $lock.Dispose()
    }

}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
