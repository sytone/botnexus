# Tests for concurrent Debug and Release build orchestration.
#
# The remote runner spends substantial wall time building the full Debug graph and then the
# source Release graph. These tests pin the process-level contract needed to overlap them without
# weakening failure handling: both children start before either is awaited, each owns its log,
# and one failure cannot prevent the other child from being reaped and reported.

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

$root = Join-Path ([System.IO.Path]::GetTempPath()) "runner-build-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null
$pwshPath = (Get-Process -Id $PID).Path

try {
    $parallelStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $parallel = @(Invoke-ParallelRunnerProcesses -Processes @(
        @{
            Name = 'debug'
            FilePath = $pwshPath
            ArgumentList = @('-NoProfile', '-Command', '[Threading.Thread]::Sleep(1500); Write-Output debug-complete; exit 0')
            LogPath = (Join-Path $root 'debug.log')
        },
        @{
            Name = 'release'
            FilePath = $pwshPath
            ArgumentList = @('-NoProfile', '-Command', '[Threading.Thread]::Sleep(300); Write-Output release-complete; exit 0')
            LogPath = (Join-Path $root 'release.log')
        }
    ))
    $parallelStopwatch.Stop()

    Assert-Equal 2 $parallel.Count '1: both child results were not returned.'
    Assert-True ($parallelStopwatch.Elapsed.TotalSeconds -lt 2.8) `
        "1: children ran sequentially ($($parallelStopwatch.Elapsed.TotalSeconds.ToString('N2'))s)."
    $startSpread = (($parallel | Sort-Object StartedAt | Select-Object -Last 1).StartedAt -
        ($parallel | Sort-Object StartedAt | Select-Object -First 1).StartedAt).TotalSeconds
    Assert-True ($startSpread -lt 0.5) `
        "1: child starts did not overlap (start spread $($startSpread.ToString('N2'))s)."
    Assert-True (($parallel | Where-Object Name -eq 'release').ElapsedSeconds -lt 1.0) `
        '1: child duration was inflated by the order in which processes were reaped.'
    Assert-True (@($parallel | Where-Object Name -eq 'debug' | Where-Object ExitCode -eq 0).Count -eq 1) `
        '2: Debug child did not report success.'
    Assert-True (@($parallel | Where-Object Name -eq 'release' | Where-Object ExitCode -eq 0).Count -eq 1) `
        '2: Release child did not report success.'
    Assert-True ((Get-Content (Join-Path $root 'debug.log') -Raw) -match 'debug-complete') `
        '3: Debug output was not captured in its own log.'
    Assert-True ((Get-Content (Join-Path $root 'release.log') -Raw) -match 'release-complete') `
        '3: Release output was not captured in its own log.'

    $failureStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $mixed = @(Invoke-ParallelRunnerProcesses -Processes @(
        @{
            Name = 'failing'
            FilePath = $pwshPath
            ArgumentList = @('-NoProfile', '-Command', 'Write-Error expected-failure; exit 7')
            LogPath = (Join-Path $root 'failing.log')
        },
        @{
            Name = 'slow-success'
            FilePath = $pwshPath
            ArgumentList = @('-NoProfile', '-Command', '[Threading.Thread]::Sleep(1000); Write-Output sibling-complete; exit 0')
            LogPath = (Join-Path $root 'slow-success.log')
        }
    ))
    $failureStopwatch.Stop()

    Assert-Equal 7 ($mixed | Where-Object Name -eq 'failing').ExitCode `
        '4: failing child exit code was not preserved.'
    Assert-Equal 0 ($mixed | Where-Object Name -eq 'slow-success').ExitCode `
        '4: successful sibling was not allowed to finish.'
    Assert-True ($failureStopwatch.Elapsed.TotalSeconds -ge 0.9) `
        '4: orchestration returned before the successful sibling finished.'
    Assert-True ((Get-Content (Join-Path $root 'slow-success.log') -Raw) -match 'sibling-complete') `
        '4: successful sibling output was lost after the other child failed.'
}
finally {
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures.Count) {
    Write-Host "FAILED ($($script:failures.Count)):" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'RunnerBuild.Tests.ps1: PASS' -ForegroundColor Green
exit 0