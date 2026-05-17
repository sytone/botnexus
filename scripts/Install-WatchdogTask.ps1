#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Registers the BotNexus Watchdog as a recurring background task.

.DESCRIPTION
    On Windows, creates a Scheduled Task named "BotNexusWatchdog" that runs
    botnexus-watchdog.ps1 every minute using Task Scheduler. Run once from an
    elevated (Administrator) PowerShell prompt.

    On Linux/macOS, installs a crontab entry that runs botnexus-watchdog.ps1
    every minute via pwsh. No elevation required.

.EXAMPLE
    # Install with defaults
    .\Install-WatchdogTask.ps1

    # Install with a custom repo path
    .\Install-WatchdogTask.ps1 -RepoPath "~/projects/botnexus"

    # Uninstall
    .\Install-WatchdogTask.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [string]$RepoPath              = '',
    [int]   $GitCheckIntervalMinutes  = 5,
    [int]   $CliCheckIntervalMinutes  = 60,
    [int]   $MaxFailures              = 3,
    [string]$GatewayUrl               = 'http://localhost:5005',
    [switch]$Uninstall
)

$TaskName    = 'BotNexusWatchdog'
$CronMarker  = '# BotNexusWatchdog'
$IsWindows   = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)

# ── Locate dependencies ─────────────────────────────────────────────────────

$pwshPath = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwshPath) {
    Write-Error "pwsh not found on PATH. Install PowerShell 7+: https://github.com/PowerShell/PowerShell/releases"
    exit 1
}

$scriptDir      = $PSScriptRoot
$watchdogScript = Join-Path $scriptDir 'botnexus-watchdog.ps1'
if (-not (Test-Path $watchdogScript)) {
    Write-Error "Watchdog script not found at: $watchdogScript"
    exit 1
}

# ── Uninstall ────────────────────────────────────────────────────────────────

if ($Uninstall) {
    if ($IsWindows) {
        Write-Host "Removing scheduled task '$TaskName'..."
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    } else {
        Write-Host "Removing crontab entry for '$TaskName'..."
        $existing = & crontab -l 2>/dev/null
        $filtered = $existing | Where-Object { $_ -notmatch [regex]::Escape($CronMarker) }
        $filtered | & crontab -
    }
    Write-Host 'Done.'
    exit 0
}

# ── Build argument string ───────────────────────────────────────────────────

$argParts = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', "`"$watchdogScript`""
)

if ($RepoPath) {
    $argParts += '-RepoPath', "`"$RepoPath`""
}
$argParts += '-GitCheckIntervalMinutes', $GitCheckIntervalMinutes
$argParts += '-CliCheckIntervalMinutes', $CliCheckIntervalMinutes
$argParts += '-MaxFailures', $MaxFailures
$argParts += '-GatewayUrl', "`"$GatewayUrl`""

$arguments = $argParts -join ' '

# ── Install ──────────────────────────────────────────────────────────────────

if ($IsWindows) {
    # Windows: Task Scheduler
    Write-Host "Creating scheduled task '$TaskName'..."
    Write-Host "  pwsh:      $pwshPath"
    Write-Host "  script:    $watchdogScript"
    Write-Host "  arguments: $arguments"
    Write-Host ''

    $action  = New-ScheduledTaskAction -Execute $pwshPath -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration ([TimeSpan]::MaxValue)

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
        -MultipleInstances IgnoreNew

    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType S4U -RunLevel Highest

    # Remove existing task if present before re-creating
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description 'BotNexus Watchdog — gateway health, git sync, and CLI auto-update'

    Write-Host ''
    Write-Host "Scheduled task '$TaskName' created successfully." -ForegroundColor Green
    Write-Host ''
    Write-Host 'To verify:  Get-ScheduledTask -TaskName BotNexusWatchdog'
    Write-Host 'To run now: Start-ScheduledTask -TaskName BotNexusWatchdog'
    Write-Host "To remove:  ./Install-WatchdogTask.ps1 -Uninstall"
    Write-Host "Logs:       $env:USERPROFILE\.botnexus\logs\watchdog-*.log"

} else {
    # Linux/macOS: crontab
    Write-Host "Installing crontab entry for '$TaskName'..."
    Write-Host "  pwsh:      $pwshPath"
    Write-Host "  script:    $watchdogScript"
    Write-Host "  arguments: $arguments"
    Write-Host ''

    $cronLine = "* * * * * $pwshPath $arguments $CronMarker"

    # Fetch existing crontab, strip any prior BotNexus entry, append new line
    $existing = & crontab -l 2>/dev/null
    $filtered = @($existing | Where-Object { $_ -notmatch [regex]::Escape($CronMarker) })
    $filtered += $cronLine
    $filtered | & crontab -

    if ($LASTEXITCODE -ne 0) {
        Write-Error "crontab install failed."
        exit 1
    }

    Write-Host ''
    Write-Host "Crontab entry installed successfully." -ForegroundColor Green
    Write-Host ''
    Write-Host 'To verify:  crontab -l'
    Write-Host "To remove:  ./Install-WatchdogTask.ps1 -Uninstall"
    Write-Host "Logs:       ~/.botnexus/logs/watchdog-*.log"
}
