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

function Get-BotNexusValidationLock {
    <#
    .SYNOPSIS
        Acquires the global BotNexus validation lock, waiting a bounded time on contention.

    .DESCRIPTION
        All BotNexus validation is serialized host-wide because separate worktrees still
        compete for the same CPU, Defender scans, package cache and tool processes. Before
        #2331 contention threw immediately, which turned a *successful* concurrent run into
        a hook failure and taught workers to use --no-verify. Waiting a bounded time and
        reporting non-acquisition as data lets the caller decide: the advisory hook skips,
        the authoritative gate still fails.

    .PARAMETER TimeoutSeconds
        Maximum time to wait for the lock. Zero attempts a single acquisition.

    .PARAMETER PollMilliseconds
        Interval between acquisition attempts.

    .PARAMETER LockPath
        Override the lock file location. Defaults to the global host lock; tests supply
        an isolated path so they never contend with a real validation run.

    .OUTPUTS
        PSCustomObject with Acquired, Handle (IDisposable or $null), Path and WaitedSeconds.
    #>
    [CmdletBinding()]
    param(
        [int]$TimeoutSeconds = 0,
        [int]$PollMilliseconds = 500,
        [string]$LockPath = (Join-Path ([IO.Path]::GetTempPath()) 'botnexus-local-validation-global.lock')
    )

    if ($PollMilliseconds -lt 1) { $PollMilliseconds = 1 }
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(0, $TimeoutSeconds))
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $announced = $false
    $handle = $null

    while ($true) {
        try {
            $handle = [IO.File]::Open($LockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            break
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { break }
            if (-not $announced) {
                Write-Host "[validation] another BotNexus validation holds the global lock; waiting up to ${TimeoutSeconds}s." -ForegroundColor Yellow
                $announced = $true
            }
            Start-Sleep -Milliseconds $PollMilliseconds
        }
    }

    $stopwatch.Stop()
    [pscustomobject]@{
        Acquired      = ($null -ne $handle)
        Handle        = $handle
        Path          = $LockPath
        WaitedSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    }
}

Export-ModuleMember -Function Invoke-BotNexusValidationStep, Get-BotNexusValidationLock
