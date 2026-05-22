#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$IssueNumber,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$WorktreeName,

    [Parameter()]
    [ValidateSet('feat', 'fix', 'refactor', 'docs', 'test', 'chore', 'ci', 'perf', 'build')]
    [string]$BranchType = 'fix'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$buildScript = Join-Path $repoRoot 'scripts\repo\build.ps1'
$testScript = Join-Path $repoRoot 'scripts\repo\test.ps1'

$worktreeSlug = ($WorktreeName.ToLowerInvariant() -replace '[^a-z0-9\-]+', '-') -replace '-{2,}', '-'
$worktreeSlug = $worktreeSlug.Trim('-')
if ([string]::IsNullOrWhiteSpace($worktreeSlug)) {
    throw 'WorktreeName must contain at least one alphanumeric character.'
}

$branchName = "$BranchType/$IssueNumber-$worktreeSlug"
$parentPath = Split-Path -Parent $repoRoot
$worktreePath = Join-Path $parentPath "botnexus-wt-$worktreeSlug"

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-ScriptCheck {
    param(
        [Parameter(Mandatory)]
        [string]$ScriptPath,

        [Parameter(Mandatory)]
        [string]$StageName,

        [Parameter(Mandatory)]
        [string]$FailureInstruction
    )

    Write-Host ""
    Write-Host "[$StageName] Running: pwsh -NoProfile $ScriptPath"
    & pwsh -NoProfile $ScriptPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Error "[$StageName] Failed with exit code $LASTEXITCODE."
        Write-Host $FailureInstruction
        exit 1
    }
}

Push-Location $repoRoot
try {
    Write-Host "[main] Preparing local main branch..."
    Invoke-Git -Arguments @('checkout', 'main')
    Invoke-Git -Arguments @('clean', '-fdx')
    Invoke-Git -Arguments @('reset', 'HEAD~1', '--hard')
    Invoke-Git -Arguments @('pull')

    $mainFailureInstruction = @'
Create a separate worktree to fix main build/test failures first, commit those fixes, and rerun this script.
Suggested flow:
  git worktree add ../botnexus-wt-main-fix -b fix/main-build-test
  cd ../botnexus-wt-main-fix
  # fix build/test, commit, and push
'@

    Invoke-ScriptCheck -ScriptPath $buildScript -StageName 'main-build' -FailureInstruction $mainFailureInstruction
    Invoke-ScriptCheck -ScriptPath $testScript -StageName 'main-test' -FailureInstruction $mainFailureInstruction

    if (Test-Path $worktreePath) {
        throw "Worktree path already exists: $worktreePath"
    }

    $existingBranch = (& git branch --list $branchName)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to check for existing branch '$branchName'."
    }

    if (-not [string]::IsNullOrWhiteSpace($existingBranch)) {
        throw "Branch already exists locally: $branchName"
    }

    Write-Host ""
    Write-Host "[worktree] Creating worktree at $worktreePath"
    Invoke-Git -Arguments @('worktree', 'add', $worktreePath, '-b', $branchName, 'main')

    $worktreeBuildScript = Join-Path $worktreePath 'scripts\repo\build.ps1'
    $worktreeTestScript = Join-Path $worktreePath 'scripts\repo\test.ps1'

    $worktreeFailureInstruction = @"
Worktree validation failed at: $worktreePath
Fix the issue in this worktree, commit your changes, and rerun:
  pwsh -NoProfile $($MyInvocation.MyCommand.Path) -IssueNumber $IssueNumber -WorktreeName $WorktreeName
"@

    Invoke-ScriptCheck -ScriptPath $worktreeBuildScript -StageName 'worktree-build' -FailureInstruction $worktreeFailureInstruction
    Invoke-ScriptCheck -ScriptPath $worktreeTestScript -StageName 'worktree-test' -FailureInstruction $worktreeFailureInstruction

    Write-Host ""
    Write-Host '✅ Worktree is ready.'
    Write-Host "Issue:      $IssueNumber"
    Write-Host "Branch:     $branchName"
    Write-Host "Worktree:   $worktreePath"
    Write-Host "Next step:  cd $worktreePath"
}
finally {
    Pop-Location
}
