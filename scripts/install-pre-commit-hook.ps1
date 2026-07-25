#!/usr/bin/env pwsh
# Install the BotNexus pre-commit validation hook.
#
# The hook body is NOT duplicated here. It lives in scripts/repo/githooks/pre-commit so it
# is reviewed and versioned like any other code; this script copies that exact file into
# the resolved Git hooks path for clones that do not use core.hooksPath. Duplicating the
# body previously let the two copies drift, so an installed hook could run a different
# gate from the one in the repository (#2331).

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceHook = Join-Path $repoRoot 'scripts/repo/githooks/pre-commit'
if (-not (Test-Path -LiteralPath $sourceHook)) { throw "Versioned hook not found: $sourceHook" }

$hookPath = (& git -C $repoRoot rev-parse --git-path hooks/pre-commit).Trim()
if (-not $hookPath) { throw 'Could not resolve the Git pre-commit hook path.' }
$hookDirectory = Split-Path -Parent $hookPath
New-Item -ItemType Directory -Path $hookDirectory -Force | Out-Null

Write-Host "Installing pre-commit hook to $hookPath..."
# LF endings and no BOM: the hook is executed by sh, which rejects CRLF and a BOM shebang.
$hookContent = (Get-Content -LiteralPath $sourceHook -Raw) -replace "`r`n", "`n"
Set-Content -Path $hookPath -Value $hookContent -Encoding utf8NoBOM -NoNewline

if ($IsLinux -or $IsMacOS) {
    chmod +x $hookPath
    Write-Host 'Hook installed and made executable.'
} else {
    Write-Host 'Hook installed.'
}

Write-Host ''
Write-Host 'The pre-commit hook is an advisory, bounded gate (#2331). It will:'
Write-Host '  1. Reuse an exact-content strict validation receipt when the staged tree matches.'
Write-Host '  2. Otherwise run IMPACTED projects only, under documented per-step timeouts.'
Write-Host '  3. Skip with a clear message when another validation holds the global lock.'
Write-Host '  4. Name the step that exceeded its budget when a timeout occurs.'
Write-Host ''
Write-Host 'Full-solution build, Playwright, and remote Azure validation remain on the'
Write-Host 'authoritative pre-push gate: scripts/repo/Validate-PreCommit.ps1'
