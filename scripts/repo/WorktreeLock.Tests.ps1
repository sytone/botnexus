#Requires -Modules Pester

# Regression coverage for issue #2409: the worktree removal path returned a structured
# `locked` outcome after bounded retries but NEVER asked who held the lock or whether that
# owner was still alive. A tombstone lock left by a killed worker therefore silently
# disabled the guard it existed to provide - it blocked forever, and no caller could tell
# "someone else is working here" from "nobody is, the file just outlived them".
#
# These tests assert the OBSERVABLE behaviour of Remove-WorktreeSafely, not the return
# value of a helper:
#   1. a lock whose recorded owner PID is dead is reclaimed and the removal SUCCEEDS;
#   2. a lock whose owner is alive is NOT reclaimed and removal returns 'locked';
#   3. a lock with no readable owner record ('Unknown') is NOT reclaimed;
#   4. a PID-reuse record ('Reused') is NOT reclaimed;
#   5. a lock BotNexus itself writes carries an owner PID.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Remove-Worktree.ps1'
    . $script:ScriptPath

    $script:Scratch = Join-Path ([IO.Path]::GetTempPath()) ("bn2409-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:Scratch -Force | Out-Null

    function New-FakeRepo {
        <#
          Builds a repo root + linked worktree layout on disk that is shaped exactly like
          git's, so Get-WorktreeLockFilePath resolves a real path. No git process needed.
        #>
        param([AllowNull()][AllowEmptyString()][string]$OwnerJson = $null)

        $id = [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $repo = Join-Path $script:Scratch "repo-$id"
        $wt = Join-Path $script:Scratch "wt-$id"
        $meta = Join-Path (Join-Path (Join-Path $repo '.git') 'worktrees') "wt-$id"
        New-Item -ItemType Directory -Path $meta -Force | Out-Null
        New-Item -ItemType Directory -Path $wt -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $wt '.git') -Value "gitdir: $meta" -Encoding utf8
        if (-not [string]::IsNullOrEmpty($OwnerJson)) {
            Set-Content -LiteralPath (Join-Path $meta 'locked') -Value $OwnerJson -Encoding utf8 -NoNewline
        }
        [pscustomobject]@{ Repo = $repo; Worktree = $wt; Meta = $meta; LockFile = (Join-Path $meta 'locked') }
    }

    function New-OwnerJson {
        param([int]$OwnerPid, [string]$StartUtc, [string]$Machine = [Environment]::MachineName)
        ([pscustomobject]@{
                Pid             = $OwnerPid
                ProcessStartUtc = $StartUtc
                Machine         = $Machine
                AcquiredUtc     = [DateTime]::UtcNow.ToString('o')
            } | ConvertTo-Json -Compress)
    }

    function Get-DeadPid {
        # Start and immediately kill a real process so the PID is genuinely gone.
        $p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 60' -PassThru -WindowStyle Hidden
        Start-Sleep -Milliseconds 200
        $deadPid = $p.Id
        Stop-Process -Id $deadPid -Force
        $p.WaitForExit()
        Start-Sleep -Milliseconds 200
        return $deadPid
    }

    function New-LockedGitInvoker {
        <#
          Simulates git: `worktree remove` fails with git's real lock message while the
          `locked` file exists, `worktree unlock` deletes it, and a subsequent remove
          succeeds. That makes "did removal succeed?" the observable under test.
        #>
        param([string]$LockFile, [ref]$Log)
        $logRef = $Log
        {
            param([string[]]$GitArgs)
            $joined = ($GitArgs -join ' ')
            $logRef.Value += $joined
            if ($joined -match 'worktree unlock') {
                Remove-Item -LiteralPath $LockFile -Force -ErrorAction SilentlyContinue
                return @{ exitCode = 0; output = '' }
            }
            if ($joined -match 'worktree remove') {
                if (Test-Path -LiteralPath $LockFile) {
                    return @{ exitCode = 1; output = "fatal: '$LockFile' is locked" }
                }
                return @{ exitCode = 0; output = '' }
            }
            if ($joined -match 'rev-parse') { return @{ exitCode = 0; output = "feature/x`n" } }
            return @{ exitCode = 0; output = '' }
        }.GetNewClosure()
    }

    function Invoke-Removal {
        param([object]$Repo, [ref]$Log)
        $invoker = New-LockedGitInvoker -LockFile $Repo.LockFile -Log $Log
        Remove-WorktreeSafely -RepoRoot $Repo.Repo -WorktreePath $Repo.Worktree `
            -MaxRetries 1 -BaseDelayMs 1 `
            -GitInvoker $invoker `
            -DirectoryRemover { param([string]$Path) if (Test-Path $Path) { Remove-Item -LiteralPath $Path -Recurse -Force } } `
            -LockerProbe { param([string]$Path) @() } `
            -Sleeper { param([int]$Ms) }
    }
}

AfterAll {
    if ($script:Scratch -and (Test-Path $script:Scratch)) {
        Remove-Item -LiteralPath $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe 'Remove-WorktreeSafely worktree lock owner liveness (issue #2409)' {

    It 'reclaims a worktree lock whose recorded owner PID is dead and the removal SUCCEEDS' {
        $deadPid = Get-DeadPid
        $repo = New-FakeRepo -OwnerJson (New-OwnerJson -OwnerPid $deadPid -StartUtc ([DateTime]::UtcNow.AddMinutes(-5).ToString('o')))
        $log = @()

        $result = Invoke-Removal -Repo $repo -Log ([ref]$log)

        $result.ownerState | Should -Be 'Dead'
        $result.reclaimed | Should -BeTrue
        $result.outcome | Should -Be 'reclaimed'
        Test-Path $repo.Worktree | Should -BeFalse
        ($log -join ' | ') | Should -Match 'worktree unlock'
    }

    It 'does NOT reclaim a worktree lock whose owner is alive and returns locked' {
        $self = Get-Process -Id $PID
        $repo = New-FakeRepo -OwnerJson (New-OwnerJson -OwnerPid $PID -StartUtc ($self.StartTime.ToUniversalTime().ToString('o')))
        $log = @()

        $result = Invoke-Removal -Repo $repo -Log ([ref]$log)

        $result.ownerState | Should -Be 'Alive'
        $result.outcome | Should -Be 'locked'
        $result.reclaimed | Should -BeFalse
        $result.reclaimAttempted | Should -BeFalse
        ($log -join ' | ') | Should -Not -Match 'worktree unlock'
        Test-Path $repo.LockFile | Should -BeTrue
    }

    It 'does NOT reclaim a worktree lock with an unreadable owner record (Unknown)' {
        $repo = New-FakeRepo -OwnerJson 'this is not json at all'
        $log = @()

        $result = Invoke-Removal -Repo $repo -Log ([ref]$log)

        $result.ownerState | Should -Be 'Unknown'
        $result.outcome | Should -Be 'locked'
        $result.reclaimed | Should -BeFalse
        ($log -join ' | ') | Should -Not -Match 'worktree unlock'
        Test-Path $repo.LockFile | Should -BeTrue
    }

    It 'does NOT reclaim a worktree lock whose PID was recycled onto another process (Reused)' {
        $self = Get-Process -Id $PID
        # Same live PID, but a start time that cannot be ours -> PID reuse.
        $repo = New-FakeRepo -OwnerJson (New-OwnerJson -OwnerPid $PID -StartUtc ($self.StartTime.ToUniversalTime().AddHours(-6).ToString('o')))
        $log = @()

        $result = Invoke-Removal -Repo $repo -Log ([ref]$log)

        $result.ownerState | Should -Be 'Reused'
        $result.outcome | Should -Be 'locked'
        $result.reclaimed | Should -BeFalse
        ($log -join ' | ') | Should -Not -Match 'worktree unlock'
    }

    It 'treats a zero-byte worktree lock as Unknown and does NOT reclaim it' {
        # The issue notes the validation lock used to write 0 bytes. A lock with no owner
        # record must fail CLOSED, never be mistaken for a dead owner.
        $repo = New-FakeRepo
        Set-Content -LiteralPath $repo.LockFile -Value '' -NoNewline
        $log = @()

        $result = Invoke-Removal -Repo $repo -Log ([ref]$log)

        $result.ownerState | Should -Be 'Unknown'
        $result.outcome | Should -Be 'locked'
        $result.reclaimed | Should -BeFalse
        ($log -join ' | ') | Should -Not -Match 'worktree unlock'
    }

    It 'reports ownerState None and outcome removed when nothing holds a lock' {
        $repo = New-FakeRepo
        $log = @()

        $result = Invoke-Removal -Repo $repo -Log ([ref]$log)

        $result.outcome | Should -Be 'removed'
        $result.ownerState | Should -Be 'None'
        $result.reclaimed | Should -BeFalse
    }

    It 'stamps an owner PID into a worktree lock that BotNexus itself writes' {
        $repo = New-FakeRepo
        $owner = Write-WorktreeLockOwner -LockFilePath $repo.LockFile

        (Get-Item -LiteralPath $repo.LockFile).Length | Should -BeGreaterThan 0
        $owner.Pid | Should -Be $PID
        Get-WorktreeLockOwnerState -LockFilePath $repo.LockFile | Should -Be 'Alive'
    }

    It 'lets only ONE concurrent reclaimer win the shared reclaim guard' {
        $repo = New-FakeRepo -OwnerJson 'x'
        $first = Enter-WorktreeReclaimGuard -LockFilePath $repo.LockFile
        try {
            $first | Should -Not -BeNullOrEmpty
            Enter-WorktreeReclaimGuard -LockFilePath $repo.LockFile | Should -BeNullOrEmpty
        }
        finally { Exit-WorktreeReclaimGuard -Guard $first }
        Enter-WorktreeReclaimGuard -LockFilePath $repo.LockFile | Should -Not -BeNullOrEmpty
    }
}
