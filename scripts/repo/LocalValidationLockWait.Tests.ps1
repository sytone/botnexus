#Requires -Modules Pester
# Regression coverage for issue #2793: `Invoke-LocalValidation.ps1 -Mode gate` discarded an
# explicitly supplied -LockWaitSeconds. The old expression was
#
#     $waitSeconds = if ($Mode -eq 'hook') { $LockWaitSeconds } else { [int][Math]::Max($LockWaitSeconds, 86400) }
#
# which uses the caller's value only as a LOWER bound, so `-LockWaitSeconds 300` became 86400
# and the gate waited up to 24h instead of failing closed with the owner diagnostic. The
# unbounded *default* was the sound part of #2331's intent; the unbounded *override* was not.
#
# These tests pin the four properties the fix must hold:
#   1. gate + explicit bound  -> that bound is used, and the run exits non-zero at ~that time;
#   2. gate + no bound        -> still effectively unbounded (86400), unchanged for CI callers;
#   3. hook                   -> default 120s, explicit honoured, -SkipOnLockContention exits 0;
#   4. the bounded-gate failure names the lock path, the owner record and the observed wait.
#
# Restoring [Math]::Max($LockWaitSeconds, 86400) reddens
# 'honours an explicitly supplied bound in gate mode' (issue #2793 AC6).

BeforeAll {
    $script:RepoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
    $script:ModulePath = Join-Path $PSScriptRoot 'ValidationSteps.psm1'
    $script:GateScript = Join-Path $PSScriptRoot 'Invoke-LocalValidation.ps1'
    Import-Module $script:ModulePath -Force
    $script:PwshPath = (Get-Process -Id $PID).Path
    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ("bn2793-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Scratch -Force | Out-Null

    function New-LockPath {
        Join-Path $script:Scratch ("lock-" + [Guid]::NewGuid().ToString('N') + '.lock')
    }

    # Holds the lock from a REAL, live child process, so the owner-liveness classifier added by
    # #2409 sees an Alive owner and genuinely blocks. A hand-written tombstone would be reaped
    # and the wait under test would never happen.
    function Start-LockHolder {
        param([string]$LockPath, [int]$HoldSeconds = 120)
        $command = @"
Import-Module '$($script:ModulePath)' -Force
`$lock = Get-BotNexusValidationLock -TimeoutSeconds 30 -LockPath '$LockPath'
if (-not `$lock.Acquired) { exit 3 }
Start-Sleep -Seconds $HoldSeconds
"@
        $process = Start-Process -FilePath $script:PwshPath -ArgumentList @('-NoProfile', '-Command', $command) -PassThru -WindowStyle Hidden
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $deadline) {
            if (Test-Path -LiteralPath $LockPath) {
                $raw = Get-Content -LiteralPath $LockPath -Raw -ErrorAction SilentlyContinue
                if (-not [string]::IsNullOrWhiteSpace($raw)) { return $process }
            }
            if ($process.HasExited) { throw "Fixture failure: lock holder exited early with $($process.ExitCode)." }
            Start-Sleep -Milliseconds 100
        }
        throw 'Fixture failure: lock holder never wrote an owner record, the contention test cannot be meaningful.'
    }

    function Stop-LockHolder {
        param($Process, [string]$LockPath)
        if ($null -ne $Process -and -not $Process.HasExited) {
            $Process.Kill()
            $Process.WaitForExit(30000) | Out-Null
        }
        Remove-Item -LiteralPath $LockPath -Force -ErrorAction SilentlyContinue
    }
}

AfterAll {
    Remove-Item $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Get-BotNexusLockWaitSeconds' {
    # AC1 / AC6: the named test that the restored [Math]::Max clamp must redden.
    It 'honours an explicitly supplied bound in gate mode' {
        Get-BotNexusLockWaitSeconds -Mode 'strict' -RequestedSeconds 300 | Should -Be 300
        Get-BotNexusLockWaitSeconds -Mode 'strict' -RequestedSeconds 1 | Should -Be 1
        Get-BotNexusLockWaitSeconds -Mode 'full' -RequestedSeconds 45 | Should -Be 45
        Get-BotNexusLockWaitSeconds -Mode 'playwright' -RequestedSeconds 600 | Should -Be 600
    }

    It 'treats an explicit zero in gate mode as a single acquisition attempt, not a 24h wait' {
        Get-BotNexusLockWaitSeconds -Mode 'strict' -RequestedSeconds 0 | Should -Be 0
    }

    # AC2: existing callers that pass nothing keep today's effectively unbounded wait.
    It 'keeps an effectively unbounded default in gate mode when no bound is supplied' {
        Get-BotNexusLockWaitSeconds -Mode 'strict' -RequestedSeconds -1 | Should -Be 86400
        Get-BotNexusLockWaitSeconds -Mode 'impacted' -RequestedSeconds -1 | Should -Be 86400
    }

    # AC3: hook defaults and explicit hook bounds are untouched.
    It 'keeps the documented 120s hook default and honours an explicit hook bound' {
        Get-BotNexusLockWaitSeconds -Mode 'hook' -RequestedSeconds -1 | Should -Be 120
        Get-BotNexusLockWaitSeconds -Mode 'hook' -RequestedSeconds 5 | Should -Be 5
        Get-BotNexusLockWaitSeconds -Mode 'hook' -RequestedSeconds 0 | Should -Be 0
    }
}

Describe 'Invoke-LocalValidation gate lock contention' {
    It 'gives up at approximately the supplied bound and exits non-zero with the owner diagnostic' {
        $lockPath = New-LockPath
        $holder = Start-LockHolder -LockPath $lockPath
        try {
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            $output = & $script:PwshPath -NoProfile -File $script:GateScript `
                -Mode 'strict' -LockWaitSeconds 5 -WorktreePath $script:RepoRoot -LockPath $lockPath 2>&1 |
                ForEach-Object { [string]$_ }
            $exitCode = $LASTEXITCODE
            $stopwatch.Stop()

            $exitCode | Should -Be 1 -Because 'a bounded gate that cannot acquire the lock must fail closed, not hang'
            # The bound is the point of the fix: 24h (or even 86400ms) would blow this ceiling.
            $stopwatch.Elapsed.TotalSeconds | Should -BeLessThan 120 -Because 'the supplied 5s bound must actually bound the wait'
            $stopwatch.Elapsed.TotalSeconds | Should -BeGreaterOrEqual 4 -Because 'the gate must still wait the bound it was given'

            # AC4: the message must let an automated caller tell contention from a real failure.
            $joined = $output -join "`n"
            $joined | Should -Match ([regex]::Escape($lockPath)) -Because 'the failure must name the lock path'
            $joined | Should -Match 'PID \d+ on \S+, held since ' -Because 'the failure must name the owner PID, machine and acquisition time'
            $joined | Should -Match 'owner state: Alive' -Because 'the failure must report the owner liveness classification'
            $joined | Should -Match 'still running after \d+' -Because 'the failure must report the observed wait'
        }
        finally {
            Stop-LockHolder -Process $holder -LockPath $lockPath
        }
    }

    # AC3: the advisory hook path still skips cleanly rather than failing on contention.
    It 'still skips with exit 0 in hook mode when -SkipOnLockContention is supplied' {
        $lockPath = New-LockPath
        $holder = Start-LockHolder -LockPath $lockPath
        try {
            $output = & $script:PwshPath -NoProfile -File $script:GateScript `
                -Mode 'hook' -LockWaitSeconds 2 -SkipOnLockContention -WorktreePath $script:RepoRoot -LockPath $lockPath 2>&1 |
                ForEach-Object { [string]$_ }
            $exitCode = $LASTEXITCODE

            $exitCode | Should -Be 0 -Because 'an advisory hook must not fail on contention (#2331)'
            ($output -join "`n") | Should -Match 'skipping this advisory run'
        }
        finally {
            Stop-LockHolder -Process $holder -LockPath $lockPath
        }
    }
}
