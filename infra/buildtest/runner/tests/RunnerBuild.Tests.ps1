# Tests for concurrent Debug and Release build orchestration.
# Real children use bounded handshakes rather than wall-clock concurrency thresholds:
# both must be launched and ready before waiting, and the short child must be observed
# before its longer sibling is released. Startup time legitimately belongs to elapsed time.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..' 'RunnerBuild.ps1')

$script:failures = @()
function Assert-True {
    param([bool]$Condition, [string]$Because)
    if (-not $Condition) { $script:failures += $Because }
}
function Assert-Equal {
    param($Expected, $Actual, [string]$Because)
    if ($Expected -ne $Actual) { $script:failures += "$Because Expected '$Expected', got '$Actual'." }
}

# Keep scratch beneath the checkout; children and parents both have bounded waits.
$root = Join-Path (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot)))) 'artifacts'
$root = Join-Path $root "runner-build-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null
$pwshPath = (Get-Process -Id $PID).Path
$script:launched = @()
$script:allChildren = @()
$script:waitCount = 0
$script:observations = @()

function Wait-TestMarker {
    param([string]$Path)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) {
        if ($deadline.Elapsed.TotalSeconds -ge 15) { throw "Timed out waiting for test marker: $Path" }
        Start-Sleep -Milliseconds 20
    }
}

# Forward every launch to the real cmdlet and retain handles for assertions and cleanup.
function Start-Process {
    [CmdletBinding()]
    param($FilePath, $ArgumentList, [switch]$NoNewWindow, [switch]$PassThru,
        $RedirectStandardOutput, $RedirectStandardError)
    $process = Microsoft.PowerShell.Management\Start-Process @PSBoundParameters
    $script:launched += $process
    $script:allChildren += $process
    return $process
}

# Observe the real wait return before allowing the longer sibling to finish. On the
# next wait, production has had an opportunity to stop the short child's stopwatch.
function Wait-Process {
    [CmdletBinding()]
    param([Diagnostics.Process[]]$InputObject, [switch]$Any)
    $script:waitCount++
    if ($script:waitCount -eq 1) {
        Assert-Equal 2 $script:launched.Count '1: both children must launch before the first wait.'
        Assert-Equal 2 $InputObject.Count '1: first wait must include both children.'
        Wait-TestMarker (Join-Path $script:scenario 'short.ready')
        Wait-TestMarker (Join-Path $script:scenario 'long.ready')
        Assert-True (-not $script:launched[0].HasExited -and -not $script:launched[1].HasExited) '1: both ready children must overlap before waiting.'
        Set-Content (Join-Path $script:scenario 'short.go') 'release'
    }
    else {
        Assert-True ($script:observations.Count -eq 1 -and $script:observations[0] -eq 'short') '1: short child must be observed before releasing the longer sibling.'
        Assert-Equal 1 $InputObject.Count '1: completed short child must not remain pending.'
        # Inspect the actual production stopwatches at this boundary: comparing only
        # final durations could pass even if both were stopped after the long child.
        $running = Get-Variable -Name running -Scope 1 -ValueOnly
        $short = $running | Where-Object { $_.Process.Id -eq $script:launched[1].Id }
        $long = $running | Where-Object { $_.Process.Id -eq $script:launched[0].Id }
        Assert-True (-not $short.Stopwatch.IsRunning -and $long.Stopwatch.IsRunning) '1: short duration must stop at observation while the longer sibling is still running.'
        Set-Content (Join-Path $script:scenario 'long.go') 'release'
    }

    Microsoft.PowerShell.Management\Wait-Process -InputObject $InputObject -Any:$Any -Timeout 20
    if ($script:waitCount -eq 1) {
        Assert-True ($script:launched[1].HasExited -and -not $script:launched[0].HasExited) '1: short child must be observed before the longer sibling exits.'
        Assert-True ((Test-Path (Join-Path $script:scenario 'short.complete')) -and -not (Test-Path (Join-Path $script:scenario 'long.complete'))) '1: short completion must precede the longer sibling at the observation boundary.'
        if ($script:launched[1].HasExited) { $script:observations += 'short' }
        if ($script:launched[0].HasExited) { $script:observations += 'long' }
    }
    else {
        $script:observations += 'long'
    }
}

function New-TestChild {
    param([string]$Name, [string]$Role, [int]$StartupDelay, [int]$ExitCode, [string]$Output)
    $directory = $script:scenario.Replace("'", "''")
    # EncodedCommand avoids platform-dependent Start-Process argument quoting.
    $command = @"
`$ErrorActionPreference = 'Stop'
Start-Sleep -Milliseconds $StartupDelay
Set-Content -LiteralPath (Join-Path '$directory' '$Role.ready') 'ready'
`$deadline = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath (Join-Path '$directory' '$Role.go'))) {
    if (`$deadline.Elapsed.TotalSeconds -ge 15) { throw 'Child release handshake timed out: $Role' }
    Start-Sleep -Milliseconds 20
}
Set-Content -LiteralPath (Join-Path '$directory' '$Role.complete') 'complete'
Write-Output '$Output'
if ($ExitCode -ne 0) { [Console]::Error.WriteLine('expected-failure') }
exit $ExitCode
"@
    @{
        Name = $Name
        FilePath = $pwshPath
        ArgumentList = @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)))
        LogPath = (Join-Path $script:scenario "$Name.log")
    }
}

function Invoke-TestScenario {
    param([string]$Name, [string]$LongName, [string]$ShortName, [int]$ShortExitCode,
        [string]$LongOutput, [string]$ShortOutput)
    $script:scenario = Join-Path $root $Name
    New-Item -ItemType Directory -Path $script:scenario | Out-Null
    $script:waitCount = 0
    $script:observations = @()
    $script:launched = @()
    $results = @(Invoke-ParallelRunnerProcesses -Processes @(
        (New-TestChild $LongName 'long' 0 0 $LongOutput),
        (New-TestChild $ShortName 'short' 1500 $ShortExitCode $ShortOutput)
    ))
    Assert-Equal 2 $results.Count '2: both child results were not returned.'
    Assert-Equal 'short,long' ($script:observations -join ',') '1: children were not observed in completion order.'
    $short = $results | Where-Object Name -eq $ShortName
    $long = $results | Where-Object Name -eq $LongName
    Assert-Equal $ShortExitCode $short.ExitCode '2: short child exit code was not preserved.'
    Assert-Equal 0 $long.ExitCode '2: successful longer sibling was not allowed to finish.'
    # Stopwatch starts before Start-Process, not when the child's script becomes ready.
    Assert-True ($short.ElapsedSeconds -ge 1.5) '1: controlled child startup delay must be included in elapsed time.'
    Assert-True ($short.ElapsedSeconds -lt $long.ElapsedSeconds) '1: short child duration was inflated by the order in which processes were reaped.'
    Assert-True ((Test-Path (Join-Path $script:scenario 'short.complete')) -and (Test-Path (Join-Path $script:scenario 'long.complete'))) '4: orchestration returned before both children completed.'
    Assert-True ((Get-Content $short.LogPath -Raw) -match $ShortOutput) '3: short child output was not captured in its own log.'
    Assert-True ((Get-Content $long.LogPath -Raw) -match $LongOutput) '3: longer sibling output was lost or captured in the wrong log.'
    Assert-True ((Get-Content $short.LogPath -Raw) -notmatch $LongOutput) '3: longer sibling output leaked into the short child log.'
    Assert-True ((Get-Content $long.LogPath -Raw) -notmatch $ShortOutput) '3: short child output leaked into the longer sibling log.'
    if ($ShortExitCode -ne 0) {
        Assert-True ((Get-Content $short.LogPath -Raw) -match 'expected-failure') '4: failing child stderr was not captured.'
    }
    Write-Host "${Name}: observations=$($script:observations -join ','); exitCodes=$($short.ExitCode),$($long.ExitCode); elapsed(short,long)=$($short.ElapsedSeconds.ToString('N2')),$($long.ElapsedSeconds.ToString('N2'))s"
}

try {
    Invoke-TestScenario 'parallel' 'debug' 'release' 0 'debug-complete' 'release-complete'
    Invoke-TestScenario 'mixed' 'slow-success' 'failing' 7 'sibling-complete' 'failure-complete'
}
finally {
    foreach ($child in $script:allChildren) {
        if (-not $child.HasExited) {
            try { $child.Kill($true) } catch { }
            try { $child.WaitForExit(5000) | Out-Null } catch { }
        }
        $child.Dispose()
    }
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\Start-Process, Function:\Wait-Process
}

if ($script:failures.Count) {
    Write-Host "FAILED ($($script:failures.Count)):" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'RunnerBuild.Tests.ps1: PASS' -ForegroundColor Green
exit 0
