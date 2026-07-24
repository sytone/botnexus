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

    # Invalidate any prior receipt before producing new evidence. A failed or interrupted
    # run must never leave a stale-but-matching receipt behind (issue #2143 fail-closed).
    try {
        Import-Module (Join-Path $PSScriptRoot 'ValidationReceipt.psm1') -Force
        Remove-BotNexusValidationReceipt -WorktreePath $repoRoot
    }
    catch { Write-Warning "Could not clear prior validation receipt: $($_.Exception.Message)" }

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
    $validationExit = $LASTEXITCODE

    # Emit a content-addressed validation receipt only after every required command has
    # succeeded (issue #2143). The exact-content rule means this certifies precisely the
    # staged tree; the pre-commit hook reuses it to skip redundant work. A failed run
    # falls through without emitting, so the hook fails closed.
    if ($validationExit -eq 0) {
        try {
            Import-Module (Join-Path $PSScriptRoot 'ValidationReceipt.psm1') -Force
            $emitted = New-BotNexusValidationReceipt -Scope $Mode -TestProjects @('impacted+safety-nets') -WorktreePath $repoRoot -BaseRef $BaseRef
            if ($null -ne $emitted) {
                Write-Host "Validation receipt written: $($emitted.Path)" -ForegroundColor DarkGray
            }
        }
        catch {
            # Receipt emission is a best-effort optimization; never fail a passing run because
            # of it. Absence of a receipt simply means the next commit revalidates.
            Write-Warning "Could not write validation receipt: $($_.Exception.Message)"
        }
    }
    exit $validationExit
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}
