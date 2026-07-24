#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
.SYNOPSIS
    Pester tests for the content-addressed validation receipt module (issue #2143).

.DESCRIPTION
    Exercises the emit/verify contract end to end against real temporary Git
    repositories: cache hit, and every invalidation reason (staged tree change, policy
    change, toolchain change, malformed, missing, expired, failed/partial, wrong scope),
    interrupted writes, linked worktree sharing, and explicit portable receipt paths.
#>

BeforeAll {
    $script:ModulePath = Join-Path (Split-Path $PSCommandPath -Parent) 'ValidationReceipt.psm1'
    Import-Module $script:ModulePath -Force

    function New-ReceiptTestRepo {
        param([switch]$WithGlobalJson)
        $path = Join-Path ([IO.Path]::GetTempPath()) "botnexus-receipt-test-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $path | Out-Null
        & git -c core.hooksPath= -C $path init --initial-branch main *> $null
        & git -C $path config user.name 'test' *> $null
        & git -C $path config user.email 'test@example.invalid' *> $null
        Set-Content (Join-Path $path 'candidate.txt') 'candidate' -Encoding utf8NoBOM
        # A stand-in policy input so policy-hash sensitivity can be exercised.
        New-Item -ItemType Directory -Path (Join-Path $path 'scripts/repo') -Force | Out-Null
        Set-Content (Join-Path $path 'scripts/repo/Validate-PreCommit.ps1') 'policy-v1' -Encoding utf8NoBOM
        if ($WithGlobalJson) {
            Set-Content (Join-Path $path 'global.json') '{ "sdk": { "version": "10.0.204" } }' -Encoding utf8NoBOM
        }
        & git -C $path add --all *> $null
        & git -c core.hooksPath= -C $path commit -m 'initial' *> $null
        & git -C $path branch 'origin/main' *> $null
        return $path
    }

    function Stage-Change {
        param([string]$Repo, [string]$Content)
        Set-Content (Join-Path $Repo 'candidate.txt') $Content -Encoding utf8NoBOM
        & git -C $Repo add --all *> $null
    }

    function Emit-Receipt {
        param([string]$Repo, [string]$Scope = 'strict', [hashtable]$Extra = @{})
        $params = @{
            Scope = $Scope
            TestProjects = @('BotNexus.Architecture.Tests', 'BotNexus.Scenarios.Tests')
            WorktreePath = $Repo
            AllowDirtyWorkingTree = $true
        }
        foreach ($k in $Extra.Keys) { $params[$k] = $Extra[$k] }
        return New-BotNexusValidationReceipt @params
    }

    $script:TestRepos = [System.Collections.Generic.List[string]]::new()
}

AfterAll {
    foreach ($repo in $script:TestRepos) {
        Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Module ValidationReceipt -ErrorAction SilentlyContinue
}

Describe 'Content-addressed validation receipt' {

    It 'accepts a receipt that matches the exact staged candidate (cache hit)' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        Emit-Receipt -Repo $repo | Should -Not -BeNullOrEmpty
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeTrue
    }

    It 'fails closed when the staged tree changes' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        Emit-Receipt -Repo $repo | Out-Null
        Stage-Change -Repo $repo -Content 'changed'
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeFalse
        $result.Reason | Should -Match 'staged tree'
    }

    It 'fails closed when the validation policy changes' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        Emit-Receipt -Repo $repo | Out-Null
        # Change a policy input without changing the staged candidate tree.
        Set-Content (Join-Path $repo 'scripts/repo/Validate-PreCommit.ps1') 'policy-v2' -Encoding utf8NoBOM
        & git -C $repo add --all *> $null
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeFalse
    }

    It 'fails closed when the toolchain changes' {
        $repo = New-ReceiptTestRepo -WithGlobalJson; $script:TestRepos.Add($repo)
        Emit-Receipt -Repo $repo | Out-Null
        Set-Content (Join-Path $repo 'global.json') '{ "sdk": { "version": "10.0.999" } }' -Encoding utf8NoBOM
        & git -C $repo add --all *> $null
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeFalse
    }

    It 'fails closed when no receipt is present' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeFalse
        $result.Reason | Should -Match 'No validation receipt'
    }

    It 'fails closed on a malformed receipt' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        $receiptPath = Get-BotNexusReceiptPath -WorktreePath $repo
        New-Item -ItemType Directory -Path (Split-Path $receiptPath -Parent) -Force | Out-Null
        Set-Content $receiptPath '{ not valid json' -Encoding utf8NoBOM
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeFalse
        $result.Reason | Should -Match 'malformed'
    }

    It 'fails closed on a partial receipt missing required fields' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        $receiptPath = Get-BotNexusReceiptPath -WorktreePath $repo
        New-Item -ItemType Directory -Path (Split-Path $receiptPath -Parent) -Force | Out-Null
        '{ "schemaVersion": 2 }' | Set-Content $receiptPath -Encoding utf8NoBOM
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeFalse
        $result.Reason | Should -Match 'missing required field'
    }

    It 'fails closed on an expired receipt' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        Emit-Receipt -Repo $repo -Extra @{ Ttl = [TimeSpan]::FromMilliseconds(1) } | Out-Null
        Start-Sleep -Milliseconds 30
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeFalse
        $result.Reason | Should -Match 'expired'
    }

    It 'fails closed when the receipt scope is not required' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        Emit-Receipt -Repo $repo -Scope 'impacted' | Out-Null
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo -RequiredScopes @('strict', 'full')
        $result.Match | Should -BeFalse
        $result.Reason | Should -Match 'scope'
    }

    It 'fails closed when build or test result is not success' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        $receiptPath = (Emit-Receipt -Repo $repo).Path
        $json = Get-Content $receiptPath -Raw | ConvertFrom-Json
        $json.testResult = 'failed'
        $json | ConvertTo-Json -Depth 6 | Set-Content $receiptPath -Encoding utf8NoBOM
        $result = Test-BotNexusValidationReceipt -WorktreePath $repo
        $result.Match | Should -BeFalse
    }

    It 'does not emit a reusable receipt when the working tree is dirty' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        # Modify a tracked file without staging it - unstaged input could change validation.
        Set-Content (Join-Path $repo 'candidate.txt') 'unstaged-edit' -Encoding utf8NoBOM
        $emit = New-BotNexusValidationReceipt -Scope strict -TestProjects @('x') -WorktreePath $repo -WarningAction SilentlyContinue
        $emit | Should -BeNullOrEmpty
    }

    It 'writes receipts atomically leaving no temp file behind' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        $emit = Emit-Receipt -Repo $repo
        $dir = Split-Path $emit.Path -Parent
        (Get-ChildItem $dir -Filter '*.tmp' | Measure-Object).Count | Should -Be 0
    }

    It 'shares receipts across linked worktrees via the git common dir' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        Emit-Receipt -Repo $repo | Out-Null
        $linked = Join-Path ([IO.Path]::GetTempPath()) "botnexus-receipt-linked-$([Guid]::NewGuid().ToString('N'))"
        $script:TestRepos.Add($linked)
        & git -C $repo worktree add $linked HEAD *> $null
        try {
            # A linked worktree resolves the same receipt path (common dir), so the primary
            # path and the linked path point at the same file.
            $primary = Get-BotNexusReceiptPath -WorktreePath $repo
            $fromLinked = Get-BotNexusReceiptPath -WorktreePath $linked
            $fromLinked | Should -Be $primary
        }
        finally {
            & git -C $repo worktree remove $linked --force *> $null
        }
    }

    It 'honours an explicit portable receipt path for container/CI exchange' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        $portable = Join-Path ([IO.Path]::GetTempPath()) "botnexus-portable-$([Guid]::NewGuid().ToString('N')).json"
        try {
            Emit-Receipt -Repo $repo -Extra @{ Path = $portable } | Out-Null
            Test-Path $portable | Should -BeTrue
            $result = Test-BotNexusValidationReceipt -WorktreePath $repo -Path $portable
            $result.Match | Should -BeTrue
        }
        finally {
            Remove-Item $portable -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Remove-BotNexusValidationReceipt invalidates an in-progress receipt' {
        $repo = New-ReceiptTestRepo; $script:TestRepos.Add($repo)
        Emit-Receipt -Repo $repo | Out-Null
        Remove-BotNexusValidationReceipt -WorktreePath $repo
        (Test-BotNexusValidationReceipt -WorktreePath $repo).Match | Should -BeFalse
    }
}
