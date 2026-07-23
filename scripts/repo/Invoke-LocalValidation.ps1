<#
.SYNOPSIS
    Runs the strict, globally serialized local repository validation gate.
#>
[CmdletBinding()]
param(
    [string]$WorktreePath = (Get-Location).Path,
    [string]$BaseRef = 'origin/main',
    [ValidateSet('strict', 'impacted', 'full', 'playwright')]
    [string]$Mode = 'strict'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (& git -C $WorktreePath rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "WorktreePath is not inside a git repository: $WorktreePath"
}

# Serialize all BotNexus local validation, not merely validation in one worktree. Separate
# worktrees still compete for the same host CPU, Defender, package cache, and tool processes.
$lockName = 'botnexus-local-validation-global'
$lockPath = Join-Path ([IO.Path]::GetTempPath()) "$lockName.lock"
$lock = $null
try {
    try {
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Another BotNexus local validation is already running. Wait for it to finish: $lockPath"
    }

    Write-Host "Running globally serialized local validation ($Mode)." -ForegroundColor Yellow
    & dotnet build (Join-Path $repoRoot 'BotNexus.slnx') --nologo --verbosity minimal --tl:off
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    switch ($Mode) {
        'full' {
            & (Join-Path $PSScriptRoot 'test-impacted.ps1') -All -NoBuild
        }
        'playwright' {
            & dotnet test (Join-Path $repoRoot 'tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj') --nologo --tl:off -c Debug --no-build
        }
        default {
            & (Join-Path $PSScriptRoot 'test-impacted.ps1') -From $BaseRef -NoBuild
            if ($LASTEXITCODE -eq 0 -and $Mode -eq 'strict') {
                & dotnet test (Join-Path $repoRoot 'tests/integration/BotNexus.Integration.E2E.Tests/BotNexus.Integration.E2E.Tests.csproj') --nologo --tl:off -c Debug --no-build
            }
        }
    }
    exit $LASTEXITCODE
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}
