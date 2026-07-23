#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Performs scheduled BotNexus health checks, update checks, and automated recovery.

.DESCRIPTION
    Runs a lightweight watchdog loop intended to be triggered by an external scheduler
    (Task Scheduler, cron, or systemd timer) at a short interval such as every minute.

    On each run, the script:
    - Checks gateway health via the configured health endpoint.
    - Tracks consecutive failures and restarts the gateway when health fails.
    - Restores the last-known-good config after a configurable failure threshold.
    - Checks for repository updates on a separate interval and runs botnexus update.
    - Checks for BotNexus CLI tool updates on a separate interval.
    - Persists state between runs in a JSON state file.

.PARAMETER GatewayUrl
    Base URL of the BotNexus gateway used for health checks.

.PARAMETER HealthEndpoint
    Relative endpoint path used for health checks.

.PARAMETER RepoPath
    Path to the local BotNexus git clone used for repository update checks.
    Pass an empty string to skip repository checks.

.PARAMETER ConfigDir
    BotNexus home directory that contains config, logs, state, and backup folders.
    Defaults to ~/.botnexus when not provided.

.PARAMETER MaxFailures
    Number of consecutive health-check failures before restoring last-known-good config.

.PARAMETER GitCheckIntervalMinutes
    Minimum minutes between repository update checks.

.PARAMETER CliCheckIntervalMinutes
    Minimum minutes between BotNexus CLI tool update checks.

.PARAMETER LogDir
    Directory where watchdog log files are written.
    Defaults to <ConfigDir>/logs when not provided.

.PARAMETER StateFile
    JSON file path used to persist run state between scheduler invocations.
    Defaults to <ConfigDir>/watchdog-state.json when not provided.

.EXAMPLE
    ./scripts/botnexus-watchdog.ps1

    Runs watchdog checks with defaults.

.EXAMPLE
    ./scripts/botnexus-watchdog.ps1 -GitCheckIntervalMinutes 15 -CliCheckIntervalMinutes 120

    Uses less frequent repository and CLI update checks.

.EXAMPLE
    ./scripts/botnexus-watchdog.ps1 -RepoPath ''

    Runs health monitoring only and skips repository update checks.

.EXAMPLE
    ./scripts/botnexus-watchdog.ps1 -RepoPath 'D:\botnexus' -ConfigDir 'D:\botnexus-home'

    Uses custom source and home paths.

.NOTES
    Intended for scheduled execution rather than long-running interactive use.
    A lock file in the system temp directory prevents overlapping script instances.
#>

[CmdletBinding()]
param(
    [string]$GatewayUrl = 'http://localhost:5005',
    [string]$HealthEndpoint = '/health',
    [string]$RepoPath = '',
    [string]$ConfigDir = '',
    [ValidateRange(1, 100)]
    [int]$MaxFailures = 3,
    [ValidateRange(1, 10080)]
    [int]$GitCheckIntervalMinutes = 5,
    [ValidateRange(1, 10080)]
    [int]$CliCheckIntervalMinutes = 60,
    [string]$LogDir = '',
    [string]$StateFile = ''
)

$ErrorActionPreference = 'Stop'

if (-not $ConfigDir) {
    $ConfigDir = Join-Path $HOME '.botnexus'
}
if (-not $PSBoundParameters.ContainsKey('RepoPath')) {
    $RepoPath = Join-Path $HOME 'botnexus'
}
if (-not $LogDir) {
    $LogDir = Join-Path $ConfigDir 'logs'
}
if (-not $StateFile) {
    $StateFile = Join-Path $ConfigDir 'watchdog-state.json'
}

$backupDir = Join-Path $ConfigDir 'config-backups'
$currentConfigPath = Join-Path $ConfigDir 'config.json'
$lastKnownGoodPath = Join-Path $backupDir 'config-last-known-good.json'
$lockFile = Join-Path ([System.IO.Path]::GetTempPath()) 'botnexus-watchdog.lock'

New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

$logFile = Join-Path $LogDir ("watchdog-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

function Write-Log {
    param(
        [Parameter(Mandatory)]
        [string]$Message,
        [ValidateSet('INF', 'WRN', 'ERR')]
        [string]$Level = 'INF'
    )

    $line = '[{0}][{1}] {2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Level, $Message
    Add-Content -Path $logFile -Value $line
    Write-Host $line
}

function New-State {
    return @{
        FailureCount = 0
        LastGitCheck = $null
        LastCliCheck = $null
        LastKnownGoodConfig = $lastKnownGoodPath
    }
}

function Read-State {
    if (-not (Test-Path $StateFile)) {
        return New-State
    }

    try {
        return Get-Content -Path $StateFile -Raw | ConvertFrom-Json -AsHashtable
    }
    catch {
        Write-Log "State file is invalid JSON. Reinitializing: $StateFile" 'WRN'
        return New-State
    }
}

function Write-State {
    param([hashtable]$State)

    $State.LastKnownGoodConfig = $lastKnownGoodPath
    $json = $State | ConvertTo-Json -Depth 4
    Set-Content -Path $StateFile -Value $json -Encoding UTF8
}

function Get-Sha256 {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return ''
    }

    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Restart-Gateway {
    Write-Log 'Restarting gateway process...'

    & botnexus gateway stop 2>&1 | ForEach-Object { Write-Log $_ 'INF' }
    & botnexus gateway start 2>&1 | ForEach-Object { Write-Log $_ 'INF' }

    if ($LASTEXITCODE -ne 0) {
        throw "Gateway start failed with exit code $LASTEXITCODE"
    }
}

function Save-LastKnownGoodConfig {
    param([hashtable]$State)

    if (-not (Test-Path $currentConfigPath)) {
        return
    }

    $currentHash = Get-Sha256 -Path $currentConfigPath
    $knownHash = Get-Sha256 -Path $lastKnownGoodPath

    if ($currentHash -and ($currentHash -ne $knownHash)) {
        Copy-Item -Path $currentConfigPath -Destination $lastKnownGoodPath -Force
        $State.LastKnownGoodConfig = $lastKnownGoodPath
        Write-Log 'Current config saved as last-known-good.'
    }
}

function Restore-LastKnownGoodConfig {
    if (-not (Test-Path $lastKnownGoodPath)) {
        Write-Log 'No last-known-good config found. Skipping fallback restore.' 'WRN'
        return
    }

    if (Test-Path $currentConfigPath) {
        $currentHash = Get-Sha256 -Path $currentConfigPath
        $knownHash = Get-Sha256 -Path $lastKnownGoodPath

        if ($currentHash -and ($currentHash -ne $knownHash)) {
            $suspectPath = Join-Path $backupDir ("config-suspect-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
            Copy-Item -Path $currentConfigPath -Destination $suspectPath -Force
            Write-Log "Backed up suspect config to: $suspectPath" 'WRN'
        }
    }

    Copy-Item -Path $lastKnownGoodPath -Destination $currentConfigPath -Force
    Write-Log 'Restored last-known-good config.' 'WRN'
}

function Test-Health {
    $uri = "$($GatewayUrl.TrimEnd('/'))/$($HealthEndpoint.TrimStart('/'))"
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 10
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300)
    }
    catch {
        Write-Log "Health check failed: $($_.Exception.Message)" 'WRN'
        return $false
    }
}

function Parse-Time {
    param([object]$Value)

    if (-not $Value) {
        return $null
    }

    try {
        return [DateTimeOffset]::Parse($Value.ToString())
    }
    catch {
        return $null
    }
}

function Is-IntervalDue {
    param(
        [object]$LastRun,
        [int]$Minutes
    )

    $lastRunTime = Parse-Time -Value $LastRun
    if (-not $lastRunTime) {
        return $true
    }

    return (([DateTimeOffset]::Now - $lastRunTime).TotalMinutes -ge $Minutes)
}

function Invoke-CliUpdate {
    Write-Log 'Checking for BotNexus CLI tool updates...'
    & dotnet tool update -g BotNexus.Cli 2>&1 | ForEach-Object { Write-Log $_ 'INF' }

    if ($LASTEXITCODE -ne 0) {
        Write-Log "CLI update check failed with exit code $LASTEXITCODE" 'WRN'
        return $false
    }

    Write-Log 'CLI update check completed.'
    return $true
}

function Run-GitFallbackCheck {
    param([string]$Repo)

    if (-not (Test-Path (Join-Path $Repo '.git'))) {
        Write-Log "Repo path is not a git repository: $Repo" 'WRN'
        return $false
    }

    Push-Location $Repo
    try {
        & git fetch origin main --quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Log 'git fetch failed during fallback check.' 'WRN'
            return $false
        }

        $behindCount = (& git rev-list --count HEAD..origin/main).Trim()
        return [int]$behindCount -gt 0
    }
    finally {
        Pop-Location
    }
}

function Invoke-RepoUpdate {
    param([string]$Repo)

    if (-not (Test-Path $Repo)) {
        Write-Log "RepoPath does not exist: $Repo" 'WRN'
        return $false
    }

    Write-Log 'Checking repository updates via botnexus update check...'
    $checkOutput = & botnexus update check --source $Repo 2>&1
    $checkExit = $LASTEXITCODE

    $checkOutput | ForEach-Object { Write-Log $_ 'INF' }

    if ($checkExit -eq 0) {
        Write-Log 'Repository is up to date.'
        return $true
    }

    if ($checkExit -eq 1) {
        Write-Log 'Updates available. Running botnexus update...'
        & botnexus update --source $Repo 2>&1 | ForEach-Object { Write-Log $_ 'INF' }
        if ($LASTEXITCODE -ne 0) {
            Write-Log "botnexus update failed with exit code $LASTEXITCODE" 'WRN'
            return $false
        }

        Write-Log 'Repository update completed successfully.'
        return $true
    }

    if ($checkOutput -match 'Unrecognized command|No such command|Was not matched') {
        Write-Log 'botnexus update check not available; using git fallback check.' 'WRN'
        if (Run-GitFallbackCheck -Repo $Repo) {
            Write-Log 'Fallback detected updates. Running botnexus update...'
            & botnexus update --source $Repo 2>&1 | ForEach-Object { Write-Log $_ 'INF' }
            if ($LASTEXITCODE -ne 0) {
                Write-Log "botnexus update failed with exit code $LASTEXITCODE" 'WRN'
                return $false
            }
            Write-Log 'Repository update completed successfully.'
            return $true
        }

        Write-Log 'Fallback check found no updates.'
        return $true
    }

    Write-Log "update check failed with exit code $checkExit" 'WRN'
    return $false
}

if (Test-Path $lockFile) {
    Write-Log "Another watchdog instance is running. Lock file: $lockFile" 'WRN'
    exit 0
}

New-Item -ItemType File -Path $lockFile -Force | Out-Null

try {
    Write-Log '--- Watchdog check started ---'

    $state = Read-State
    if (-not $state.ContainsKey('FailureCount')) { $state.FailureCount = 0 }
    if (-not $state.ContainsKey('LastGitCheck')) { $state.LastGitCheck = $null }
    if (-not $state.ContainsKey('LastCliCheck')) { $state.LastCliCheck = $null }

    if (Test-Health) {
        if ($state.FailureCount -ne 0) {
            Write-Log 'Health restored. Resetting failure count.'
        }

        $state.FailureCount = 0
        Save-LastKnownGoodConfig -State $state
    }
    else {
        $state.FailureCount = [int]$state.FailureCount + 1
        Write-Log "Gateway health check failed ($($state.FailureCount)/$MaxFailures)." 'WRN'

        if ($state.FailureCount -ge $MaxFailures) {
            Write-Log 'Failure threshold reached. Attempting config fallback and restart.' 'WRN'
            Restore-LastKnownGoodConfig
        }

        Restart-Gateway
    }

    if ($RepoPath -and (Is-IntervalDue -LastRun $state.LastGitCheck -Minutes $GitCheckIntervalMinutes)) {
        [void](Invoke-RepoUpdate -Repo $RepoPath)
        $state.LastGitCheck = [DateTimeOffset]::Now.ToString('o')
    }

    if (Is-IntervalDue -LastRun $state.LastCliCheck -Minutes $CliCheckIntervalMinutes) {
        [void](Invoke-CliUpdate)
        $state.LastCliCheck = [DateTimeOffset]::Now.ToString('o')
    }

    Write-State -State $state
    Write-Log '--- Watchdog check complete ---'
}
catch {
    Write-Log "Watchdog failed: $($_.Exception.Message)" 'ERR'
    exit 1
}
finally {
    Remove-Item -Path $lockFile -Force -ErrorAction SilentlyContinue
}
