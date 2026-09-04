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
        'create-body-file' {
            $flag = Join-Path $env:CI_PR_COMMENT_STUB_DIR 'created.flag'
            if (Test-Path $flag) { Emit-Existing } else { '[]' }
            exit 0
        }
        'create-body-file-fail' { '[]'; exit 0 }
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
    if ($mode -in @('create-body-file', 'create-body-file-fail')) {
        # Issue #3850. This branch stands in for the real gh.exe argument parser.
        # An inline multi-line --body does not survive the native boundary, so
        # under the old implementation the body's own lines arrive here as extra
        # arguments and no --body-file pair exists at all. Rather than guess at
        # the exact split, assert the contract directly: the body must arrive as
        # a FILE whose content round-trips byte-for-byte, and no --body may be
        # present. Anything else is recorded as a positional-parse failure in the
        # shape real gh emits.
        $bodyIdx     = [Array]::IndexOf($ghArgs, '--body')
        $bodyFileIdx = [Array]::IndexOf($ghArgs, '--body-file')
        if ($bodyIdx -ge 0 -or $bodyFileIdx -lt 0) {
            $stray = if ($bodyIdx -ge 0) { $ghArgs[$bodyIdx + 1] } else { $ghArgs[-1] }
            [Console]::Error.WriteLine("GraphQL: Could not resolve to a Repository with the name '$stray'. (repository)")
            exit 1
        }
        $path = $ghArgs[$bodyFileIdx + 1]
        if (-not (Test-Path -LiteralPath $path)) {
            [Console]::Error.WriteLine("gh: could not open body file '$path'")
            exit 1
        }
        $content = Get-Content -LiteralPath $path -Raw
        Set-Content -LiteralPath (Join-Path $env:CI_PR_COMMENT_STUB_DIR 'body.txt') -Value $content -NoNewline
        Set-Content -LiteralPath (Join-Path $env:CI_PR_COMMENT_STUB_DIR 'bodypath.txt') -Value $path -NoNewline
        if ($mode -eq 'create-body-file-fail') {
            [Console]::Error.WriteLine('gh: simulated create failure after reading body file')
            exit 1
        }
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
            [string]$ScriptPath = $script:ScriptPath,
            [string]$ActionsLiteral = "@('none')",
            [switch]$KeepStubDir
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
            $psi.Arguments = "-NoProfile -Command `"& { `$rows = @([pscustomobject]@{name='core-tests';status='pass'}); & '$ScriptPath' -PR $PR -CheckRows `$rows -BehindBy 0 -Mergeable MERGEABLE -Actions $ActionsLiteral -Blockers @('None') }; exit `$LASTEXITCODE`""
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
            [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $stdout; StdErr = $stderr; StubDir = $stub.Dir }
        } finally {
            Remove-Item -LiteralPath $outPath, $errPath -Force -ErrorAction SilentlyContinue
            if (-not $KeepStubDir) {
                Remove-Item -LiteralPath $stub.Dir -Recurse -Force -ErrorAction SilentlyContinue
            }
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

Describe 'create path passes a multi-line body as a file, not an inline argument (#3850)' {
    BeforeAll {
        # Every one of these action entries is a line that gh would parse as a
        # positional if the body were split at the native boundary. The first is
        # verbatim the string from the live #3830 failure.
        $script:Actions = "@('Merged origin/main (was behindBy=6) and pushed 9295ed7bd to re-trigger CI.','- leading dash line','| pipe | row |','<html-ish line>','*starred line*')"
        $script:BodyFile = Invoke-CiPrComment -Mode 'create-body-file' -ActionsLiteral $script:Actions -KeepStubDir
    }
    AfterAll {
        if ($script:BodyFile) {
            Remove-Item -LiteralPath $script:BodyFile.StubDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # MUTATION PROOF: revert the create call to
    #   @('pr','comment',"$PR",'--repo',$Repo,'--body',$newBody)
    # and this Describe fails -- the stub sees no --body-file pair and emits the
    # real-world `Could not resolve to a Repository with the name '<body line>'`,
    # so the script exits 1 with error='create-failed'.
    It 'creates the comment successfully with a body full of positional-looking lines' {
        $script:BodyFile.ExitCode | Should -Be 0
        $script:BodyFile.StdOut | Should -Match '"action"\s*:\s*"created"'
    }
    It 'does not report the repository-resolution failure the inline form produces' {
        $script:BodyFile.StdErr | Should -Not -Match 'Could not resolve to a Repository'
        $script:BodyFile.StdOut | Should -Not -Match 'create-failed'
    }
    It 'delivers the body through --body-file with its content intact' {
        $captured = Get-Content -LiteralPath (Join-Path $script:BodyFile.StubDir 'body.txt') -Raw
        $captured | Should -Match '<!-- farnsworth:ci-monitor-9001 -->'
        $captured | Should -Match 'was behindBy=6'
        $captured | Should -Match '\|\s*core-tests\s*\|'
    }
    It 'removes the temporary body file after the call' {
        $recorded = Join-Path $script:BodyFile.StubDir 'bodypath.txt'
        # Fail closed: no recorded path means the create never reached --body-file.
        Test-Path -LiteralPath $recorded | Should -BeTrue
        $path = Get-Content -LiteralPath $recorded -Raw
        $path | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $path | Should -BeFalse
    }
    It 'never passes the body as an inline --body argument on the create path' {
        $text = Get-Content $script:ScriptPath -Raw
        $text | Should -Not -Match "'--body',\s*\`$newBody"
        $text | Should -Match "'--body-file'"
    }
}

Describe 'create path removes its body file when gh fails (#3850 AC5)' {
    BeforeAll {
        $script:BodyFileFailure = Invoke-CiPrComment -Mode 'create-body-file-fail' -KeepStubDir
    }
    AfterAll {
        if ($script:BodyFileFailure) {
            Remove-Item -LiteralPath $script:BodyFileFailure.StubDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'reports the create failure' {
        $script:BodyFileFailure.ExitCode | Should -Not -Be 0
        $script:BodyFileFailure.StdOut | Should -Match 'create-failed'
        $script:BodyFileFailure.StdErr | Should -Match 'simulated create failure'
    }
    It 'removes the temporary body file after gh returns failure' {
        $recorded = Join-Path $script:BodyFileFailure.StubDir 'bodypath.txt'
        Test-Path -LiteralPath $recorded | Should -BeTrue
        $path = Get-Content -LiteralPath $recorded -Raw
        $path | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $path | Should -BeFalse
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
