#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateRange(1, 65535)]
    [int]$Port = 5005
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$gatewayProject = Join-Path $repoRoot "src\gateway\BotNexus.Gateway.Api\BotNexus.Gateway.Api.csproj"
$gatewayUrl = "http://localhost:$Port"
$webUiUrl = "$gatewayUrl/webui"

Write-Host "🔧 Building Gateway API project..."
dotnet build $gatewayProject --nologo --tl:off
if ($LASTEXITCODE -ne 0) {
    throw "Gateway API build failed."
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $gatewayUrl

Write-Host ""
Write-Host "🚀 Starting Gateway API"
Write-Host "   URL:        $gatewayUrl"
Write-Host "   WebUI:      $webUiUrl"
Write-Host "   Environment: $($env:ASPNETCORE_ENVIRONMENT)"
Write-Host ""
Write-Host "Press Ctrl+C to stop."

dotnet run --project $gatewayProject --no-build
