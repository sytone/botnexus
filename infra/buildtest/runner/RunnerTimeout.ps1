Set-StrictMode -Version Latest

# RUNNER TIMEOUT AND ATTRIBUTION (#3305)
#
# WHY THIS EXISTS. The Container Apps replica timeout is enforced by the platform: when it
# expires the replica is destroyed, the entrypoint's `finally` block never executes, and the
# artifact upload that lives in that block never happens. The observable outcome of a `full`
# run that overruns is therefore an EMPTY artifact directory -- no result.json, no TRX, no
# timing log -- which cannot distinguish a genuine hang from a suite that is merely slow, and
# cannot name the project that was still running. Three measured runs on one worktree
# (#3305) produced exactly that: two 20.1-minute kills with zero bytes of evidence.
#
# THE FIX IS NOT A LONGER BUDGET. A bigger number would hide the defect rather than remove
# it, because the failure mode is "killed, no evidence", not "killed too early". Instead the
# runner imposes its OWN deadline strictly INSIDE the platform budget, so the run ends on a
# path we control, with time left to write a result and upload it.
#
# Deliberately signal-free: it must not matter whether the platform sends SIGTERM before
# SIGKILL, because that behaviour is undocumented for Container Apps jobs and would be
# untestable here. Landing first, by our own clock, is the only mechanism that does not
# depend on the platform's courtesy.
#
# Everything below is PURE so it can be tested without Azure, a container, or a test run.
# `tests/RunnerTimeout.Tests.ps1` pins each function.

<#
.SYNOPSIS
    Derives the runner's self-imposed deadline from the platform replica budget.
.DESCRIPTION
    The reserve is time deliberately left unspent so the runner can finalise: write
    result.json over whatever TRX exist, and complete the azcopy upload. Landing exactly on
    the platform budget would reproduce the very defect this closes, because the finalisation
    itself takes measurable time (artifact-upload has been measured at 6-9 seconds, and a
    partial TRX set is larger than a complete one is small).

    The floor exists so a misconfigured or absurdly small budget still yields a positive
    deadline rather than a negative one that would abort the run before it began.
#>
function Get-RunnerDeadlineSeconds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int] $BudgetSeconds,
        [int] $ReserveSeconds = 90,
        [int] $FloorSeconds = 30
    )

    $deadline = $BudgetSeconds - $ReserveSeconds
    if ($deadline -lt $FloorSeconds) { return $FloorSeconds }
    return $deadline
}

<#
.SYNOPSIS
    Lists the test assemblies that produced results, read from the TRX rows themselves.
.DESCRIPTION
    Attribution cannot come from TRX FILENAMES. `dotnet test` over a traversal project writes
    every project's TRX into the one --results-directory using the same LogFilePrefix, so the
    names differ only by host and timestamp and carry no project identity at all.

    The rows do carry it: each `UnitTest` element records the assembly it came from in its
    `storage` (and `codeBase`) attribute. Read over the raw text rather than parsing the XML,
    because a TRX truncated by a kill is frequently not well-formed, and refusing to attribute
    a partial run would defeat the purpose of collecting it.

    BOTH the dll basename AND the owning project directory are recorded, because they are not
    the same string. Measured on a real green run: `tests/agent/BotNexus.Agent.Core.Tests`
    sets `<AssemblyName>BotNexus.AgentCore.Tests</AssemblyName>`, so matching on the dll name
    alone reports that project as never having run -- a false accusation against a project that
    passed 438 tests. Two projects in the tree do this today. The directory segment preceding
    `/bin/` is the project's own identity and survives any AssemblyName rename.
#>
function Get-CompletedTestAssemblies {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $TrxPaths
    )

    $assemblies = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $TrxPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $text = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrEmpty($text)) { continue }
        foreach ($match in [regex]::Matches($text, '(?:storage|codeBase)="([^"]+)"')) {
            $value = $match.Groups[1].Value.Replace('\', '/')
            $leaf = [System.IO.Path]::GetFileNameWithoutExtension($value)
            if (-not [string]::IsNullOrWhiteSpace($leaf)) { [void]$assemblies.Add($leaf) }

            # The project directory: the segment immediately before /bin/. Independent of
            # AssemblyName, so an assembly rename cannot make a project look unfinished.
            $binIndex = $value.LastIndexOf('/bin/', [StringComparison]::OrdinalIgnoreCase)
            if ($binIndex -gt 0) {
                $projectDir = $value.Substring(0, $binIndex).Split('/')[-1]
                if (-not [string]::IsNullOrWhiteSpace($projectDir)) { [void]$assemblies.Add($projectDir) }
            }
        }
    }

    return @($assemblies | Sort-Object)
}

<#
.SYNOPSIS
    Enumerates the test projects the run was supposed to cover.
.DESCRIPTION
    Mirrors the wildcard in tests/dirs.proj, including its bin/obj exclusion: those
    directories are being created and deleted concurrently by the very run doing the walk,
    and #2666 showed that racing them can evaluate to an empty set.

    Only projects that CAN produce a TRX are counted. `tests/` also holds shared harnesses and
    support libraries -- measured on a real green run, three of them: BotNexus.Scenarios.Harness
    and BotNexus.Integration.Tests reference no test SDK, and BotNexus.Providers.Conformance.Tests
    sets IsTestProject=false. `dotnet test` never runs those, so their absence from the results
    is normal and naming them as unfinished would be a false accusation on every timeout.
#>
function Get-ExpectedTestProjects {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $TestsRoot
    )

    if (-not (Test-Path -LiteralPath $TestsRoot -PathType Container)) { return @() }

    return @(Get-ChildItem -LiteralPath $TestsRoot -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName.Replace('\', '/') -notmatch '/(bin|obj)/' } |
        Where-Object {
            $text = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
            $text -and ($text -match 'Microsoft\.NET\.Test\.Sdk') -and ($text -notmatch '<IsTestProject>\s*false')
        } |
        ForEach-Object { $_.BaseName } |
        Sort-Object -Unique)
}

<#
.SYNOPSIS
    Names the projects that had not produced results when the deadline expired.
.DESCRIPTION
    This is the answer to "which project was the run still in", which the old empty-artifact
    outcome could not give at any price. It deliberately reports a SET rather than a single
    project: `dotnet test` runs assemblies concurrently, so more than one can be in flight,
    and naming just one of them would be a guess presented as a measurement.

    A filtered run (core excludes the browser/E2E projects) legitimately never produces
    results for the excluded projects, so the caller passes them in ExcludedProjects and they
    are not reported as unfinished. Reporting a project the run never intended to execute
    would be a false accusation, and a wrong attribution is worse than none.
#>
function Get-UnfinishedTestProjects {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ExpectedProjects,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $CompletedAssemblies,
        [AllowEmptyCollection()][string[]] $ExcludedProjects = @()
    )

    $completed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $CompletedAssemblies) { if ($item) { [void]$completed.Add($item) } }

    return @($ExpectedProjects |
        Where-Object { $_ } |
        Where-Object { -not $completed.Contains($_) } |
        Where-Object {
            $candidate = $_
            -not ($ExcludedProjects | Where-Object { $_ -and $candidate -like "*$_*" })
        } |
        Sort-Object -Unique)
}

<#
.SYNOPSIS
    Runs a child process under a hard deadline, streaming its output to a log.
.DESCRIPTION
    `& dotnet test ... | Tee-Object` cannot be bounded: the pipeline blocks until the child
    exits, so there is no moment at which the runner can notice its own deadline. Starting the
    process explicitly and polling WaitForExit gives the runner a point of control, which is
    the whole mechanism by which artifacts survive an overrun.

    The child is killed with its entire tree. `dotnet test` spawns testhost processes that do
    not die with their parent -- exactly the orphan-leak shape documented for local validation
    -- and a surviving testhost would keep writing into the results directory while it is
    being uploaded.

    Returns the exit code, whether the deadline expired, and the measured elapsed time. A
    timed-out process has no meaningful exit code, so it is reported as 1 alongside the
    TimedOut flag rather than being conflated with a genuine test failure.
#>
function Invoke-BoundedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $ArgumentList,
        [Parameter(Mandatory)][string] $LogPath,
        [Parameter(Mandatory)][int] $TimeoutSeconds,
        [int] $PollMilliseconds = 1000
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $stdout = "$LogPath.stdout"
    $stderr = "$LogPath.stderr"
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    $timedOut = $false
    while (-not $process.HasExited) {
        if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            $timedOut = $true
            try { $process.Kill($true) } catch { }
            try { $process.WaitForExit(30000) | Out-Null } catch { }
            break
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    $stopwatch.Stop()

    # Merge the redirected streams into the log the rest of the runner expects. Done after the
    # fact rather than streamed, because the point of this function is to survive a kill and a
    # half-written interleave would be less readable than two complete sections.
    $merged = @()
    foreach ($part in @($stdout, $stderr)) {
        if (Test-Path -LiteralPath $part) {
            $merged += (Get-Content -LiteralPath $part -Raw -ErrorAction SilentlyContinue)
            Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        }
    }
    Set-Content -Path $LogPath -Value ($merged -join [Environment]::NewLine) -ErrorAction SilentlyContinue

    $exitCode = if ($timedOut) { 1 } else { try { $process.ExitCode } catch { 1 } }

    return [pscustomobject]@{
        ExitCode = $exitCode
        TimedOut = $timedOut
        ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
    }
}

<#
.SYNOPSIS
    Builds the timeout record embedded in result.json and rendered into the failure message.
.DESCRIPTION
    Structured, not merely a log line: the client reads result.json to decide whether a run
    passed, so the timeout has to be representable THERE or the client is once again left
    inferring an outcome from an absence. This is the same shape as #3244 -- a bound that is
    enforced but not reported leaves the caller unable to tell "no results" from "results
    truncated".
#>
function New-RunnerTimeoutRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Phase,
        [Parameter(Mandatory)][double] $ElapsedSeconds,
        [Parameter(Mandatory)][int] $DeadlineSeconds,
        [AllowEmptyCollection()][string[]] $UnfinishedProjects = @(),
        [AllowEmptyCollection()][string[]] $CompletedAssemblies = @()
    )

    $unfinished = @($UnfinishedProjects)
    $attribution = if ($unfinished.Count -eq 0) {
        # Honest about not knowing. Every project having reported while the phase still
        # overran is a real, different finding (the overrun is after the tests, e.g. in
        # result aggregation), and inventing a culprit to fill the field would hide it.
        'No test project was outstanding when the deadline expired; the overrun is outside test execution.'
    }
    else {
        "Outstanding when the deadline expired: $($unfinished -join ', ')."
    }

    return [ordered]@{
        timedOut = $true
        phase = $Phase
        elapsedSeconds = [Math]::Round($ElapsedSeconds, 2)
        deadlineSeconds = $DeadlineSeconds
        unfinishedProjects = $unfinished
        completedAssemblies = @($CompletedAssemblies)
        attribution = $attribution
    }
}

