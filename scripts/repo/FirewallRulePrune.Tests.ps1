[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'FirewallRulePrune.ps1')

$failures = [Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { $failures.Add($Message) } }

# Roots used by every case below. Nothing outside these may ever be pruned.
$roots = @('Q:\repos\botnexus', 'Q:\repos\botnexus-wt')

# Only these paths "exist on disk" for the purposes of these tests.
$livePaths = @(
    'Q:\repos\botnexus-wt\live-worktree\tests\gateway\BotNexus.Cli.Tests\bin\debug\net10.0\testhost.exe',
    'Q:\repos\botnexus\tests\gateway\BotNexus.Cli.Tests\bin\debug\net10.0\testhost.exe'
)
$pathExists = { param($p) $livePaths -contains $p }

function New-Rule([string]$Name, [string]$Group, [string]$Program) {
    [pscustomobject]@{ Name = $Name; Group = $Group; Program = $Program }
}

# --- AC3: prompt-created, ungrouped `Query User{GUID}` rules are reclaimable ---
# Windows names these `TCP Query User{<GUID>}<program path>` and assigns NO group,
# which is exactly why a group-only prune was a structural no-op against them.
$deadProgram = 'Q:\repos\botnexus-wt\fix-compaction-summary-converter\tests\gateway\BotNexus.Cli.Tests\bin\debug\net10.0\testhost.exe'
$promptRule = New-Rule 'TCP Query User{2B4E1B0C-9E4F-4F0B-9A61-2C6D1A3F5E77}Q:\repos\botnexus-wt\fix-compaction-summary-converter\tests\gateway\botnexus.cli.tests\bin\debug\net10.0\testhost.exe' '' $deadProgram
$udpPromptRule = New-Rule 'UDP Query User{7C0A6E31-1D22-4A7E-8E3A-9B5F2D4C6A18}Q:\repos\botnexus-wt\fix-1709-recover\...\testhost.exe' $null 'Q:\repos\botnexus-wt\fix-1709-recover\tests\gateway\BotNexus.Cli.Tests\bin\debug\net10.0\testhost.exe'

$selected = @(Select-OrphanedFirewallRuleName -Rule @($promptRule, $udpPromptRule) -RepoRoot $roots -PathExists $pathExists)
Assert-True ($selected -contains $promptRule.Name) 'AC3: ungrouped TCP Query User rule for a nonexistent in-root path must be pruned.'
Assert-True ($selected -contains $udpPromptRule.Name) 'AC3: ungrouped UDP Query User rule with a null group for a nonexistent in-root path must be pruned.'

# --- AC4 (negative): nothing outside the repo/worktree roots may ever be pruned ---
$outOfRootRules = @(
    (New-Rule 'TCP Query User{AAAA1111-0000-0000-0000-000000000001}C:\Program Files\Contoso\vpn.exe' '' 'C:\Program Files\Contoso\vpn.exe'),
    (New-Rule 'TCP Query User{AAAA1111-0000-0000-0000-000000000002}D:\dev\other-repo\bin\testhost.exe' '' 'D:\dev\other-repo\bin\testhost.exe'),
    (New-Rule 'Core Networking - DNS (UDP-Out)' '@FirewallAPI.dll,-25000' $null),
    (New-Rule 'Some rule with no program at all' '' ''),
    # a path that merely *starts with* a root's text but is a different directory
    (New-Rule 'TCP Query User{AAAA1111-0000-0000-0000-000000000003}Q:\repos\botnexus-other\bin\testhost.exe' '' 'Q:\repos\botnexus-other\bin\testhost.exe')
)
$selectedOut = @(Select-OrphanedFirewallRuleName -Rule $outOfRootRules -RepoRoot $roots -PathExists $pathExists)
foreach ($r in $outOfRootRules) {
    Assert-True (-not ($selectedOut -contains $r.Name)) "AC4: rule outside repo roots must survive the prune: $($r.Name)"
}
Assert-True ($selectedOut.Count -eq 0) 'AC4: no out-of-root rule may be selected for removal.'

# --- AC4 (negative): a rule whose program still exists on disk survives ---
# A live lease held by a running process always points at an existing binary.
$liveRules = @(
    (New-Rule 'TCP Query User{BBBB2222-0000-0000-0000-000000000001}live' '' $livePaths[0]),
    (New-Rule 'TCP Query User{BBBB2222-0000-0000-0000-000000000002}live' '' $livePaths[1])
)
$selectedLive = @(Select-OrphanedFirewallRuleName -Rule $liveRules -RepoRoot $roots -PathExists $pathExists)
Assert-True ($selectedLive.Count -eq 0) 'AC4: rules whose program path still exists on disk must survive the prune.'

# --- AC4: a lease rule owned by a live process survives; a dead owner is reclaimed ---
$aliveOwner = { param($processId, $startTicks) $processId -eq 4242 }
$leaseAlive = New-Rule 'BotNexus-Testhost-4242-63800000000000000-abc-0' 'BotNexus-Testhost' $livePaths[0]
$leaseDead = New-Rule 'BotNexus-Testhost-9999-63800000000000000-def-0' 'BotNexus-Testhost' $livePaths[0]
$selectedLease = @(Select-OrphanedFirewallRuleName -Rule @($leaseAlive, $leaseDead) -RepoRoot $roots -PathExists $pathExists -LeaseOwnerIsAlive $aliveOwner)
Assert-True (-not ($selectedLease -contains $leaseAlive.Name)) 'AC4: a lease rule owned by a live process must survive.'
Assert-True ($selectedLease -contains $leaseDead.Name) 'AC3/AC4: a lease rule whose owner process is gone must be reclaimed.'

# --- mixed batch: only the orphans come back, in one pass -------------------
$mixed = @($promptRule) + $outOfRootRules + $liveRules + @($leaseAlive)
$selectedMixed = @(Select-OrphanedFirewallRuleName -Rule $mixed -RepoRoot $roots -PathExists $pathExists -LeaseOwnerIsAlive $aliveOwner)
Assert-True ($selectedMixed.Count -eq 1 -and $selectedMixed[0] -eq $promptRule.Name) 'Mixed batch must select exactly the one in-root orphan.'

# --- root containment helper is case-insensitive and boundary-safe ---------
Assert-True (Test-PathUnderRoot -Path 'q:\REPOS\BOTNEXUS-WT\x\y.exe' -RepoRoot $roots) 'Root containment must be case-insensitive.'
Assert-True (-not (Test-PathUnderRoot -Path 'Q:\repos\botnexus-wtx\y.exe' -RepoRoot $roots)) 'Root containment must not match a sibling directory sharing a prefix.'
Assert-True (-not (Test-PathUnderRoot -Path '' -RepoRoot $roots)) 'Empty program path is never under a root.'
Assert-True (-not (Test-PathUnderRoot -Path $null -RepoRoot $roots)) 'Null program path is never under a root.'

# --- root derivation includes worktree containers, not just live worktrees ---
# Deleted worktrees are absent from `git worktree list`, so the container dir
# must be a root or the deleted-worktree case is unreachable.
$derived = @(Get-BotNexusFirewallRoot -RepositoryRoot 'Q:\repos\botnexus' -WorktreePath @('Q:\repos\botnexus-wt\live-worktree'))
Assert-True ($derived -contains 'Q:\repos\botnexus') 'Derived roots must include the repository root.'
Assert-True ($derived -contains 'Q:\repos\botnexus-wt') 'Derived roots must include the worktree container so deleted worktrees are reachable.'
Assert-True (-not ($derived -contains 'Q:\repos')) 'Derived roots must not widen to the whole repos parent directory.'
Assert-True (-not ($derived -contains 'Q:\')) 'Derived roots must never include a drive root.'

# --- root derivation must not widen when INVOKED FROM A WORKTREE (#2774) ---
# Found by running the reclaim script from inside a worktree: `git rev-parse
# --show-toplevel` returns the WORKTREE, so the old guard (compare each
# container against the repository root's parent) compared `Q:\repos` against
# `Q:\repos\botnexus-wt` and let `Q:\repos` through - putting every unrelated
# repository on the drive in scope. Live evidence: the dry run offered to
# remove BotNexus.Gateway and BotNexus.Probe rules under `Q:\repos\botnexus`
# while the repository root was a worktree.
$fromWorktree = @(Get-BotNexusFirewallRoot -RepositoryRoot 'Q:\repos\botnexus-wt\fix-2774-firewall-prune' -WorktreePath @(
    'Q:\repos\botnexus',
    'Q:\repos\botnexus-wt\fix-2774-firewall-prune',
    'Q:\repos\botnexus-wt\other-worktree'
))
Assert-True (-not ($fromWorktree -contains 'Q:\repos')) 'Invoked from a worktree, derived roots must not widen to the repos parent directory.'
Assert-True ($fromWorktree -contains 'Q:\repos\botnexus-wt') 'Invoked from a worktree, the worktree container must still be a root.'
Assert-True (-not ($fromWorktree -contains 'Q:\')) 'Invoked from a worktree, derived roots must never include a drive root.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Host "FirewallRulePrune.Tests.ps1: $($failures.Count) failure(s)."
    exit 1
}

Write-Host 'FirewallRulePrune.Tests.ps1: all assertions passed.'
exit 0
