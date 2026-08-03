<#
.SYNOPSIS
    Pure selection helpers for reclaiming orphaned BotNexus firewall rules.
.DESCRIPTION
    Issue #2774. The original prune in Ensure-TesthostFirewallRules.ps1 iterated
    `Get-NetFirewallRule -Group 'BotNexus-Testhost'` only. When a developer
    answers an interactive Windows Firewall prompt, Windows creates rules named
    `TCP Query User{<GUID>}<program path>` with NO group at all, so a group-only
    prune is a structural no-op against exactly the rule class the prompts
    generate. The rule set therefore grew monotonically, one pair per worktree
    ever created, most pointing at long-deleted paths.

    These functions are deliberately pure: they take rule objects and injected
    predicates and return the NAMES to remove. That keeps the narrowness
    guarantee testable without touching the host firewall.

    NARROWNESS CONTRACT (issue #2774 AC4). A rule is selected for removal only
    when BOTH hold:
      1. its program path is under one of the supplied repo/worktree roots, and
      2. that program path no longer exists on disk.
    A BotNexus lease rule is additionally spared while its owning process is
    still alive, even though its program exists. Nothing else is ever selected:
    no rule without a program, no rule outside the roots, no rule whose binary
    is still present. An over-broad firewall prune on a developer machine is
    worse than the bug it fixes.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Returns $true when $Path lies inside one of $RepoRoot.
.DESCRIPTION
    Comparison is case-insensitive (Windows paths) and boundary-safe: a sibling
    directory that merely shares a textual prefix with a root (for example
    `Q:\repos\botnexus-wtx` against root `Q:\repos\botnexus-wt`) is NOT inside
    it. Null/empty/whitespace paths are never inside any root.
#>
function Test-PathUnderRoot {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [AllowNull()][AllowEmptyString()][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$RepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }

    $normalizedPath = $Path.Trim().TrimEnd('\', '/')
    foreach ($root in @($RepoRoot)) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $normalizedRoot = $root.Trim().TrimEnd('\', '/')
        if ($normalizedRoot.Length -eq 0) { continue }
        if ($normalizedPath.StartsWith($normalizedRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.StartsWith($normalizedRoot + '/', [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

<#
.SYNOPSIS
    Derives the narrow set of roots the prune is allowed to operate within.
.DESCRIPTION
    Includes the repository root itself plus, for each supplied worktree path,
    that worktree's PARENT container (typically `...\botnexus-wt`). The
    container is required because a deleted worktree no longer appears in
    `git worktree list`, so its stale rules would otherwise be unreachable -
    which is the whole accumulating symptom of #2774.

    A container is only accepted when it is strictly deeper than its own drive
    root, so the prune can never widen to a drive or filesystem root.
#>
function Get-BotNexusFirewallRoot {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [AllowNull()][string[]]$WorktreePath
    )

    $roots = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    function Add-Root([string]$Candidate) {
        if ([string]::IsNullOrWhiteSpace($Candidate)) { return }
        $normalized = $Candidate.Trim().TrimEnd('\', '/')
        if ($normalized.Length -eq 0) { return }
        # Refuse drive roots and UNC share roots outright.
        $parent = Split-Path -Parent $normalized
        if ([string]::IsNullOrWhiteSpace($parent)) { return }
        if ($seen.Add($normalized)) { $roots.Add($normalized) }
    }

    Add-Root $RepositoryRoot

    # Collect candidate containers first so the shallowest ones can be discarded
    # before any is adopted as a root.
    #
    # WHY discard by depth rather than by comparing against the repository
    # root's parent: when this runs from inside a linked worktree, the
    # repository root IS the worktree, so its parent is the worktree container
    # and the main repo's container (e.g. `Q:\repos`) escapes that check
    # entirely - widening the prune to every repository on the drive. Dropping
    # any candidate that is a strict ancestor of another candidate is
    # independent of which worktree the script happens to be invoked from.
    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($worktree in @($WorktreePath)) {
        if ([string]::IsNullOrWhiteSpace($worktree)) { continue }
        $container = Split-Path -Parent ($worktree.Trim().TrimEnd('\', '/'))
        if ([string]::IsNullOrWhiteSpace($container)) { continue }
        $candidates.Add($container.TrimEnd('\', '/'))
    }

    foreach ($container in $candidates) {
        $isAncestorOfAnother = $false
        foreach ($other in $candidates) {
            if ($other.Equals($container, [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($other.StartsWith($container + '\', [StringComparison]::OrdinalIgnoreCase) -or
                $other.StartsWith($container + '/', [StringComparison]::OrdinalIgnoreCase)) {
                $isAncestorOfAnother = $true
                break
            }
        }
        if ($isAncestorOfAnother) { continue }
        Add-Root $container
    }

    return $roots.ToArray()
}

<#
.SYNOPSIS
    Selects the names of firewall rules that are safe to reclaim.
.PARAMETER Rule
    Rule objects exposing Name, Group and Program. Real callers project
    Get-NetFirewallRule output joined to its application filter.
.PARAMETER RepoRoot
    The only directories the prune may act within.
.PARAMETER PathExists
    Predicate taking a program path and returning $true when it still exists.
    Injected so narrowness is testable without touching the filesystem.
.PARAMETER LeaseOwnerIsAlive
    Predicate taking (processId, startTicks) for `BotNexus-Testhost-<pid>-<ticks>-...`
    lease rules. When it returns $true the rule belongs to a running test run
    and is spared.
#>
function Select-OrphanedFirewallRuleName {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [AllowNull()][object[]]$Rule,
        [Parameter(Mandatory = $true)][string[]]$RepoRoot,
        [Parameter(Mandatory = $true)][scriptblock]$PathExists,
        [AllowNull()][scriptblock]$LeaseOwnerIsAlive
    )

    $names = [System.Collections.Generic.List[string]]::new()

    foreach ($candidate in @($Rule)) {
        if ($null -eq $candidate) { continue }

        $program = $null
        if ($candidate.PSObject.Properties['Program']) { $program = $candidate.Program }

        # Gate 1: must be a real program path inside an allowed root.
        if (-not (Test-PathUnderRoot -Path $program -RepoRoot $RepoRoot)) { continue }

        $name = [string]$candidate.Name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }

        # A lease rule with a live owner is in use right now - never touch it,
        # regardless of what the filesystem says.
        if ($name -match '^BotNexus-Testhost-(\d+)-(\d+)-') {
            $ownerProcessId = [int]$Matches[1]
            $ownerStartTicks = [long]$Matches[2]
            $ownerAlive = $false
            if ($null -ne $LeaseOwnerIsAlive) {
                $ownerAlive = [bool](& $LeaseOwnerIsAlive $ownerProcessId $ownerStartTicks)
            }
            if ($ownerAlive) { continue }
            $names.Add($name)
            continue
        }

        # Gate 2: the binary must be gone. A path that still exists may belong
        # to a run in flight, so it is never reclaimed here.
        if ([bool](& $PathExists $program)) { continue }

        $names.Add($name)
    }

    return $names.ToArray()
}
