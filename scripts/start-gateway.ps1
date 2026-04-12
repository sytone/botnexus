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
$solution = Join-Path $repoRoot "BotNexus.slnx"
$gatewayDll = Join-Path $repoRoot "src\gateway\BotNexus.Gateway.Api\bin\Release\net10.0\BotNexus.Gateway.Api.dll"
$gatewayUrl = "http://localhost:$Port"
$tcpAddress = [System.Net.IPAddress]::Loopback

function Test-PortAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    $listener = [System.Net.Sockets.TcpListener]::new($tcpAddress, $Port)
    try {
        $listener.Server.ExclusiveAddressUse = $true
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        try { $listener.Stop() } catch { }
    }
}

function Build-Gateway {
    Write-Host "[build] Building solution (Release)..."
    dotnet build $solution -c Release --nologo --tl:off
    if ($LASTEXITCODE -ne 0) {
        throw "Gateway API build failed."
    }
    if (-not (Test-Path $gatewayDll)) {
        throw "Release build output not found at $gatewayDll."
    }
    Deploy-Extensions
}

function Deploy-Extensions {
    Write-Host "[deploy] Deploying extensions..."
    $deployScript = Join-Path $PSScriptRoot "deploy-extensions.ps1"
    if (Test-Path $deployScript) {
        & $deployScript -Configuration Release
    }
    else {
        Write-Host "WARNING: deploy-extensions.ps1 not found - skipping extension deployment." -ForegroundColor Yellow
    }
}

function Wait-ForRestartOrAbort {
    param([int]$Seconds = 5)

    Write-Host ""
    Write-Host "[restart] Gateway will restart in $Seconds seconds. Press 'q' to quit instead." -ForegroundColor Yellow

    for ($i = $Seconds; $i -gt 0; $i--) {
        Write-Host "`r   Restarting in $i... " -NoNewline
        $deadline = [DateTime]::UtcNow.AddSeconds(1)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true)
                if ($key.KeyChar -eq 'q' -or $key.KeyChar -eq 'Q') {
                    Write-Host "`r   Quit requested. Exiting.       "
                    return $false
                }
            }
            Start-Sleep -Milliseconds 50
        }
    }
    Write-Host "`r   Restarting now...       "
    return $true
}

# --- Initial startup ---

if (-not (Test-PortAvailable -Port $Port)) {
    throw "Port $Port is already in use. Stop the existing process or choose a different port (for example: -Port 5007)."
}

if (-not $SkipBuild) {
    Build-Gateway
}
elseif (-not (Test-Path $gatewayDll)) {
    throw "Release build output not found at $gatewayDll. Run without -SkipBuild first."
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $gatewayUrl

# --- Ctrl+C handler: kill child process, keep script alive ---

$script:gwProc = $null

# Use a C# handler for CancelKeyPress because PowerShell script blocks
# crash on the native signal thread (no Runspace available).
if (-not ([System.Management.Automation.PSTypeName]'CtrlCHandler').Type) {
    Add-Type -TypeDefinition @"
using System;
public static class CtrlCHandler {
    public static volatile bool Pressed;
    public static void OnCancel(object sender, ConsoleCancelEventArgs e) {
        e.Cancel = true;   // keep PowerShell alive
        Pressed = true;    // signal the run loop
        // The child shares the console and already received Ctrl+C
        // and will begin graceful shutdown on its own.
    }
}
"@
}

$cancelHandler = [System.ConsoleCancelEventHandler]{
    param($sender, $e)
    [CtrlCHandler]::OnCancel($sender, $e)
}
[Console]::add_CancelKeyPress($cancelHandler)

function Stop-GatewayGracefully {
    param([int]$TimeoutSeconds = 10)
    $p = $script:gwProc
    if (-not $p -or $p.HasExited) { return }
    # Wait for the process to finish its graceful shutdown
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Host "WARNING: Gateway did not exit within $TimeoutSeconds seconds - force-killing." -ForegroundColor Red
        try { $p.Kill($true) } catch { }
    }
}

function Start-Gateway {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.Arguments = "`"$gatewayDll`""
    $psi.UseShellExecute = $false
    $psi.Environment["ASPNETCORE_ENVIRONMENT"] = $env:ASPNETCORE_ENVIRONMENT
    $psi.Environment["ASPNETCORE_URLS"] = $env:ASPNETCORE_URLS
    [CtrlCHandler]::Pressed = $false
    $script:gwProc = [System.Diagnostics.Process]::Start($psi)
    $script:gwProc.WaitForExit()
    return $script:gwProc.ExitCode
}

# --- Run loop ---

try {
    while ($true) {
        Write-Host ""
        Write-Host "[start] Starting Gateway API"
        Write-Host "   URL:         $gatewayUrl"
        Write-Host "   Environment: $($env:ASPNETCORE_ENVIRONMENT)"
        Write-Host ""
        Write-Host "Press Ctrl+C to stop the gateway."

        $exitCode = Start-Gateway

        if ([CtrlCHandler]::Pressed) {
            Stop-GatewayGracefully -TimeoutSeconds 10
        }

        Write-Host ""
        Write-Host "[stop] Gateway process exited (code $exitCode)." -ForegroundColor Cyan

        if (-not (Wait-ForRestartOrAbort -Seconds 5)) {
            break
        }

        # Rebuild before restarting
        try {
            Build-Gateway
        }
        catch {
            Write-Host "ERROR: Build failed: $($_.Exception.Message)" -ForegroundColor Red
            if (-not (Wait-ForRestartOrAbort -Seconds 5)) {
                break
            }
        }
    }
}
finally {
    [Console]::remove_CancelKeyPress($cancelHandler)
    Stop-GatewayGracefully -TimeoutSeconds 10
}
