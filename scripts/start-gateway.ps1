#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateRange(1, 65535)]
    [int]$Port = 5005,

    [Parameter()]
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repoRoot "src\gateway\BotNexus.Cli\BotNexus.Cli.csproj"

if (-not $SkipBuild) {
    Write-Host "[build] Building via CLI (Release)..."
    dotnet run --project $cliProject --no-launch-profile -- build --path $repoRoot --verbose
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}

dotnet run --project $cliProject --no-launch-profile -- serve gateway --path $repoRoot --port $Port
exit $LASTEXITCODE
