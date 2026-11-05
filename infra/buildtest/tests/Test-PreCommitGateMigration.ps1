<#
.SYNOPSIS
Pins the repository-only breaking migration to the approved validation command.
.DESCRIPTION
Checks canonical paths and computes local receipt policy identity against disposable
synthetic Git. Never invokes a validation delegate, Azure, dotnet or a product host.
.EXAMPLE
Invoke-Pester -Path ./infra/buildtest/tests/Test-PreCommitGateMigration.ps1
.OUTPUTS
None. Pester reports migration and receipt invalidation assertions.
#>
[CmdletBinding()]
param()

BeforeAll {
    $script:repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $script:newGate = 'scripts/repo/Invoke-PreCommitValidation.ps1'
    $script:oldGate = 'scripts/repo/' + 'Validate' + '-PreCommit.ps1'
}

Describe 'Scoped canonical validation gate migration' {
    It 'has one canonical gate and no legacy wrapper or alias file' {
        Test-Path -LiteralPath (Join-Path $script:repository $script:newGate) | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $script:repository $script:oldGate) | Should -BeFalse
    }

    It 'migrates only the authorized gate and artifact fixture entry points' {
        Test-Path -LiteralPath (Join-Path $script:repository 'scripts/repo/Test-PreCommitValidation.ps1') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $script:repository 'scripts/repo/Test-AzureBuildTestArtifacts.ps1') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $script:repository ('scripts/repo/' + 'Validate' + '-PreCommit.Tests.ps1')) | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $script:repository ('scripts/repo/' + 'AzureBuildTestArtifacts' + '.Tests.ps1')) | Should -BeFalse
    }

    It 'binds policy identity to the new canonical path and invalidates pre-migration receipts' {
        $source = [IO.File]::ReadAllText((Join-Path $script:repository 'scripts/repo/ValidationReceipt.psm1'))
        $source | Should -Match ([regex]::Escape($script:newGate))
        $source | Should -Not -Match ([regex]::Escape($script:oldGate))
        if (-not $env:BOTNEXUS_TEST_SCRATCH_ROOT) {
            throw 'Set BOTNEXUS_TEST_SCRATCH_ROOT to owned task scratch.'
        }
        $root = Join-Path $env:BOTNEXUS_TEST_SCRATCH_ROOT ([guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory((Join-Path $root 'scripts/repo')) | Out-Null
        try {
            & git -C $root init --quiet
            if ($LASTEXITCODE) {
                throw 'Fixture init failed.'
            }
            [IO.File]::WriteAllText((Join-Path $root 'candidate.txt'), 'candidate')
            & git -C $root add .
            if ($LASTEXITCODE) {
                throw 'Fixture add failed.'
            }
            $hooks = Join-Path $root '.git/empty-hooks'
            [IO.Directory]::CreateDirectory($hooks) | Out-Null
            & git -C $root -c user.name=Fixture -c user.email=fixture@example.invalid -c core.hooksPath=$hooks commit --quiet -m fixture
            if ($LASTEXITCODE) {
                throw 'Fixture commit failed.'
            }
            Import-Module (Join-Path $script:repository 'scripts/repo/ValidationReceipt.psm1') -Force
            $module = Get-Module ValidationReceipt | Select-Object -First 1
            [IO.File]::WriteAllText((Join-Path $root $script:oldGate), 'policy-v1')
            $legacy = Get-BotNexusValidationIdentity -WorktreePath $root -BaseRef HEAD
            [IO.File]::Move((Join-Path $root $script:oldGate), (Join-Path $root $script:newGate))
            $current = Get-BotNexusValidationIdentity -WorktreePath $root -BaseRef HEAD
            $current.policyHash | Should -Not -BeExactly $legacy.policyHash
            # No staging between identities: the change must be policy, not candidate tree.
            $current.tree | Should -BeExactly $legacy.tree
            [IO.File]::WriteAllText((Join-Path $root $script:newGate), 'policy-v2')
            $changed = Get-BotNexusValidationIdentity -WorktreePath $root -BaseRef HEAD
            $changed.policyHash | Should -Not -BeExactly $current.policyHash
            $changed.tree | Should -BeExactly $current.tree
            $module.Name | Should -BeExactly 'ValidationReceipt'
        }
        finally {
            Remove-Item -LiteralPath $root -Recurse -Force
        }
    }
}
