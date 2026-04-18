#!/usr/bin/env pwsh
# Deploy ALL extension build outputs to ~/.botnexus/extensions/ for runtime discovery.
# Extensions are discovered dynamically from the src/extensions/ folder — no hardcoded list.
# Enable/disable individual extensions via your gateway config.

[CmdletBinding()]
param(
    [Parameter()]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$extensionsRoot = Join-Path $repoRoot "src" "extensions"
$dest = Join-Path $env:USERPROFILE ".botnexus" "extensions"

if (-not (Test-Path $extensionsRoot)) {
    Write-Warning "Extensions folder not found: $extensionsRoot"
    return
}

# Discover all extension projects by finding *.csproj files under src/extensions/
$projects = Get-ChildItem -Path $extensionsRoot -Recurse -Filter "*.csproj"
$deployed = 0

foreach ($proj in $projects) {
    $projDir = $proj.DirectoryName
    $projName = $proj.BaseName

    # Only deploy extensions that have a manifest — projects without one
    # (e.g., BlazorClient bundled inside SignalR, stub channels) are not standalone extensions.
    $manifest = Join-Path $projDir "botnexus-extension.json"
    if (-not (Test-Path $manifest)) {
        Write-Host "⏭ Skipped $projName (no manifest)"
        continue
    }
    $extId = (Get-Content $manifest -Raw | ConvertFrom-Json).id

    # Find build output
    $srcDir = Join-Path $projDir "bin" $Configuration
    if (-not (Test-Path $srcDir)) {
        Write-Warning "No build output for $projName — skipping."
        continue
    }

    # Find the TFM folder (net9.0, net10.0, etc.)
    $tfmDirs = Get-ChildItem -Path $srcDir -Directory | Where-Object { $_.Name -match '^net\d' } | Sort-Object Name -Descending
    if ($tfmDirs.Count -eq 0) {
        Write-Warning "No TFM output folder in $srcDir — skipping $projName."
        continue
    }
    $tfmDir = $tfmDirs[0].FullName

    $extDest = Join-Path $dest $extId

    New-Item -ItemType Directory -Path $extDest -Force | Out-Null
    Copy-Item "$tfmDir\*" $extDest -Recurse -Force

    # Copy manifest if present
    $manifest = Join-Path $projDir "botnexus-extension.json"
    if (Test-Path $manifest) {
        Copy-Item $manifest $extDest -Force
    }

    Write-Host "✅ Deployed $extId → $extDest"
    $deployed++
}

Write-Host "[deploy] $deployed extension(s) deployed to $dest"
