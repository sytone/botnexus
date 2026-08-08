<#
.SYNOPSIS
    Runs the local repository validation gate: strict (authoritative) or hook (fast, impacted-only).

.DESCRIPTION
    Two distinct jobs live here, and issue #2331 was caused by conflating them.

    * 'strict' is the authoritative pre-push gate: one full solution build, impacted tests
      plus the architecture/scenario safety nets, and Playwright. It is allowed to be slow
      and it must never be skipped, so it waits for the global lock without a bound.
    * 'hook' is the advisory pre-commit gate: impacted projects only, built and tested
      without a full-solution build and without Playwright. It is bounded per step, and if
      another validation already holds the global lock it SKIPS with a clear message rather
      than exiting non-zero. A gate that fails on contention just teaches people to pass
      --no-verify, which is exactly what #2331 documented.

    Every step runs under Invoke-BotNexusValidationStep, so an overrun names the step that
    exceeded its budget instead of producing a mystery hang, and every step is launched with
    the repository root as its working directory (see the .editorconfig note below).
#>
[CmdletBinding()]
param(
    [string]$WorktreePath = (Get-Location).Path,
    [string]$BaseRef = 'origin/main',
    [ValidateSet('strict', 'impacted', 'full', 'playwright', 'hook')]
    [string]$Mode = 'strict',

    # Bounded wait for the global validation lock. Hook mode waits briefly then skips;
    # the authoritative gate waits indefinitely because it must not be bypassed.
    [int]$LockWaitSeconds = -1,

    # When set, failing to acquire the lock is a clean skip (exit 0) rather than a failure.
    [switch]$SkipOnLockContention,

    # Per-step budget in seconds. Zero or negative means unbounded.
    [int]$BuildTimeoutSeconds = -1,
    [int]$TestTimeoutSeconds = -1,

    [string]$LockPath = (Join-Path ([IO.Path]::GetTempPath()) 'botnexus-local-validation-global.lock')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ValidationSteps.psm1') -Force

$repoRoot = (& git -C $WorktreePath rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "WorktreePath is not inside a git repository: $WorktreePath"
}

# Documented, mode-specific defaults (#2331 acceptance: "realistic, documented timeout").
# Hook budgets are sized for an impacted-only build/test on a warm developer machine; the
# authoritative gate is deliberately unbounded so it can never be silently truncated.
if ($LockWaitSeconds -lt 0) { $LockWaitSeconds = if ($Mode -eq 'hook') { 120 } else { 0 } }
if ($BuildTimeoutSeconds -lt 0) { $BuildTimeoutSeconds = if ($Mode -eq 'hook') { 300 } else { 0 } }
if ($TestTimeoutSeconds -lt 0) { $TestTimeoutSeconds = if ($Mode -eq 'hook') { 600 } else { 0 } }

# Serialize all BotNexus validation host-wide, not merely validation in one worktree.
# Separate worktrees still compete for the same CPU, Defender, package cache and tool
# processes. Unlike the pre-#2331 behaviour this WAITS a bounded time instead of throwing.
$waitSeconds = if ($Mode -eq 'hook') { $LockWaitSeconds } else { [int][Math]::Max($LockWaitSeconds, 86400) }
$lock = Get-BotNexusValidationLock -TimeoutSeconds $waitSeconds -LockPath $LockPath
if (-not $lock.Acquired) {
    if ($SkipOnLockContention) {
        Write-Host "Another BotNexus validation held the global lock for $($lock.WaitedSeconds)s; skipping this advisory run. Full validation still runs at pre-push and in CI." -ForegroundColor Yellow
        exit 0
    }
    $ownerDetail = if ($null -ne $lock.Owner) { "PID $($lock.Owner.Pid) on $($lock.Owner.Machine), held since $($lock.Owner.AcquiredUtc)" } else { 'an unidentified process (no owner record)' }
    Write-Host "Another BotNexus local validation is still running after $($lock.WaitedSeconds)s: $($lock.Path) is held by $ownerDetail (owner state: $($lock.OwnerState)). If that process is gone, delete the file to clear the tombstone." -ForegroundColor Red
    exit 1
}

$testImpacted = Join-Path $PSScriptRoot 'test-impacted.ps1'
$playwrightProject = Join-Path $repoRoot 'tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj'
# The E2E run goes through Invoke-E2ETests.ps1 rather than bare `dotnet test` so a
# run whose skip count meets or exceeds its passed count is reported as a FAILURE
# instead of "Passed!" (issue #2739: the fixture prebuild race turned the whole
# suite into 265 silent skips and the gate still exited 0).
$e2eRunner = Join-Path $PSScriptRoot 'Invoke-E2ETests.ps1'
$pwshPath = (Get-Process -Id $PID).Path

try {
    Write-Host "Running globally serialized local validation ($Mode) after waiting $($lock.WaitedSeconds)s for the lock." -ForegroundColor Yellow

    # Invalidate any prior receipt before producing new evidence. A failed or interrupted
    # run must never leave a stale-but-matching receipt behind (issue #2143 fail-closed).
    try {
        Import-Module (Join-Path $PSScriptRoot 'ValidationReceipt.psm1') -Force
        Remove-BotNexusValidationReceipt -WorktreePath $repoRoot
    }
    catch { Write-Warning "Could not clear prior validation receipt: $($_.Exception.Message)" }

    $steps = [Collections.Generic.List[object]]::new()

    # Every step is launched with -WorkingDirectory $repoRoot. MSBuild resolves
    # .editorconfig and analyzer configuration relative to the current directory, so a
    # process that inherits a working directory belonging to a different worktree can
    # report "the .editorconfig file could not be found" (#2331 item 4). Pinning the
    # working directory per step removes that inheritance entirely.
    if ($Mode -ne 'hook') {
        # The authoritative gate keeps its single full-solution build.
        $steps.Add((Invoke-BotNexusValidationStep -Name 'full solution build' -FilePath 'dotnet' `
                    -Arguments @('build', (Join-Path $repoRoot 'dirs.proj'), '--nologo', '--verbosity', 'minimal', '--tl:off') `
                    -WorkingDirectory $repoRoot -TimeoutSeconds $BuildTimeoutSeconds))
        if ($steps[-1].ExitCode -ne 0) { exit $steps[-1].ExitCode }
    }

    switch ($Mode) {
        'hook' {
            # Impacted projects only, and test-impacted builds them itself. No full-solution
            # build and no Playwright: that is the pre-push gate's job, not every commit's.
            $steps.Add((Invoke-BotNexusValidationStep -Name 'impacted tests (hook scope)' -FilePath $pwshPath `
                        -Arguments @('-NoProfile', '-File', $testImpacted, '-From', $BaseRef) `
                        -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds))
        }
        'full' {
            $steps.Add((Invoke-BotNexusValidationStep -Name 'full test suite' -FilePath $pwshPath `
                        -Arguments @('-NoProfile', '-File', $testImpacted, '-All', '-NoBuild') `
                        -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds))
        }
        'playwright' {
            $steps.Add((Invoke-BotNexusValidationStep -Name 'playwright end-to-end tests' -FilePath $pwshPath `
                        -Arguments @('-NoProfile', '-File', $e2eRunner, '-Project', $playwrightProject, '-Configuration', 'Debug', '-NoBuild') `
                        -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds))
        }
        default {
            $steps.Add((Invoke-BotNexusValidationStep -Name 'impacted tests and safety nets' -FilePath $pwshPath `
                        -Arguments @('-NoProfile', '-File', $testImpacted, '-From', $BaseRef, '-NoBuild') `
                        -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds))
            if ($steps[-1].ExitCode -eq 0 -and $Mode -eq 'strict') {
                $steps.Add((Invoke-BotNexusValidationStep -Name 'playwright end-to-end tests' -FilePath $pwshPath `
                            -Arguments @('-NoProfile', '-File', $e2eRunner, '-Project', $playwrightProject, '-Configuration', 'Debug', '-NoBuild') `
                            -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds))
            }
        }
    }

    $failedStep = $steps | Where-Object { $_.ExitCode -ne 0 } | Select-Object -First 1
    if ($null -ne $failedStep) {
        if ($failedStep.TimedOut) {
            Write-Host "Validation FAILED: step '$($failedStep.Name)' exceeded its $($failedStep.TimeoutSeconds)s budget. Re-run that step directly to diagnose it, or raise the budget with -TestTimeoutSeconds/-BuildTimeoutSeconds." -ForegroundColor Red
        }
        else {
            Write-Host "Validation FAILED at step '$($failedStep.Name)' (exit $($failedStep.ExitCode)) after $($failedStep.DurationSeconds)s." -ForegroundColor Red
        }
        exit $failedStep.ExitCode
    }

    # Emit a content-addressed validation receipt only after every required command has
    # succeeded (issue #2143). Hook mode is a subset of the required policy, so it never
    # emits a receipt: an advisory pass must not certify the candidate for push.
    if ($Mode -ne 'hook') {
        try {
            Import-Module (Join-Path $PSScriptRoot 'ValidationReceipt.psm1') -Force
            $emitted = New-BotNexusValidationReceipt -Scope $Mode -TestProjects @('impacted+safety-nets') -WorktreePath $repoRoot -BaseRef $BaseRef
            if ($null -ne $emitted) {
                Write-Host "Validation receipt written: $($emitted.Path)" -ForegroundColor DarkGray
            }
        }
        catch {
            # Receipt emission is a best-effort optimization; never fail a passing run because
            # of it. Absence of a receipt simply means the next commit revalidates.
            Write-Warning "Could not write validation receipt: $($_.Exception.Message)"
        }
    }

    $total = ($steps | Measure-Object -Property DurationSeconds -Sum).Sum
    Write-Host "Local validation ($Mode) passed in ${total}s across $($steps.Count) step(s)." -ForegroundColor Green
    exit 0
}
finally {
    # Idempotent: Get-BotNexusValidationLock also registered this same release on
    # PowerShell.Exiting and AppDomain ProcessExit, because a `finally` alone is skipped
    # by an abrupt exit and stranded the lock for every later worker (issue #2393).
    Remove-BotNexusValidationLock -Lock $lock
}
