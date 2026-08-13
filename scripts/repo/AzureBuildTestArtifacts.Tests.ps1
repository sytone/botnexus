#Requires -Modules Pester
# Regression coverage for issue #3115: Invoke-AzureBuildTest.ps1 downloaded run artifacts into
# artifacts/azure-buildtest/<runId>/<runId>/ while advertising artifacts/azure-buildtest/<runId>/
# on success, so the result contract did not resolve at the path the script itself printed.
#
# Root cause (verified in source, not assumed): `az storage blob download-batch` reproduces the
# full blob NAME under --destination. The artifact blobs are named "<runId>/result.json", and
# --destination was $OutputPath, which ALREADY ended in the run id. The prefix was applied twice.
#
# These tests exercise the real filesystem against a fake blob download - the thing under test is
# the placement of files on disk, so mocking the filesystem would prove nothing.

BeforeAll {
    $script:ModulePath = Join-Path $PSScriptRoot 'AzureBuildTestArtifacts.psm1'
    Import-Module $script:ModulePath -Force

    $script:ScriptPath = Join-Path $PSScriptRoot 'Invoke-AzureBuildTest.ps1'

    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ("bn3115-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Scratch -Force | Out-Null

    # Reproduces what `az storage blob download-batch --destination <root> --pattern "<runId>/*"`
    # actually writes: every blob laid down under its own full name, run-id prefix included.
    function New-DownloadedBlobTree {
        param([Parameter(Mandatory)][string]$StagingRoot, [Parameter(Mandatory)][string]$RunId)
        $prefixed = Join-Path $StagingRoot $RunId
        New-Item -ItemType Directory -Path (Join-Path $prefixed 'test-results') -Force | Out-Null
        Set-Content -Path (Join-Path $prefixed 'result.json') -Value '{"exitCode":0,"tests":{"total":13,"isComplete":true}}' -Encoding utf8NoBOM
        Set-Content -Path (Join-Path $prefixed 'test-result.json') -Value '{}' -Encoding utf8NoBOM
        Set-Content -Path (Join-Path $prefixed 'build.log') -Value 'build ok' -Encoding utf8NoBOM
        Set-Content -Path (Join-Path $prefixed 'test-results/Domain.Tests.trx') -Value '<TestRun />' -Encoding utf8NoBOM
        return $prefixed
    }

    function New-Case {
        param([string]$RunId = '20260814120000-abcd1234')
        $case = Join-Path $script:Scratch ("case-" + [Guid]::NewGuid().ToString('N'))
        $staging = Join-Path $case 'staging'
        $destination = Join-Path $case "artifacts/azure-buildtest/$RunId"
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        New-DownloadedBlobTree -StagingRoot $staging -RunId $RunId | Out-Null
        [pscustomobject]@{ RunId = $RunId; Staging = $staging; Destination = $destination }
    }
}

AfterAll {
    if (Test-Path $script:Scratch) { Remove-Item $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue }
}

Describe 'Move-AzureBuildTestArtifacts' {
    It 'places result.json directly in the run directory and it parses as JSON (AC1)' {
        $case = New-Case
        $returned = Move-AzureBuildTestArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination

        $contract = Join-Path $case.Destination 'result.json'
        Test-Path -LiteralPath $contract -PathType Leaf | Should -BeTrue
        { Get-Content -LiteralPath $contract -Raw | ConvertFrom-Json } | Should -Not -Throw
        (Get-Content -LiteralPath $contract -Raw | ConvertFrom-Json).exitCode | Should -Be 0
        $returned | Should -Be $case.Destination
    }

    It 'does not create a duplicated runId-inside-runId directory (AC2, AC4 mutation target)' {
        $case = New-Case
        Move-AzureBuildTestArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination | Out-Null

        # This is the assertion that reddens if the run-id prefix is ever applied twice again.
        Test-Path -LiteralPath (Join-Path $case.Destination $case.RunId) | Should -BeFalse
        @(Get-ChildItem -LiteralPath $case.Destination -Recurse -Filter 'result.json').Count | Should -Be 1
    }

    It 'returns the directory that directly contains result.json, so the printed path cannot drift (AC3)' {
        $case = New-Case
        $reported = Move-AzureBuildTestArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination

        # The success line prints $OutputPath, which is reassigned from this return value.
        Test-Path -LiteralPath (Join-Path $reported 'result.json') -PathType Leaf | Should -BeTrue
    }

    It 'keeps test-results/*.trx reachable under the same run directory (AC5)' {
        $case = New-Case
        $reported = Move-AzureBuildTestArtifacts -StagingRoot $case.Staging -RunId $case.RunId -Destination $case.Destination

        @(Get-ChildItem -Path (Join-Path $reported 'test-results') -Filter '*.trx').Count | Should -Be 1
        Test-Path -LiteralPath (Join-Path $reported 'test-results/Domain.Tests.trx') -PathType Leaf | Should -BeTrue
    }

    It 'is a no-op-safe passthrough when the blob prefix is absent' {
        # If the prefix is ever dropped upstream, flattening must not empty the run directory.
        $case = New-Case
        $flat = Join-Path $script:Scratch ("flat-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $flat -Force | Out-Null
        Set-Content -Path (Join-Path $flat 'result.json') -Value '{"exitCode":0}' -Encoding utf8NoBOM

        $reported = Move-AzureBuildTestArtifacts -StagingRoot $flat -RunId $case.RunId -Destination $case.Destination
        Test-Path -LiteralPath (Join-Path $reported 'result.json') -PathType Leaf | Should -BeTrue
    }
}

Describe 'Invoke-AzureBuildTest.ps1 artifact wiring' {
    It 'downloads into a staging root rather than the advertised output directory (AC3)' {
        $source = Get-Content -LiteralPath $script:ScriptPath -Raw
        $source | Should -Match '--destination \$downloadStaging'
        $source | Should -Not -Match '--destination \$OutputPath'
    }

    It 'reports the same variable it flattened the artifacts into (AC3)' {
        $source = Get-Content -LiteralPath $script:ScriptPath -Raw
        $source | Should -Match '\$OutputPath = Move-AzureBuildTestArtifacts'
        $source | Should -Match 'Azure validation passed\. Artifacts: \$OutputPath'
    }

    It 'reads the result contract non-recursively so a nested copy cannot be accepted (AC2)' {
        $source = Get-Content -LiteralPath $script:ScriptPath -Raw
        $source | Should -Not -Match 'Filter result\.json -Recurse'
    }
}
