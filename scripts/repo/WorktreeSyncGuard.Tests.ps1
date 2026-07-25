[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'WorktreeSyncGuard.ps1')

$failures = [Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { $failures.Add($Message) } }

# --- happy paths: an idle worktree is safe to sync -------------------------
Assert-True (Test-WorktreeIsIdle -StatusOutput $null) 'null status should be idle.'
Assert-True (Test-WorktreeIsIdle -StatusOutput '') 'empty status should be idle.'
Assert-True (Test-WorktreeIsIdle -StatusOutput "`n") 'newline-only status should be idle.'
Assert-True (Test-WorktreeIsIdle -StatusOutput @()) 'empty array status should be idle.'
Assert-True (Test-WorktreeIsIdle -StatusOutput "   `t ") 'whitespace-only status should be idle.'

# --- sad paths: any porcelain entry means work is in flight ----------------
Assert-True (-not (Test-WorktreeIsIdle -StatusOutput ' M src/Foo.cs')) 'modified file should not be idle.'
Assert-True (-not (Test-WorktreeIsIdle -StatusOutput 'A  src/Foo.cs')) 'staged add should not be idle.'
Assert-True (-not (Test-WorktreeIsIdle -StatusOutput '?? tmp/scratch.txt')) 'untracked file should not be idle.'
Assert-True (-not (Test-WorktreeIsIdle -StatusOutput 'D  src/Gone.cs')) 'staged delete should not be idle.'
Assert-True (-not (Test-WorktreeIsIdle -StatusOutput @(' M a.cs', '?? b.cs'))) 'array status with entries should not be idle.'

# --- refusal message names the branch, the worktree and the issue ----------
$msg = Get-DirtyWorktreeSyncMessage -Branch 'fix/2330-sync' -WorktreePath 'Q:/repos/botnexus-wt/x'
Assert-True ($msg -like '*fix/2330-sync*') 'message should name the branch.'
Assert-True ($msg -like '*Q:/repos/botnexus-wt/x*') 'message should name the worktree path.'
Assert-True ($msg -like '*#2330*') 'message should reference the issue.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Host "WorktreeSyncGuard.Tests.ps1: $($failures.Count) failure(s)."
    exit 1
}

Write-Host 'WorktreeSyncGuard.Tests.ps1: all assertions passed.'
exit 0
