<#
.SYNOPSIS
Pins compatibility enforcement at the actual cached and recovered receipt boundaries.
.DESCRIPTION
Uses disposable Git repositories and synthetic provider results. Only the copied
remote receipt consumer is launched, with both execution delegates pointing at a
nonexistent file. Therefore it cannot launch Azure, dotnet, testhost or a gateway.
Set BOTNEXUS_TEST_SCRATCH_ROOT to owned task scratch before invoking Pester 5.
.EXAMPLE
Invoke-Pester -Path ./infra/buildtest/tests/Test-RunnerReleaseReceipt.ps1
.OUTPUTS
None. Pester reports behavioral regression assertions.
#>
[CmdletBinding()]
param()

BeforeAll {
    $script:repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $script:scripts = Join-Path $script:repository 'scripts/repo'
    Import-Module (Join-Path $script:scripts 'AzureBuildTestRecovery.psm1') -Force
    Import-Module (Join-Path $script:scripts 'RunnerReleaseCompatibility.psm1') -Force

    function Initialize-ApprovedRelease {
        <#
        .SYNOPSIS
        Creates synthetic external approval and v2 launch binding for one fixture.
        .DESCRIPTION
        Independently hashes the explicit copied fixture dependencies, then validates
        the external fixture manifest. No operator approval or real provider is used.
        .EXAMPLE
        Initialize-ApprovedRelease
        .OUTPUTS
        None. Updates only the disposable fixture and its context.
        #>
        [CmdletBinding()]
        param()
        $material = "botnexus-qualified-execution-inputs-v1`0"
        $names = [string[]]$script:dependencies.Clone()
        [Array]::Sort($names, [StringComparer]::Ordinal)
        foreach ($name in $names) {
            $relative = 'scripts/repo/' + $name
            $content = [IO.File]::ReadAllText((Join-Path $script:candidate $relative)).Replace("`r`n", "`n")
            $material += '{0}:{1}{2}:{3}' -f [Text.Encoding]::UTF8.GetByteCount($relative), $relative, [Text.Encoding]::UTF8.GetByteCount($content), $content
        }
        $script:approval = [ordered]@{
            schemaVersion = 1
            releaseId = 'fixture-qualified-1'
            image = $script:provider.properties.template.containers[0].image
            approved = $true
            revoked = $false
            runnerInputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
            modes = @('core', 'diagnostic')
            payloadVersion = 1
            evidenceVersion = 1
            capabilities = @('exact-source-v1', 'diagnostic-containment-v1')
        }
        $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = Join-Path $script:root ('approved-' + [guid]::NewGuid().ToString('N') + '.json')
        $script:approval | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
        $binding = Assert-RunnerReleaseCompatibility -ManifestPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -CandidateRoot $script:candidate -DeployedImage $script:approval.image -Mode core
        $script:context.version = 2
        $script:context | Add-Member -NotePropertyName runnerRelease -NotePropertyValue $binding -Force
        $script:context | Add-Member -NotePropertyName executionConfiguration -NotePropertyValue 'image-entrypoint-v1' -Force
    }

    function Invoke-FixtureReceiptGate {
        <#
        .SYNOPSIS
        Runs the copied remote receipt consumer with both execution delegates blocked.
        .DESCRIPTION
        Uses only the disposable Git candidate and nonexistent delegate paths; cannot
        launch Azure or a local product host even when approval or a receipt fails.
        .EXAMPLE
        Invoke-FixtureReceiptGate
        .OUTPUTS
        System.Management.Automation.PSCustomObject. ExitCode and captured Output.
        #>
        [CmdletBinding()]
        param()
        $blocked = Join-Path $script:root 'execution-delegate-does-not-exist.ps1'
        $gate = Join-Path $script:candidate 'scripts/repo/Invoke-PreCommitValidation.ps1'
        $output = & pwsh -NoProfile -File $gate -WorktreePath $script:candidate -BaseRef HEAD -ValidationMode remote -AzureValidationScript $blocked -LocalValidationScript $blocked 2>&1
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }
    if (-not $env:BOTNEXUS_TEST_SCRATCH_ROOT) {
        throw 'Set BOTNEXUS_TEST_SCRATCH_ROOT to owned isolated scratch.'
    }
    $script:root = Join-Path $env:BOTNEXUS_TEST_SCRATCH_ROOT ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($script:root) | Out-Null
    $script:savedManifest = $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST
}

AfterAll {
    $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = $script:savedManifest
    Remove-Item -LiteralPath $script:root -Recurse -Force
}

Describe 'Real receipt boundary compatibility' {
    BeforeEach {
        $script:candidate = Join-Path $script:root ([guid]::NewGuid().ToString('N'))
        $fixtureScripts = Join-Path $script:candidate 'scripts/repo'
        [IO.Directory]::CreateDirectory($fixtureScripts) | Out-Null
        $script:dependencies = @('Invoke-PreCommitValidation.ps1', 'Get-ValidationMode.ps1', 'Get-WorktreeValidationFingerprint.ps1', 'SourceSnapshot.psm1')
        if (Test-Path -LiteralPath (Join-Path $script:scripts 'RunnerReleaseCompatibility.psm1')) {
            $script:dependencies += 'RunnerReleaseCompatibility.psm1'
        }
        foreach ($name in $script:dependencies) {
            Copy-Item -LiteralPath (Join-Path $script:scripts $name) -Destination $fixtureScripts
        }
        & git -C $script:candidate init --quiet
        if ($LASTEXITCODE -ne 0) {
            throw 'Synthetic Git init failed.'
        }
        & git -C $script:candidate -c core.autocrlf=false add .
        if ($LASTEXITCODE -ne 0) {
            throw 'Synthetic Git add failed.'
        }
        $emptyHooks = Join-Path $script:root 'empty-hooks'
        [IO.Directory]::CreateDirectory($emptyHooks) | Out-Null
        & git -C $script:candidate -c user.name=Fixture -c user.email=fixture@example.invalid -c core.hooksPath=$emptyHooks commit --quiet -m fixture
        if ($LASTEXITCODE -ne 0) {
            throw 'Synthetic Git commit failed.'
        }
        $script:fingerprint = & (Join-Path $script:scripts 'Get-WorktreeValidationFingerprint.ps1') -WorktreePath $script:candidate -BaseRef HEAD
        $script:receiptPath = Join-Path $script:candidate '.git/botnexus-validation/azure-buildtest.json'
        $script:context = [pscustomobject]@{
            version = 1
            runId = 'fixture-run'
            executionName = 'fixture-execution'
            mode = 'core'
            subscriptionId = 'fixture'
            resourceGroup = 'fixture'
            jobName = 'fixture'
            fingerprint = $script:fingerprint
            expectedPreviousReceipt = 'absent'
        }
        $script:provider = [pscustomobject]@{
            name = 'fixture-execution'
            id = '/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture/executions/fixture-execution'
            properties = [pscustomobject]@{
                status = 'Succeeded'
                template = [pscustomobject]@{
                    containers = @([pscustomobject]@{
                            image = ('example.invalid/runner@sha256:' + ('b' * 64))
                            env = @(
                                [pscustomobject]@{ name = 'RUN_ID'; value = 'fixture-run' }
                                [pscustomobject]@{ name = 'TEST_MODE'; value = 'core' }
                            )
                        })
                }
            }
        }
        $script:result = [pscustomobject]@{
            runId = 'fixture-run'
            mode = 'core'
            exitCode = 0
            completedUtc = [DateTime]::UtcNow.ToString('o')
            sourceSnapshot = [pscustomobject]@{
                version = 1
                verified = $true
                digest = $script:fingerprint.sourceSnapshot.digest
                runId = 'fixture-run'
            }
            tests = [pscustomobject]@{
                total = 12000
                executed = 12000
                passed = 12000
                failed = 0
                skipped = 0
                fixtureFailures = 0
                isComplete = $true
                failureReason = $null
            }
        }
        $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = Join-Path $script:root 'absent-operator-manifest.json'
    }

    It 'does not reuse an exact-candidate legacy receipt with <State> operator evidence' -ForEach @(
        @{ State = 'missing' }, @{ State = 'revoked' }, @{ State = 'changed' }
    ) {
        if ($State -ne 'missing') {
            $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = Join-Path $script:root 'current-operator-manifest.json'
            [ordered]@{
                schemaVersion = 1
                releaseId = 'fixture-release-1'
                image = $script:provider.properties.template.containers[0].image
                approved = $true
                revoked = ($State -eq 'revoked')
                runnerInputDigest = ('a' * 64)
                modes = @('core')
                payloadVersion = 1
                evidenceVersion = 1
                capabilities = @('exact-source-v1')
            } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
        }
        $receipt = [ordered]@{
            version = 1
            fingerprint = $script:fingerprint.fingerprint
            head = $script:fingerprint.head
            baseRef = $script:fingerprint.baseRef
            baseCommit = $script:fingerprint.baseCommit
            tree = $script:fingerprint.tree
            mode = 'core'
            runId = 'fixture-run'
        }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $script:receiptPath)) | Out-Null
        $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:receiptPath -Encoding utf8NoBOM
        $blockedDelegate = Join-Path $script:root 'execution-delegate-does-not-exist.ps1'
        $gate = Join-Path $script:candidate 'scripts/repo/Invoke-PreCommitValidation.ps1'
        $output = & pwsh -NoProfile -File $gate -WorktreePath $script:candidate -BaseRef HEAD -ValidationMode remote -AzureValidationScript $blockedDelegate -LocalValidationScript $blockedDelegate 2>&1
        $code = $LASTEXITCODE
        ($output -join "`n") | Set-Content -LiteralPath (Join-Path $env:BOTNEXUS_TEST_SCRATCH_ROOT "cached-$State-output.txt") -Encoding utf8NoBOM
        $code | Should -Not -Be 0 -Because 'a cached candidate fingerprint is not current operator release approval'
        ($output -join "`n") | Should -Not -Match 'skipping redundant remote validation'
    }

    It 'does not recover a receipt without current operator manifest approval' {
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
        Test-Path -LiteralPath $script:receiptPath | Should -BeFalse
    }

    It 'does not recover after the operator revokes the release during execution' {
        $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = Join-Path $script:root 'revoked.json'
        [ordered]@{
            schemaVersion = 1
            releaseId = 'fixture-release-1'
            image = $script:provider.properties.template.containers[0].image
            approved = $true
            revoked = $true
            runnerInputDigest = ('a' * 64)
            modes = @('core')
            payloadVersion = 1
            evidenceVersion = 1
            capabilities = @('exact-source-v1')
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
        Test-Path -LiteralPath $script:receiptPath | Should -BeFalse
    }

    It 'recovers a v2 bound receipt and reuses it only for current approved qualification' {
        Initialize-ApprovedRelease
        Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate | Should -BeExactly $script:receiptPath
        $receipt = Get-Content -LiteralPath $script:receiptPath -Raw | ConvertFrom-Json
        $receipt.version | Should -Be 2
        $receipt.runnerRelease.manifestSha256 | Should -BeExactly $script:context.runnerRelease.manifestSha256
        $receipt.runnerRelease.image | Should -BeExactly $script:provider.properties.template.containers[0].image
        $receipt.executionConfiguration | Should -BeExactly 'image-entrypoint-v1'
        $receipt.sourceSnapshot.digest | Should -BeExactly $script:fingerprint.sourceSnapshot.digest
        $gate = Invoke-FixtureReceiptGate
        $gate.ExitCode | Should -Be 0 -Because $gate.Output
        $gate.Output | Should -Match 'skipping redundant remote validation'
        $before = [IO.File]::ReadAllText($script:receiptPath)
        Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate | Out-Null
        [IO.File]::ReadAllText($script:receiptPath) | Should -BeExactly $before
    }

    It 'rejects current <Change> after a valid launch and receipt without replacing evidence' -ForEach @(
        @{ Change = 'revoked' }, @{ Change = 'unapproved' }, @{ Change = 'bytes' }, @{ Change = 'missing' }, @{ Change = 'release' }
    ) {
        Initialize-ApprovedRelease
        Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate | Out-Null
        $before = [IO.File]::ReadAllText($script:receiptPath)
        switch ($Change) {
            'revoked' {
                $script:approval.revoked = $true
            }
            'unapproved' {
                $script:approval.approved = $false
            }
            'release' {
                $script:approval.releaseId = 'fixture-qualified-2'
            }
        }
        if ($Change -eq 'missing') {
            Remove-Item -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST
        }
        elseif ($Change -eq 'bytes') {
            [IO.File]::AppendAllText($env:BOTNEXUS_RUNNER_RELEASE_MANIFEST, "`n ")
        }
        else {
            $script:approval | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
        }
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
        [IO.File]::ReadAllText($script:receiptPath) | Should -BeExactly $before
        $gate = Invoke-FixtureReceiptGate
        $gate.ExitCode | Should -Not -Be 0
        $gate.Output | Should -Not -Match 'skipping redundant remote validation'
    }

    It 'rejects provider or launch binding violation <Case>' -ForEach @(
        @{ Case = 'provider-image' }, @{ Case = 'launch-image' }, @{ Case = 'launch-hash' },
        @{ Case = 'command' }, @{ Case = 'args' }, @{ Case = 'scalar-command' },
        @{ Case = 'configuration' }, @{ Case = 'binding-type' }, @{ Case = 'legacy-context' }
    ) {
        Initialize-ApprovedRelease
        switch ($Case) {
            'provider-image' {
                $script:provider.properties.template.containers[0].image = 'other.invalid/runner@sha256:' + ('b' * 64)
            }
            'launch-image' {
                $script:context.runnerRelease.image = 'other.invalid/runner@sha256:' + ('b' * 64)
            }
            'launch-hash' {
                $script:context.runnerRelease.manifestSha256 = '0' * 64
            }
            'command' {
                $script:provider.properties.template.containers[0] | Add-Member -NotePropertyName command -NotePropertyValue @('pwsh', '-Command', 'exit 0')
            }
            'args' {
                $script:provider.properties.template.containers[0] | Add-Member -NotePropertyName args -NotePropertyValue @('-Command', 'exit 0')
            }
            'scalar-command' {
                $script:provider.properties.template.containers[0] | Add-Member -NotePropertyName command -NotePropertyValue ''
            }
            'configuration' {
                $script:context.executionConfiguration = 'arbitrary'
            }
            'binding-type' {
                $script:context.runnerRelease.payloadVersion = '1'
            }
            'legacy-context' {
                $script:context.version = 1
            }
        }
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw -ErrorId 'RunnerReleaseCompatibility*'
        Test-Path -LiteralPath $script:receiptPath | Should -BeFalse
    }

    It 'rejects unsafe cached binding <Case> even with exact candidate fingerprint' -ForEach @(
        @{ Case = 'legacy-version' }, @{ Case = 'binding-hash' }, @{ Case = 'binding-image' },
        @{ Case = 'binding-input' }, @{ Case = 'binding-mode' }, @{ Case = 'binding-version-type' },
        @{ Case = 'configuration' }, @{ Case = 'diagnostic' }, @{ Case = 'selector-absent' }
    ) {
        Initialize-ApprovedRelease
        Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate | Out-Null
        $receipt = Get-Content -LiteralPath $script:receiptPath -Raw | ConvertFrom-Json
        switch ($Case) {
            'legacy-version' {
                $receipt.version = 1
            }
            'binding-hash' {
                $receipt.runnerRelease.manifestSha256 = '0' * 64
            }
            'binding-image' {
                $receipt.runnerRelease.image = 'other.invalid/runner@sha256:' + ('b' * 64)
            }
            'binding-input' {
                $receipt.runnerRelease.runnerInputDigest = '0' * 64
            }
            'binding-mode' {
                $receipt.runnerRelease.mode = 'diagnostic'
            }
            'binding-version-type' {
                $receipt.runnerRelease.payloadVersion = '1'
            }
            'configuration' {
                $receipt.executionConfiguration = 'arbitrary'
            }
            'diagnostic' {
                $receipt.mode = 'diagnostic'
            }
            'selector-absent' {
                $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = $null
            }
        }
        $receipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $script:receiptPath -Encoding utf8NoBOM
        $gate = Invoke-FixtureReceiptGate
        $gate.ExitCode | Should -Not -Be 0
        $gate.Output | Should -Not -Match 'skipping redundant remote validation'
    }

    It 'rejects diagnostic context certification and preserves predecessor atomicity' {
        Initialize-ApprovedRelease
        $script:context.mode = 'diagnostic'
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw '*Invalid recovery run or mode*'
        $script:context.mode = 'core'
        [IO.Directory]::CreateDirectory((Split-Path -Parent $script:receiptPath)) | Out-Null
        [IO.File]::WriteAllText($script:receiptPath, 'intervening receipt')
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw '*predecessor conflict*'
        [IO.File]::ReadAllText($script:receiptPath) | Should -BeExactly 'intervening receipt'
    }

    It 'accepts absent or empty provider overrides but rejects ambiguous singleton identity' {
        Initialize-ApprovedRelease
        $container = $script:provider.properties.template.containers[0]
        $container | Add-Member -NotePropertyName command -NotePropertyValue @()
        $container | Add-Member -NotePropertyName args -NotePropertyValue @()
        Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate | Out-Null
        $before = [IO.File]::ReadAllText($script:receiptPath)
        $script:provider.properties.template.containers = @($container, $container)
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw '*Ambiguous provider execution container*'
        [IO.File]::ReadAllText($script:receiptPath) | Should -BeExactly $before
    }

    It 'preserves numeric completeness and run identity checks with a valid v2 binding: <Case>' -ForEach @(
        @{ Case = 'string-exit' }, @{ Case = 'string-total' }, @{ Case = 'incomplete' }, @{ Case = 'wrong-run' }, @{ Case = 'wrong-source' }
    ) {
        Initialize-ApprovedRelease
        switch ($Case) {
            'string-exit' {
                $script:result.exitCode = '0'
            }
            'string-total' {
                $script:result.tests.total = '12000'
            }
            'incomplete' {
                $script:result.tests.isComplete = $false
            }
            'wrong-run' {
                $script:provider.properties.template.containers[0].env[0].value = 'other-run'
            }
            'wrong-source' {
                $script:result.sourceSnapshot.digest = '0' * 64
            }
        }
        $reason = switch ($Case) {
            'string-exit' {
                '*Runner exit code is not numeric success*'
            }
            'string-total' {
                '*Invalid test counters*'
            }
            'incomplete' {
                '*Incomplete or inconsistent test result*'
            }
            'wrong-run' {
                '*Provider execution run/mode identity mismatch*'
            }
            'wrong-source' {
                '*Source proof does not match*'
            }
        }
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw $reason
        Test-Path -LiteralPath $script:receiptPath | Should -BeFalse
    }

    It 'still rejects missing candidate snapshot proof (existing safety control)' {
        $script:result.sourceSnapshot = $null
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw '*Missing exact-source proof*'
        Test-Path -LiteralPath $script:receiptPath | Should -BeFalse
    }

    It 'still rejects incomplete tests (existing safety control)' {
        $script:result.tests.isComplete = $false
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw '*Incomplete or inconsistent test result*'
        Test-Path -LiteralPath $script:receiptPath | Should -BeFalse
    }

    It 'still rejects a changed candidate snapshot (existing safety control)' {
        [IO.File]::WriteAllText((Join-Path $script:candidate 'changed.txt'), 'new candidate')
        { Complete-AzureBuildTestRecovery -Context $script:context -Provider $script:provider -Result $script:result -WorktreePath $script:candidate } | Should -Throw '*Source changed before receipt*'
        Test-Path -LiteralPath $script:receiptPath | Should -BeFalse
    }
}
