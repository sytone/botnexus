[CmdletBinding()]
param(
    [string]$WorktreePath = (Get-Location).Path,
    [string]$BaseRef = 'origin/main'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SourceSnapshot.psm1') -Force
$repoRoot = (Invoke-SnapshotGit $WorktreePath @('rev-parse', '--show-toplevel')).Trim()
$head = (Invoke-SnapshotGit $repoRoot @('rev-parse', 'HEAD')).Trim()
$baseCommit = (Invoke-SnapshotGit $repoRoot @('rev-parse', '--verify', $BaseRef)).Trim()
# Inspect the REAL index first. read-tree HEAD would discard forced ignored additions and
# hide unresolved conflicts. A resolved, still-uncommitted merge is a valid candidate.
$manifest = Get-SourceSnapshotManifest -RepoRoot $repoRoot
$hadIndex = Test-Path Env:GIT_INDEX_FILE
$savedIndex = $env:GIT_INDEX_FILE
$indexPath = (Invoke-SnapshotGit $repoRoot @('rev-parse', '--git-path', 'index')).Trim()
if (-not [IO.Path]::IsPathRooted($indexPath)) { $indexPath = Join-Path $repoRoot $indexPath }
$tempIndex = "$indexPath.snapshot-$([guid]::NewGuid().ToString('N'))"
try {
    if (Test-Path -LiteralPath $indexPath) { [IO.File]::Copy($indexPath, $tempIndex) }
    $env:GIT_INDEX_FILE = $tempIndex
    Invoke-SnapshotGit $repoRoot @('add', '--all') | Out-Null
    $tree = (Invoke-SnapshotGit $repoRoot @('write-tree')).Trim()
}
finally {
    if ($hadIndex) { $env:GIT_INDEX_FILE = $savedIndex }
    else { Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tempIndex, "$tempIndex.lock" -Force -ErrorAction SilentlyContinue
}
$after = Get-SourceSnapshotManifest -RepoRoot $repoRoot
if ($after.digest -cne $manifest.digest -or (Invoke-SnapshotGit $repoRoot @('rev-parse','HEAD')).Trim() -cne $head -or (Invoke-SnapshotGit $repoRoot @('rev-parse','--verify',$BaseRef)).Trim() -cne $baseCommit) {
    throw 'Source changed while calculating validation fingerprint. Retry from a stable worktree.'
}
# Domain/version separation deliberately invalidates legacy receipts, even for clean trees.
$material = "botnexus-validation-exact-source-v1`n$head`n$baseCommit`n$tree`n$($manifest.digest)`n"
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()
[pscustomobject]@{
    fingerprint = $fingerprint
    head = $head
    baseRef = $BaseRef
    baseCommit = $baseCommit
    tree = $tree
    sourceSnapshot = [pscustomobject]@{version=1;digest=$manifest.digest}
}
