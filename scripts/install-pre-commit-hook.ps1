#!/usr/bin/env pwsh
# Install pre-commit hook for BotNexus
# Created by: Leela (2026-04-03)
# Purpose: Set up build validation gate for commits

$ErrorActionPreference = "Stop"

$hookPath = Join-Path $PSScriptRoot ".git" "hooks" "pre-commit"
$hookContent = @'
#!/bin/sh
# BotNexus pre-commit hook — Prevent broken commits
# Created by: Leela (2026-04-03)
# Purpose: Build validation gate for all commits

echo "🔧 Pre-commit: Building solution..."

# Build the solution with minimal verbosity
dotnet build BotNexus.slnx --nologo --verbosity minimal --tl:off

if [ $? -ne 0 ]; then
    echo "❌ Build failed. Fix errors before committing."
    echo "   Run: dotnet build BotNexus.slnx --nologo --tl:off"
    exit 1
fi

echo "✅ Build succeeded."

echo "🧪 Pre-commit: Running unit tests..."

# Run unit tests only (not E2E or integration — those are expensive)
dotnet test tests/BotNexus.Tests.Unit/BotNexus.Tests.Unit.csproj --nologo --verbosity minimal --tl:off --no-build

if [ $? -ne 0 ]; then
    echo "❌ Tests failed. Fix failing tests before committing."
    echo "   Run: dotnet test BotNexus.slnx --nologo --tl:off"
    exit 1
fi

echo "✅ Tests passed."
echo "✅ Commit validation complete."
exit 0
'@

Write-Host "Installing pre-commit hook to $hookPath..."
Set-Content -Path $hookPath -Value $hookContent -Encoding UTF8 -NoNewline

# On Windows, Git uses bash emulation, so the shebang line works
# No chmod needed on Windows
if ($IsLinux -or $IsMacOS) {
    chmod +x $hookPath
    Write-Host "✅ Hook installed and made executable"
} else {
    Write-Host "✅ Hook installed"
}

Write-Host ""
Write-Host "The pre-commit hook will now:"
Write-Host "  1. Build the full solution (BotNexus.slnx)"
Write-Host "  2. Run unit tests"
Write-Host "  3. Block commits if build or tests fail"
Write-Host ""
Write-Host "To bypass the hook (docs-only commits): git commit --no-verify"
