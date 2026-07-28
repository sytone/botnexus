[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ValidationSteps.psm1') -Force

$failures = [Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { $failures.Add($Message) } }
function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) { $failures.Add("$Message Expected '$Expected', got '$Actual'.") }
}

$pwshPath = (Get-Process -Id $PID).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) "botnexus-validationsteps-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

try {
    # --- Invoke-BotNexusValidationStep --------------------------------------

    # A successful step reports its own name so a log line identifies the stage.
    $result = Invoke-BotNexusValidationStep -Name 'fast-step' -FilePath $pwshPath `
        -Arguments @('-NoProfile', '-Command', 'exit 0') -WorkingDirectory $scratch -TimeoutSeconds 60
    Assert-Equal 'fast-step' $result.Name 'Step result should carry the step name.'
    Assert-Equal 0 $result.ExitCode 'A successful step should report exit code 0.'
    Assert-Equal $false $result.TimedOut 'A fast step should not report a timeout.'

    # A failing step preserves the child exit code rather than masking it.
    $result = Invoke-BotNexusValidationStep -Name 'failing-step' -FilePath $pwshPath `
        -Arguments @('-NoProfile', '-Command', 'exit 7') -WorkingDirectory $scratch -TimeoutSeconds 60
    Assert-Equal 7 $result.ExitCode 'A failing step should preserve its exit code.'
    Assert-Equal $false $result.TimedOut 'A failing step should not be reported as a timeout.'

    # The core #2331 requirement: an overrunning step is terminated and NAMED, so the
    # developer can see which stage blew the budget instead of a mystery hang.
    $result = Invoke-BotNexusValidationStep -Name 'slow-step' -FilePath $pwshPath `
        -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') -WorkingDirectory $scratch -TimeoutSeconds 2
    Assert-Equal $true $result.TimedOut 'An overrunning step must report TimedOut.'
    Assert-Equal 'slow-step' $result.Name 'A timed-out step must identify which step overran.'
    Assert-Equal 124 $result.ExitCode 'A timed-out step should use the POSIX timeout exit code.'
    Assert-True ($result.DurationSeconds -lt 25) 'A timed-out step must actually be terminated, not merely reported.'

    # The step must run in the directory it is given. MSBuild resolves .editorconfig
    # relative to the working directory, so an inherited directory from another worktree
    # is the likely cause of the transient ".editorconfig could not be found" failure.
    $probeDirectory = Join-Path $scratch 'probe'
    New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
    $probeOutput = Join-Path $scratch 'cwd.txt'
    $null = Invoke-BotNexusValidationStep -Name 'cwd-step' -FilePath $pwshPath `
        -Arguments @('-NoProfile', '-Command', "(Get-Location).Path | Set-Content -LiteralPath '$probeOutput'") `
        -WorkingDirectory $probeDirectory -TimeoutSeconds 60
    $observed = (Get-Content -LiteralPath $probeOutput -Raw).Trim()
    Assert-Equal (Resolve-Path $probeDirectory).ProviderPath $observed 'A step must execute in the supplied working directory.'

    # A missing working directory is a caller bug and must fail loudly, not silently
    # inherit whatever directory the hook happened to start in.
    $threw = $false
    try {
        $null = Invoke-BotNexusValidationStep -Name 'bad-cwd' -FilePath $pwshPath `
            -Arguments @('-NoProfile', '-Command', 'exit 0') `
            -WorkingDirectory (Join-Path $scratch 'does-not-exist') -TimeoutSeconds 5
    }
    catch { $threw = $true }
    Assert-True $threw 'A nonexistent working directory must throw rather than be inherited.'

    # --- Get-BotNexusValidationLock -----------------------------------------

    $lockPath = Join-Path $scratch 'validation.lock'

    # Uncontended acquisition succeeds immediately.
    $lock = Get-BotNexusValidationLock -TimeoutSeconds 5 -LockPath $lockPath
    Assert-Equal $true $lock.Acquired 'An uncontended lock should be acquired.'
    Assert-True ($null -ne $lock.Handle) 'An acquired lock should expose a disposable handle.'

    # Contention must be reported as data (Acquired=$false), never as a throw. The old
    # behaviour threw on contention, which failed the hook and forced --no-verify.
    $contended = Get-BotNexusValidationLock -TimeoutSeconds 1 -PollMilliseconds 50 -LockPath $lockPath
    Assert-Equal $false $contended.Acquired 'A contended lock must report non-acquisition rather than throwing.'
    Assert-True ($null -eq $contended.Handle) 'A non-acquired lock must not expose a handle.'
    Assert-True ($contended.WaitedSeconds -ge 0.5) 'A contended acquisition must actually wait for the bounded timeout.'

    # Contention resolves: once the holder releases, a waiter acquires within its budget.
    # Release goes through Remove-BotNexusValidationLock (#2393): disposing the handle alone
    # leaves the owner record on disk, which is exactly the stranded-tombstone state the
    # liveness reaper now has to classify.
    Remove-BotNexusValidationLock -Lock $lock
    $reacquired = Get-BotNexusValidationLock -TimeoutSeconds 5 -PollMilliseconds 50 -LockPath $lockPath
    Assert-Equal $true $reacquired.Acquired 'A waiter must acquire the lock once the holder releases it.'
    Remove-BotNexusValidationLock -Lock $reacquired

    # --- Wiring assertions on the callers -----------------------------------

    $repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
    $localRunner = Get-Content (Join-Path $repoRoot 'scripts/repo/Invoke-LocalValidation.ps1') -Raw
    Assert-True ($localRunner -match 'Get-BotNexusValidationLock') 'The local runner must use the bounded waiting lock.'
    Assert-True ($localRunner -match 'Invoke-BotNexusValidationStep') 'The local runner must run steps under named, bounded timeouts.'
    Assert-True ($localRunner -notmatch "throw \`"Another BotNexus local validation is already running") `
        'The local runner must no longer throw on lock contention.'
    Assert-True ($localRunner -match "'hook'") 'The local runner must support an impacted-only hook mode.'

    $hook = Get-Content (Join-Path $repoRoot 'scripts/repo/githooks/pre-commit') -Raw
    Assert-True ($hook -match '-Hook') 'The pre-commit hook must invoke the bounded hook-scoped gate.'

    $validate = Get-Content (Join-Path $repoRoot 'scripts/repo/Validate-PreCommit.ps1') -Raw
    Assert-True ($validate -match '\[switch\]\$Hook') 'Validate-PreCommit must expose a -Hook switch.'
    Assert-True ($validate -match 'SkipOnLockContention') 'Hook mode must be able to skip rather than fail on lock contention.'
}
finally {
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host 'ValidationSteps tests passed.' -ForegroundColor Green
exit 0
