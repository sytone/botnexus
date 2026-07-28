#Requires -Modules Pester

# Regression coverage for issue #2393: the global local-validation lock file was stranded by
# killed workers. It carried no owner metadata, had no liveness check and was only released in
# a PowerShell `finally`, so a killed or timed-out worker left a tombstone that blocked every
# later validation with no diagnostic.
#
# These tests exercise the four properties the fix must hold:
#   1. owner metadata (PID, process start time, machine, timestamp) is written INTO the file;
#   2. a lock whose owner PID is gone is reclaimed;
#   3. a lock whose owner PID is alive but has a different start time (PID reuse) is stale;
#   4. a genuinely live owner still BLOCKS, and the lock is released on abrupt process exit.

BeforeAll {
    $script:ModulePath = Join-Path $PSScriptRoot 'ValidationSteps.psm1'
    Import-Module $script:ModulePath -Force

    $script:PwshPath = (Get-Process -Id $PID).Path
    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ("bn2393-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Scratch -Force | Out-Null

    function New-LockPath {
        Join-Path $script:Scratch ("lock-" + [Guid]::NewGuid().ToString('N') + '.lock')
    }

    function Write-OwnerRecord {
        param([string]$Path, [int]$OwnerPid, [string]$StartUtc, [string]$Machine = [Environment]::MachineName)
        $record = [pscustomobject]@{
            Pid             = $OwnerPid
            ProcessStartUtc = $StartUtc
            Machine         = $Machine
            AcquiredUtc     = [DateTime]::UtcNow.AddMinutes(-40).ToString('o')
        }
        Set-Content -LiteralPath $Path -Value ($record | ConvertTo-Json -Compress) -NoNewline -Encoding utf8
    }

    # Starts a real child process, records its identity, then kills it. The returned PID is
    # genuinely dead, which is exactly the tombstone state observed in the issue.
    function New-DeadOwner {
        $p = Start-Process -FilePath $script:PwshPath -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 120') -PassThru
        $deadPid = $p.Id
        $start = $p.StartTime.ToUniversalTime().ToString('o')
        $p.Kill()
        $p.WaitForExit(30000) | Out-Null
        # Wait until the id is genuinely unresolvable, otherwise the test would assert on a
        # race rather than on the reaper.
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ((Get-Process -Id $deadPid -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
        }
        if (Get-Process -Id $deadPid -ErrorAction SilentlyContinue) {
            throw "Fixture failure: process $deadPid did not exit, the dead-owner test cannot be meaningful."
        }
        [pscustomobject]@{ Pid = $deadPid; StartUtc = $start }
    }
}

AfterAll {
    Remove-Item $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'ValidationSteps global lock owner metadata (#2393)' {
    It 'writes PID, process start time, machine and timestamp into the lock file' {
        $path = New-LockPath
        $lock = Get-BotNexusValidationLock -TimeoutSeconds 5 -LockPath $path -NoExitHandler
        try {
            $lock.Acquired | Should -BeTrue
            $record = Read-BotNexusLockOwner -LockPath $path
            $record | Should -Not -BeNullOrEmpty
            $record.Pid | Should -Be $PID
            $record.Machine | Should -Be ([Environment]::MachineName)
            $record.ProcessStartUtc | Should -Not -BeNullOrEmpty
            $record.AcquiredUtc | Should -Not -BeNullOrEmpty
            # Assert on the RAW file text, not the ConvertFrom-Json projection: Pester's
            # rehydrated DateTime loses the UTC kind, and the property that matters is that
            # the exact round-trippable owner start time is persisted on disk.
            $raw = Get-Content -LiteralPath $path -Raw
            $expectedStart = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o')
            $raw | Should -Match ([regex]::Escape($expectedStart))
        }
        finally { Remove-BotNexusValidationLock -Lock $lock }
    }
}

Describe 'ValidationSteps owner liveness classification (#2393)' {
    It 'classifies the current process as Alive' {
        $owner = ConvertTo-BotNexusLockOwnerRecord
        Test-BotNexusLockOwnerAlive -Owner $owner | Should -Be 'Alive'
    }

    It 'classifies a killed owner as Dead' {
        $dead = New-DeadOwner
        $owner = [pscustomobject]@{ Pid = $dead.Pid; ProcessStartUtc = $dead.StartUtc; Machine = [Environment]::MachineName; AcquiredUtc = (Get-Date).ToUniversalTime().ToString('o') }
        Test-BotNexusLockOwnerAlive -Owner $owner | Should -Be 'Dead'
    }

    It 'classifies a live PID with a mismatched start time as Reused' {
        $owner = [pscustomobject]@{
            Pid             = $PID
            ProcessStartUtc = ((Get-Process -Id $PID).StartTime.ToUniversalTime().AddHours(-6).ToString('o'))
            Machine         = [Environment]::MachineName
            AcquiredUtc     = (Get-Date).ToUniversalTime().ToString('o')
        }
        Test-BotNexusLockOwnerAlive -Owner $owner | Should -Be 'Reused'
    }

    It 'fails closed with Unknown when the record has no usable start time' {
        $owner = [pscustomobject]@{ Pid = $PID; ProcessStartUtc = ''; Machine = [Environment]::MachineName; AcquiredUtc = '' }
        Test-BotNexusLockOwnerAlive -Owner $owner | Should -Be 'Unknown'
    }
}

Describe 'ValidationSteps stale lock reaping (#2393)' {
    It 'reclaims a lock file whose owner PID is dead' {
        $dead = New-DeadOwner
        $path = New-LockPath
        Write-OwnerRecord -Path $path -OwnerPid $dead.Pid -StartUtc $dead.StartUtc

        $lock = Get-BotNexusValidationLock -TimeoutSeconds 3 -PollMilliseconds 50 -LockPath $path -NoExitHandler
        try {
            $lock.Acquired | Should -BeTrue
            $lock.Reaped | Should -BeTrue
            $lock.OwnerState | Should -Be 'Dead'
            # The reclaiming process must now own the record, not the tombstone.
            (Read-BotNexusLockOwner -LockPath $path).Pid | Should -Be $PID
        }
        finally { Remove-BotNexusValidationLock -Lock $lock }
    }

    It 'reclaims a lock file whose PID was recycled onto a different process' {
        $path = New-LockPath
        # PID is alive (it is this very process) but the recorded start time does not match,
        # so the original owner is gone and the operating system reused its id.
        Write-OwnerRecord -Path $path -OwnerPid $PID -StartUtc ((Get-Process -Id $PID).StartTime.ToUniversalTime().AddHours(-6).ToString('o'))

        $lock = Get-BotNexusValidationLock -TimeoutSeconds 3 -PollMilliseconds 50 -LockPath $path -NoExitHandler
        try {
            $lock.Acquired | Should -BeTrue
            $lock.Reaped | Should -BeTrue
            $lock.OwnerState | Should -Be 'Reused'
        }
        finally { Remove-BotNexusValidationLock -Lock $lock }
    }

    It 'emits a reap diagnostic naming the stale owner' {
        $dead = New-DeadOwner
        $path = New-LockPath
        Write-OwnerRecord -Path $path -OwnerPid $dead.Pid -StartUtc $dead.StartUtc

        $output = Get-BotNexusValidationLock -TimeoutSeconds 3 -PollMilliseconds 50 -LockPath $path -NoExitHandler 6>&1 |
            Out-String
        $output | Should -Match 'STALE'
        $output | Should -Match ([regex]::Escape([string]$dead.Pid))
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

Describe 'ValidationSteps live holders still block (#2393)' {
    It 'does not reclaim a lock recorded by a live process' {
        $path = New-LockPath
        # Record this process as the owner with its true start time, with NO open handle.
        # Liveness, not handle state, must be what keeps the lock closed.
        $me = ConvertTo-BotNexusLockOwnerRecord
        Set-Content -LiteralPath $path -Value ($me | ConvertTo-Json -Compress) -NoNewline -Encoding utf8

        $result = Get-BotNexusValidationLock -TimeoutSeconds 1 -PollMilliseconds 50 -LockPath $path -NoExitHandler
        $result.Acquired | Should -BeFalse
        $result.Reaped | Should -BeFalse
        $result.OwnerState | Should -Be 'Alive'
        # The tombstone-classification must not have overwritten the live owner's record.
        (Read-BotNexusLockOwner -LockPath $path).Pid | Should -Be $me.Pid
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }

    It 'still blocks when another process physically holds the file handle' {
        $path = New-LockPath
        $held = Get-BotNexusValidationLock -TimeoutSeconds 5 -LockPath $path -NoExitHandler
        try {
            $held.Acquired | Should -BeTrue
            $contended = Get-BotNexusValidationLock -TimeoutSeconds 1 -PollMilliseconds 50 -LockPath $path -NoExitHandler
            $contended.Acquired | Should -BeFalse
            $contended.Handle | Should -BeNullOrEmpty
            $contended.WaitedSeconds | Should -BeGreaterOrEqual 0.5
        }
        finally { Remove-BotNexusValidationLock -Lock $held }
    }

    It 'emits a BLOCKED diagnostic naming the live owner instead of failing silently' {
        $path = New-LockPath
        $me = ConvertTo-BotNexusLockOwnerRecord
        Set-Content -LiteralPath $path -Value ($me | ConvertTo-Json -Compress) -NoNewline -Encoding utf8

        $output = Get-BotNexusValidationLock -TimeoutSeconds 1 -PollMilliseconds 50 -LockPath $path -NoExitHandler 6>&1 |
            Out-String
        $output | Should -Match 'BLOCKED'
        $output | Should -Match ([regex]::Escape([string]$PID))
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

Describe 'ValidationSteps release on process exit (#2393)' {
    It 'releases the lock when the owning process exits without running a finally block' {
        $path = New-LockPath
        $childScript = Join-Path $script:Scratch ("child-" + [Guid]::NewGuid().ToString('N') + '.ps1')
        # No try/finally anywhere: the ONLY release path available to this child is the
        # registered process-exit handler. `exit` from a script skips a `finally` in the
        # caller, which is precisely how workers stranded the lock.
        @(
            'param([string]$ModulePath, [string]$LockPath)',
            'Import-Module $ModulePath -Force',
            '$lock = Get-BotNexusValidationLock -TimeoutSeconds 10 -LockPath $LockPath',
            'if (-not $lock.Acquired) { exit 9 }',
            'exit 0'
        ) | Set-Content -LiteralPath $childScript -Encoding utf8

        $proc = Start-Process -FilePath $script:PwshPath -PassThru -Wait -WindowStyle Hidden `
            -ArgumentList @('-NoProfile', '-File', $childScript, '-ModulePath', $script:ModulePath, '-LockPath', $path)
        $proc.ExitCode | Should -Be 0

        Test-Path -LiteralPath $path | Should -BeFalse
    }

    It 'releases the lock when the owning process exits down an error path' {
        $path = New-LockPath
        $childScript = Join-Path $script:Scratch ("child-err-" + [Guid]::NewGuid().ToString('N') + '.ps1')
        @(
            'param([string]$ModulePath, [string]$LockPath)',
            'Import-Module $ModulePath -Force',
            '$lock = Get-BotNexusValidationLock -TimeoutSeconds 10 -LockPath $LockPath',
            'if (-not $lock.Acquired) { exit 9 }',
            'exit 3'
        ) | Set-Content -LiteralPath $childScript -Encoding utf8

        $proc = Start-Process -FilePath $script:PwshPath -PassThru -Wait -WindowStyle Hidden `
            -ArgumentList @('-NoProfile', '-File', $childScript, '-ModulePath', $script:ModulePath, '-LockPath', $path)
        $proc.ExitCode | Should -Be 3

        Test-Path -LiteralPath $path | Should -BeFalse
    }

    It 'Remove-BotNexusValidationLock is idempotent and deletes the file' {
        $path = New-LockPath
        $lock = Get-BotNexusValidationLock -TimeoutSeconds 5 -LockPath $path -NoExitHandler
        $lock.Acquired | Should -BeTrue
        Remove-BotNexusValidationLock -Lock $lock
        Test-Path -LiteralPath $path | Should -BeFalse
        { Remove-BotNexusValidationLock -Lock $lock } | Should -Not -Throw
    }
}

Describe 'Invoke-LocalValidation wiring (#2393)' {
    It 'releases the lock through the shared release helper rather than an ad-hoc dispose' {
        $runner = Get-Content (Join-Path $PSScriptRoot 'Invoke-LocalValidation.ps1') -Raw
        $runner | Should -Match 'Remove-BotNexusValidationLock'
    }

    It 'reports the blocking owner instead of a bare "already running" message' {
        $runner = Get-Content (Join-Path $PSScriptRoot 'Invoke-LocalValidation.ps1') -Raw
        $runner | Should -Match 'lock\.Owner'
        $runner | Should -Match 'OwnerState'
    }
}
