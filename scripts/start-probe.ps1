#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Launches BotNexus Probe — the standalone diagnostic tool.

.DESCRIPTION
    Builds and runs BotNexus.Probe, which provides a web UI for browsing logs,
    sessions, OTEL traces, and live Gateway activity.

.PARAMETER Port
    Port for the Probe web UI (default: 5050).

.PARAMETER GatewayUrl
    URL of a running BotNexus Gateway to connect to (e.g., http://localhost:5005).
    If omitted, Gateway integration is disabled.

.PARAMETER LogsPath
    Directory containing Serilog rolling log files.
    Default: ~/.botnexus/logs

.PARAMETER SessionsPath
    Directory containing session JSONL files.
    Default: ~/.botnexus/sessions

.PARAMETER OtlpPort
    Port for the optional OTLP HTTP trace receiver. If omitted, OTLP is disabled.

.PARAMETER SkipBuild
    Skip the build step and run the last successful build.

.EXAMPLE
    .\start-probe.ps1
    # Launches Probe on http://localhost:5050 with default log/session paths

.EXAMPLE
    .\start-probe.ps1 -GatewayUrl http://localhost:5005
    # Launches Probe connected to a running Gateway

.EXAMPLE
    .\start-probe.ps1 -GatewayUrl http://localhost:5005 -OtlpPort 4318 -Port 5050
    # Full setup: Probe + Gateway connection + OTLP trace receiver
#>

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateRange(1, 65535)]
    [int]$Port = 5050,

    [Parameter()]
    [string]$GatewayUrl = "http://localhost:5005",

    [Parameter()]
    [string]$LogsPath,

    [Parameter()]
    [string]$SessionsPath,

    [Parameter()]
    [ValidateRange(1, 65535)]
    [int]$OtlpPort,

    [Parameter()]
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$probeSln = Join-Path $repoRoot "tools\BotNexus.Probe\BotNexus.Probe.sln"
$probeProject = Join-Path $repoRoot "tools\BotNexus.Probe\src\BotNexus.Probe\BotNexus.Probe.csproj"
$probeUrl = "http://localhost:$Port"

if (-not (Test-Path $probeSln)) {
    throw "Probe solution not found at $probeSln. Are you in the BotNexus repo root?"
}

function Test-PortAvailable {
    param([Parameter(Mandatory)][int]$Port)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Server.ExclusiveAddressUse = $true
        $listener.Start()
        return $true
    }
    catch { return $false }
    finally { try { $listener.Stop() } catch { } }
}

if (-not (Test-PortAvailable -Port $Port)) {
    throw "Port $Port is already in use. Choose a different port (e.g., -Port 5051)."
}

if ($PSBoundParameters.ContainsKey('OtlpPort') -and -not (Test-PortAvailable -Port $OtlpPort)) {
    throw "OTLP port $OtlpPort is already in use. Choose a different port."
}

# --- Build ---

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "[build] Building BotNexus.Probe..." -ForegroundColor Cyan
    dotnet build $probeSln --nologo --tl:off
    if ($LASTEXITCODE -ne 0) {
        throw "Probe build failed."
    }
    Write-Host "[build] Build succeeded." -ForegroundColor Green
}

# --- Assemble arguments ---

$runArgs = @("run", "--project", $probeProject, "--no-build", "--no-launch-profile", "--")
$runArgs += "--port", $Port

if ($GatewayUrl) {
    $runArgs += "--gateway", $GatewayUrl
}

if ($LogsPath) {
    $runArgs += "--logs", $LogsPath
}

if ($SessionsPath) {
    $runArgs += "--sessions", $SessionsPath
}

if ($PSBoundParameters.ContainsKey('OtlpPort')) {
    $runArgs += "--otlp-port", $OtlpPort
}

# --- Launch ---

Write-Host ""
Write-Host "[start] Starting BotNexus Probe" -ForegroundColor Cyan
Write-Host "   UI:       $probeUrl"
Write-Host "   Gateway:  $(if ($GatewayUrl) { $GatewayUrl } else { 'disabled (use -GatewayUrl to connect)' })"
Write-Host "   OTLP:     $(if ($PSBoundParameters.ContainsKey('OtlpPort')) { "http://localhost:$OtlpPort/v1/traces" } else { 'disabled (use -OtlpPort to enable)' })"
Write-Host ""
Write-Host "Press Ctrl+C to stop."
Write-Host ""

dotnet @runArgs
