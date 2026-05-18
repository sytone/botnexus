# BotNexus Watchdog — Automated Health, Updates & Recovery

A cross-platform PowerShell script that keeps your BotNexus gateway running, up-to-date, and self-healing on Windows and Linux.

## What It Does

| Check | Default Interval | Action |
|---|---|---|
| **Gateway health** | Every run (1 min) | Restart if `/health` fails; fall back to last-known-good config after N failures |
| **Git repo updates** | Every 5 minutes | `botnexus update check` to detect new commits, then `botnexus update` to pull, build, and restart |
| **CLI tool updates** | Every 60 minutes | `dotnet tool update -g BotNexus.Cli` |

The script itself runs on a short interval (e.g. every minute). Each check type has its own timer so you control how often expensive operations happen independently.

## Prerequisites

- **Windows** 10/11 or Windows Server 2016+, **or Linux** (any distro with pwsh support)
- [PowerShell 7+](https://github.com/PowerShell/PowerShell/releases) (`pwsh`)
- [.NET SDK](https://dotnet.microsoft.com/download) installed
- BotNexus CLI installed (`dotnet tool install -g BotNexus.Cli`)
- Git installed and on PATH (only if using repo sync)
- A running BotNexus gateway (`botnexus gateway start`)

## Quick Start

=== "Windows"

    ```powershell
    # 1. Test the script manually first
    .\scripts\botnexus-watchdog.ps1

    # 2. Register a Windows Scheduled Task (runs every 1 minute)
    .\scripts\Install-WatchdogTask.ps1

    # 3. Check the logs
    Get-Content "$HOME\.botnexus\logs\watchdog-*.log" -Tail 50
    ```

=== "Linux"

    ```bash
    # 1. Test the script manually first
    pwsh ./scripts/botnexus-watchdog.ps1

    # 2a. Install using cron (default)
    pwsh ./scripts/Install-WatchdogTask.ps1

    # 2b. Or install using a systemd timer (preferred on systemd-based distros)
    pwsh ./scripts/Install-WatchdogTask.ps1 -Method systemd

    # 3. Check the logs
    tail -50 ~/.botnexus/logs/watchdog-*.log
    ```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `-GatewayUrl` | `http://localhost:5005` | Gateway base URL |
| `-HealthEndpoint` | `/health` | Health check path |
| `-RepoPath` | `~/botnexus` | Path to local BotNexus git clone (empty string to skip) |
| `-ConfigDir` | `~/.botnexus` | BotNexus user data directory |
| `-MaxFailures` | `3` | Consecutive health failures before config fallback |
| `-GitCheckIntervalMinutes` | `5` | Minutes between git fetch checks |
| `-CliCheckIntervalMinutes` | `60` | Minutes between CLI update checks |
| `-LogDir` | `~/.botnexus/logs` | Log file directory |
| `-StateFile` | `~/.botnexus/watchdog-state.json` | Persists timers and failure counts between runs |

## How the Health Recovery Works

```
Health check fails
       │
       ▼
  Failure count < MaxFailures?
       │              │
      YES             NO
       │              │
       ▼              ▼
  Stop + Restart    Back up current config as "suspect"
       │            Restore last-known-good config
       │            Stop + Restart
       │              │
       ▼              ▼
  Success? ────► Reset failure count to 0
                 Save current config as new "last-known-good"
```

**Last-known-good config** is automatically saved when a health check passes and the config file has changed (compared by SHA256 hash). Config backups are stored in `~/.botnexus/config-backups/`:

- `config-last-known-good.json` — most recent working config (only updated when the config changes)
- `config-suspect-20260517-143022.json` — timestamped backup of the config that was replaced (skipped if identical to last-known-good)

## Scheduling the Watchdog

### Windows

#### Option A: Use the installer script

```powershell
.\scripts\Install-WatchdogTask.ps1
```

This creates a task named `BotNexusWatchdog` that runs every minute. Pass parameters to customise:

```powershell
.\scripts\Install-WatchdogTask.ps1 -RepoPath "D:\botnexus" -GitCheckIntervalMinutes 15 -CliCheckIntervalMinutes 120
```

To remove:

```powershell
.\scripts\Install-WatchdogTask.ps1 -Uninstall
```

#### Option B: Manual Task Scheduler setup

1. Open **Task Scheduler** (`taskschd.msc`)
2. Click **Create Task**
3. **General** tab:
   - Name: `BotNexusWatchdog`
   - Check: *Run whether user is logged on or not*
   - Check: *Run with highest privileges*
4. **Triggers** tab → New:
   - Begin the task: On a schedule
   - Repeat every: **1 minute** for a duration of **Indefinitely**
   - Check: *Enabled*
5. **Actions** tab → New:
   - Program: `pwsh` (or full path to `pwsh.exe`)
   - Arguments:
     ```
     -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\src\botnexus\scripts\botnexus-watchdog.ps1" -GitCheckIntervalMinutes 5 -CliCheckIntervalMinutes 60
     ```
6. **Settings** tab:
   - Check: *Do not start a new instance if already running*
   - Uncheck: *Stop the task if it runs longer than*

#### Option C: Using `schtasks` from the command line

```powershell
$pwsh    = (Get-Command pwsh).Source
$script  = "C:\src\botnexus\scripts\botnexus-watchdog.ps1"
$taskArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`""

schtasks /Create /TN "BotNexusWatchdog" /TR "`"$pwsh`" $taskArgs" /SC MINUTE /MO 1 /RL HIGHEST /F
```

To remove:

```powershell
schtasks /Delete /TN "BotNexusWatchdog" /F
```

### Linux

#### Option A: Use the installer script (cron)

```bash
# Install with defaults (cron, every 1 minute)
pwsh ./scripts/Install-WatchdogTask.ps1

# Customise parameters
pwsh ./scripts/Install-WatchdogTask.ps1 -GitCheckIntervalMinutes 15 -CliCheckIntervalMinutes 120

# Remove
pwsh ./scripts/Install-WatchdogTask.ps1 -Uninstall
```

#### Option B: Use the installer script (systemd timer)

Preferred on systemd-based distros. Installs user-level units — no root required.

```bash
# Install
pwsh ./scripts/Install-WatchdogTask.ps1 -Method systemd

# Customise parameters
pwsh ./scripts/Install-WatchdogTask.ps1 -Method systemd -GitCheckIntervalMinutes 15

# Remove
pwsh ./scripts/Install-WatchdogTask.ps1 -Uninstall -Method systemd
```

#### Option C: Manual cron setup

```bash
# Add the cron entry
(crontab -l 2>/dev/null; echo '* * * * * /usr/bin/pwsh -NoProfile -NonInteractive -File ~/botnexus/scripts/botnexus-watchdog.ps1') | crontab -

# Verify
crontab -l
```

To customise parameters:

```bash
(crontab -l 2>/dev/null; echo '* * * * * /usr/bin/pwsh -NoProfile -NonInteractive -File ~/botnexus/scripts/botnexus-watchdog.ps1 -GitCheckIntervalMinutes 15 -CliCheckIntervalMinutes 120') | crontab -
```

To remove:

```bash
crontab -l | grep -v botnexus-watchdog | crontab -
```

#### Option D: Manual systemd timer setup

Create two unit files:

`~/.config/systemd/user/botnexus-watchdog.service`:

```ini
[Unit]
Description=BotNexus Watchdog
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/bin/pwsh -NoProfile -NonInteractive -File %h/botnexus/scripts/botnexus-watchdog.ps1
```

`~/.config/systemd/user/botnexus-watchdog.timer`:

```ini
[Unit]
Description=Run BotNexus Watchdog every minute

[Timer]
OnBootSec=60
OnUnitActiveSec=60
AccuracySec=5

[Install]
WantedBy=timers.target
```

Enable and start:

```bash
mkdir -p ~/.config/systemd/user
# (create the two files above)
systemctl --user daemon-reload
systemctl --user enable --now botnexus-watchdog.timer

# Check status
systemctl --user status botnexus-watchdog.timer
journalctl --user -u botnexus-watchdog.service -f
```

To disable:

```bash
systemctl --user disable --now botnexus-watchdog.timer
```

## Customising Check Intervals

The script runs every time the scheduler fires (e.g. every minute), but each check tracks its own timer in `watchdog-state.json`. Examples:

```powershell
# Health every 1 min, git every 2 min, CLI every 30 min
./botnexus-watchdog.ps1 -GitCheckIntervalMinutes 2 -CliCheckIntervalMinutes 30

# Health-only — no git or CLI updates (pass empty RepoPath)
./botnexus-watchdog.ps1 -RepoPath ''

# Aggressive polling (useful during active development)
./botnexus-watchdog.ps1 -GitCheckIntervalMinutes 1 -CliCheckIntervalMinutes 5

# Conservative (production — check git every 15 min, CLI daily)
./botnexus-watchdog.ps1 -GitCheckIntervalMinutes 15 -CliCheckIntervalMinutes 1440

# Custom repo location
./botnexus-watchdog.ps1 -RepoPath "/opt/botnexus"    # Linux
./botnexus-watchdog.ps1 -RepoPath "D:\botnexus"      # Windows
```

## Logs & State

**Log files** are written daily to `~/.botnexus/logs/watchdog-YYYY-MM-DD.log`:

```
[2026-05-17 14:30:01] [INFO] --- Watchdog check started ---
[2026-05-17 14:30:01] [INFO] Git repo is up to date.
[2026-05-17 14:30:01] [INFO] CLI tool is already at the latest version.
[2026-05-17 14:30:02] [INFO] Current config saved as last-known-good.
[2026-05-17 14:30:02] [INFO] --- Watchdog check complete ---
```

**State file** (`~/.botnexus/watchdog-state.json`) persists between runs:

```json
{
  "FailureCount": 0,
  "LastGitCheck": "2026-05-17T14:30:01.1234567+00:00",
  "LastCliCheck": "2026-05-17T14:00:01.1234567+00:00",
  "LastKnownGoodConfig": "/home/you/.botnexus/config-backups/config-last-known-good.json"
}
```

## Troubleshooting

| Problem | Fix |
|---|---|
| Script never runs | Verify `pwsh` is on PATH: `Get-Command pwsh` |
| "Another instance running" | Delete the lock file if a previous run crashed — `%TEMP%\botnexus-watchdog.lock` (Windows) or `/tmp/botnexus-watchdog.lock` (Linux) |
| Gateway keeps failing | Check `~/.botnexus/logs/` for gateway errors; verify config with `botnexus validate` |
| Git check not triggering | Ensure `-RepoPath` points to a valid git repo with an `origin` remote |
| CLI update fails | Run `dotnet tool update -g BotNexus.Cli` manually to see errors |
| Config fallback loop | Inspect `config-backups/` — the "last-known-good" may also be broken; restore manually |
