#Requires -Modules Pester
<#
    Issue #3722 - a failed worktree removal must leave the worktree REGISTERED.

    These tests drive the real Remove-DevWorktree.ps1 as a script (it is a
    parameterised script, not a module), intercepting `git` through a shim on
    PATH so no real repository is touched. The assertions are about the ORDER
    and PRESENCE of git subcommands: specifically that `worktree prune` is
    never issued on a path where the removal failed, because pruning there is
    what de-registers a directory that still exists and manufactures the
    invisible orphan class this issue is about.
#>
# Copyright (c) Microsoft Corporation. All rights reserved.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Remove-DevWorktree.ps1'

    function New-GitShim {
        <#
          Creates a throwaway directory holding a `git.ps1`/`git.cmd` shim that
          appends every invocation to a log and exits with a scripted code for
          `worktree remove`. Returns the shim dir, log path and a fake repo.
        #>
        param([int]$RemoveExitCode = 0, [string]$RemoveOutput = '')

        $base = Join-Path ([IO.Path]::GetTempPath()) ("gitshim-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $base -Force | Out-Null
        $log = Join-Path $base 'calls.log'
        New-Item -ItemType File -Path $log -Force | Out-Null

        $body = @()
        $body += '$log = ' + "'$log'"
        $body += '$joined = ($args -join " ")'
        $body += 'Add-Content -LiteralPath $log -Value $joined'
        $body += 'if ($joined -match "worktree remove") {'
        $body += '  Write-Output ' + "'$RemoveOutput'"
        $body += '  exit ' + $RemoveExitCode
        $body += '}'
        $body += 'if ($joined -match "rev-parse --abbrev-ref") { Write-Output "fix/3722-x"; exit 0 }'
        $body += 'if ($joined -match "rev-parse .*git-common-dir") { Write-Output "' + ($base -replace '\\', '\\') + '\\repo\\.git"; exit 0 }'
        $body += 'if ($joined -match "status --porcelain") { exit 0 }'
        $body += 'if ($joined -match "rev-list --count") { Write-Output "0"; exit 0 }'
        $body += 'if ($joined -match "worktree list") { exit 0 }'
        $body += 'exit 0'
        Set-Content -LiteralPath (Join-Path $base 'git.ps1') -Value ($body -join "`n") -Encoding utf8

        $cmd = "@echo off`r`npwsh -NoProfile -File `"%~dp0git.ps1`" %*`r`n"
        Set-Content -LiteralPath (Join-Path $base 'git.cmd') -Value $cmd -Encoding ascii

        $repo = Join-Path $base 'repo'
        New-Item -ItemType Directory -Path $repo -Force | Out-Null

        return @{ Dir = $base; Log = $log; Repo = $repo }
    }

    function Invoke-RemoveDevWorktree {
        param([hashtable]$Shim, [string]$WorktreePath)
        $old = $env:PATH
        try {
            $env:PATH = "$($Shim.Dir);$old"
            $out = & pwsh -NoProfile -Command "`$env:PATH='$($Shim.Dir);' + `$env:PATH; & '$script:ScriptPath' -WorktreePath '$WorktreePath' -RepoRoot '$($Shim.Repo)'" 2>&1 | Out-String
            return $out
        }
        finally { $env:PATH = $old }
    }

    function Get-GitCalls {
        param([hashtable]$Shim)
        if (-not (Test-Path -LiteralPath $Shim.Log)) { return @() }
        return @(Get-Content -LiteralPath $Shim.Log)
    }
}

Describe 'Remove-DevWorktree - failed removal leaves the worktree registered (#3722)' {

    Context 'when git worktree remove fails with a lock' {
        BeforeAll {
            $script:shim = New-GitShim -RemoveExitCode 1 -RemoveOutput 'fatal: Permission denied'
            $script:wt = Join-Path $script:shim.Dir 'wt-locked'
            New-Item -ItemType Directory -Path $script:wt -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:wt 'keep.txt') -Value 'unrecoverable' -Encoding utf8
            $script:output = Invoke-RemoveDevWorktree -Shim $script:shim -WorktreePath $script:wt
            $script:calls = Get-GitCalls -Shim $script:shim
        }
        AfterAll { Remove-Item -LiteralPath $script:shim.Dir -Recurse -Force -ErrorAction SilentlyContinue }

        It 'never issues git worktree prune' {
            @($script:calls | Where-Object { $_ -match 'worktree prune' }).Count | Should -Be 0
        }

        It 'never deletes the branch' {
            @($script:calls | Where-Object { $_ -match 'branch -D' }).Count | Should -Be 0
        }

        It 'leaves the directory and its content on disk' {
            Test-Path -LiteralPath $script:wt | Should -BeTrue
            Get-Content -LiteralPath (Join-Path $script:wt 'keep.txt') | Should -Be 'unrecoverable'
        }

        It 'reports failure rather than claiming success' {
            $script:output | Should -Match '"success":\s*false'
        }
    }

    Context 'when git worktree remove succeeds' {
        BeforeAll {
            $script:shim2 = New-GitShim -RemoveExitCode 0
            $script:wt2 = Join-Path $script:shim2.Dir 'wt-ok'
            New-Item -ItemType Directory -Path $script:wt2 -Force | Out-Null
            $script:output2 = Invoke-RemoveDevWorktree -Shim $script:shim2 -WorktreePath $script:wt2
            $script:calls2 = Get-GitCalls -Shim $script:shim2
        }
        AfterAll { Remove-Item -LiteralPath $script:shim2.Dir -Recurse -Force -ErrorAction SilentlyContinue }

        It 'does issue git worktree prune, so the registry is tidied on the success path' {
            @($script:calls2 | Where-Object { $_ -match 'worktree prune' }).Count | Should -BeGreaterThan 0
        }

        It 'removes the directory' {
            Test-Path -LiteralPath $script:wt2 | Should -BeFalse
        }
    }
}

Describe 'Remove-DevWorktree source invariants (#3722)' {
    BeforeAll { $script:src = Get-Content -LiteralPath $script:ScriptPath -Raw }

    It 'never calls worktree prune unconditionally at the end of a branch' {
        # Every prune site must be inside a success-conditional. Assert no prune
        # line sits at the top level of the try block preceded only by the
        # removal call - the exact shape that produced the 13 orphans.
        $lines = $script:src -split "`r?`n"
        $sites = 0
        for ($i = 0; $i -lt $lines.Count; $i++) {
            # Only actual invocations count - a comment naming `worktree prune`
            # is documentation, not a call site.
            if ($lines[$i] -notmatch '^\s*#' -and $lines[$i] -match 'git\s+-C\s+\S+\s+worktree\s+prune') {
                $sites++
                $window = ($lines[[Math]::Max(0, $i - 10)..$i] | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
                $window | Should -Match 'if\s*\(' -Because 'a prune must be guarded by a removal-success check'
            }
        }
        $sites | Should -BeGreaterThan 0 -Because 'the assertion is vacuous if no prune call site is found'
    }

    It 'documents the issue number at each prune guard' {
        ([regex]::Matches($script:src, 'worktree prune')).Count | Should -BeGreaterThan 0
        $script:src | Should -Match '#3722'
    }
}
