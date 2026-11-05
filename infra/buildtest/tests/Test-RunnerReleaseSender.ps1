<#
.SYNOPSIS
Exercises sender and resume against a fenced Azure fixture only.
.DESCRIPTION
Copies actual tooling to disposable scratch, uses synthetic Git/provider evidence,
and replaces az with a fail-closed in-process fixture. No Azure CLI, network,
credentials, dotnet, testhost or gateway can run through this harness.
.EXAMPLE
Invoke-Pester -Path ./infra/buildtest/tests/Test-RunnerReleaseSender.ps1
.OUTPUTS
None. Pester reports offline regression results.
#>
[CmdletBinding()]
param()

BeforeAll {
    $script:repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    if (-not $env:BOTNEXUS_TEST_SCRATCH_ROOT) {
        throw 'Set BOTNEXUS_TEST_SCRATCH_ROOT to owned task scratch.'
    }
    $script:root = Join-Path $env:BOTNEXUS_TEST_SCRATCH_ROOT ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($script:root) | Out-Null
    $script:oldSelector = $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST

    function Invoke-FixtureAzure {
        <#
        .SYNOPSIS
        Fences all Azure calls with synthetic responses.
        .DESCRIPTION
        Never invokes an executable. Unknown operations throw before any network access.
        .EXAMPLE
        az account show
        .OUTPUTS
        System.String. Synthetic JSON only.
        #>
        $arguments = @($args)
        $fixture.calls.Add(($arguments -join ' '))
        $global:LASTEXITCODE = 0
        if ($arguments[0] -eq 'account') {
            '{"user":{"name":"fixture"},"name":"fixture","environmentName":"AzureCloud"}'
            return
        }
        if (($arguments[0..2] -join ' ') -eq 'acr manifest show-metadata') {
            if ($fixture.case -eq 'unresolved') {
                '{"tags":["src-unrelated"]}'
            }
            else {
                @{ digest = ('sha256:' + ('b' * 64)); tags = @('src-unrelated') } | ConvertTo-Json
            }
            return
        }
        if ($arguments[0] -eq 'rest') {
            $url = $arguments[[Array]::IndexOf($arguments, '--url') + 1]
            if ($arguments -contains 'post') {
                $path = $arguments[[Array]::IndexOf($arguments, '--body') + 1].TrimStart('@')
                $fixture.start = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                if ($fixture.case -eq 'revoke-on-start') {
                    $fixture.approval.revoked = $true
                    $fixture.approval | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
                }
                '{"name":"fixture-execution"}'
                return
            }
            if ($url -like '*/executions/*') {
                if ($null -eq $fixture.context) {
                    $contextFile = Get-ChildItem -LiteralPath (Join-Path $fixture.candidate '.git/botnexus-validation/runs') -Filter '*.json' | Where-Object Name -NotLike '*-start.json' | Select-Object -First 1
                    $fixture.context = Get-Content -LiteralPath $contextFile.FullName -Raw | ConvertFrom-Json
                    $fixture.provider = [pscustomobject]@{
                        name = 'fixture-execution'
                        id = '/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture/executions/fixture-execution'
                        properties = [pscustomobject]@{ status = 'Succeeded'; template = $fixture.start }
                    }
                    $fixture.result = [pscustomobject]@{
                        runId = $fixture.context.runId; mode = $fixture.context.mode; exitCode = 0
                        completedUtc = [DateTime]::UtcNow.ToString('o')
                        sourceSnapshot = [pscustomobject]@{ version = 1; verified = $true; digest = $fixture.context.fingerprint.sourceSnapshot.digest; runId = $fixture.context.runId }
                        tests = [pscustomobject]@{ total = 12000; executed = 12000; passed = 12000; failed = 0; skipped = 0; fixtureFailures = 0; isComplete = $true; failureReason = $null }
                    }
                    if ($fixture.case -eq 'provider-image') {
                        $fixture.provider.properties.template.containers[0].image = 'fixture.azurecr.io/runner@sha256:' + ('c' * 64)
                    }
                    if ($fixture.case -eq 'provider-command') {
                        $fixture.provider.properties.template.containers[0] | Add-Member -NotePropertyName command -NotePropertyValue @('false')
                    }
                }
                $fixture.provider | ConvertTo-Json -Depth 20
            }
            else {
                $fixture.job | ConvertTo-Json -Depth 20
            }
            return
        }
        if (($arguments[0..2] -join ' ') -eq 'storage blob upload') {
            if ($fixture.case -eq 'policy-on-upload') {
                [IO.File]::WriteAllText((Join-Path $fixture.candidate 'scripts/repo/test-impacted.ps1'), '# changed impacted policy')
            }
            if ($fixture.case -eq 'revoke-on-upload') {
                $fixture.approval.revoked = $true
                $fixture.approval | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
            }
            return
        }
        if (($arguments[0..2] -join ' ') -eq 'storage blob download-batch') {
            $destination = $arguments[[Array]::IndexOf($arguments, '--destination') + 1]
            $run = Join-Path $destination $fixture.context.runId
            [IO.Directory]::CreateDirectory($run) | Out-Null
            $fixture.result | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8NoBOM
            if ($fixture.context.mode -eq 'strict' -and $fixture.case -ne 'missing-strict') {
                [IO.File]::WriteAllText((Join-Path $run 'playwright.log'), 'fixture strict evidence')
            }
            if ($fixture.context.mode -eq 'diagnostic') {
                [ordered]@{
                    runId = $fixture.context.runId; mode = 'diagnostic'; runnerImage = $fixture.start.containers[0].image
                    eventEvidenceComplete = $true; hangEvidenceComplete = $true
                    project = 'tests/unit/Example.Tests/Example.Tests.csproj'; qualifiesForCoreReceipt = $false
                    sourceSnapshot = $fixture.result.sourceSnapshot
                } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'diagnostic-evidence.json') -Encoding utf8NoBOM
            }
            return
        }
        throw 'OFFLINE FENCE: unexpected Azure operation.'
    }

    function Initialize-SenderResult {
        <#
        .SYNOPSIS
        Builds synthetic execution and canonical result for the captured start.
        .DESCRIPTION
        Uses actual persisted v2 context and start template. No runner code is executed.
        .PARAMETER Launch
        NoWait result returned by the real copied sender.
        .EXAMPLE
        Initialize-SenderResult -Launch $launch
        .OUTPUTS
        None. Updates disposable fixture evidence.
        #>
        [CmdletBinding()]
        param([Parameter(Mandatory)]$Launch)
        $fixture = $script:fixture
        $fixture.context = Get-Content -LiteralPath $Launch.ContextPath -Raw | ConvertFrom-Json
        $fixture.provider = [pscustomobject]@{
            name = 'fixture-execution'
            id = '/subscriptions/fixture/resourceGroups/fixture/providers/Microsoft.App/jobs/fixture/executions/fixture-execution'
            properties = [pscustomobject]@{ status = 'Succeeded'; template = $fixture.start }
        }
        $fixture.result = [pscustomobject]@{
            runId = $fixture.context.runId
            mode = $fixture.context.mode
            exitCode = 0
            completedUtc = [DateTime]::UtcNow.ToString('o')
            sourceSnapshot = [pscustomobject]@{ version = 1; verified = $true; digest = $fixture.context.fingerprint.sourceSnapshot.digest; runId = $fixture.context.runId }
            tests = [pscustomobject]@{ total = 12000; executed = 12000; passed = 12000; failed = 0; skipped = 0; fixtureFailures = 0; isComplete = $true; failureReason = $null }
        }
    }
}

AfterAll {
    $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = $script:oldSelector
    Remove-Item -LiteralPath $script:root -Recurse -Force
}

Describe 'Fenced sender qualification and resume' {
    BeforeEach {
        $script:candidate = Join-Path $script:root ([guid]::NewGuid().ToString('N'))
        $names = @('Invoke-AzureBuildTest.ps1', 'Resume-AzureBuildTest.ps1', 'AzureBuildTestRecovery.psm1', 'AzureBuildTestArtifacts.psm1', 'SourceSnapshot.psm1', 'Get-WorktreeValidationFingerprint.ps1', 'RunnerReleaseCompatibility.psm1')
        foreach ($name in $names) {
            $path = Join-Path $script:candidate ('scripts/repo/' + $name)
            [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
            Copy-Item -LiteralPath (Join-Path $script:repository ('scripts/repo/' + $name)) -Destination $path
        }
        foreach ($relative in @('infra/buildtest/RunnerImageProvenance.ps1', 'infra/buildtest/runner/RunnerDiagnostic.ps1')) {
            $path = Join-Path $script:candidate $relative
            [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
            Copy-Item -LiteralPath (Join-Path $script:repository $relative) -Destination $path
        }
        [IO.File]::WriteAllText((Join-Path $script:candidate 'infra/buildtest/runner/entrypoint.ps1'), '# fixture checkout differs from approved image source tag')
        [IO.Directory]::CreateDirectory((Join-Path $script:candidate 'tests/unit/Example.Tests')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $script:candidate 'tests/unit/Example.Tests/Example.Tests.csproj'), '<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>')
        & git -C $script:candidate init --quiet
        if ($LASTEXITCODE) {
            throw 'Fixture init failed.'
        }
        & git -C $script:candidate -c core.autocrlf=false add .
        if ($LASTEXITCODE) {
            throw 'Fixture add failed.'
        }
        $hooks = Join-Path $script:root 'empty-hooks'
        [IO.Directory]::CreateDirectory($hooks) | Out-Null
        & git -C $script:candidate -c user.name=Fixture -c user.email=fixture@example.invalid -c core.hooksPath=$hooks commit --quiet -m fixture
        if ($LASTEXITCODE) {
            throw 'Fixture commit failed.'
        }
        $material = "botnexus-qualified-execution-inputs-v1`0"
        $files = [string[]]@(Get-ChildItem -LiteralPath $script:candidate -Recurse -File | Where-Object FullName -NotMatch '[\\/]\.git[\\/]' | ForEach-Object { [IO.Path]::GetRelativePath($script:candidate, $_.FullName).Replace('\', '/') })
        [Array]::Sort($files, [StringComparer]::Ordinal)
        foreach ($relative in $files) {
            $content = [IO.File]::ReadAllText((Join-Path $script:candidate $relative)).Replace("`r`n", "`n")
            $material += '{0}:{1}{2}:{3}' -f [Text.Encoding]::UTF8.GetByteCount($relative), $relative, [Text.Encoding]::UTF8.GetByteCount($content), $content
        }
        $script:image = 'fixture.azurecr.io/runner@sha256:' + ('b' * 64)
        $approval = [ordered]@{
            schemaVersion = 1; releaseId = 'fixture-qualification-1'; image = $script:image; approved = $true; revoked = $false
            runnerInputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
            modes = @('core', 'strict', 'diagnostic'); payloadVersion = 1; evidenceVersion = 1
            capabilities = @('exact-source-v1', 'diagnostic-containment-v1')
        }
        $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = Join-Path $script:root 'operator.json'
        $approval | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
        $script:fixture = @{
            candidate = $script:candidate; case = 'valid'; approval = $approval; calls = [Collections.Generic.List[string]]::new(); start = $null; context = $null; provider = $null; result = $null
            job = [pscustomobject]@{ properties = [pscustomobject]@{ template = [pscustomobject]@{ containers = @([pscustomobject]@{ image = 'fixture.azurecr.io/runner:operator'; env = @([pscustomobject]@{name = 'AZURE_CLIENT_ID'; value = 'fixture' }) }) } } }
        }
        $fixture = $script:fixture
        Set-Item -Path Function:az -Value (${function:Invoke-FixtureAzure}.GetNewClosure())
        $script:sender = Join-Path $script:candidate 'scripts/repo/Invoke-AzureBuildTest.ps1'
        $script:resume = Join-Path $script:candidate 'scripts/repo/Resume-AzureBuildTest.ps1'
        $script:parameters = @{ WorktreePath = $script:candidate; BaseRef = 'HEAD'; SubscriptionId = 'fixture'; ResourceGroup = 'fixture'; StorageAccount = 'fixturestorage'; JobName = 'fixture'; Mode = 'core'; NoWait = $true; OutputPath = (Join-Path $script:candidate '.git/download') }
    }

    It 'pins approved immutable identity and v2 contexts despite unrelated checkout source tags' {
        $launch = & $script:sender @script:parameters
        $start = $script:fixture.start
        $start.containers[0].image | Should -BeExactly $script:image
        Initialize-SenderResult -Launch $launch
        $context = $script:fixture.context
        $context.version | Should -Be 2
        $context.runnerRelease.image | Should -BeExactly $script:image
        $context.executionConfiguration | Should -BeExactly 'image-entrypoint-v1'
        $startContext = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $launch.ContextPath) ($context.runId + '-start.json')) -Raw | ConvertFrom-Json
        $startContext.version | Should -Be 2
        $startContext.runnerRelease.manifestSha256 | Should -BeExactly $context.runnerRelease.manifestSha256
        & $script:resume -ContextPath $launch.ContextPath -WorktreePath $script:candidate -OutputPath (Join-Path $script:candidate '.git/download') | Should -Not -BeNullOrEmpty
    }

    It 'rejects <Case> before any upload or start' -ForEach @(
        @{ Case = 'missing' }, @{ Case = 'revoked' }, @{ Case = 'unresolved' }, @{ Case = 'command' }, @{ Case = 'args' }, @{ Case = 'wrong-digest' }
    ) {
        $fixture = $script:fixture
        $fixture.case = $Case
        switch ($Case) {
            'missing' {
                $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST = $null
            }
            'revoked' {
                $fixture.approval.revoked = $true; $fixture.approval | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST
            }
            'command' {
                $fixture.job.properties.template.containers[0] | Add-Member -NotePropertyName command -NotePropertyValue @('pwsh', '-Command', 'exit 0')
            }
            'args' {
                $fixture.job.properties.template.containers[0] | Add-Member -NotePropertyName args -NotePropertyValue @('-Command', 'exit 0')
            }
            'wrong-digest' {
                $fixture.job.properties.template.containers[0].image = 'fixture.azurecr.io/runner@sha256:' + ('c' * 64)
            }
        }
        { & $script:sender @script:parameters } | Should -Throw
        @($fixture.calls | Where-Object { $_ -match 'storage blob upload|--method post' }).Count | Should -Be 0
    }

    It 'rejects impacted policy changed <Timing> for <Mode> before unsafe transfer or start' -ForEach @(
        @{ Mode = 'strict'; Timing = 'before-upload' }
        @{ Mode = 'impacted'; Timing = 'before-upload' }
        @{ Mode = 'strict'; Timing = 'after-upload' }
        @{ Mode = 'impacted'; Timing = 'after-upload' }
    ) {
        $fixture = $script:fixture
        $policyPath = Join-Path $script:candidate 'scripts/repo/test-impacted.ps1'
        [IO.File]::WriteAllText($policyPath, '# initial impacted policy')
        $script:parameters.Mode = $Mode
        $fixture.approval.modes = @('core', 'strict', 'impacted', 'diagnostic')
        Import-Module (Join-Path $script:candidate 'scripts/repo/RunnerReleaseCompatibility.psm1') -Force
        $modulePath = Join-Path $script:candidate 'scripts/repo/RunnerReleaseCompatibility.psm1'
        $loadedModule = Get-Module RunnerReleaseCompatibility | Where-Object Path -EQ $modulePath | Select-Object -First 1
        $fixture.approval.runnerInputDigest = & $loadedModule { param($root) Get-RunnerReleaseInputDigest -Root $root } $script:candidate
        $fixture.approval | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -Encoding utf8NoBOM
        # Pre-mutation control proves that the synthetic candidate was qualified.
        Assert-RunnerReleaseCompatibility -ManifestPath $env:BOTNEXUS_RUNNER_RELEASE_MANIFEST -CandidateRoot $script:candidate -DeployedImage $script:image -Mode $Mode | Out-Null
        if ($Timing -eq 'before-upload') {
            [IO.File]::WriteAllText($policyPath, '# changed impacted policy')
        }
        else {
            $fixture.case = 'policy-on-upload'
        }
        { & $script:sender @script:parameters } | Should -Throw '*Execution inputs changed*' -ErrorId 'RunnerReleaseCompatibility*'
        @($fixture.calls | Where-Object { $_ -match '--method post' }).Count | Should -Be 0
        $uploads = @($fixture.calls | Where-Object { $_ -match 'storage blob upload' }).Count
        $uploads | Should -Be $(if ($Timing -eq 'before-upload') {
                0
            }
            else {
                1
            })
    }

    It 'rechecks approval after upload and before start' {
        $script:fixture.case = 'revoke-on-upload'
        { & $script:sender @script:parameters } | Should -Throw
        @($script:fixture.calls | Where-Object { $_ -match '--method post' }).Count | Should -Be 0
    }

    It 'resume rejects <Case> without replacing receipt or starting new work' -ForEach @(
        @{ Case = 'revoke-on-start' }, @{ Case = 'provider-image' }, @{ Case = 'provider-command' }, @{ Case = 'manifest-bytes' }
    ) {
        $fixture = $script:fixture
        $fixture.case = $Case
        $launch = & $script:sender @script:parameters
        Initialize-SenderResult -Launch $launch
        switch ($Case) {
            'provider-image' {
                $fixture.provider.properties.template.containers[0].image = 'fixture.azurecr.io/runner@sha256:' + ('c' * 64)
            }
            'provider-command' {
                $fixture.provider.properties.template.containers[0] | Add-Member -NotePropertyName command -NotePropertyValue @('false')
            }
            'manifest-bytes' {
                [IO.File]::AppendAllText($env:BOTNEXUS_RUNNER_RELEASE_MANIFEST, "`n ")
            }
        }
        $fixture.calls.Clear()
        { & $script:resume -ContextPath $launch.ContextPath -WorktreePath $script:candidate -OutputPath (Join-Path $script:candidate '.git/download') } | Should -Throw
        Test-Path -LiteralPath (Join-Path $script:candidate '.git/botnexus-validation/azure-buildtest.json') | Should -BeFalse
        @($fixture.calls | Where-Object { $_ -match 'upload|--method post|delete' }).Count | Should -Be 0
    }

    It 'normal sender finalization <Case> preserves receipt and diagnostic contracts' -ForEach @(
        @{ Case = 'valid' }, @{ Case = 'provider-image' }, @{ Case = 'provider-command' }, @{ Case = 'revoke-on-start' }, @{ Case = 'strict' }, @{ Case = 'missing-strict' }, @{ Case = 'diagnostic' }, @{ Case = 'diagnostic-image' }
    ) {
        $fixture = $script:fixture
        $fixture.case = if ($Case -eq 'diagnostic-image') {
            'provider-image'
        }
        else {
            $Case
        }
        $script:parameters.NoWait = $false
        $script:parameters.KeepRemoteArtifacts = $true
        if ($Case -in @('strict', 'missing-strict')) {
            $script:parameters.Mode = 'strict'
        }
        if ($Case -in @('diagnostic', 'diagnostic-image')) {
            $script:parameters.Mode = 'diagnostic'
            $script:parameters.DiagnosticProject = 'tests/unit/Example.Tests/Example.Tests.csproj'
        }
        Mock Start-Sleep {}
        $receiptPath = Join-Path $script:candidate '.git/botnexus-validation/azure-buildtest.json'
        if ($Case -in @('valid', 'strict', 'diagnostic')) {
            { & $script:sender @script:parameters } | Should -Not -Throw
            (Test-Path -LiteralPath $receiptPath) | Should -Be ($Case -ne 'diagnostic')
        }
        else {
            { & $script:sender @script:parameters } | Should -Throw
            Test-Path -LiteralPath $receiptPath | Should -BeFalse
        }
    }

    It 'admits qualified diagnostic containment despite source-tag drift but never certifies it' {
        $script:parameters.Mode = 'diagnostic'
        $script:parameters.DiagnosticProject = 'tests/unit/Example.Tests/Example.Tests.csproj'
        $launch = & $script:sender @script:parameters
        Initialize-SenderResult -Launch $launch
        $script:fixture.context.runnerRelease.mode | Should -BeExactly 'diagnostic'
        { & $script:resume -ContextPath $launch.ContextPath -WorktreePath $script:candidate } | Should -Throw
        Test-Path -LiteralPath (Join-Path $script:candidate '.git/botnexus-validation/azure-buildtest.json') | Should -BeFalse
    }
}
