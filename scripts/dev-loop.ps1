<#
.SYNOPSIS
    Full dev-loop: build, pack, stop gateway, install CLI, install packages, restart gateway.

.DESCRIPTION
    Combines pack.ps1, install-cli.ps1, and update.ps1 into a single script so the
    inner dev loop is just:  .\scripts\dev-loop.ps1

.PARAMETER InstallPath
    Where BotNexus packages are installed. Defaults to %LOCALAPPDATA%\BotNexus.

#>
[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "BotNexus")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$commonScript = Join-Path $PSScriptRoot "common.ps1"
. $commonScript

$packageVersion = Resolve-Version
$artifactsRoot = Join-Path $repoRoot "artifacts"
$resolvedInstallPath = [System.IO.Path]::GetFullPath($InstallPath)

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " BotNexus Dev Loop  (v$packageVersion)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ── Step 1: Stop the gateway (before build so files aren't locked) ─────────

Write-Host "[1/5] Stopping gateway..." -ForegroundColor Yellow
$gatewayProcesses = @(
    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine -match "BotNexus\.Gateway" }
)

if ($gatewayProcesses.Count -gt 0) {
    foreach ($process in $gatewayProcesses) {
        Stop-Process -Id $process.ProcessId -Force
    }
    Write-Host "  Stopped PID(s): $($gatewayProcesses.ProcessId -join ', ')" -ForegroundColor DarkGray
}
else {
    Write-Host "  Not running" -ForegroundColor DarkGray
}

# ── Step 2: Pack all components ────────────────────────────────────────────

Write-Host "[2/5] Packing all components..." -ForegroundColor Yellow

& (Join-Path $PSScriptRoot "pack.ps1")
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    throw "pack.ps1 failed"
}

# ── Step 3: Install CLI tool ──────────────────────────────────────────────

Write-Host "[3/5] Installing CLI tool..." -ForegroundColor Yellow

$toolsPath = Join-Path $artifactsRoot "tools"
dotnet pack (Join-Path $repoRoot "src\BotNexus.Cli") -c Release -o $toolsPath `
    /p:Version=$packageVersion /p:InformationalVersion=$packageVersion --nologo --verbosity minimal --tl:off
if ($LASTEXITCODE -ne 0) { throw "dotnet pack (CLI) failed" }

$resolvedToolsPath = [System.IO.Path]::GetFullPath($toolsPath)
$isInstalled = dotnet tool list --global | Select-String -Pattern 'botnexus\.cli'

if ($null -ne $isInstalled) {
    dotnet tool uninstall --global BotNexus.Cli | Out-Null
}

dotnet tool install --global --add-source $resolvedToolsPath BotNexus.Cli --version $packageVersion
if ($LASTEXITCODE -ne 0) { throw "dotnet tool install failed" }

# ── Step 4: Install packages ──────────────────────────────────────────────

Write-Host "[4/5] Installing packages to $resolvedInstallPath..." -ForegroundColor Yellow

& (Join-Path $PSScriptRoot "install.ps1") -InstallPath $resolvedInstallPath -PackagesPath $artifactsRoot

# ── Step 5: Restart gateway ───────────────────────────────────────────────

Write-Host "[5/5] Starting gateway..." -ForegroundColor Yellow
$gatewayDir = Join-Path $resolvedInstallPath "gateway"
$gatewayDll = Join-Path $gatewayDir "BotNexus.Gateway.dll"
if (-not (Test-Path -LiteralPath $gatewayDll)) {
    throw "Gateway DLL not found: $gatewayDll"
}

$started = Start-Process -FilePath "dotnet" -ArgumentList "`"$gatewayDll`"" `
    -WorkingDirectory $gatewayDir -PassThru
$restartPid = $started.Id
Write-Host "  Gateway started (PID $restartPid)" -ForegroundColor DarkGray

# ── Done ──────────────────────────────────────────────────────────────────

$stopwatch.Stop()
$elapsed = $stopwatch.Elapsed.ToString("mm\:ss")

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " ✅  Dev loop complete  ($elapsed)" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Version:     $packageVersion"
Write-Host "  Install:     $resolvedInstallPath"
Write-Host "  Gateway PID: $restartPid"
Write-Host ""
