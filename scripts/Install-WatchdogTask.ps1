#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Registers the BotNexus Watchdog as a recurring background task.

.DESCRIPTION
    On Windows, creates a Scheduled Task named "BotNexusWatchdog" that runs
    botnexus-watchdog.ps1 every minute using Task Scheduler. Run once from an
    elevated (Administrator) PowerShell prompt.

    On Linux/macOS, choose between two scheduling methods via -Method:
      cron    (default) - adds a crontab entry that runs every minute.
      systemd           - installs user-level systemd service and timer units
                          (preferred on systemd-based distros; survives reboots
                          without requiring a cron daemon).

.PARAMETER RepoPath
    Optional path to the BotNexus git repository passed to botnexus-watchdog.ps1.
    When omitted, the watchdog script uses its own default path.

.PARAMETER GitCheckIntervalMinutes
    Interval in minutes between repository update checks performed by the watchdog.

.PARAMETER CliCheckIntervalMinutes
    Interval in minutes between BotNexus CLI tool update checks performed by the watchdog.

.PARAMETER MaxFailures
    Consecutive health-check failure threshold before watchdog config fallback is triggered.

.PARAMETER GatewayUrl
    Gateway base URL used by the watchdog health check.

.PARAMETER Method
    Linux/macOS only. Scheduling back-end: 'cron' (default) or 'systemd'.
    Ignored on Windows.

.PARAMETER Uninstall
    Removes the previously installed watchdog scheduler integration.
    - Windows: removes the BotNexusWatchdog scheduled task.
    - cron: removes the marked crontab entry.
    - systemd: disables/removes user timer and unit files.

.EXAMPLE
    # Windows - Scheduled Task
    .\Install-WatchdogTask.ps1

    # Linux - cron (default)
    ./Install-WatchdogTask.ps1

    # Linux - systemd timer
    ./Install-WatchdogTask.ps1 -Method systemd

    # Custom repo path
    ./Install-WatchdogTask.ps1 -RepoPath "~/projects/botnexus" -Method systemd

    # Uninstall (removes whichever method was used)
    ./Install-WatchdogTask.ps1 -Uninstall
    ./Install-WatchdogTask.ps1 -Uninstall -Method systemd

.NOTES
    This script configures scheduling only. Runtime watchdog behavior is implemented in
    botnexus-watchdog.ps1 and controlled through the forwarded arguments documented above.
#>

[CmdletBinding()]
param(
    [string]$RepoPath                 = '',
    [int]   $GitCheckIntervalMinutes  = 5,
    [int]   $CliCheckIntervalMinutes  = 60,
    [int]   $MaxFailures              = 3,
    [string]$GatewayUrl               = 'http://localhost:5005',
    [ValidateSet('cron', 'systemd')]
    [string]$Method                   = 'cron',
    [switch]$Uninstall
)

$TaskName       = 'BotNexusWatchdog'
$CronMarker     = '# BotNexusWatchdog'
$SystemdService = 'botnexus-watchdog.service'
$SystemdTimer   = 'botnexus-watchdog.timer'
$SystemdDir     = Join-Path $HOME '.config' 'systemd' 'user'
$IsWindows      = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)

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
    } elseif ($Method -eq 'systemd') {
        Write-Host "Disabling systemd timer '$SystemdTimer'..."
        & systemctl --user disable --now $SystemdTimer 2>&1 | ForEach-Object { Write-Host "  $_" }
        Remove-Item (Join-Path $SystemdDir $SystemdService) -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $SystemdDir $SystemdTimer)   -Force -ErrorAction SilentlyContinue
        & systemctl --user daemon-reload
        Write-Host "Unit files removed and daemon reloaded."
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

# ── Install: Windows ─────────────────────────────────────────────────────────

if ($IsWindows) {
    Write-Host "Creating scheduled task '$TaskName'..."
    Write-Host "  pwsh:      $pwshPath"
    Write-Host "  script:    $watchdogScript"
    Write-Host "  arguments: $arguments"
    Write-Host ''

    $action   = New-ScheduledTaskAction -Execute $pwshPath -Argument $arguments
    $trigger  = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration ([TimeSpan]::MaxValue)
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
        -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType S4U -RunLevel Highest

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

# ── Install: Linux/macOS — systemd ───────────────────────────────────────────

} elseif ($Method -eq 'systemd') {
    Write-Host "Installing systemd user units for '$TaskName'..."
    Write-Host "  pwsh:      $pwshPath"
    Write-Host "  script:    $watchdogScript"
    Write-Host "  unit dir:  $SystemdDir"
    Write-Host ''

    New-Item -ItemType Directory -Force -Path $SystemdDir | Out-Null

    $serviceContent = @"
[Unit]
Description=BotNexus Watchdog
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=$pwshPath $arguments
"@

    $timerContent = @"
[Unit]
Description=Run BotNexus Watchdog every minute

[Timer]
OnBootSec=60
OnUnitActiveSec=60
AccuracySec=5

[Install]
WantedBy=timers.target
"@

    Set-Content -Path (Join-Path $SystemdDir $SystemdService) -Value $serviceContent -Encoding UTF8
    Set-Content -Path (Join-Path $SystemdDir $SystemdTimer)   -Value $timerContent   -Encoding UTF8

    Write-Host "Reloading systemd and enabling timer..."
    & systemctl --user daemon-reload
    & systemctl --user enable --now $SystemdTimer

    if ($LASTEXITCODE -ne 0) {
        Write-Error "systemctl enable failed. Ensure systemd --user is available on this system."
        exit 1
    }

    Write-Host ''
    Write-Host "systemd timer '$SystemdTimer' installed and started." -ForegroundColor Green
    Write-Host ''
    Write-Host "To verify:      systemctl --user status $SystemdTimer"
    Write-Host "To view logs:   journalctl --user -u botnexus-watchdog.service -f"
    Write-Host "To remove:      ./Install-WatchdogTask.ps1 -Uninstall -Method systemd"
    Write-Host "Log files:      ~/.botnexus/logs/watchdog-*.log"

# ── Install: Linux/macOS — cron ──────────────────────────────────────────────

} else {
    Write-Host "Installing crontab entry for '$TaskName'..."
    Write-Host "  pwsh:      $pwshPath"
    Write-Host "  script:    $watchdogScript"
    Write-Host "  arguments: $arguments"
    Write-Host ''

    $cronLine = "* * * * * $pwshPath $arguments $CronMarker"

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
