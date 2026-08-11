<#
.SYNOPSIS
    Freshness guards for the local validation gate (issue #2785).

.DESCRIPTION
    Two cached inputs used to be consumed without ever checking whether they were
    current, and a stale one produced a confident verdict anyway:

      1. TEST ASSEMBLIES. The gate does one solution build and then runs every test
         step with `--no-build`. When that build does not actually refresh a given
         project/configuration, `dotnet test --no-build` silently executes whatever
         `.dll` happens to be on disk. Observed live on 2026-08-03: a gate run
         reported 564 tests / 3 failed from an assembly compiled 15 minutes before
         the commit under validation, while a forced-clean run of the same source
         reported 591 / 0. The false-red cost two hours of triage; the same
         mechanism produces a false GREEN just as easily, which is the severe
         direction for an authoritative pre-push gate.

      2. THE BASE REF. `origin/main` is the default diff base and nothing fetched
         it, so the impacted set was derived from whatever that ref last pointed at
         in that particular checkout. A 7-commit-stale base produced a 26-file diff
         across projects the branch never touched (true diff: 10 files). The
         inflating direction merely wastes time; the omitting direction skips test
         projects a change genuinely impacts.

    Both are the same shape: a step consuming a cached input without verifying its
    freshness and reporting success as though it had. These functions make the
    staleness observable and fail closed on it.

    Everything here is deliberately side-effect-light and parameterised on paths and
    timestamps so it can be exercised against real temporary git repositories rather
    than mocks - a freshness guard verified against a fake clock is not evidence.
#>

Set-StrictMode -Version Latest

function Get-BotNexusTestAssemblyState {
    <#
    .SYNOPSIS
        Classifies each test project's compiled assembly as fresh, stale or missing.

    .DESCRIPTION
        WHY A DIRECTORY SEARCH RATHER THAN AN MSBUILD QUERY: resolving `TargetPath`
        per project costs an MSBuild evaluation each, which is a large fraction of
        the build this guard is meant to protect. The output layout
        `bin/<Configuration>/<tfm>/<name>.dll` is stable across this repository, and
        an unfound assembly is reported as `missing` - which fails closed - so a
        layout surprise cannot silently pass.

        A project whose assembly is ABSENT is `missing`, not `fresh`. Absence is the
        strongest possible evidence that the build step did not cover it.

    .PARAMETER ProjectPath
        Full paths of the `.csproj` files about to be run with `--no-build`.

    .PARAMETER Configuration
        The configuration those tests will run under. Must be the same value the
        build step used; a Debug build followed by a Release test run is precisely
        the divergence that produced the original defect.

    .PARAMETER ReferenceTimeUtc
        The instant the assemblies must not predate - normally the commit timestamp
        of the code under validation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ProjectPath,
        [Parameter(Mandatory)][string]$Configuration,
        [Parameter(Mandatory)][DateTime]$ReferenceTimeUtc
    )

    $reference = $ReferenceTimeUtc.ToUniversalTime()
    $results = [Collections.Generic.List[object]]::new()

    foreach ($project in $ProjectPath) {
        $name = [IO.Path]::GetFileNameWithoutExtension($project)
        $binRoot = Join-Path (Split-Path -Parent $project) (Join-Path 'bin' $Configuration)

        $assembly = $null
        if (Test-Path -LiteralPath $binRoot -PathType Container) {
            $assembly = Get-ChildItem -LiteralPath $binRoot -Filter "$name.dll" -Recurse -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTimeUtc -Descending |
                Select-Object -First 1
        }

        if ($null -eq $assembly) {
            $results.Add([pscustomobject]@{
                    Project          = $project
                    Name             = $name
                    AssemblyPath     = $null
                    LastWriteTimeUtc = $null
                    State            = 'missing'
                })
            continue
        }

        $state = if ($assembly.LastWriteTimeUtc -lt $reference) { 'stale' } else { 'fresh' }
        $results.Add([pscustomobject]@{
                Project          = $project
                Name             = $name
                AssemblyPath     = $assembly.FullName
                LastWriteTimeUtc = $assembly.LastWriteTimeUtc
                State            = $state
            })
    }

    return $results.ToArray()
}

function Assert-BotNexusTestAssemblyFreshness {
    <#
    .SYNOPSIS
        Fails closed when any assembly about to be executed predates the validated commit.

    .DESCRIPTION
        Returns an outcome object rather than throwing so the caller controls its own
        exit code and diagnostics. `IsFresh` is false when ANY project is stale or
        missing; `Message` names the offending projects and their timestamps, because
        "the gate failed" without naming the stale artifact is what made the original
        occurrence take two hours to understand.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ProjectPath,
        [Parameter(Mandatory)][string]$Configuration,
        [Parameter(Mandatory)][DateTime]$ReferenceTimeUtc
    )

    # The @() wrapper is load-bearing: PowerShell unrolls a returned empty array to $null,
    # and under Set-StrictMode reading .Count off $null is a terminating error. An empty
    # project set is a legitimate input (nothing impacted), so it must not blow up the gate.
    $states = @(Get-BotNexusTestAssemblyState -ProjectPath $ProjectPath -Configuration $Configuration -ReferenceTimeUtc $ReferenceTimeUtc)
    $offenders = @($states | Where-Object { $_.State -ne 'fresh' })

    if ($offenders.Count -eq 0) {
        return [pscustomobject]@{
            IsFresh   = $true
            States    = $states
            Offenders = @()
            Message   = "All $($states.Count) test assemblies for configuration '$Configuration' postdate the validated commit."
        }
    }

    $detail = ($offenders | ForEach-Object {
            $when = if ($null -ne $_.LastWriteTimeUtc) { $_.LastWriteTimeUtc.ToString('o') } else { 'no assembly on disk' }
            "  - $($_.Name) [$($_.State)] $when"
        }) -join [Environment]::NewLine

    $message = @(
        "Refusing to run tests with --no-build: $($offenders.Count) of $($states.Count) test assemblies are stale or missing for configuration '$Configuration'.",
        "The validated commit is dated $($ReferenceTimeUtc.ToUniversalTime().ToString('o')); these artifacts predate it, so running them would report a verdict about code that was never compiled:",
        $detail,
        "Re-run the build step for configuration '$Configuration', or run without --no-build."
    ) -join [Environment]::NewLine

    return [pscustomobject]@{
        IsFresh   = $false
        States    = $states
        Offenders = $offenders
        Message   = $message
    }
}

function Resolve-BotNexusValidationBaseRef {
    <#
    .SYNOPSIS
        Refreshes the base ref and resolves the MERGE-BASE to diff the impacted set against.

    .DESCRIPTION
        Two corrections in one place, because they are the same bug:

          * FETCH. A remote-tracking ref is a cache. `git fetch` is attempted for
            remote-tracking refs so the impacted set is not computed against
            whatever that checkout last saw. A fetch failure (offline, no auth) is
            NOT fatal - the gate still runs - but it is reported, and `Fetched` is
            false so the caller can say so out loud. Silence is the defect.

          * MERGE-BASE. `git diff <base>` is a two-dot diff, so every commit that
            landed on the base since the branch forked enters the change set. The
            impacted set must be computed against `git merge-base <base> HEAD`,
            which is by construction unaffected by later base commits.

        `BehindCount` is the number of commits on the base ref that are not in HEAD,
        i.e. exactly how stale the raw two-dot diff would have been. It is reported
        for diagnostics; it is not a failure on its own, because merge-base already
        removes the harm.

    .PARAMETER NoFetch
        Skips the network round-trip (used by tests and by callers that already
        fetched). Staleness is still measured and reported.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$BaseRef,
        [switch]$NoFetch
    )

    $fetched = $false
    $fetchError = $null

    # Only remote-tracking refs are caches worth refreshing. A local ref, a tag or a
    # raw SHA is already authoritative in this checkout, and fetching for it would be
    # a pointless network call on every gate run.
    $isRemoteTracking = $BaseRef -match '^[^/]+/.+' -and $BaseRef -notmatch '^(HEAD|refs/)'
    $remoteName = if ($isRemoteTracking) { $BaseRef.Split('/')[0] } else { $null }

    if ($isRemoteTracking -and -not $NoFetch) {
        $output = & git -C $RepoRoot fetch $remoteName 2>&1
        if ($LASTEXITCODE -eq 0) {
            $fetched = $true
        }
        else {
            $fetchError = ($output | Out-String).Trim()
        }
    }

    $baseCommit = (& git -C $RepoRoot rev-parse --verify "$BaseRef^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($baseCommit)) {
        throw "Base ref cannot be resolved after fetch: $BaseRef"
    }
    $baseCommit = "$baseCommit".Trim()

    $mergeBase = (& git -C $RepoRoot merge-base $baseCommit HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($mergeBase)) {
        # No common ancestor (orphan branch / shallow clone). Fall back to the base
        # commit itself and say so, rather than pretending a merge-base was used.
        $mergeBase = $baseCommit
    }
    $mergeBase = "$mergeBase".Trim()

    $behind = 0
    $countOutput = (& git -C $RepoRoot rev-list --count "HEAD..$baseCommit" 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($countOutput)) {
        $behind = [int]("$countOutput".Trim())
    }

    return [pscustomobject]@{
        BaseRef     = $BaseRef
        BaseCommit  = $baseCommit
        MergeBase   = $mergeBase
        BehindCount = $behind
        Fetched     = $fetched
        FetchError  = $fetchError
        IsStale     = ($behind -gt 0)
    }
}

Export-ModuleMember -Function Get-BotNexusTestAssemblyState, Assert-BotNexusTestAssemblyFreshness, Resolve-BotNexusValidationBaseRef
