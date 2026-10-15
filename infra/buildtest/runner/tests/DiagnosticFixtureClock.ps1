# Test-only logical time, driven by actual atomic readiness markers and owned results.
# The real architecture harness retains its one-minute emergency guard.
if (-not ('DiagnosticFixtureSignal' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public static class DiagnosticFixtureSignal
{
    public static void Wait(string path)
    {
        using var signal = new ManualResetEventSlim(false);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path));
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
        FileSystemEventHandler changed = (_, e) => signal.Set();
        RenamedEventHandler renamed = (_, e) => signal.Set();
        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Renamed += renamed;
        watcher.EnableRaisingEvents = true;
        var guard = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            signal.Reset();
            if (File.Exists(path)) return;
            var remaining = TimeSpan.FromSeconds(15) - guard.Elapsed;
            if (remaining <= TimeSpan.Zero || !signal.Wait(remaining))
                throw new TimeoutException("Fixture readiness not signalled: " + path);
        }
    }
}
'@
}
function New-FixtureElapsedProvider {
    param([string]$ReadyPath, [string]$DumpReadyPath='', [switch]$BlockedDump, [switch]$MissingDump, [switch]$Descendant)
    $state=@{Seconds=0.0; Ready=$ReadyPath; DumpReady=$DumpReadyPath; Blocked=$BlockedDump.IsPresent; Missing=$MissingDump.IsPresent; Descendant=$Descendant.IsPresent; DumpConfirmed=$false; BudgetAdvanced=$false; CaptureBeforeKill=$false}
    $provider={
        param($Phase,$Owner,$DumpChildren,$Captures)
        if ($state.Seconds -eq 0) {
            [DiagnosticFixtureSignal]::Wait($state.Ready)
            $state.Seconds=3.0
        }
        elseif ($Phase -eq 'poll' -and $state.Descendant) {
            # Prove launcher exit before the deadline, with the inherited-pipe leaf live.
            [DiagnosticFixtureSignal]::Wait($Owner.Result)
            if ([IO.File]::ReadAllText($Owner.Result) -ne '0') { throw 'Synthetic launcher failed.' }
            $leafId=[int][IO.File]::ReadAllText($state.Ready)
            if (-not (Get-DiagnosticFixtureLiveProcess $leafId)) { throw 'Leaf exited before containment cleanup.' }
            $state.Seconds=5.0
        }
        elseif ($DumpChildren.Count -gt 0) {
            $child=$DumpChildren[0]
            if ($state.Missing) {
                if ($null -eq $child.Record.exitCode) { [DiagnosticFixtureSignal]::Wait($child.Owner.Result) }
                if ($Phase -eq 'poll' -and $null -ne $child.Record.exitCode) { $state.Seconds=5.0 }
            }
            elseif (-not $state.DumpConfirmed) {
                # Identity confirms Process.Start, marker confirms the dump script itself ran.
                [DiagnosticFixtureSignal]::Wait($child.Owner.Identity)
                [DiagnosticFixtureSignal]::Wait($state.DumpReady)
                $state.DumpConfirmed=$true
                if (-not $state.Blocked) { [DiagnosticFixtureSignal]::Wait($child.Owner.Result) }
            }
            elseif ($Phase -eq 'poll' -and $child.LaunchConfirmed -and -not $state.BudgetAdvanced -and $state.Blocked) {
                if ($child.Deadline -ne 4.0) { throw "Dump budget is not one logical second after confirmed startup: $($child.Deadline)" }
                $state.Seconds=4.0; $state.BudgetAdvanced=$true
            }
            elseif ($Phase -eq 'poll' -and $null -ne $child.Record.exitCode) {
                $state.CaptureBeforeKill=$Captures.Count -eq 1 -and $state.DumpConfirmed
                $state.Seconds=5.0
            }
        }
        return $state.Seconds
    }.GetNewClosure()
    return @{Provider=$provider; State=$state}
}
function Assert-FixtureClock($fixture,$run,[switch]$Dump,[switch]$Blocked) {
    Require ($fixture.State.Seconds -eq 5 -and $run.ElapsedSeconds -eq 5) 'Logical outer kill deadline did not execute.'
    Require ($run.TimedOut -and $run.CleanupComplete -and $run.DrainComplete -and $run.ReadersClosed) 'Real containment/EOF cleanup failed.'
    foreach ($identity in $run.OwnedProcesses) {
        Require (-not (Get-DiagnosticFixtureLiveProcess $identity.processId)) "Observed owned process survived: $($identity.processId)"
    }
    if ($Dump) {
        Require ($fixture.State.DumpConfirmed -and $fixture.State.CaptureBeforeKill -and $run.Captures[0].startedSeconds -eq 3) 'Capture startup was not proven before logical kill.'
    }
    if ($Blocked) { Require ($fixture.State.BudgetAdvanced -and $run.Captures[0].timedOut) 'Confirmed dump did not consume its one-second logical budget.' }
    "PROOF logicalElapsed=$($run.ElapsedSeconds) captureBeforeKill=$($fixture.State.CaptureBeforeKill) dumpReady=$($fixture.State.DumpConfirmed) dumpBudgetAdvanced=$($fixture.State.BudgetAdvanced) cleanup=$($run.CleanupComplete) EOF=$($run.DrainComplete) readersClosed=$($run.ReadersClosed)"
}
function Get-DiagnosticFixtureLiveProcess([int]$processId) {
    $process=Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($IsLinux -and $null -ne $process) {
        $stat=[IO.File]::ReadAllText("/proc/$processId/stat")
        if ($stat.Substring($stat.LastIndexOf(')')+2).StartsWith('Z ')) { $process.Dispose(); return $null }
    }
    return $process
}
