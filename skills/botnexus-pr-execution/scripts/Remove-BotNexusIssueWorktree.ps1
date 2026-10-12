[CmdletBinding()]
param(
  [Parameter(Mandatory)][int]$Issue,
  [Parameter(Mandatory)][int]$PullRequestNumber,
  [string]$Repository = 'Sytone/botnexus',
  [Parameter(Mandatory)][string]$RepoRoot,
  [string]$WorktreePath
)
$ErrorActionPreference = 'Stop'
function Get-CheckedGitValue([string]$Path, [string[]]$Arguments) {
  $records = @(& git -C $Path @Arguments)
  if ($LASTEXITCODE -ne 0) { throw "Git $($Arguments -join ' ') failed for $Path." }
  if ($records.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$records[0])) {
    throw "Git $($Arguments -join ' ') returned invalid output for $Path."
  }
  ([string]$records[0]).Trim()
}
function Get-NormalizedWorktreePath([string]$Path) {
  if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)) { throw 'Worktree registration requires an absolute path.' }
  [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
}
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$canonicalRepo = Get-NormalizedWorktreePath $RepoRoot
$worktreeRoot = Join-Path (Split-Path $canonicalRepo -Parent) ((Split-Path $canonicalRepo -Leaf) + '-wt')
$rootPrefix = $worktreeRoot + [IO.Path]::DirectorySeparatorChar
$worktreeLines = @(& git -C $RepoRoot worktree list --porcelain)
if ($LASTEXITCODE -ne 0 -or $worktreeLines.Count -eq 0) { throw 'Worktree registration read failed.' }
$prJson = gh pr view $PullRequestNumber --repo $Repository --json number,state,mergedAt,mergeCommit,headRefName,headRefOid,baseRefName
if ($LASTEXITCODE -ne 0) { throw "PR #$PullRequestNumber read failed." }
$pr = $prJson | ConvertFrom-Json
if ($null -eq $pr -or @($pr).Count -ne 1 -or $pr.state -ne 'MERGED' -or -not $pr.mergedAt) { throw "PR #$PullRequestNumber is not merged." }
foreach ($value in @($pr.headRefName, $pr.headRefOid, $pr.baseRefName)) {
  if ([string]::IsNullOrWhiteSpace([string]$value)) { throw "PR #$PullRequestNumber has incomplete branch/head/base metadata." }
}
# Registration metadata selects candidates without opening unrelated repositories.
$entries = [Collections.Generic.List[object]]::new()
$entry = $null
foreach ($line in $worktreeLines) {
  if ($line -like 'worktree *') {
    $entry = @{path=($line.Substring(9));branch=$null;head=$null}
    $entries.Add($entry)
  } elseif ($line -like 'branch *' -and $null -ne $entry) {
    if ($null -ne $entry.branch) { throw 'Duplicate branch registration metadata.' }
    $entry.branch = $line.Substring(7)
  } elseif ($line -like 'HEAD *' -and $null -ne $entry) {
    if ($null -ne $entry.head) { throw 'Duplicate HEAD registration metadata.' }
    $entry.head = $line.Substring(5)
  }
}
if ($PSBoundParameters.ContainsKey('WorktreePath')) {
  $requested = Get-NormalizedWorktreePath $WorktreePath
  if (-not $requested.StartsWith($rootPrefix, $comparison)) { throw "Worktree must be under $worktreeRoot." }
  $matches = @($entries | Where-Object { (Get-NormalizedWorktreePath $_.path).Equals($requested, $comparison) })
} else {
  $matches = @($entries | Where-Object {
    $_.branch -ceq "refs/heads/$($pr.headRefName)" -and $_.head -ceq $pr.headRefOid
  } | Where-Object { (Get-NormalizedWorktreePath $_.path).StartsWith($rootPrefix, $comparison) })
}
if ($matches.Count -ne 1) { throw "Expected one registered worktree matching merged PR #$PullRequestNumber; found $($matches.Count)." }
$selected = $matches[0]
$path = Get-NormalizedWorktreePath $selected.path
if ($selected.branch -cne "refs/heads/$($pr.headRefName)" -or $selected.head -cne $pr.headRefOid) { throw 'Registered worktree branch/head does not match merged PR.' }
if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Worktree not found: $path" }
# Do not let a junction/symlink turn lexical containment into an out-of-root target.
$current = Get-Item -LiteralPath $path
while ($null -ne $current -and $current.FullName.StartsWith($rootPrefix, $comparison)) {
  if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Worktree path contains a link: $($current.FullName)" }
  $current = $current.Parent
}
if ((Get-Item -LiteralPath $worktreeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Canonical worktree root is a link.' }
$branch = Get-CheckedGitValue $path @('rev-parse','--abbrev-ref','HEAD')
$head = Get-CheckedGitValue $path @('rev-parse','HEAD')
if ($branch -cne $pr.headRefName -or $head -cne $pr.headRefOid) { throw 'Worktree branch/head does not match merged PR.' }
$status = @(& git -C $path status --porcelain)
if ($LASTEXITCODE -ne 0) { throw "Worktree status read failed: $path" }
# Zero records is the only clean status. Blank/null records are not evidence of clean.
if ($status.Count -ne 0) { throw "Worktree is dirty or status output is invalid: $path" }
& git -C $RepoRoot fetch origin $pr.baseRefName
if ($LASTEXITCODE -ne 0) { throw 'Fetch failed.' }
$mergeOid = [string]$pr.mergeCommit.oid
if ([string]::IsNullOrWhiteSpace($mergeOid)) {
  $mergeJson = gh pr view $PullRequestNumber --repo $Repository --json mergeCommit
  if ($LASTEXITCODE -ne 0) { throw 'Merge commit read failed.' }
  $mergeRecord = $mergeJson | ConvertFrom-Json
  if ($null -eq $mergeRecord -or @($mergeRecord).Count -ne 1) { throw 'Invalid merge commit response.' }
  $mergeOid = [string]$mergeRecord.mergeCommit.oid
}
if ([string]::IsNullOrWhiteSpace($mergeOid)) { throw "PR #$PullRequestNumber has no merge commit OID." }
& git -C $RepoRoot merge-base --is-ancestor $mergeOid "origin/$($pr.baseRefName)"
if ($LASTEXITCODE -ne 0) { throw "PR merge commit is not on origin/$($pr.baseRefName): $mergeOid" }
. (Join-Path $RepoRoot 'scripts/repo/Remove-Worktree.ps1')
$result = Remove-WorktreeSafely -RepoRoot $RepoRoot -WorktreePath $path -DeleteBranch
if ($result.outcome -notin @('removed','reclaimed','absent')) { throw "Cleanup failed: $($result.outcome)" }
$result | ConvertTo-Json -Depth 6
