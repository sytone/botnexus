#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ProbeArgs
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$probeProject = Join-Path $repoRoot "tools\BotNexus.Probe\src\BotNexus.Probe\BotNexus.Probe.csproj"

if (-not (Test-Path $probeProject)) {
    throw "Probe project not found at $probeProject. Are you in the BotNexus repo root?"
}

dotnet run --project $probeProject -- @ProbeArgs
exit $LASTEXITCODE
