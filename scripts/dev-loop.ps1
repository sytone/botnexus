#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateRange(1, 65535)]
    [int]$Port = 5005,

    [Parameter()]
    [switch]$Watch,

    [Parameter()]
    [switch]$SkipBuild,

    [Parameter()]
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot "BotNexus.slnx"
$cliProject = Join-Path $repoRoot "src\gateway\BotNexus.Cli\BotNexus.Cli.csproj"
$gatewayTestsPath = Join-Path $repoRoot "tests\BotNexus.Gateway.Tests"

Push-Location $repoRoot
try {
    if ($SkipBuild) {
        Write-Host "⏭️  Skipping build (-SkipBuild)."
    }
    else {
        Write-Host "🔧 Building solution via CLI (Release)..."
        dotnet run --project $cliProject --no-launch-profile -- build --path $repoRoot --verbose
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed."
        }
    }

    if ($SkipTests) {
        Write-Host ""
        Write-Host "⏭️  Skipping Gateway tests (-SkipTests)."
    }
    else {
        Write-Host ""
        Write-Host "🧪 Running Gateway tests..."
        dotnet test $gatewayTestsPath --nologo --tl:off
        if ($LASTEXITCODE -ne 0) {
            throw "Gateway tests failed. Not starting Gateway."
        }
    }

    Write-Host ""

    if ($Watch) {
        $gatewayProject = Join-Path $repoRoot "src\gateway\BotNexus.Gateway.Api\BotNexus.Gateway.Api.csproj"
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $env:ASPNETCORE_URLS = "http://localhost:$Port"

        Write-Host "👀 Starting Gateway in watch mode at http://localhost:$Port"
        dotnet watch --project $gatewayProject run --no-launch-profile
    }
    else {
        Write-Host "🚀 Starting Gateway via CLI at http://localhost:$Port"
        dotnet run --project $cliProject --no-launch-profile -- serve gateway --path $repoRoot --port $Port
    }
}
catch {
    Write-Error "❌ Dev loop failed: $($_.Exception.Message)"
    exit 1
}
finally {
    Pop-Location
}

if (-not $Watch) {
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "✅ Dev loop completed."
    }
}
