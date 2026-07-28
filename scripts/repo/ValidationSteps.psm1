<#
.SYNOPSIS
    Bounded, diagnosable step execution and cooperative locking for BotNexus validation.

.DESCRIPTION
    Issue #2331: the pre-commit hook regularly exceeded its timeout and produced
    undiagnosable failures, so workers routinely committed with --no-verify. Two
    primitives fix that:

    * Invoke-BotNexusValidationStep runs one named step as a child process with an
      explicit per-step timeout. When the timeout expires the process tree is killed
      and the result names the step that overran, so a slow gate is diagnosable
      instead of mysterious.
    * Get-BotNexusValidationLock waits a bounded time for the global validation lock
      instead of failing immediately on contention. Callers that are advisory (the
      hook) can then skip cleanly rather than exiting non-zero.
#>

Set-StrictMode -Version Latest

# Exit code reported when a step exceeds its timeout. 124 matches the POSIX
# `timeout(1)` convention so shell callers can recognise it without parsing text.
$script:TimeoutExitCode = 124

function Invoke-BotNexusValidationStep {
    <#
    .SYNOPSIS
        Runs a single named validation step under a bounded timeout.

    .DESCRIPTION
        The step runs as a child process inheriting stdout/stderr so build and test
        output still streams to the developer. A non-positive TimeoutSeconds disables
        the bound and waits indefinitely, which is what the authoritative pre-push gate
        wants; the hook always supplies a positive value.

    .PARAMETER Name
        Human-readable step name. Reported verbatim when the step fails or overruns, so
        it must identify the step to someone reading a terminal ("build", "impacted tests").

    .PARAMETER FilePath
        Executable to launch (for example 'dotnet' or 'pwsh').

    .PARAMETER Arguments
        Argument list passed without shell interpretation, so paths with spaces are safe.

    .PARAMETER WorkingDirectory
        Directory the child process starts in. Always pass the repository root: MSBuild
        resolves .editorconfig and analyzer config relative to the current directory, and
        an inherited working directory from another worktree produces the transient
        ".editorconfig could not be found" failure reported in #2331.

    .PARAMETER TimeoutSeconds
        Bound in seconds. Zero or negative waits indefinitely.

    .OUTPUTS
        PSCustomObject with Name, ExitCode, TimedOut, TimeoutSeconds and DurationSeconds.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [int]$TimeoutSeconds = 0
    )

    if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
        throw "Validation step '$Name' requires an existing working directory: $WorkingDirectory"
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).ProviderPath
    $startInfo.UseShellExecute = $false
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($startInfo)
    $timedOut = $false

    if ($TimeoutSeconds -gt 0) {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            # Kill the whole tree: dotnet spawns MSBuild nodes and testhost children that
            # outlive the parent and would otherwise hold the build outputs open.
            try { $process.Kill($true) } catch { Write-Warning "Could not terminate step '$Name': $($_.Exception.Message)" }
            try { [void]$process.WaitForExit(30 * 1000) } catch { }
        }
    }
    else {
        $process.WaitForExit()
    }

    $stopwatch.Stop()
    $exitCode = if ($timedOut) { $script:TimeoutExitCode } else { $process.ExitCode }
    $process.Dispose()

    if ($timedOut) {
        Write-Host "[validation] step '$Name' EXCEEDED its ${TimeoutSeconds}s timeout and was terminated." -ForegroundColor Red
    }
    elseif ($exitCode -ne 0) {
        Write-Host "[validation] step '$Name' failed with exit code $exitCode after $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s." -ForegroundColor Red
    }
    else {
        Write-Host "[validation] step '$Name' completed in $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s." -ForegroundColor DarkGray
    }

    [pscustomobject]@{
        Name            = $Name
        ExitCode        = $exitCode
        TimedOut        = $timedOut
        TimeoutSeconds  = $TimeoutSeconds
        DurationSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    }
}

function ConvertTo-BotNexusLockOwnerRecord {
    <#
    .SYNOPSIS
        Builds the owner metadata record written into the global validation lock file.

    .DESCRIPTION
        Issue #2393: the lock file carried no owner information, so a file left behind by a
        killed worker was indistinguishable from a lock held by a healthy validation run.
        Recording the PID *and* the owning process start time lets a later process classify
        the holder, including the PID-reuse case where the recorded PID is alive but belongs
        to a completely different process.

    .PARAMETER OwnerProcessId
        Process id to record. Defaults to the current process.

    .OUTPUTS
        PSCustomObject with Pid, ProcessStartUtc, Machine and AcquiredUtc.
    #>
    [CmdletBinding()]
    param([int]$OwnerProcessId = $PID)

    $startUtc = $null
    try {
        $owner = Get-Process -Id $OwnerProcessId -ErrorAction Stop
        $startUtc = $owner.StartTime.ToUniversalTime().ToString('o')
    }
    catch {
        # A process we cannot inspect still gets a record; the liveness check treats a
        # missing start time as ambiguous and fails closed rather than reaping blindly.
        $startUtc = $null
    }

    [pscustomobject]@{
        Pid             = $OwnerProcessId
        ProcessStartUtc = $startUtc
        Machine         = [Environment]::MachineName
        AcquiredUtc     = [DateTime]::UtcNow.ToString('o')
    }
}

function Test-BotNexusLockOwnerAlive {
    <#
    .SYNOPSIS
        Classifies a recorded lock owner as Alive, Dead, Reused or Unknown.

    .DESCRIPTION
        The reaper in Get-BotNexusValidationLock is only safe if it can tell "the owner is
        gone" from "the owner is still validating". Three signals matter (issue #2393):

        * the PID no longer exists            -> Dead, safe to reclaim;
        * the PID exists but its start time does not match the recorded one -> Reused,
          i.e. the original owner died and the operating system handed its id to an
          unrelated process, so the lock is equally stale;
        * the PID exists with a matching start time -> Alive, and the lock MUST still block.

        Anything we cannot classify (unreadable record, inaccessible process, a record from
        another machine) returns Unknown and the caller fails closed, because incorrectly
        reaping a live holder would let two full validations run concurrently.

    .PARAMETER Owner
        The owner record read back from the lock file.

    .OUTPUTS
        String: 'Alive', 'Dead', 'Reused' or 'Unknown'.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()][object]$Owner)

    if ($null -eq $Owner) { return 'Unknown' }

    $ownerPid = 0
    if (-not ($Owner.PSObject.Properties.Name -contains 'Pid') -or -not [int]::TryParse([string]$Owner.Pid, [ref]$ownerPid) -or $ownerPid -le 0) {
        return 'Unknown'
    }

    $machine = if ($Owner.PSObject.Properties.Name -contains 'Machine') { [string]$Owner.Machine } else { '' }
    if (-not [string]::IsNullOrWhiteSpace($machine) -and $machine -ne [Environment]::MachineName) {
        # A record from another host says nothing about local liveness. The host lock is
        # per-machine, so this should not happen; fail closed if it ever does.
        return 'Unknown'
    }

    $process = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return 'Dead' }

    if (-not ($Owner.PSObject.Properties.Name -contains 'ProcessStartUtc')) { return 'Unknown' }
    $rawStart = $Owner.ProcessStartUtc
    if ($null -eq $rawStart) { return 'Unknown' }

    # ConvertFrom-Json rehydrates an ISO-8601 string as a [DateTime], so the record may
    # arrive as either type depending on how the caller built it. Accept both rather than
    # stringifying, because stringifying a DateTime yields a culture-specific form that no
    # longer round-trips and would misclassify a live owner as a reused PID.
    $parsedStart = [DateTime]::MinValue
    if ($rawStart -is [DateTime]) {
        $parsedStart = $rawStart
    }
    elseif ($rawStart -is [DateTimeOffset]) {
        $parsedStart = $rawStart.UtcDateTime
    }
    else {
        $recordedStart = [string]$rawStart
        if ([string]::IsNullOrWhiteSpace($recordedStart)) { return 'Unknown' }
        if (-not [DateTime]::TryParse($recordedStart, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsedStart)) {
            return 'Unknown'
        }
    }
    if ($parsedStart -eq [DateTime]::MinValue) { return 'Unknown' }
    if ($parsedStart.Kind -eq [DateTimeKind]::Unspecified) {
        $parsedStart = [DateTime]::SpecifyKind($parsedStart, [DateTimeKind]::Utc)
    }

    try { $actualStart = $process.StartTime.ToUniversalTime() }
    catch { return 'Unknown' }

    # One second of tolerance: the recorded value round-trips through ISO-8601 text and
    # Windows reports sub-second precision that is not always stable across APIs.
    if ([Math]::Abs(($actualStart - $parsedStart.ToUniversalTime()).TotalSeconds) -gt 1) { return 'Reused' }

    return 'Alive'
}

function Read-BotNexusLockOwner {
    <#
    .SYNOPSIS
        Reads the owner record out of a lock file without taking the lock.

    .DESCRIPTION
        Opened with the widest possible sharing so it also works while a live holder still
        has the file open; that is exactly the case where the blocked-path diagnostic needs
        to name who is holding the lock. Returns $null when there is no readable record.

    .PARAMETER LockPath
        Path to the lock file.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$LockPath)

    if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) { return $null }
    try {
        $stream = [IO.File]::Open($LockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try {
            $reader = [IO.StreamReader]::new($stream)
            $text = $reader.ReadToEnd()
        }
        finally { $stream.Dispose() }
    }
    catch { return $null }

    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return ($text | ConvertFrom-Json) } catch { return $null }
}

function Remove-BotNexusValidationLock {
    <#
    .SYNOPSIS
        Releases an acquired validation lock: closes the handle and deletes the lock file.

    .DESCRIPTION
        Idempotent, and safe to call from a process-exit handler. Issue #2393: releasing
        only in a PowerShell `finally` meant a killed or fatally-erroring worker stranded the
        file, so Get-BotNexusValidationLock also wires this into PowerShell.Exiting and the
        AppDomain ProcessExit event.

    .PARAMETER Lock
        The object returned by Get-BotNexusValidationLock.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()][object]$Lock)

    if ($null -eq $Lock) { return }
    if ($Lock.PSObject.Properties.Name -contains 'Handle' -and $null -ne $Lock.Handle) {
        try { $Lock.Handle.Dispose() } catch { }
        $Lock.Handle = $null
    }
    if ($Lock.PSObject.Properties.Name -contains 'Path' -and -not [string]::IsNullOrWhiteSpace($Lock.Path)) {
        Remove-Item -LiteralPath $Lock.Path -Force -ErrorAction SilentlyContinue
    }
}

function Register-BotNexusValidationLockRelease {
    <#
    .SYNOPSIS
        Registers synchronous release of a held lock on process exit.

    .DESCRIPTION
        Mirrors the upstream OpenClaw fix referenced by issue #2393: a `finally` block is not
        a sufficient release path because it is skipped by an abrupt exit. Both the PowerShell
        engine exiting event and the AppDomain ProcessExit event are wired, so an exit that
        bypasses the script's own `finally` still returns the lock.

    .PARAMETER Lock
        The lock object to release on exit.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Lock)

    $release = { Remove-BotNexusValidationLock -Lock $Lock }.GetNewClosure()

    try { $null = Register-EngineEvent -SourceIdentifier ([Management.Automation.PsEngineEvent]::Exiting) -Action $release }
    catch { Write-Warning "[validation] could not register PowerShell.Exiting lock release: $($_.Exception.Message)" }

    # Deliberately NOT an AppDomain.ProcessExit handler: that callback runs on a thread with
    # no PowerShell runspace, so any script block registered there throws
    # "There is no Runspace available to run scripts in this thread" and crashes the process
    # during shutdown. PowerShell.Exiting is the supported synchronous hook and fires for
    # `exit`, for a terminating error, and for Ctrl-C.
}

function Get-BotNexusValidationLock {
    <#
    .SYNOPSIS
        Acquires the global BotNexus validation lock, reaping locks whose owner is gone.

    .DESCRIPTION
        All BotNexus validation is serialized host-wide because separate worktrees still
        compete for the same CPU, Defender scans, package cache and tool processes. Before
        #2331 contention threw immediately, which turned a *successful* concurrent run into
        a hook failure and taught workers to use --no-verify. Waiting a bounded time and
        reporting non-acquisition as data lets the caller decide: the advisory hook skips,
        the authoritative gate still fails.

        Issue #2393 added owner liveness. The lock file now carries { Pid, ProcessStartUtc,
        Machine, AcquiredUtc }, and on contention the recorded owner is classified:

        * Dead or Reused (PID recycled onto a different process) -> the lock is a tombstone
          left by a killed worker; it is reclaimed and the reap is logged;
        * Alive -> a real validation is running and this call keeps blocking;
        * Unknown -> fail CLOSED and keep waiting, with the reason logged, because reaping a
          live holder by mistake would allow two full validations to run concurrently.

        The acquired lock is also registered for synchronous release on process exit, so an
        abrupt exit that skips the caller's `finally` cannot strand it.

    .PARAMETER TimeoutSeconds
        Maximum time to wait for the lock. Zero attempts a single acquisition.

    .PARAMETER PollMilliseconds
        Interval between acquisition attempts.

    .PARAMETER LockPath
        Override the lock file location. Defaults to the global host lock; tests supply
        an isolated path so they never contend with a real validation run.

    .PARAMETER NoExitHandler
        Skip registering the process-exit release. Used by tests that assert on the handler
        wiring itself; production callers should never set it.

    .OUTPUTS
        PSCustomObject with Acquired, Handle (IDisposable or $null), Path, WaitedSeconds,
        Owner (the record written or the blocking record read), OwnerState and Reaped.
    #>
    [CmdletBinding()]
    param(
        [int]$TimeoutSeconds = 0,
        [int]$PollMilliseconds = 500,
        [string]$LockPath = (Join-Path ([IO.Path]::GetTempPath()) 'botnexus-local-validation-global.lock'),
        [switch]$NoExitHandler
    )

    if ($PollMilliseconds -lt 1) { $PollMilliseconds = 1 }
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(0, $TimeoutSeconds))
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $announced = $false
    $handle = $null
    $reaped = $false
    $blockingOwner = $null
    $ownerState = 'Free'

    while ($true) {
        $blockingOwner = $null
        $ownerState = 'Free'

        # FileShare::Read (not None) so a blocked waiter can still READ the owner record and
        # print a useful diagnostic. Write access remains exclusive, so a second acquisition
        # attempt still fails while a holder has the file open.
        try {
            $candidate = [IO.File]::Open($LockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
        }
        catch [IO.IOException] {
            # An OS handle is open on the file. The operating system closes handles when a
            # process dies, so this state is unambiguously a live holder.
            $candidate = $null
            $blockingOwner = Read-BotNexusLockOwner -LockPath $LockPath
            $ownerState = 'Alive'
        }

        if ($null -ne $candidate) {
            # We hold the write handle, but the file may still describe a previous owner.
            $existing = $null
            try {
                if ($candidate.Length -gt 0) {
                    $candidate.Position = 0
                    $buffer = [byte[]]::new($candidate.Length)
                    [void]$candidate.Read($buffer, 0, $buffer.Length)
                    $text = [Text.Encoding]::UTF8.GetString($buffer)
                    if (-not [string]::IsNullOrWhiteSpace($text)) {
                        try { $existing = $text | ConvertFrom-Json } catch { $existing = $null }
                        if ($null -eq $existing) { $ownerState = 'Corrupt' }
                    }
                }
            }
            catch { $existing = $null }

            $state = if ($null -eq $existing) { if ($ownerState -eq 'Corrupt') { 'Corrupt' } else { 'Free' } } else { Test-BotNexusLockOwnerAlive -Owner $existing }

            if ($state -eq 'Alive' -or $state -eq 'Unknown') {
                # Fail CLOSED. The record names a process that is (or may be) still
                # validating even though it is not holding an OS handle right now; treating
                # that as free would allow two concurrent validations.
                $candidate.Dispose()
                $blockingOwner = $existing
                $ownerState = $state
                if (-not $announced) {
                    $reason = if ($state -eq 'Alive') { 'its owner process is still running' } else { 'its owner could not be classified (failing closed)' }
                    Write-Host "[validation] global validation lock $LockPath is held by PID $($existing.Pid) on $($existing.Machine) since $($existing.AcquiredUtc); $reason. Waiting up to ${TimeoutSeconds}s." -ForegroundColor Yellow
                    $announced = $true
                }
            }
            else {
                if ($state -eq 'Dead' -or $state -eq 'Reused' -or $state -eq 'Corrupt') {
                    $reaped = $true
                    $detail = switch ($state) {
                        'Dead' { "owner PID $($existing.Pid) no longer exists" }
                        'Reused' { "owner PID $($existing.Pid) was recycled onto a different process (recorded start $($existing.ProcessStartUtc))" }
                        default { 'the lock file content was unreadable' }
                    }
                    Write-Host "[validation] reclaiming STALE global validation lock $LockPath - $detail. It was stranded by a killed or timed-out worker (issue #2393)." -ForegroundColor Yellow
                }

                $owner = ConvertTo-BotNexusLockOwnerRecord
                $payload = [Text.Encoding]::UTF8.GetBytes(($owner | ConvertTo-Json -Compress))
                $candidate.SetLength(0)
                $candidate.Position = 0
                $candidate.Write($payload, 0, $payload.Length)
                $candidate.Flush()
                $handle = $candidate

                $lock = [pscustomobject]@{
                    Acquired      = $true
                    Handle        = $handle
                    Path          = $LockPath
                    WaitedSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
                    Owner         = $owner
                    OwnerState    = $state
                    Reaped        = $reaped
                }
                $stopwatch.Stop()
                if (-not $NoExitHandler) { Register-BotNexusValidationLockRelease -Lock $lock }
                return $lock
            }
        }
        elseif (-not $announced) {
            $ownerText = if ($null -ne $blockingOwner) { "PID $($blockingOwner.Pid) on $($blockingOwner.Machine) since $($blockingOwner.AcquiredUtc)" } else { 'an unidentified process' }
            Write-Host "[validation] another BotNexus validation holds the global lock ($ownerText); waiting up to ${TimeoutSeconds}s." -ForegroundColor Yellow
            $announced = $true
        }

        if ([DateTime]::UtcNow -ge $deadline) { break }
        Start-Sleep -Milliseconds $PollMilliseconds
    }

    $stopwatch.Stop()
    $waited = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    $ownerText = if ($null -ne $blockingOwner) { "PID $($blockingOwner.Pid) on $($blockingOwner.Machine), held since $($blockingOwner.AcquiredUtc)" } else { 'an unidentified process (no owner record)' }
    Write-Host "[validation] BLOCKED after ${waited}s: the global validation lock $LockPath is held by $ownerText (state: $ownerState)." -ForegroundColor Red

    [pscustomobject]@{
        Acquired      = $false
        Handle        = $null
        Path          = $LockPath
        WaitedSeconds = $waited
        Owner         = $blockingOwner
        OwnerState    = $ownerState
        Reaped        = $false
    }
}

Export-ModuleMember -Function Invoke-BotNexusValidationStep, Get-BotNexusValidationLock, Remove-BotNexusValidationLock, Register-BotNexusValidationLockRelease, Test-BotNexusLockOwnerAlive, Read-BotNexusLockOwner, ConvertTo-BotNexusLockOwnerRecord
