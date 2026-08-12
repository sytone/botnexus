#Requires -Modules Pester
# Regression coverage for issue #3005: scripts/ci-pr-comment.ps1 emitted
# {"action":"created","commentId":null} and exited 0 when the underlying
# `gh pr comment` call failed (expired / under-scoped token). `| Out-Null`
# discards stdout and does not propagate a native exit code, and
# $ErrorActionPreference = 'Stop' does not apply to native commands, so the
# caller saw a success envelope for a comment that was never written.
#
# The write path cannot be unit-tested in-process: PowerShell's `2>` redirection
# operator only applies to a real child process, and a fake `gh` written as a
# .ps1 dot-sourced into the same runspace would bypass it entirely (that trap
# cost a full red/green cycle on issue #2761). Every test here therefore forks
# a real `pwsh` child with a `gh.cmd` shim first on PATH.

BeforeAll {
    $script:RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:ScriptPath = Join-Path $script:RepoRoot 'scripts/ci-pr-comment.ps1'

    # Builds a throwaway directory containing a gh.cmd shim that forks a real
    # pwsh child running the stub logic, so stderr/exit codes behave exactly as
    # they do for the genuine gh CLI.
    function New-GhStubDir {
        param([Parameter(Mandatory)][string]$Mode)

        $dir = Join-Path ([IO.Path]::GetTempPath()) ("ghstub-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null

        $stub = @'
param()
$ghArgs = $args
$mode   = $env:CI_PR_COMMENT_STUB_MODE
$marker = "<!-- farnsworth:ci-monitor-$($env:CI_PR_COMMENT_STUB_PR) -->"
$joined = ($ghArgs -join ' ')

function Emit-Existing {
    $body = "$marker`n| check | status |`n| --- | --- |`n| stale | pass |`n"
    ConvertTo-Json -Compress -Depth 4 -InputObject @(@{ id = 4242; body = $body })
}

# A read of the comment list.
if ($joined -match 'issues/\d+/comments') {
    switch ($mode) {
        'create-fail'        { '[]'; exit 0 }
        'create-unverified'  { '[]'; exit 0 }
        'create-ok' {
            $flag = Join-Path $env:CI_PR_COMMENT_STUB_DIR 'created.flag'
            if (Test-Path $flag) { Emit-Existing } else { '[]' }
            exit 0
        }
        'patch-fail'         { Emit-Existing; exit 0 }
        default              { '[]'; exit 0 }
    }
}

# A PATCH of an existing comment.
if ($joined -match '-X PATCH') {
    if ($mode -eq 'patch-fail') {
        [Console]::Error.WriteLine('gh: Resource not accessible by integration (HTTP 403)')
        exit 1
    }
    '{"id":4242}'
    exit 0
}

# A create.
if ($ghArgs[0] -eq 'pr' -and $ghArgs[1] -eq 'comment') {
    if ($mode -eq 'create-fail') {
        [Console]::Error.WriteLine('gh: Your token has not been granted the required scopes (EMU)')
        exit 1
    }
    if ($mode -eq 'create-ok') {
        New-Item -ItemType File -Path (Join-Path $env:CI_PR_COMMENT_STUB_DIR 'created.flag') -Force | Out-Null
    }
    'https://github.com/Sytone/botnexus/pull/1#issuecomment-4242'
    exit 0
}

exit 0
'@
        Set-Content -LiteralPath (Join-Path $dir 'gh-stub.ps1') -Value $stub -Encoding UTF8

        $cmd = "@echo off`r`npwsh -NoProfile -File `"%~dp0gh-stub.ps1`" %*`r`n"
        Set-Content -LiteralPath (Join-Path $dir 'gh.cmd') -Value $cmd -Encoding ASCII

        [pscustomobject]@{ Dir = $dir; Mode = $Mode }
    }

    # Forks ci-pr-comment.ps1 in a child pwsh with the stub first on PATH.
    # Returns the exit code plus the captured stdout / stderr text.
    function Invoke-CiPrComment {
        param(
            [Parameter(Mandatory)][string]$Mode,
            [int]$PR = 9001,
            [string]$ScriptPath = $script:ScriptPath
        )
        $stub    = New-GhStubDir -Mode $Mode
        $outPath = [IO.Path]::GetTempFileName()
        $errPath = [IO.Path]::GetTempFileName()
        try {
            $psi = [Diagnostics.ProcessStartInfo]::new()
            $psi.FileName  = (Get-Process -Id $PID).Path
            # PowerShell -File cannot take structured arguments, so drive the
            # script through -Command instead. `& script` sets $LASTEXITCODE
            # from the script's `exit`, so re-exiting with it reproduces the
            # exit-code propagation a real `pwsh -File` caller sees.
            $psi.Arguments = "-NoProfile -Command `"& { `$rows = @([pscustomobject]@{name='core-tests';status='pass'}); & '$ScriptPath' -PR $PR -CheckRows `$rows -BehindBy 0 -Mergeable MERGEABLE -Actions @('none') -Blockers @('None') }; exit `$LASTEXITCODE`""
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError  = $true
            $psi.UseShellExecute        = $false
            $psi.Environment['PATH']                     = $stub.Dir + [IO.Path]::PathSeparator + $env:PATH
            $psi.Environment['CI_PR_COMMENT_STUB_MODE']  = $Mode
            $psi.Environment['CI_PR_COMMENT_STUB_PR']    = "$PR"
            $psi.Environment['CI_PR_COMMENT_STUB_DIR']   = $stub.Dir

            $p = [Diagnostics.Process]::Start($psi)
            $stdout = $p.StandardOutput.ReadToEnd()
            $stderr = $p.StandardError.ReadToEnd()
            $p.WaitForExit()
            [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $stdout; StdErr = $stderr }
        } finally {
            Remove-Item -LiteralPath $outPath, $errPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $stub.Dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe 'ci-pr-comment.ps1 create path fails loudly (AC1, AC5)' {
    BeforeAll { $script:Fail = Invoke-CiPrComment -Mode 'create-fail' }

    It 'exits non-zero when gh pr comment exits non-zero' {
        $script:Fail.ExitCode | Should -Not -Be 0
    }
    It 'does not emit action = created when the write failed' {
        $script:Fail.StdOut | Should -Not -Match '"action"\s*:\s*"created"'
    }
    It 'emits a failure envelope naming the create failure' {
        $script:Fail.StdOut | Should -Match '"action"\s*:\s*"failed"'
        $script:Fail.StdOut | Should -Match 'create-failed'
    }
    It 'replays the underlying gh error to stderr instead of discarding it' {
        $script:Fail.StdErr | Should -Match 'required scopes'
    }
}

Describe 'ci-pr-comment.ps1 unverifiable create fails loudly (AC2)' {
    BeforeAll { $script:Unverified = Invoke-CiPrComment -Mode 'create-unverified' }

    It 'exits non-zero when the marker comment cannot be found after the write' {
        $script:Unverified.ExitCode | Should -Not -Be 0
    }
    It 'does not emit action = created for an unverifiable write' {
        $script:Unverified.StdOut | Should -Not -Match '"action"\s*:\s*"created"'
    }
    It 'names the verification failure' {
        $script:Unverified.StdOut | Should -Match 'comment-not-found-after-create'
    }
}

Describe 'ci-pr-comment.ps1 update path fails loudly (AC4)' {
    BeforeAll { $script:Patch = Invoke-CiPrComment -Mode 'patch-fail' }

    It 'exits non-zero when the PATCH exits non-zero' {
        $script:Patch.ExitCode | Should -Not -Be 0
    }
    It 'does not emit action = updated when the patch failed' {
        $script:Patch.StdOut | Should -Not -Match '"action"\s*:\s*"updated"'
    }
    It 'replays the underlying gh error to stderr' {
        $script:Patch.StdErr | Should -Match '403'
    }
}

Describe 'ci-pr-comment.ps1 success path still works (non-vacuity)' {
    BeforeAll { $script:Ok = Invoke-CiPrComment -Mode 'create-ok' }

    It 'exits zero on a confirmed create' {
        $script:Ok.ExitCode | Should -Be 0
    }
    It 'emits action = created with a non-null commentId' {
        $script:Ok.StdOut | Should -Match '"action"\s*:\s*"created"'
        $envelope = $script:Ok.StdOut.Trim() | ConvertFrom-Json
        $envelope.commentId | Should -Not -BeNullOrEmpty
    }
}

Describe 'no success envelope can carry a null commentId (AC3)' {
    It 'every created/updated envelope in the script is emitted after a non-null id check' {
        $text = Get-Content $script:ScriptPath -Raw
        # The created envelope must be preceded by a guard on $newId.
        $text | Should -Match '(?s)if\s*\(\s*-not\s+\$newId\s*\).*?action\s*=\s*''created'''
        # The updated envelope is only reachable when $existingId is truthy and
        # the PATCH exit code was checked.
        $text | Should -Match '(?s)if\s*\(\s*\$existingId\s*\).*?LASTEXITCODE\s*-ne\s*0.*?action\s*=\s*''updated'''
    }
    It 'the write path never pipes gh straight to Out-Null without an exit-code check' {
        $lines = Get-Content $script:ScriptPath
        $offenders = $lines | Where-Object { $_ -match '^\s*gh\s.*\|\s*Out-Null' }
        @($offenders).Count | Should -Be 0
    }
}
