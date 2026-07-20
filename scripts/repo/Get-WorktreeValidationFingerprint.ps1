[CmdletBinding()]
param(
    [string]$WorktreePath = (Get-Location).Path,
    [string]$BaseRef = 'origin/main'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (& git -C $WorktreePath rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "WorktreePath is not inside a git repository: $WorktreePath"
}
$repoRoot = $repoRoot.Trim()
$head = (& git -C $repoRoot rev-parse HEAD).Trim()
$baseCommit = (& git -C $repoRoot rev-parse $BaseRef 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($baseCommit)) {
    throw "Base ref cannot be resolved: $BaseRef"
}
$baseCommit = $baseCommit.Trim()

$tempIndex = Join-Path ([IO.Path]::GetTempPath()) "botnexus-validation-$([Guid]::NewGuid().ToString('N')).index"
try {
    $env:GIT_INDEX_FILE = $tempIndex
    & git -C $repoRoot read-tree HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize temporary Git index.' }
    & git -C $repoRoot add --all
    if ($LASTEXITCODE -ne 0) { throw 'Unable to capture the worktree in the temporary Git index.' }
    $tree = (& git -C $repoRoot write-tree).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tree)) { throw 'Unable to calculate worktree tree hash.' }
}
finally {
    Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    Remove-Item $tempIndex -Force -ErrorAction SilentlyContinue
    Remove-Item "$tempIndex.lock" -Force -ErrorAction SilentlyContinue
}

$material = "$head`n$baseCommit`n$tree`n"
$bytes = [Text.Encoding]::UTF8.GetBytes($material)
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()

[pscustomobject]@{
    fingerprint = $fingerprint
    head = $head
    baseRef = $BaseRef
    baseCommit = $baseCommit
    tree = $tree
}
