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
# BotNexus pre-commit hook — use an exact strict Azure receipt when available;
# otherwise run authoritative Azure validation. Local fallback is explicit only.

pwsh -NoProfile -File scripts/repo/Validate-PreCommit.Tests.ps1
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
Write-Host '  1. Resolve BOTNEXUS_VALIDATION_MODE from process, user, then machine scope.'
Write-Host '  2. Run the strict, globally serialized local gate by default.'
Write-Host '  3. Preserve exact-content Azure receipts when remote mode is selected.'
