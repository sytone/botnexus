#!/usr/bin/env pwsh
# Install the BotNexus pre-commit validation hook.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hookPath = (& git -C $repoRoot rev-parse --git-path hooks/pre-commit).Trim()
if (-not $hookPath) { throw 'Could not resolve the Git pre-commit hook path.' }
$hookDirectory = Split-Path -Parent $hookPath
New-Item -ItemType Directory -Path $hookDirectory -Force | Out-Null
$hookContent = @'
#!/bin/sh
# BotNexus pre-commit hook — use an exact remote-validation receipt when available,
# otherwise fall back to the local build and Gateway test gate.

pwsh -NoProfile -File scripts/repo/Validate-PreCommit.ps1
exit $?
'@

Write-Host "Installing pre-commit hook to $hookPath..."
Set-Content -Path $hookPath -Value $hookContent -Encoding UTF8 -NoNewline

if ($IsLinux -or $IsMacOS) {
    chmod +x $hookPath
    Write-Host 'Hook installed and made executable.'
} else {
    Write-Host 'Hook installed.'
}

Write-Host ''
Write-Host 'The pre-commit hook will now:'
Write-Host '  1. Accept an Azure validation receipt only when it matches the exact worktree tree and base commit.'
Write-Host '  2. Otherwise build the full solution and run Gateway tests locally.'
Write-Host '  3. Block commits if validation fails.'
