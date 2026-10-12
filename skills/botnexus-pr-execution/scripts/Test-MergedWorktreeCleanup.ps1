[CmdletBinding()]
param([string]$ScriptDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
# Standalone boundary tests: real production scripts, fake Git/gh, and a removal
# helper that records authorization only. No gateway, credentials, or deletion.
$fixture = Join-Path $PSScriptRoot ('.cleanup-test-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $fixture 'botnexus'
$root = Join-Path $fixture 'botnexus-wt'
$target = Join-Path $root 'target with spaces'
$other = Join-Path $root 'unreadable'
$second = Join-Path $root 'second'
$outside = Join-Path $fixture 'outside'
$authDirectory = Join-Path $fixture 'auth'
$oldSkillDirectory = $env:BOTNEXUS_SKILL_DIRECTORY
$global:cleanupTestPaths = @{repo=$repo;other=$other;target=$target;second=$second;outside=$outside}
$passed = 0; $failed = 0
function Assert-That($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function global:git {
  $repo = $global:cleanupTestPaths.repo; $other = $global:cleanupTestPaths.other
  $target = $global:cleanupTestPaths.target; $second = $global:cleanupTestPaths.second; $outside = $global:cleanupTestPaths.outside
  $arguments = @($args)
  $global:cleanupTestcalls.Add(($arguments -join ' '))
  $global:LASTEXITCODE = 0
  $path = [IO.Path]::GetFullPath($arguments[1]); $command = $arguments[2]
  if ($command -eq 'worktree') {
    if ($global:cleanupTestcase -eq 'list-failure') { $global:LASTEXITCODE = 128; return }
    $records = @(@{path=$repo;branch='main';head='base'}, @{path=$other;branch='fix/unrelated';head='unrelated'}, @{path=$target;branch='fix/4855';head='pr-head'})
    if ($global:cleanupTestcase -eq 'registered-wrong-branch') { $records[2].branch = 'wrong' }
    if ($global:cleanupTestcase -eq 'registered-wrong-head') { $records[2].head = 'wrong' }
    if ($global:cleanupTestcase -eq 'ambiguity') { $records += @{path=$second;branch='fix/4855';head='pr-head'} }
    if ($global:cleanupTestcase -eq 'outside') { $records += @{path=$outside;branch='fix/4855';head='pr-head'} }
    if ($global:cleanupTestcase -eq 'unregistered') { $records = @($records | Where-Object path -ne $target) }
    foreach ($record in $records) { "worktree $($record.path.Replace('\','/'))"; "HEAD $($record.head)"; "branch refs/heads/$($record.branch)"; '' }
    return
  }
  if ($path -eq $other) { $global:LASTEXITCODE = 128; throw 'Unrelated worktree was inspected.' }
  if ($command -eq 'rev-parse') {
    $kind = if ($arguments -contains '--abbrev-ref') { 'branch' } else { 'head' }
    if ($global:cleanupTestcase -eq "failed-$kind") { $global:LASTEXITCODE = 128; return }
    if ($global:cleanupTestcase -eq "failed-$kind-output") { $global:LASTEXITCODE = 128; if ($kind -eq 'branch') { 'fix/4855' } else { 'pr-head' }; return }
    if ($global:cleanupTestcase -eq "empty-$kind") { return }
    if ($global:cleanupTestcase -eq "blank-$kind") { ' '; return }
    if ($global:cleanupTestcase -eq "multiple-$kind") { 'one'; 'two'; return }
    if ($global:cleanupTestcase -eq "wrong-$kind") { 'wrong'; return }
    if ($kind -eq 'branch') { 'fix/4855' } else { 'pr-head' }; return
  }
  if ($command -eq 'status') {
    if ($global:cleanupTestcase -eq 'failed-status') { $global:LASTEXITCODE = 128; return }
    if ($global:cleanupTestcase -eq 'failed-status-output') { $global:LASTEXITCODE = 128; ' M file'; return }
    if ($global:cleanupTestcase -eq 'dirty') { '?? file' }
    if ($global:cleanupTestcase -eq 'blank-status') { '' }
    return
  }
  if ($command -eq 'fetch') { if ($global:cleanupTestcase -eq 'fetch-failure') { $global:LASTEXITCODE = 128 }; return }
  if ($command -eq 'merge-base') {
    if ($global:cleanupTestcase -eq 'not-ancestor') { $global:LASTEXITCODE = 1 }
    if ($global:cleanupTestcase -eq 'ancestry-error') { $global:LASTEXITCODE = 128 }; return
  }
  throw "Unexpected Git command: $arguments"
}
function global:gh {
  $global:LASTEXITCODE = 0
  if ($args[0] -eq 'issue') { '{"number":4855,"state":"OPEN"}'; return }
  if ($global:cleanupTestcase -eq 'pr-failure') { $global:LASTEXITCODE = 1; return }
  $pr = @{number=4856;state='MERGED';mergedAt='2026-08-01';mergeCommit=@{oid='merge-head'};headRefName='fix/4855';headRefOid='pr-head';baseRefName='main';body='Refs #4855';url='https://example.invalid/pr/4856'}
  if ($global:cleanupTestcase -eq 'nonmerged') { $pr.state = 'OPEN' }
  if ($global:cleanupTestcase -eq 'missing-merge') { $pr.mergeCommit.oid = '' }
  if ($global:cleanupTestcase -eq 'merge-read-failure' -and $args[-1] -eq 'mergeCommit') { $global:LASTEXITCODE = 1; return }
  if ($global:cleanupTestcase -eq 'merge-read-failure') { $pr.mergeCommit.oid = '' }
  if ($global:cleanupTestcase -eq 'missing-pr-head') { $pr.headRefOid = '' }
  $pr | ConvertTo-Json -Compress
}
try {
  foreach ($directory in @($target,$other,$second,$outside,(Join-Path $repo 'scripts/repo'),$authDirectory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
  Set-Content -LiteralPath (Join-Path $repo 'scripts/repo/Remove-Worktree.ps1') -Value 'function Remove-WorktreeSafely { param($RepoRoot,$WorktreePath,[switch]$DeleteBranch) $global:cleanupTestremovals.Add($WorktreePath); [pscustomobject]@{outcome=$(if($global:cleanupTestcase -eq "locked") { "locked" } else { "removed" });path=$WorktreePath} }'
  Set-Content -LiteralPath (Join-Path $authDirectory 'Use-FarnsworthBot.ps1') -Value 'param([switch]$Scrub) $global:cleanupTestauthCalls.Add([bool]$Scrub)'
  $env:BOTNEXUS_SKILL_DIRECTORY = $authDirectory
  $cases = @(
    @{name='unreadable-unrelated';success=$true;explicit=$false},
    @{name='explicit';success=$true;explicit=$true},
    @{name='ambiguity';success=$false;explicit=$false},
    @{name='explicit-ambiguity';success=$true;explicit=$true;scenario='ambiguity'}
  )
  foreach ($name in @('registered-wrong-branch','registered-wrong-head','pr-failure','wrong-branch','wrong-head','failed-branch','failed-head','failed-branch-output','failed-head-output','empty-branch','empty-head','blank-branch','blank-head','multiple-branch','multiple-head','failed-status','failed-status-output','blank-status','dirty','nonmerged','missing-merge','merge-read-failure','missing-pr-head','fetch-failure','not-ancestor','ancestry-error','list-failure','unregistered','outside','locked')) { $cases += @{name=$name;success=$false;explicit=$true} }
  # Completion must run its existing gates, authenticate/scrub using the fallback,
  # and pass the explicit verified worktree rather than rediscovering it.
  foreach ($name in @('explicit','failed-branch','failed-head','failed-status','empty-branch','multiple-head','dirty','missing-merge','fetch-failure','ancestry-error')) { $cases += @{name="completion-$name";scenario=$name;success=($name -eq 'explicit');explicit=$true;completion=$true} }
  foreach ($test in $cases) {
    $global:cleanupTestcase = if ($test.scenario) { $test.scenario } else { $test.name }
    $global:cleanupTestcalls = [Collections.Generic.List[string]]::new()
    $global:cleanupTestremovals = [Collections.Generic.List[string]]::new()
    $global:cleanupTestauthCalls = [Collections.Generic.List[bool]]::new()
    $errorRecord = $null
    try {
      $parameters = @{Issue=4855;PullRequestNumber=4856;RepoRoot=$repo}
      if ($test.completion) {
        $parameters.Worktree = $target
        & (Join-Path $ScriptDirectory 'Complete-BotNexusPullRequest.ps1') @parameters | Out-Null
      } else {
        if ($test.explicit) { $parameters.WorktreePath = if ($global:cleanupTestcase -eq 'outside') { $outside } else { $target } }
        & (Join-Path $ScriptDirectory 'Remove-BotNexusIssueWorktree.ps1') @parameters | Out-Null
      }
    } catch { $errorRecord = $_ }
    try {
      if ($test.success) {
        Assert-That ($null -eq $errorRecord) "Unexpected refusal: $errorRecord"
        Assert-That ($global:cleanupTestremovals.Count -eq 1) 'Expected exactly one authorized removal.'
        Assert-That ([IO.Path]::GetFullPath($global:cleanupTestremovals[0]) -eq [IO.Path]::GetFullPath($target)) 'Wrong removal target.'
        Assert-That (@($global:cleanupTestcalls | Where-Object { $_.Replace('\','/') -like "-C $($other.Replace('\','/')) *" -or $_.Replace('\','/') -like "-C $($second.Replace('\','/')) *" }).Count -eq 0) 'Inspected unrelated worktree.'
        if ($test.completion) { Assert-That ($global:cleanupTestauthCalls.Count -eq 2 -and -not $global:cleanupTestauthCalls[0] -and $global:cleanupTestauthCalls[1]) 'Authentication/scrub fallback was not preserved.' }
      } else {
        Assert-That ($null -ne $errorRecord) 'Expected fail-closed refusal.'
        Assert-That ($global:cleanupTestremovals.Count -eq $(if ($test.name -eq 'locked') { 1 } else { 0 })) 'Unauthorized removal.'
        # Binding/startup failures cannot masquerade as passing safety tests.
        Assert-That ($errorRecord.Exception.Message -notmatch 'parameter.*WorktreePath|not recognized|cannot find path') "Harness/script did not start: $errorRecord"
      }
      Assert-That ((Test-Path $target) -and (Test-Path $other)) 'Fixture directory was deleted.'
      $passed++; Write-Host "PASS $($test.name)"
    } catch { $failed++; Write-Host "FAIL $($test.name): $_" }
  }
} finally {
  $env:BOTNEXUS_SKILL_DIRECTORY = $oldSkillDirectory
  Remove-Item Function:git,Function:gh,Function:Remove-WorktreeSafely -ErrorAction SilentlyContinue
  Remove-Variable -Scope Global -Name cleanupTestPaths,cleanupTestcase,cleanupTestcalls,cleanupTestremovals,cleanupTestauthCalls -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $fixture -Recurse -Force
}
Write-Host "RESULT passed=$passed failed=$failed"
[pscustomobject]@{total=($passed+$failed);passed=$passed;failed=$failed} | ConvertTo-Json -Compress
if ($failed -gt 0) { exit 1 }
