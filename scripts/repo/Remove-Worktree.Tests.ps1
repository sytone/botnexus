#Requires -Modules Pester
# Copyright (c) Microsoft Corporation. All rights reserved.

BeforeAll {
    . (Join-Path $PSScriptRoot 'Remove-Worktree.ps1')

    # -----------------------------------------------------------------------
    # Test harness: a fake git invoker that scripts responses per subcommand and
    # records the ordered call log so we can assert prune/branch ordering.
    # -----------------------------------------------------------------------
    function New-FakeGit {
        param(
            [scriptblock]$RemoveResponse,   # param($attempt) -> @{exitCode;output}
            [string]$Branch = 'fix/2104-worktree-locks',
            [scriptblock]$PruneResponse = { @{ exitCode = 0; output = '' } }
        )
        $state = [ordered]@{
            calls       = [Collections.Generic.List[string]]::new()
            removeCalls = 0
            removeResp  = $RemoveResponse
            branch      = $Branch
            pruneResp   = $PruneResponse
        }
        $invoker = {
            param([string[]]$GitArgs)
            $joined = ($GitArgs -join ' ')
            $state.calls.Add($joined) | Out-Null
            if ($joined -match 'worktree remove') {
                $state.removeCalls++
                return (& $state.removeResp $state.removeCalls)
            }
            if ($joined -match 'worktree prune') {
                return (& $state.pruneResp)
            }
            if ($joined -match 'rev-parse --abbrev-ref HEAD') {
                return @{ exitCode = 0; output = "$($state.branch)`n" }
            }
            return @{ exitCode = 0; output = '' }
        }.GetNewClosure()
        return @{ invoker = $invoker; state = $state }
    }

    function New-TempWorktreeDir {
        $p = Join-Path ([IO.Path]::GetTempPath()) ("wt-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $p -Force | Out-Null
        return $p
    }

    function New-MetadataFixture {
        $root = New-TempWorktreeDir
        $repo = Join-Path $root 'repo'
        $wt = Join-Path $root 'wt'
        $meta = Join-Path (Join-Path (Join-Path $repo '.git') 'worktrees') 'wt'
        New-Item -ItemType Directory -Path $meta, $wt -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $wt '.git') -Value "gitdir: $meta" -Encoding utf8
        return @{ Root = $root; Repo = $repo; Worktree = $wt; Meta = $meta }
    }

    function Get-CallIndex {
        param([object]$State, [string]$Pattern)
        $arr = @($State.calls)
        for ($i = 0; $i -lt $arr.Count; $i++) {
            if ($arr[$i] -match $Pattern) { return $i }
        }
        return -1
    }

    function Get-CallCount {
        param([object]$State, [string]$Pattern)
        return @(@($State.calls) | Where-Object { $_ -match $Pattern }).Count
    }

    # A stand-in "repo root" that is never actually touched (git is faked).
    $script:RepoRoot = Join-Path ([IO.Path]::GetTempPath()) 'fake-repo-root-2349'
}

Describe 'Remove-WorktreeSafely' {

    Context 'prune and metadata verification (issue #4856)' {
        It 'fails closed when prune fails even if metadata is <MetadataState>' -ForEach @(
            @{ MetadataState = 'retained'; RemoveMetadata = $false }
            @{ MetadataState = 'gone'; RemoveMetadata = $true }
        ) {
            $fixture = New-MetadataFixture
            try {
                $pruneResponse = {
                    if ($RemoveMetadata) { Remove-Item -LiteralPath $fixture.Meta -Recurse -Force }
                    @{ exitCode = 1; output = 'fatal: injected prune failure' }
                }.GetNewClosure()
                $fake = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 0; output = '' } } -PruneResponse $pruneResponse
                $result = Remove-WorktreeSafely -RepoRoot $fixture.Repo -WorktreePath $fixture.Worktree -DeleteBranch -GitInvoker $fake.invoker -DirectoryRemover { param($p) Remove-Item -LiteralPath $p -Recurse -Force } -LockerProbe { param($p) @() } -Sleeper { param($ms) }

                $result.outcome | Should -Be 'error'
                $result.pruned | Should -BeFalse
                $result.metadataGone | Should -Be $RemoveMetadata
                $result.branchDeleted | Should -BeFalse
                $result.reclaimed | Should -BeFalse
                $result.lastError | Should -Match 'injected prune failure'
                $result.path | Should -Be $fixture.Worktree
                $result.branch | Should -Be 'fix/2104-worktree-locks'
                $result.metadataName | Should -Be 'wt'
                Test-Path -LiteralPath $fixture.Worktree | Should -BeFalse
                Get-CallCount -State $fake.state -Pattern 'worktree prune' | Should -Be 1
                Get-CallCount -State $fake.state -Pattern 'branch -D' | Should -Be 0
            }
            finally { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
        }

        It 'fails closed when successful prune leaves metadata behind' {
            $fixture = New-MetadataFixture
            try {
                $fake = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 0; output = '' } }
                $result = Remove-WorktreeSafely -RepoRoot $fixture.Repo -WorktreePath $fixture.Worktree -DeleteBranch -GitInvoker $fake.invoker -DirectoryRemover { param($p) Remove-Item -LiteralPath $p -Recurse -Force } -LockerProbe { param($p) @() } -Sleeper { param($ms) }

                $result.outcome | Should -Be 'error'
                $result.pruned | Should -BeTrue
                $result.metadataGone | Should -BeFalse
                $result.branchDeleted | Should -BeFalse
                $result.reclaimed | Should -BeFalse
                $result.lastError | Should -Match 'metadata'
                $result.lastError | Should -Match ([regex]::Escape($fixture.Meta))
                Test-Path -LiteralPath $fixture.Worktree | Should -BeFalse
                Test-Path -LiteralPath $fixture.Meta | Should -BeTrue
                Get-CallCount -State $fake.state -Pattern 'worktree prune' | Should -Be 1
                Get-CallCount -State $fake.state -Pattern 'branch -D' | Should -Be 0
            }
            finally { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
        }

        It 'deletes the branch only after successful prune removes metadata' {
            $fixture = New-MetadataFixture
            try {
                $pruneResponse = {
                    Test-Path -LiteralPath $fixture.Worktree | Should -BeFalse
                    Remove-Item -LiteralPath $fixture.Meta -Recurse -Force
                    @{ exitCode = 0; output = '' }
                }.GetNewClosure()
                $fake = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 0; output = '' } } -PruneResponse $pruneResponse
                $result = Remove-WorktreeSafely -RepoRoot $fixture.Repo -WorktreePath $fixture.Worktree -DeleteBranch -GitInvoker $fake.invoker -DirectoryRemover { param($p) Remove-Item -LiteralPath $p -Recurse -Force } -LockerProbe { param($p) @() } -Sleeper { param($ms) }

                $result.outcome | Should -Be 'removed'
                $result.pruned | Should -BeTrue
                $result.metadataGone | Should -BeTrue
                $result.branchDeleted | Should -BeTrue
                Test-Path -LiteralPath $fixture.Meta | Should -BeFalse
                Get-CallCount -State $fake.state -Pattern 'branch -D' | Should -Be 1
                (Get-CallIndex -State $fake.state -Pattern 'worktree prune') | Should -BeLessThan (Get-CallIndex -State $fake.state -Pattern 'branch -D')
            }
            finally { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
        }

        It 'does not claim reclaimed when stale-lock removal succeeds but <Failure> fails' -ForEach @(
            @{ Failure = 'prune'; PruneExitCode = 1 }
            @{ Failure = 'metadata verification'; PruneExitCode = 0 }
        ) {
            $fixture = New-MetadataFixture
            try {
                Set-Content -LiteralPath (Join-Path $fixture.Meta 'locked') -Value 'injected stale lock'
                Mock Get-WorktreeLockOwnerState { 'Dead' }
                $pruneResponse = { @{ exitCode = $PruneExitCode; output = 'injected stale cleanup failure' } }.GetNewClosure()
                $fake = New-FakeGit -RemoveResponse {
                    param($n)
                    if ($n -eq 1) { return @{ exitCode = 1; output = 'fatal: worktree is locked' } }
                    @{ exitCode = 0; output = '' }
                } -PruneResponse $pruneResponse
                $result = Remove-WorktreeSafely -RepoRoot $fixture.Repo -WorktreePath $fixture.Worktree -DeleteBranch -MaxRetries 0 -GitInvoker $fake.invoker -DirectoryRemover { param($p) Remove-Item -LiteralPath $p -Recurse -Force } -LockerProbe { param($p) @() } -Sleeper { param($ms) }

                $result.outcome | Should -Be 'error'
                $result.ownerState | Should -Be 'Dead'
                $result.reclaimAttempted | Should -BeTrue
                $result.reclaimed | Should -BeFalse
                $result.attempts | Should -Be 2
                $result.pruned | Should -Be ($PruneExitCode -eq 0)
                $result.metadataGone | Should -BeFalse
                $result.branchDeleted | Should -BeFalse
                Test-Path -LiteralPath $fixture.Worktree | Should -BeFalse
                Test-Path -LiteralPath (Join-Path $fixture.Meta 'locked.reclaim') | Should -BeFalse
                Get-CallCount -State $fake.state -Pattern 'worktree unlock' | Should -Be 1
                Get-CallCount -State $fake.state -Pattern 'branch -D' | Should -Be 0
            }
            finally { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
        }
    }

    Context 'when the worktree is persistently locked' {
        BeforeAll {
            $script:lockedWt = New-TempWorktreeDir
            $script:lockedFake = New-FakeGit -RemoveResponse {
                param($n)
                @{ exitCode = 1; output = 'fatal: ... : The process cannot access the file because it is being used by another process' }
            }
            $script:lockedResult = Remove-WorktreeSafely -RepoRoot $script:RepoRoot -WorktreePath $script:lockedWt -DeleteBranch `
                -MaxRetries 3 -BaseDelayMs 1 `
                -GitInvoker $script:lockedFake.invoker `
                -DirectoryRemover { param($p) throw 'should not remove dir on locked' } `
                -LockerProbe { param($p) @(@{ pid = 123; name = 'node' }) } `
                -Sleeper { param($ms) }
        }
        AfterAll {
            Remove-Item -LiteralPath $script:lockedWt -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'returns a structured locked outcome' {
            $script:lockedResult.outcome | Should -Be 'locked'
        }

        It 'retains the worktree path' {
            $script:lockedResult.path | Should -Be $script:lockedWt
        }

        It 'retains the branch name so the caller can record-and-skip' {
            $script:lockedResult.branch | Should -Be 'fix/2104-worktree-locks'
        }

        It 'reports likely lockers' {
            $script:lockedResult.ContainsKey('likelyLockers') | Should -BeTrue
            @($script:lockedResult.likelyLockers).Count | Should -BeGreaterOrEqual 1
        }

        It 'does not report the branch as deleted' {
            $script:lockedResult.branchDeleted | Should -BeFalse
        }

        It 'never invokes git branch -D when removal failed' {
            Get-CallCount -State $script:lockedFake.state -Pattern 'branch -D' | Should -Be 0
        }

        It 'never prunes when removal failed' {
            Get-CallCount -State $script:lockedFake.state -Pattern 'worktree prune' | Should -Be 0
            $script:lockedResult.pruned | Should -BeFalse
        }
    }

    Context 'bounded retry with exponential backoff' {
        BeforeAll {
            $script:delays = [Collections.Generic.List[int]]::new()
            $script:retryWt = New-TempWorktreeDir
            $script:retryFake = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 1; output = 'Access is denied' } }
            $script:retryResult = Remove-WorktreeSafely -RepoRoot $script:RepoRoot -WorktreePath $script:retryWt `
                -MaxRetries 3 -BaseDelayMs 200 `
                -GitInvoker $script:retryFake.invoker `
                -DirectoryRemover { param($p) } `
                -LockerProbe { param($p) @() } `
                -Sleeper { param($ms) $script:delays.Add($ms) | Out-Null }
        }
        AfterAll {
            Remove-Item -LiteralPath $script:retryWt -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'attempts exactly MaxRetries + 1 times' {
            $script:retryResult.attempts | Should -Be 4
        }

        It 'backs off once between each failed attempt' {
            $script:delays.Count | Should -Be 3
        }

        It 'uses an exponential 200/400/800 backoff schedule' {
            $script:delays[0] | Should -Be 200
            $script:delays[1] | Should -Be 400
            $script:delays[2] | Should -Be 800
        }
    }

    Context 'when a transient lock clears on retry' {
        BeforeAll {
            $script:okWt = New-TempWorktreeDir
            $script:okFake = New-FakeGit -RemoveResponse {
                param($n)
                if ($n -lt 2) { return @{ exitCode = 1; output = 'being used by another process' } }
                return @{ exitCode = 0; output = '' }
            }
            $script:okResult = Remove-WorktreeSafely -RepoRoot $script:RepoRoot -WorktreePath $script:okWt -DeleteBranch `
                -MaxRetries 4 -BaseDelayMs 1 `
                -GitInvoker $script:okFake.invoker `
                -DirectoryRemover { param($p) Remove-Item -LiteralPath $p -Recurse -Force } `
                -LockerProbe { param($p) @() } `
                -Sleeper { param($ms) }
        }
        AfterAll {
            Remove-Item -LiteralPath $script:okWt -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'ends in the removed outcome' {
            $script:okResult.outcome | Should -Be 'removed'
        }

        It 'succeeds on the second attempt' {
            $script:okResult.attempts | Should -Be 2
        }

        It 'prunes after the directory is actually gone' {
            $script:okResult.pruned | Should -BeTrue
            Get-CallCount -State $script:okFake.state -Pattern 'worktree prune' | Should -Be 1
        }

        It 'deletes the branch only after a fully successful removal' {
            $script:okResult.branchDeleted | Should -BeTrue
        }

        It 'prunes before deleting the branch' {
            $pruneIdx = Get-CallIndex -State $script:okFake.state -Pattern 'worktree prune'
            $branchIdx = Get-CallIndex -State $script:okFake.state -Pattern 'branch -D'
            $pruneIdx | Should -BeGreaterOrEqual 0
            $branchIdx | Should -BeGreaterOrEqual 0
            $pruneIdx | Should -BeLessThan $branchIdx
        }
    }

    Context 'when the directory survives a git-reported success' {
        BeforeAll {
            $script:survWt = New-TempWorktreeDir
            $script:survFake = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 0; output = '' } }
            $script:survResult = Remove-WorktreeSafely -RepoRoot $script:RepoRoot -WorktreePath $script:survWt -DeleteBranch `
                -GitInvoker $script:survFake.invoker `
                -DirectoryRemover { param($p) } `
                -LockerProbe { param($p) @() } `
                -Sleeper { param($ms) }
        }
        AfterAll {
            Remove-Item -LiteralPath $script:survWt -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'downgrades the outcome to locked' {
            $script:survResult.outcome | Should -Be 'locked'
        }

        It 'does not prune' {
            Get-CallCount -State $script:survFake.state -Pattern 'worktree prune' | Should -Be 0
        }

        It 'does not delete the branch' {
            Get-CallCount -State $script:survFake.state -Pattern 'branch -D' | Should -Be 0
        }
    }

    Context 'when git fails for a non-lock reason' {
        BeforeAll {
            $script:dirtyWt = New-TempWorktreeDir
            $script:dirtyFake = New-FakeGit -RemoveResponse {
                param($n)
                @{ exitCode = 1; output = "fatal: contains modified or untracked files, use --force to delete it" }
            }
            $script:dirtyResult = Remove-WorktreeSafely -RepoRoot $script:RepoRoot -WorktreePath $script:dirtyWt -DeleteBranch `
                -MaxRetries 4 -BaseDelayMs 1 `
                -GitInvoker $script:dirtyFake.invoker `
                -DirectoryRemover { param($p) } `
                -LockerProbe { param($p) @() } `
                -Sleeper { param($ms) }
        }
        AfterAll {
            Remove-Item -LiteralPath $script:dirtyWt -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'returns the error outcome' {
            $script:dirtyResult.outcome | Should -Be 'error'
        }

        It 'does not retry' {
            $script:dirtyResult.attempts | Should -Be 1
        }

        It 'must not delete the branch' {
            Get-CallCount -State $script:dirtyFake.state -Pattern 'branch -D' | Should -Be 0
        }
    }

    Context 'when the worktree is on a detached HEAD' {
        BeforeAll {
            $script:detWt = New-TempWorktreeDir
            # git reports 'HEAD' for a detached checkout; the subject maps that to $null.
            $script:detFake = New-FakeGit -Branch 'HEAD' -RemoveResponse { param($n) @{ exitCode = 0; output = '' } }
            $script:detResult = Remove-WorktreeSafely -RepoRoot $script:RepoRoot -WorktreePath $script:detWt -DeleteBranch `
                -GitInvoker $script:detFake.invoker `
                -DirectoryRemover { param($p) Remove-Item -LiteralPath $p -Recurse -Force } `
                -LockerProbe { param($p) @() } `
                -Sleeper { param($ms) }
        }
        AfterAll {
            Remove-Item -LiteralPath $script:detWt -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'still removes the worktree' {
            $script:detResult.outcome | Should -Be 'removed'
        }

        It 'resolves no branch' {
            $script:detResult.branch | Should -BeNullOrEmpty
        }

        It 'must not invoke git branch -D with an empty branch name' {
            Get-CallCount -State $script:detFake.state -Pattern 'branch -D' | Should -Be 0
            $script:detResult.branchDeleted | Should -BeFalse
        }
    }

    Context 'when asked to remove the main working tree' {
        It 'refuses with an error outcome' {
            $result = Remove-WorktreeSafely -RepoRoot $script:RepoRoot -WorktreePath $script:RepoRoot `
                -GitInvoker { param($a) @{ exitCode = 0; output = '' } } `
                -DirectoryRemover { param($p) } -LockerProbe { param($p) @() } -Sleeper { param($ms) }
            $result.outcome | Should -Be 'error'
        }

        It 'never invokes git at all' {
            $fake = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 0; output = '' } }
            $null = Remove-WorktreeSafely -RepoRoot $script:RepoRoot -WorktreePath $script:RepoRoot `
                -GitInvoker $fake.invoker `
                -DirectoryRemover { param($p) } -LockerProbe { param($p) @() } -Sleeper { param($ms) }
            $fake.state.calls.Count | Should -Be 0
        }
    }
}
