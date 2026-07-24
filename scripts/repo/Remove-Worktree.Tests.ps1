[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Remove-Worktree.ps1')

$failures = [Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { $failures.Add($Message) } }
function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) { if ($Expected -ne $Actual) { $failures.Add("$Message Expected '$Expected', got '$Actual'.") } }

# ---------------------------------------------------------------------------
# Test harness: a fake git invoker that scripts responses per subcommand and
# records the ordered call log so we can assert prune/branch ordering.
# ---------------------------------------------------------------------------
function New-FakeGit {
    param(
        [scriptblock]$RemoveResponse,   # param($attempt) -> @{exitCode;output}
        [hashtable]$Static = @{}        # keyed by subcommand -> @{exitCode;output}
    )
    $state = [ordered]@{
        calls        = [Collections.Generic.List[string]]::new()
        removeCalls  = 0
        removeResp   = $RemoveResponse
        static       = $Static
    }
    $invoker = {
        param([string[]]$GitArgs)
        $joined = ($GitArgs -join ' ')
        $state.calls.Add($joined) | Out-Null
        if ($joined -match 'worktree remove') {
            $state.removeCalls++
            return (& $state.removeResp $state.removeCalls)
        }
        if ($joined -match 'rev-parse --abbrev-ref HEAD') {
            if ($state.static.ContainsKey('branch')) { return $state.static['branch'] }
            return @{ exitCode = 0; output = "fix/2104-worktree-locks`n" }
        }
        if ($joined -match 'worktree prune') { return @{ exitCode = 0; output = '' } }
        if ($joined -match 'branch -D') { return @{ exitCode = 0; output = '' } }
        return @{ exitCode = 0; output = '' }
    }.GetNewClosure()
    return @{ invoker = $invoker; state = $state }
}

$repo = 'Q:\repos\botnexus'
$wt = 'Q:\repos\botnexus-wt\fix-2104-worktree-locks'

# ---------------------------------------------------------------------------
# 1. A locked worktree returns a structured 'locked' outcome and retains
#    path/branch/metadata.
# ---------------------------------------------------------------------------
$fake = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 1; output = 'fatal: ... : The process cannot access the file because it is being used by another process' } }
$result = Remove-WorktreeSafely -RepoRoot $repo -WorktreePath $wt -DeleteBranch `
    -MaxRetries 3 -BaseDelayMs 1 `
    -GitInvoker $fake.invoker `
    -DirectoryRemover { param($p) throw 'should not remove dir on locked' } `
    -LockerProbe { param($p) @(@{ pid = 123; name = 'node' }) } `
    -Sleeper { param($ms) }
Assert-Equal 'locked' $result.outcome 'Locked worktree should return locked outcome.'
Assert-Equal $wt $result.path 'Locked outcome should retain the path.'
Assert-Equal 'fix/2104-worktree-locks' $result.branch 'Locked outcome should retain the branch.'
Assert-True ($result.ContainsKey('likelyLockers') -and $result.likelyLockers.Count -ge 1) 'Locked outcome should report likely lockers.'

# ---------------------------------------------------------------------------
# 2. Branch is NOT deleted when removal fails.
# ---------------------------------------------------------------------------
Assert-Equal $false $result.branchDeleted 'Branch must not be deleted on failed removal.'
Assert-True (@($fake.state.calls | Where-Object { $_ -match 'branch -D' }).Count -eq 0) 'git branch -D must never be called when removal failed.'

# ---------------------------------------------------------------------------
# 3. Prune only runs after a SUCCESSFUL removal (never on a locked outcome).
# ---------------------------------------------------------------------------
Assert-True (@($fake.state.calls | Where-Object { $_ -match 'worktree prune' }).Count -eq 0) 'prune must not run when removal failed.'
Assert-Equal $false $result.pruned 'Locked outcome must not be pruned.'

# ---------------------------------------------------------------------------
# 4. Bounded retry with backoff: exactly MaxRetries+1 attempts, and backoff
#    delays follow an exponential (200,400,800...) schedule.
# ---------------------------------------------------------------------------
$delays = [Collections.Generic.List[int]]::new()
$fake2 = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 1; output = 'Access is denied' } }
$r2 = Remove-WorktreeSafely -RepoRoot $repo -WorktreePath $wt `
    -MaxRetries 3 -BaseDelayMs 200 `
    -GitInvoker $fake2.invoker `
    -DirectoryRemover { param($p) } `
    -LockerProbe { param($p) @() } `
    -Sleeper { param($ms) $delays.Add($ms) | Out-Null }
Assert-Equal 4 $r2.attempts 'Bounded retry should attempt MaxRetries+1 times.'
Assert-Equal 3 $delays.Count 'Should back off between each failed attempt (MaxRetries times).'
Assert-Equal 200 $delays[0] 'First backoff should be BaseDelayMs.'
Assert-Equal 400 $delays[1] 'Second backoff should double.'
Assert-Equal 800 $delays[2] 'Third backoff should double again.'

# ---------------------------------------------------------------------------
# 5. A transient lock that clears on retry -> success, prune runs, branch deleted.
# ---------------------------------------------------------------------------
$tmpWt = Join-Path ([IO.Path]::GetTempPath()) ("wt-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpWt -Force | Out-Null
$fake3 = New-FakeGit -RemoveResponse {
    param($n)
    if ($n -lt 2) { return @{ exitCode = 1; output = 'being used by another process' } }
    return @{ exitCode = 0; output = '' }
}
$r3 = Remove-WorktreeSafely -RepoRoot $repo -WorktreePath $tmpWt -DeleteBranch `
    -MaxRetries 4 -BaseDelayMs 1 `
    -GitInvoker $fake3.invoker `
    -DirectoryRemover { param($p) Remove-Item -LiteralPath $p -Recurse -Force } `
    -LockerProbe { param($p) @() } `
    -Sleeper { param($ms) }
Assert-Equal 'removed' $r3.outcome 'Transient lock that clears should end in removed.'
Assert-Equal 2 $r3.attempts 'Should succeed on the second attempt.'
Assert-Equal $true $r3.pruned 'Prune should run after successful directory removal.'
Assert-Equal $true $r3.branchDeleted 'Branch should be deleted after fully successful removal.'
# Ordering: prune must come AFTER the successful remove, and branch -D after prune.
$order = $fake3.state.calls
$pruneIdx = ($order | Select-String -SimpleMatch 'worktree prune' | Select-Object -First 1).LineNumber
$branchIdx = ($order | Select-String -SimpleMatch 'branch -D' | Select-Object -First 1).LineNumber
Assert-True ($pruneIdx -lt $branchIdx) 'Prune must run before branch deletion.'

# ---------------------------------------------------------------------------
# 6. Directory survives git-reported success (residual lock) -> locked, no prune,
#    no branch deletion.
# ---------------------------------------------------------------------------
$survWt = Join-Path ([IO.Path]::GetTempPath()) ("wt-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $survWt -Force | Out-Null
try {
    $fake4 = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 0; output = '' } }
    $r4 = Remove-WorktreeSafely -RepoRoot $repo -WorktreePath $survWt -DeleteBranch `
        -GitInvoker $fake4.invoker `
        -DirectoryRemover { param($p) } `
        -LockerProbe { param($p) @() } `
        -Sleeper { param($ms) }
    Assert-Equal 'locked' $r4.outcome 'Residual directory after git success should be locked.'
    Assert-True (@($fake4.state.calls | Where-Object { $_ -match 'worktree prune' }).Count -eq 0) 'No prune when directory survives.'
    Assert-True (@($fake4.state.calls | Where-Object { $_ -match 'branch -D' }).Count -eq 0) 'No branch deletion when directory survives.'
}
finally { Remove-Item -LiteralPath $survWt -Recurse -Force -ErrorAction SilentlyContinue }

# ---------------------------------------------------------------------------
# 7. Non-lock error (e.g. dirty tree) -> error outcome, no retry, no branch touch.
# ---------------------------------------------------------------------------
$fake5 = New-FakeGit -RemoveResponse { param($n) @{ exitCode = 1; output = "fatal: '$wt' contains modified or untracked files, use --force to delete it" } }
$r5 = Remove-WorktreeSafely -RepoRoot $repo -WorktreePath $wt -DeleteBranch `
    -MaxRetries 4 -BaseDelayMs 1 `
    -GitInvoker $fake5.invoker `
    -DirectoryRemover { param($p) } `
    -LockerProbe { param($p) @() } `
    -Sleeper { param($ms) }
Assert-Equal 'error' $r5.outcome 'Non-lock failure should return error outcome.'
Assert-Equal 1 $r5.attempts 'Non-lock failure should not retry.'
Assert-True (@($fake5.state.calls | Where-Object { $_ -match 'branch -D' }).Count -eq 0) 'Non-lock failure must not delete branch.'

# ---------------------------------------------------------------------------
# 8. Refusing to remove the main working tree.
# ---------------------------------------------------------------------------
$r6 = Remove-WorktreeSafely -RepoRoot $repo -WorktreePath $repo `
    -GitInvoker { param($a) @{ exitCode = 0; output = '' } } `
    -DirectoryRemover { param($p) } -LockerProbe { param($p) @() } -Sleeper { param($ms) }
Assert-Equal 'error' $r6.outcome 'Removing the main working tree should error.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}
Write-Host 'Remove-Worktree tests passed.' -ForegroundColor Green
