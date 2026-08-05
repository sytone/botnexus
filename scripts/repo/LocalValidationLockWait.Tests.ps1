#Requires -Modules Pester

# Regression coverage for issue #2793: Invoke-LocalValidation in gate (non-hook) mode ignored
# an explicitly supplied -LockWaitSeconds because the effective wait was recomputed as
# [Math]::Max($LockWaitSeconds, 86400). A caller asking for a bounded wait was silently
# clamped up to 24 hours, so the gate could never report lock contention within any practical
# window and shipping lanes starved.
#
# These tests are behavioural: every one of them holds the global lock from a REAL child
# process, so the script under test can never acquire it and can never run a real validation.

BeforeAll {
    $script:RepoScripts = Split-Path -Parent $PSCommandPath
    $script:Runner = Join-Path $script:RepoScripts 'Invoke-LocalValidation.ps1'
    $script:ModulePath = Join-Path $script:RepoScripts 'ValidationSteps.psm1'
    $script:RepoRoot = (& git -C $script:RepoScripts rev-parse --show-toplevel).Trim()
    $script:PwshPath = (Get-Process -Id $PID).Path

    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ("bn2793-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Scratch -Force | Out-Null
    $script:Holders = [Collections.Generic.List[object]]::new()

    function New-LockPath {
        Join-Path $script:Scratch ("lock-" + [Guid]::NewGuid().ToString('N') + '.lock')
    }

    # Starts a real, live holder of the global validation lock at $Path and blocks until the
    # lock file genuinely exists with an owner record. Using a real process (rather than a
    # hand-written tombstone) matters: #2409 classifies a dead owner as reclaimable, so a fake
    # record would be reaped and the waiter would acquire instead of contending.
    function Start-LockHolder {
        param([string]$Path)
        $command = @(
            "Import-Module '$script:ModulePath' -Force",
            "`$lock = Get-BotNexusValidationLock -TimeoutSeconds 60 -LockPath '$Path' -NoExitHandler",
            "if (-not `$lock.Acquired) { exit 9 }",
            "Start-Sleep -Seconds 300"
        ) -join '; '
        $holder = Start-Process -FilePath $script:PwshPath -ArgumentList @('-NoProfile', '-Command', $command) -PassThru -WindowStyle Hidden
        $script:Holders.Add($holder)

        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while ([DateTime]::UtcNow -lt $deadline) {
            if (Test-Path -LiteralPath $Path) {
                $raw = ''
                try { $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop } catch { $raw = '' }
                if ($raw -match '"Pid"') { return $holder }
            }
            Start-Sleep -Milliseconds 200
        }
        throw "Fixture failure: lock holder never acquired $Path; the contention tests would be vacuous."
    }

    function Stop-LockHolder {
        param($Holder)
        if ($null -eq $Holder) { return }
        try { $Holder.Kill() } catch { }
        try { $Holder.WaitForExit(30000) | Out-Null } catch { }
    }

    # Runs the gate out-of-process so the assertion is on a real exit code and real stdout,
    # not on a mocked helper. Returns exit code, combined output and the observed wall clock.
    function Invoke-Gate {
        param([string[]]$Arguments, [int]$TimeoutSeconds = 180)
        $outFile = Join-Path $script:Scratch ("out-" + [Guid]::NewGuid().ToString('N') + '.txt')
        $all = @('-NoProfile', '-File', $script:Runner) + $Arguments
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $script:PwshPath -ArgumentList $all -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $outFile -RedirectStandardError ($outFile + '.err')
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            $sw.Stop()
            return [pscustomobject]@{ Exited = $false; ExitCode = $null; Output = ''; ElapsedSeconds = $sw.Elapsed.TotalSeconds }
        }
        $sw.Stop()
        $text = ''
        foreach ($f in @($outFile, ($outFile + '.err'))) {
            if (Test-Path -LiteralPath $f) { $text += (Get-Content -LiteralPath $f -Raw) }
        }
        [pscustomobject]@{ Exited = $true; ExitCode = $process.ExitCode; Output = $text; ElapsedSeconds = $sw.Elapsed.TotalSeconds }
    }
}

AfterAll {
    foreach ($h in $script:Holders) { Stop-LockHolder -Holder $h }
    Remove-Item $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Invoke-LocalValidation bounded gate wait (#2793)' {

    It 'honours an explicit -LockWaitSeconds in gate mode instead of clamping it up to 86400' {
        $path = New-LockPath
        $holder = Start-LockHolder -Path $path
        try {
            $result = Invoke-Gate -Arguments @(
                '-WorktreePath', $script:RepoRoot, '-Mode', 'strict', '-LockWaitSeconds', '10', '-LockPath', $path
            ) -TimeoutSeconds 120

            $result.Exited | Should -BeTrue -Because 'a bounded gate must return; the pre-#2793 clamp waited 24h'
            # Generous head-room for process start and module import, but far below the 86400s
            # clamp and far below any plausible re-introduction of it.
            $result.ElapsedSeconds | Should -BeLessThan 90
        }
        finally { Stop-LockHolder -Holder $holder }
    }

    It 'exits non-zero on a bounded gate timeout so contention is distinguishable from failure' {
        $path = New-LockPath
        $holder = Start-LockHolder -Path $path
        try {
            $result = Invoke-Gate -Arguments @(
                '-WorktreePath', $script:RepoRoot, '-Mode', 'strict', '-LockWaitSeconds', '10', '-LockPath', $path
            ) -TimeoutSeconds 120

            $result.Exited | Should -BeTrue
            $result.ExitCode | Should -Not -Be 0
        }
        finally { Stop-LockHolder -Holder $holder }
    }

    It 'names the lock path, the live owner PID/machine and the observed wait on a bounded gate timeout' {
        $path = New-LockPath
        $holder = Start-LockHolder -Path $path
        try {
            $result = Invoke-Gate -Arguments @(
                '-WorktreePath', $script:RepoRoot, '-Mode', 'strict', '-LockWaitSeconds', '10', '-LockPath', $path
            ) -TimeoutSeconds 120

            $result.Exited | Should -BeTrue
            $result.Output | Should -Match ([regex]::Escape([IO.Path]::GetFileName($path)))
            $result.Output | Should -Match "PID $($holder.Id)"
            $result.Output | Should -Match ([regex]::Escape([Environment]::MachineName))
            $result.Output | Should -Match 'still running after \d+'
            $result.Output | Should -Match 'owner state'
        }
        finally { Stop-LockHolder -Holder $holder }
    }

    It 'keeps the gate default effectively unbounded when no -LockWaitSeconds is supplied' {
        $path = New-LockPath
        $holder = Start-LockHolder -Path $path
        try {
            # If the default regressed to 0 (single attempt) or to any small bound, the gate
            # would give up almost immediately. Assert it is STILL waiting well past that.
            $result = Invoke-Gate -Arguments @(
                '-WorktreePath', $script:RepoRoot, '-Mode', 'strict', '-LockPath', $path
            ) -TimeoutSeconds 45

            $result.Exited | Should -BeFalse -Because 'the gate default must not be silently truncated (#2331)'
        }
        finally { Stop-LockHolder -Holder $holder }
    }

    It 'leaves hook mode unchanged: -SkipOnLockContention still exits 0 on contention' {
        $path = New-LockPath
        $holder = Start-LockHolder -Path $path
        try {
            $result = Invoke-Gate -Arguments @(
                '-WorktreePath', $script:RepoRoot, '-Mode', 'hook', '-LockWaitSeconds', '5',
                '-SkipOnLockContention', '-LockPath', $path
            ) -TimeoutSeconds 120

            $result.Exited | Should -BeTrue
            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'skipping this advisory run'
        }
        finally { Stop-LockHolder -Holder $holder }
    }

    It 'leaves the hook default at 120 seconds' {
        $runner = Get-Content $script:Runner -Raw
        $runner | Should -Match "if \(\`$Mode -eq 'hook'\) \{ 120 \}"
    }

    It 'resolves the effective wait once and never re-derives it with a Max clamp' {
        $runner = Get-Content $script:Runner -Raw
        $runner | Should -Not -Match '\[Math\]::Max\(\$LockWaitSeconds'
    }
}
