#Requires -Modules Pester
# Copyright (c) Microsoft Corporation. All rights reserved.

BeforeAll {
    . (Join-Path $PSScriptRoot 'WorktreeOrphanAudit.ps1')

    function New-TempRoot {
        $p = Join-Path ([IO.Path]::GetTempPath()) ("wtroot-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $p -Force | Out-Null
        return $p
    }

    function New-Dir {
        param([string]$Root, [string]$Name)
        $p = Join-Path $Root $Name
        New-Item -ItemType Directory -Path $p -Force | Out-Null
        return $p
    }

    function New-File {
        param([string]$Dir, [string]$Relative, [string]$Content = 'x')
        $full = Join-Path $Dir $Relative
        $parent = Split-Path -Parent $full
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Set-Content -LiteralPath $full -Value $Content -NoNewline -Encoding utf8
        return $full
    }

    # Fake `git` covering the batched probe contract (#3722).
    function New-RegistryGit {
        param([string[]]$RegisteredPaths, [string[]]$KnownBlobs = @())
        $state = [ordered]@{ calls = [Collections.Generic.List[string]]::new(); blobs = @($KnownBlobs) }
        $paths = @($RegisteredPaths)
        $invoker = {
            param([string[]]$GitArgs, [string]$StdIn)
            $joined = ($GitArgs -join ' ')
            $state.calls.Add($joined) | Out-Null
            if ($joined -match 'worktree list') {
                $lines = foreach ($p in $paths) { "worktree $($p -replace '\\','/')"; 'HEAD 0000000000000000000000000000000000000000'; '' }
                return @{ exitCode = 0; output = ($lines -join "`n") }
            }
            if ($joined -match 'hash-object') {
                # Deterministic pseudo-hash per input path, one line per path.
                $in = @($StdIn -split "`r?`n" | Where-Object { $_ })
                $out = foreach ($f in $in) { "blob-$([IO.Path]::GetFileName($f))" }
                return @{ exitCode = 0; output = ($out -join "`n") }
            }
            if ($joined -match 'cat-file --batch-check') {
                $in = @($StdIn -split "`r?`n" | Where-Object { $_ })
                $out = foreach ($h in $in) {
                    if ($state.blobs -contains $h) { "$h blob 42" } else { "$h missing" }
                }
                return @{ exitCode = 0; output = ($out -join "`n") }
            }
            return @{ exitCode = 0; output = '' }
        }.GetNewClosure()
        return @{ invoker = $invoker; state = $state }
    }
}

Describe 'Get-WorktreeOrphanAudit' {

    Context 'AC1 - unregistered directories are reported, not skipped' {
        BeforeAll {
            $script:root = New-TempRoot
            New-Dir -Root $script:root -Name 'fix-1-registered' | Out-Null
            $orphan = New-Dir -Root $script:root -Name 'feat-hotpath-metrics-v2'
            New-File -Dir $orphan -Relative 'src/Thing.cs' -Content 'code' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @((Join-Path $script:root 'fix-1-registered'))
            $script:audit = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker
        }
        AfterAll { Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue }

        It 'reports the unregistered directory as an orphan' {
            @($script:audit.orphans).Count | Should -Be 1
            $script:audit.orphans[0].name | Should -Be 'feat-hotpath-metrics-v2'
        }

        It 'does not report the registered worktree as an orphan' {
            @($script:audit.orphans | Where-Object { $_.name -eq 'fix-1-registered' }).Count | Should -Be 0
        }

        It 'counts registered worktrees and filesystem directories separately' {
            $script:audit.registeredCount | Should -Be 1
            $script:audit.directoryCount | Should -Be 2
            $script:audit.orphanCount | Should -Be 1
        }

        It 'matches registered paths case-insensitively and regardless of slash direction' {
            $mixed = ($script:root -replace '\\', '/').ToUpperInvariant()
            $fake2 = New-RegistryGit -RegisteredPaths @("$mixed/FIX-1-REGISTERED")
            $a2 = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake2.invoker
            @($a2.orphans | Where-Object { $_.name -eq 'fix-1-registered' }).Count | Should -Be 0
        }
    }

    Context 'AC4 - byte totals are reported' {
        BeforeAll {
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'orphan-bytes'
            New-File -Dir $d -Relative 'a.txt' -Content ('x' * 100) | Out-Null
            New-File -Dir $d -Relative 'obj/big.dll' -Content ('y' * 400) | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @()
            $script:audit = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker
        }
        AfterAll { Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue }

        It 'reports total bytes held under the worktree root' {
            $script:audit.totalBytes | Should -Be 500
        }

        It 'reports bytes held by orphans specifically' {
            $script:audit.orphanBytes | Should -Be 500
        }

        It 'reports per-orphan bytes including build output' {
            $script:audit.orphans[0].bytes | Should -Be 500
        }

        It 'separates non-build bytes from build output' {
            $script:audit.orphans[0].nonBuildBytes | Should -Be 100
            $script:audit.orphans[0].nonBuildFiles | Should -Be 1
        }
    }

    Context 'AC2 - disposition is computed, never hardcoded' {
        AfterEach {
            if ($script:root -and (Test-Path $script:root)) { Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'dispositions a build-output-only directory as reclaimable' {
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'artifacts-only'
            New-File -Dir $d -Relative 'obj/x.dll' | Out-Null
            New-File -Dir $d -Relative 'node_modules/pkg/index.js' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @()
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker
            $a.orphans[0].disposition | Should -Be 'reclaimable'
            $a.orphans[0].reason | Should -Match 'build output'
        }

        It 'dispositions a directory whose non-build content is all in the object store as reclaimable' {
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'all-recoverable'
            New-File -Dir $d -Relative 'src/A.cs' | Out-Null
            New-File -Dir $d -Relative 'src/B.cs' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @() -KnownBlobs @('blob-A.cs', 'blob-B.cs')
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker
            $a.orphans[0].disposition | Should -Be 'reclaimable'
            $a.orphans[0].unreconstructableCount | Should -Be 0
        }

        It 'preserves a directory holding content absent from the object store, and names the reason' {
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'has-untracked'
            New-File -Dir $d -Relative 'src/A.cs' | Out-Null
            New-File -Dir $d -Relative 'infra/network.bicep' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @() -KnownBlobs @('blob-A.cs')
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker
            $a.orphans[0].disposition | Should -Be 'preserved'
            $a.orphans[0].unreconstructableCount | Should -Be 1
            $a.orphans[0].reason | Should -Match 'not present in the object store'
            $a.orphans[0].unreconstructable | Should -Contain 'infra/network.bicep'
        }

        It 'fails closed to preserved when the object-store probe itself errors' {
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'probe-broken'
            New-File -Dir $d -Relative 'src/A.cs' | Out-Null
            $broken = {
                param([string[]]$GitArgs, [string]$StdIn)
                $joined = ($GitArgs -join ' ')
                if ($joined -match 'worktree list') { return @{ exitCode = 0; output = '' } }
                if ($joined -match 'hash-object') { return @{ exitCode = 128; output = 'fatal: boom' } }
                return @{ exitCode = 0; output = '' }
            }
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $broken
            $a.orphans[0].disposition | Should -Be 'preserved'
        }

        It 'fails closed when the hash batch returns fewer lines than files supplied' {
            # A truncated batch destroys the path<->hash correspondence. Trusting
            # a positional match there would mark a file recoverable on the
            # strength of a DIFFERENT file's hash - the exact way a false
            # 'reclaimable' causes permanent data loss.
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'short-batch'
            New-File -Dir $d -Relative 'src/A.cs' | Out-Null
            New-File -Dir $d -Relative 'src/B.cs' | Out-Null
            $short = {
                param([string[]]$GitArgs, [string]$StdIn)
                $joined = ($GitArgs -join ' ')
                if ($joined -match 'worktree list') { return @{ exitCode = 0; output = '' } }
                if ($joined -match 'hash-object') { return @{ exitCode = 0; output = 'blob-A.cs' } }
                if ($joined -match 'cat-file') { return @{ exitCode = 0; output = 'blob-A.cs blob 42' } }
                return @{ exitCode = 0; output = '' }
            }
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $short
            $a.orphans[0].disposition | Should -Be 'preserved'
            $a.orphans[0].unreconstructableCount | Should -Be 2
        }

        It 'treats a missing blob line as not present' {
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'explicit-missing'
            New-File -Dir $d -Relative 'src/A.cs' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @() -KnownBlobs @()
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker
            $a.orphans[0].unreconstructable | Should -Contain 'src/A.cs'
        }

        It 'issues a bounded number of git calls regardless of file count' {
            # Regression guard: the per-file probe spawned two git processes per
            # file and took >30 minutes on the real root, which forced a probe
            # cap so low the largest orphans were never evaluated at all.
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'many-files'
            1..40 | ForEach-Object { New-File -Dir $d -Relative "src/F$_.cs" | Out-Null }
            $fake = New-RegistryGit -RegisteredPaths @()
            Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker | Out-Null
            @($fake.state.calls | Where-Object { $_ -match 'hash-object|cat-file' }).Count | Should -Be 2
        }

        It 'never emits a disposition outside the known set' {
            $script:root = New-TempRoot
            New-File -Dir (New-Dir -Root $script:root -Name 'a') -Relative 'x.txt' | Out-Null
            New-File -Dir (New-Dir -Root $script:root -Name 'b') -Relative 'obj/x.dll' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @()
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker
            foreach ($o in $a.orphans) { $o.disposition | Should -BeIn @('reclaimable', 'preserved') }
        }
    }

    Context 'the audit is read-only' {
        It 'never invokes a destructive git subcommand' {
            $script:root = New-TempRoot
            New-File -Dir (New-Dir -Root $script:root -Name 'orphan') -Relative 'x.txt' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @()
            Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker | Out-Null
            @($fake.state.calls | Where-Object { $_ -match 'worktree (remove|prune)|branch -D|clean' }).Count | Should -Be 0
            Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue
        }

        It 'leaves every orphan directory on disk' {
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'orphan'
            New-File -Dir $d -Relative 'x.txt' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @()
            Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker | Out-Null
            Test-Path -LiteralPath $d | Should -BeTrue
            Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context 'edge cases' {
        It 'returns an empty, well-formed audit when the worktree root does not exist' {
            $fake = New-RegistryGit -RegisteredPaths @()
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot (Join-Path ([IO.Path]::GetTempPath()) 'no-such-root-3722') -GitInvoker $fake.invoker
            $a.rootExists | Should -BeFalse
            @($a.orphans).Count | Should -Be 0
            $a.totalBytes | Should -Be 0
        }

        It 'treats a directory containing a .git entry but absent from the registry as an orphan' {
            $script:root = New-TempRoot
            $d = New-Dir -Root $script:root -Name 'stale-registration'
            New-File -Dir $d -Relative '.git' -Content 'gitdir: Q:/repos/botnexus/.git/worktrees/stale' | Out-Null
            $fake = New-RegistryGit -RegisteredPaths @()
            $a = Get-WorktreeOrphanAudit -RepoRoot 'Q:\repos\botnexus' -WorktreeRoot $script:root -GitInvoker $fake.invoker
            @($a.orphans).Count | Should -Be 1
            Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
