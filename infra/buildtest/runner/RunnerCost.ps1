Set-StrictMode -Version Latest

# PER-PROJECT COST ATTRIBUTION (#3314)
#
# WHY THIS EXISTS. #3305 made a TIMED-OUT run attributable: when the runner's own deadline
# expires it names the projects that had not reported. That is the right answer to "which
# project was still executing", but it fires ONLY on the timeout path and it reports a SET
# with no cost attached.
#
# #3314 asked a different question -- where does the `core`/`full` delta actually go -- and
# the measurement showed why the timeout-only answer could not reach it. `-Mode full` on
# unmodified main (run 20260819033413-38a9b933) finished the whole tree in 13.7 minutes of
# its 20-minute budget with `timeout: null`. It DID NOT OVERRUN, so #3305's attribution never
# ran, and the artifacts carried no per-project cost at all. Answering the question required
# writing a throwaway TRX parser outside the repo; the resulting numbers
# (botnexus.integration.e2e.tests: 325.8s of a 472.7s test phase, for 283 tests, against
# botnexus.gateway.tests at 240.0s for 5,587) exist nowhere a future reader can find them.
#
# A diagnostic that has to be reinvented ad hoc for every investigation is not a diagnostic.
# So cost attribution becomes an ALWAYS-ON artifact of every run, green or red, timed out or
# not. The next person asking "what is expensive" reads runner-cost.log instead of rebuilding
# this parser and re-deriving numbers nobody can check.
#
# Read over the raw TEXT rather than parsing the XML, for the same reason RunnerTimeout.ps1
# does: a TRX truncated by a kill is usually not well-formed, and an [xml] cast would throw
# away the only evidence a dying run produced -- exactly the case where cost data matters most.
#
# Everything here is PURE and must never throw: it runs in the finalisation path alongside
# artifact upload, and a cost-report bug that turned a passing suite red would be strictly
# worse than having no cost report at all.

<#
.SYNOPSIS
    Attributes measured wall time and test counts to the project that produced each TRX.
.DESCRIPTION
    Attribution comes from the TRX ROWS, never from filenames: `dotnet test` over a traversal
    project writes every project's TRX into one results directory under a shared LogFilePrefix,
    so the filenames differ only by timestamp and carry no project identity whatsoever.

    Results are ordered most-expensive-first so the answer to "what is expensive" is the first
    row, not a sort the reader has to perform. Costs for the same project are SUMMED rather
    than overwritten, because a project can emit more than one TRX and keeping only the last
    would under-report precisely the project most likely to be the cost driver.
#>
function Get-RunnerProjectCosts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $TrxPaths
    )

    $byProject = [System.Collections.Specialized.OrderedDictionary]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($path in $TrxPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $text = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrEmpty($text)) { continue }

        $storage = [regex]::Match($text, '(?:storage|codeBase)="([^"]+)"')
        if (-not $storage.Success) { continue }
        $project = [System.IO.Path]::GetFileNameWithoutExtension($storage.Groups[1].Value.Replace('\', '/'))
        if ([string]::IsNullOrWhiteSpace($project)) { continue }

        # A TRX with no <Times> cannot be costed. Report it at zero rather than inventing a
        # duration: a fabricated measurement is worse than an absent one, and dropping the row
        # entirely would hide that the project ran at all.
        $seconds = 0.0
        $times = [regex]::Match($text, '<Times\b[^>]*start="([^"]+)"[^>]*finish="([^"]+)"')
        if ($times.Success) {
            try {
                $span = ([datetime]$times.Groups[2].Value - [datetime]$times.Groups[1].Value).TotalSeconds
                if ($span -gt 0) { $seconds = $span }
            }
            catch {
                # Unparseable timestamps degrade to zero rather than failing finalisation.
            }
        }

        # A kill-truncated TRX frequently has no <Counters> at all. Degrade to zero counts and
        # keep the duration, which is the part that still carries information.
        $counters = [regex]::Match($text, '<Counters\b([^>]*)/?>')
        $readCount = {
            param([string]$Name)
            if (-not $counters.Success) { return 0 }
            $m = [regex]::Match($counters.Groups[1].Value, "$Name=`"(\d+)`"")
            if ($m.Success) { return [int]$m.Groups[1].Value }
            return 0
        }

        if (-not $byProject.Contains($project)) {
            $byProject[$project] = [pscustomobject]@{
                project = $project
                seconds = 0.0
                total = 0
                passed = 0
                failed = 0
            }
        }
        $entry = $byProject[$project]
        $entry.seconds += $seconds
        $entry.total += (& $readCount 'total')
        $entry.passed += (& $readCount 'passed')
        $entry.failed += (& $readCount 'failed')
    }

    foreach ($key in @($byProject.Keys)) {
        $byProject[$key].seconds = [Math]::Round($byProject[$key].seconds, 1)
    }

    return @($byProject.Values | Sort-Object -Property seconds -Descending)
}

<#
.SYNOPSIS
    Renders the per-project cost table written to runner-cost.log.
.DESCRIPTION
    Cost is expressed as a SHARE of the test phase as well as in seconds, because 300 seconds
    is only alarming relative to the phase that contains it. The mode is recorded because a
    cost profile is meaningless without knowing which test set produced it -- `core` and `full`
    are different runs and their tables would otherwise be indistinguishable on disk.

    An empty cost set produces an EXPLICIT statement rather than an empty file. "No project
    reported" is a real and different finding -- it is what a run killed before any TRX landed
    looks like -- and stating it beats implying it by silence, which is the #3305 empty-artifact
    failure in miniature.
#>
function Format-RunnerCostReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Costs,
        [Parameter(Mandatory)][double] $TestPhaseSeconds,
        [Parameter(Mandatory)][string] $Mode
    )

    $lines = @(
        "per-project test cost for mode=$Mode (test phase $([Math]::Round($TestPhaseSeconds, 1))s)"
        # DISCLOSE THE OVERLAP. `dotnet test` runs assemblies CONCURRENTLY, so these shares are
        # not exclusive slices and legitimately sum past 100% -- the real run
        # 20260819033413-38a9b933 attributes 65.2% + 49.1% + 48.2% to its top three alone.
        # Without this line a reader either concludes the arithmetic is broken or, far worse,
        # concludes that deleting the top project would recover 65% of the phase. It would not:
        # it would recover only the tail that no other assembly was covering.
        'Assemblies run concurrently, so these shares overlap and do not sum to 100%.'
    )

    if (@($Costs).Count -eq 0) {
        $lines += 'No per-project cost could be attributed: no TRX carried an assembly reference.'
        return ($lines -join [Environment]::NewLine)
    }

    foreach ($cost in $Costs) {
        # Guard the divisor explicitly. A zero-length phase is representable (a run killed
        # before the phase was measured) and must not emit Infinity or NaN into an artifact
        # that exists to be trusted.
        $share = if ($TestPhaseSeconds -gt 0) {
            '{0:N1}%' -f (100.0 * $cost.seconds / $TestPhaseSeconds)
        }
        else {
            'n/a'
        }
        $lines += "{0,-58} {1,8:N1}s {2,7}  total={3} passed={4} failed={5}" -f `
            $cost.project, $cost.seconds, $share, $cost.total, $cost.passed, $cost.failed
    }

    return ($lines -join [Environment]::NewLine)
}
