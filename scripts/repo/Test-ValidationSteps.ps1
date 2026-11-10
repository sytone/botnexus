<#
.SYNOPSIS
Tests bounded validation process and lock policy.
.DESCRIPTION
Uses source inspection and isolated synthetic fixtures only. Does not launch a product gateway, dotnet test host or Azure job.
.EXAMPLE
./Test-ValidationSteps.ps1
.OUTPUTS
None. Reports assertion failures with a nonzero exit code.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ValidationSteps.psm1') -Force

$failures = [Collections.Generic.List[string]]::new()
<#
.SYNOPSIS
Records a failed boolean assertion.
.DESCRIPTION
Preserves all assertions and continues collecting failures.
.PARAMETER Condition
Actual boolean condition.
.PARAMETER Message
Failure explanation.
.EXAMPLE
Assert-True -Condition $true -Message 'Expected policy.'
.OUTPUTS
None. Reports assertion failures with a nonzero exit code.
#>
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        $failures.Add($Message)
    }
}
<#
.SYNOPSIS
Records unequal expected and actual values.
.DESCRIPTION
Collects useful failure messages without hiding later assertions.
.PARAMETER Expected
Expected value.
.PARAMETER Actual
Observed value.
.PARAMETER Message
Failure explanation.
.EXAMPLE
Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'Expected success.'
.OUTPUTS
None. Reports assertion failures with a nonzero exit code.
#>
function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        $failures.Add("$Message Expected '$Expected', got '$Actual'.")
    }
}

$pwshPath = (Get-Process -Id $PID).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) "botnexus-validationsteps-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

try {
    # --- Invoke-BotNexusValidationStep --------------------------------------

    # A successful step reports its own name so a log line identifies the stage.
    $result = Invoke-BotNexusValidationStep -Name 'fast-step' -FilePath $pwshPath -Arguments @('-NoProfile', '-Command', 'exit 0') -WorkingDirectory $scratch -TimeoutSeconds 60
    Assert-Equal -Expected 'fast-step' -Actual $result.Name -Message 'Step result should carry the step name.'
    Assert-Equal -Expected 0 -Actual $result.ExitCode -Message 'A successful step should report exit code 0.'
    Assert-Equal -Expected $false -Actual $result.TimedOut -Message 'A fast step should not report a timeout.'

    # A failing step preserves the child exit code rather than masking it.
    $result = Invoke-BotNexusValidationStep -Name 'failing-step' -FilePath $pwshPath -Arguments @('-NoProfile', '-Command', 'exit 7') -WorkingDirectory $scratch -TimeoutSeconds 60
    Assert-Equal -Expected 7 -Actual $result.ExitCode -Message 'A failing step should preserve its exit code.'
    Assert-Equal -Expected $false -Actual $result.TimedOut -Message 'A failing step should not be reported as a timeout.'

    # The core #2331 requirement: an overrunning step is terminated and NAMED, so the
    # developer can see which stage blew the budget instead of a mystery hang.
    $result = Invoke-BotNexusValidationStep -Name 'slow-step' -FilePath $pwshPath -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') -WorkingDirectory $scratch -TimeoutSeconds 2
    Assert-Equal -Expected $true -Actual $result.TimedOut -Message 'An overrunning step must report TimedOut.'
    Assert-Equal -Expected 'slow-step' -Actual $result.Name -Message 'A timed-out step must identify which step overran.'
    Assert-Equal -Expected 124 -Actual $result.ExitCode -Message 'A timed-out step should use the POSIX timeout exit code.'
    Assert-True -Condition ($result.DurationSeconds -lt 25) -Message 'A timed-out step must actually be terminated, not merely reported.'

    # The step must run in the directory it is given. MSBuild resolves .editorconfig
    # relative to the working directory, so an inherited directory from another worktree
    # is the likely cause of the transient ".editorconfig could not be found" failure.
    $probeDirectory = Join-Path $scratch 'probe'
    New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
    $probeOutput = Join-Path $scratch 'cwd.txt'
    $null = Invoke-BotNexusValidationStep -Name 'cwd-step' -FilePath $pwshPath -Arguments @('-NoProfile', '-Command', "(Get-Location).Path | Set-Content -LiteralPath '$probeOutput'") -WorkingDirectory $probeDirectory -TimeoutSeconds 60
    $observed = (Get-Content -LiteralPath $probeOutput -Raw).Trim()
    Assert-Equal -Expected (Resolve-Path $probeDirectory).ProviderPath -Actual $observed -Message 'A step must execute in the supplied working directory.'

    # A missing working directory is a caller bug and must fail loudly, not silently
    # inherit whatever directory the hook happened to start in.
    $threw = $false
    try {
        $null = Invoke-BotNexusValidationStep -Name 'bad-cwd' -FilePath $pwshPath -Arguments @('-NoProfile', '-Command', 'exit 0') -WorkingDirectory (Join-Path $scratch 'does-not-exist') -TimeoutSeconds 5
    }
    catch {
        $threw = $true
    }
    Assert-True -Condition $threw -Message 'A nonexistent working directory must throw rather than be inherited.'

    # --- Get-BotNexusValidationLock -----------------------------------------

    $lockPath = Join-Path $scratch 'validation.lock'

    # Uncontended acquisition succeeds immediately.
    $lock = Get-BotNexusValidationLock -TimeoutSeconds 5 -LockPath $lockPath
    Assert-Equal -Expected $true -Actual $lock.Acquired -Message 'An uncontended lock should be acquired.'
    Assert-True -Condition ($null -ne $lock.Handle) -Message 'An acquired lock should expose a disposable handle.'

    # Contention must be reported as data (Acquired=$false), never as a throw. The old
    # behaviour threw on contention, which failed the hook and forced --no-verify.
    $contended = Get-BotNexusValidationLock -TimeoutSeconds 1 -PollMilliseconds 50 -LockPath $lockPath
    Assert-Equal -Expected $false -Actual $contended.Acquired -Message 'A contended lock must report non-acquisition rather than throwing.'
    Assert-True -Condition ($null -eq $contended.Handle) -Message 'A non-acquired lock must not expose a handle.'
    Assert-True -Condition ($contended.WaitedSeconds -ge 0.5) -Message 'A contended acquisition must actually wait for the bounded timeout.'

    # Contention resolves: once the holder releases, a waiter acquires within its budget.
    # Release goes through Remove-BotNexusValidationLock (#2393): disposing the handle alone
    # leaves the owner record on disk, which is exactly the stranded-tombstone state the
    # liveness reaper now has to classify.
    Remove-BotNexusValidationLock -Lock $lock
    $reacquired = Get-BotNexusValidationLock -TimeoutSeconds 5 -PollMilliseconds 50 -LockPath $lockPath
    Assert-Equal -Expected $true -Actual $reacquired.Acquired -Message 'A waiter must acquire the lock once the holder releases it.'
    Remove-BotNexusValidationLock -Lock $reacquired

    # --- Wiring assertions on the callers -----------------------------------

    $repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent
    $localRunner = Get-Content (Join-Path $repoRoot 'scripts/repo/Invoke-LocalValidation.ps1') -Raw
    Assert-True -Condition ($localRunner -match 'Get-BotNexusValidationLock') -Message 'The local runner must use the bounded waiting lock.'
    Assert-True -Condition ($localRunner -match 'Invoke-BotNexusValidationStep') -Message 'The local runner must run steps under named, bounded timeouts.'
    Assert-True -Condition ($localRunner -notmatch "throw \`"Another BotNexus local validation is already running") -Message 'The local runner must no longer throw on lock contention.'
    Assert-True -Condition ($localRunner -match "'hook'") -Message 'The local runner must support an impacted-only hook mode.'

    # The pre-commit hook was REMOVED deliberately: it ran local validation on the developer
    # workstation, which is banned. Local gates spawn real gateway processes that outlive their
    # parent, starve the live gateway, and claim its scheduled jobs out of the shared cron store.
    # Remote container validation is authoritative. Assert absence so it cannot be reinstated
    # silently -- a reintroduced hook would quietly restore the failure mode.
    Assert-True -Condition (-not (Test-Path (Join-Path $repoRoot 'scripts/repo/githooks/pre-commit'))) -Message 'The pre-commit hook must not exist: local validation is banned, remote is authoritative.'
    Assert-True -Condition (-not (Test-Path (Join-Path $repoRoot 'scripts/install-pre-commit-hook.ps1'))) -Message 'The pre-commit hook installer must not exist: local validation is banned.'

    $validate = Get-Content (Join-Path $repoRoot 'scripts/repo/Invoke-PreCommitValidation.ps1') -Raw
    Assert-True -Condition ($validate -match '\[switch\]\$Hook') -Message 'Invoke-PreCommitValidation must expose a -Hook switch.'
    Assert-True -Condition ($validate -match 'SkipOnLockContention') -Message 'Hook mode must be able to skip rather than fail on lock contention.'
}
finally {
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Output 'ValidationSteps tests passed.'
exit 0
