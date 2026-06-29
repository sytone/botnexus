<#
.SYNOPSIS
    Activates the versioned BotNexus git hooks (#1602) by pointing core.hooksPath at
    scripts/repo/githooks. Run once per clone; survives worktree churn (config-level).

.DESCRIPTION
    The default .git/hooks pre-commit only built + tested. The versioned hooks add the
    #1602 core.bare guard at commit AND push time, and live in-tree so they're reviewed,
    versioned, and shared across every worktree. Setting core.hooksPath also covers all
    existing worktrees (they share .git/config).

.EXAMPLE
    pwsh -NoProfile -File scripts/repo/install-hooks.ps1
#>
[CmdletBinding()]
param([string]$RepoPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RepoPath) { $RepoPath = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent }
$hooks = Join-Path $RepoPath 'scripts/repo/githooks'
& git -C $RepoPath config core.hooksPath 'scripts/repo/githooks'
if ($IsLinux -or $IsMacOS) { foreach ($h in 'pre-commit', 'pre-push') { chmod +x (Join-Path $hooks $h) } }
Write-Host "[install-hooks] core.hooksPath -> scripts/repo/githooks (pre-commit + pre-push #1602 guard active)" -ForegroundColor Green