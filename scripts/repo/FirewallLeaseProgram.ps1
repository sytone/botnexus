<#
.SYNOPSIS
    Derives the set of programs a testhost firewall lease must cover.
.DESCRIPTION
    Issue #2774 Gap 2. `Ensure-TesthostFirewallRules.ps1` used to compose exactly
    one path per project:

        <project-dir>/bin/<config>/<tfw>/testhost.exe

    That literal is wrong whenever a test fixture launches an additional
    process. `CliTestFixture.cs` and `CrossProcessConfigWriteTests.cs` both start
    child processes, and `BotNexus.Cli.exe` sits in the very same output
    directory - unleased, so it prompts, and the prompt creates the ungrouped
    `TCP Query User{GUID}` rule that drove the monotonic accumulation in #2774.

    This helper derives the leased set from BUILD OUTPUT instead: every
    executable actually present in the project's output directory. Adding a new
    child-process fixture therefore requires no change here.

    NARROWNESS (the lease is a mutation of host firewall state, so it gets the
    same treatment as the prune): only files that live directly in the project's
    own `bin/<config>/<tfw>` directory are ever leased. The enumeration is
    injected so this is testable without a build and without touching the real
    firewall.

    FALL-BACK: when the output directory does not exist yet - a first run before
    any build - the composed `testhost.exe` path is still returned so the lease
    behaves no worse than it did before. It is the floor, never the ceiling.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Returns the output directory a project's leasable binaries live in.
#>
function Get-ProjectOutputDirectory {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$TargetFramework
    )

    $projectDirectory = Split-Path -Parent $ProjectPath
    if ([string]::IsNullOrWhiteSpace($projectDirectory)) { return $null }

    $combined = Join-Path $projectDirectory (Join-Path 'bin' (Join-Path $Configuration $TargetFramework))
    return [System.IO.Path]::GetFullPath($combined)
}

<#
.SYNOPSIS
    Derives every program that should be covered by a testhost firewall lease.
.PARAMETER ProjectPath
    One or more test project file paths.
.PARAMETER GetOutputExecutable
    Predicate taking an output directory and returning the executable file
    paths inside it. Injected so the derivation is testable against a seeded
    directory listing rather than a real build. Defaults to enumerating `*.exe`
    directly inside the directory (non-recursive - obj/ and runtime sub-folders
    are deliberately out of scope).
.OUTPUTS
    A de-duplicated, case-insensitive array of absolute program paths.
#>
function Get-LeasedProgramPath {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [AllowNull()][string[]]$ProjectPath,
        [string]$Configuration = 'Debug',
        [string]$TargetFramework = 'net10.0',
        [AllowNull()][scriptblock]$GetOutputExecutable
    )

    if ($null -eq $GetOutputExecutable) {
        $GetOutputExecutable = {
            param($directory)
            if (-not (Test-Path -LiteralPath $directory)) { return @() }
            return @(Get-ChildItem -LiteralPath $directory -Filter '*.exe' -File -ErrorAction SilentlyContinue |
                ForEach-Object { $_.FullName })
        }
    }

    $programs = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($project in @($ProjectPath)) {
        if ([string]::IsNullOrWhiteSpace($project)) { continue }

        $outputDirectory = Get-ProjectOutputDirectory -ProjectPath $project -Configuration $Configuration -TargetFramework $TargetFramework
        if ([string]::IsNullOrWhiteSpace($outputDirectory)) { continue }

        $found = @(& $GetOutputExecutable $outputDirectory)
        $added = 0

        foreach ($candidate in $found) {
            if ([string]::IsNullOrWhiteSpace($candidate)) { continue }

            $full = [System.IO.Path]::GetFullPath([string]$candidate)

            # Narrowness: only binaries directly inside this project's own output
            # directory. A predicate that walks wider must not widen the lease.
            $parent = Split-Path -Parent $full
            if (-not $parent.TrimEnd('\', '/').Equals($outputDirectory.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            if ($seen.Add($full)) { $programs.Add($full) }
            $added++
        }

        # Nothing built yet: keep the pre-#2774 floor so a lease still happens.
        if ($added -eq 0) {
            $fallback = [System.IO.Path]::GetFullPath((Join-Path $outputDirectory 'testhost.exe'))
            if ($seen.Add($fallback)) { $programs.Add($fallback) }
        }
    }

    return $programs.ToArray()
}
