<#
.SYNOPSIS
    Break-glass gateway recovery assistant. Diagnoses why the BotNexus gateway is
    down and -- when the platform (and therefore Farnsworth) cannot help you itself --
    optionally hands off to an interactive GitHub Copilot CLI session primed with
    everything it needs to find the fault and propose a fix.

    Motivation: when the gateway is down, the built-in BotNexus helper agent
    (Nexus Trailguide) cannot help either -- the gateway that hosts the agent
    runtime is the very thing that is down.

.DESCRIPTION
    When the gateway fails to start (a common cause is an extension assembly-load
    regression that aborts host startup -- see PR #2218 / issue #2184), the CLI only
    surfaces a generic 10s /health timeout and no in-platform agent can help, because
    the agent runtime is the thing that is down.

    This script runs completely independently of a running gateway. It:
      1. Gathers diagnostics -- gateway process/port state, a /health probe, the tail of
         the newest hourly log with ERR/FTL/exception lines extracted, recent git state,
         and the deployed extension set.
      2. Writes a structured diagnostic report to a temp file you can attach to an issue.
      3. Optionally launches GitHub Copilot CLI interactively, inside the repo, with a
         platform-aware prompt so it can diagnose, file an issue, and propose a fix.

    Nothing here restarts or rebuilds the gateway automatically -- recovery actions stay
    in your hands (or Copilot's, with your confirmation).

.PARAMETER GatewayUrl
    Base URL of the gateway health endpoint. Defaults to http://localhost:5005.

.PARAMETER RepoPath
    Path to the BotNexus repo. Defaults to the repo containing this script.

.PARAMETER ConfigDir
    BotNexus config/state directory. Defaults to ~/.botnexus.

.PARAMETER LogLines
    Number of trailing lines to capture from the newest gateway log. Default 200.

.PARAMETER NoCopilot
    Skip the interactive Copilot handoff and only produce the diagnostic report.

.PARAMETER Yes
    Do not prompt before launching Copilot (assume yes).

.EXAMPLE
    pwsh -File scripts/recover-gateway.ps1
    Gathers diagnostics, prints a summary, and offers to launch Copilot interactively.

.EXAMPLE
    pwsh -File scripts/recover-gateway.ps1 -NoCopilot
    Just produces the diagnostic report (no Copilot handoff).
#>
[CmdletBinding()]
param(
    [string]$GatewayUrl = 'http://localhost:5005',
    [string]$RepoPath,
    [string]$ConfigDir,
    [int]$LogLines = 200,
    [switch]$NoCopilot,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'RecoveryPrompt.psm1') -Force

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

# --- Resolve paths -----------------------------------------------------------
if (-not $RepoPath) {
    $RepoPath = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
}
if (-not $ConfigDir) {
    $userHome = [Environment]::GetFolderPath('UserProfile')
    if (-not $userHome) { $userHome = $env:USERPROFILE }
    if (-not $userHome) { $userHome = $env:HOME }
    $ConfigDir = Join-Path $userHome '.botnexus'
}
$LogDir = Join-Path $ConfigDir 'logs'
$ExtDir = Join-Path $ConfigDir 'extensions'

$report = [System.Collections.Generic.List[string]]::new()
function Add-Report([string]$line) { $report.Add($line) | Out-Null }

$timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
Add-Report "# BotNexus gateway recovery diagnostic"
Add-Report ""
Add-Report "Generated: $timestamp"
Add-Report "Repo:      $RepoPath"
Add-Report "ConfigDir: $ConfigDir"
Add-Report ""

Write-Host "BotNexus Gateway Recovery" -ForegroundColor Yellow
Write-Host "Repo:      $RepoPath"
Write-Host "ConfigDir: $ConfigDir"

# --- 1. Health probe ---------------------------------------------------------
Write-Section 'Health probe'
$healthUrl = "$($GatewayUrl.TrimEnd('/'))/health"
$healthy = $false
try {
    $resp = Invoke-WebRequest -Uri $healthUrl -TimeoutSec 5 -UseBasicParsing
    $healthy = ($resp.StatusCode -eq 200)
    $msg = "HTTP $($resp.StatusCode) from $healthUrl"
    Write-Host $msg -ForegroundColor ($(if ($healthy) { 'Green' } else { 'Yellow' }))
    Add-Report "## Health"
    Add-Report "$msg"
    if ($resp.Content) { Add-Report "Body: $($resp.Content)" }
} catch {
    $msg = "UNREACHABLE: $healthUrl -> $($_.Exception.Message)"
    Write-Host $msg -ForegroundColor Red
    Add-Report "## Health"
    Add-Report "$msg"
}
Add-Report ""

# --- 2. Process / port state -------------------------------------------------
Write-Section 'Process & port'
Add-Report "## Process & port"
$dotnetProcs = @(Get-Process dotnet -ErrorAction SilentlyContinue)
Add-Report "dotnet processes running: $($dotnetProcs.Count)"
Write-Host "dotnet processes running: $($dotnetProcs.Count)"
$gwProc = $dotnetProcs | Where-Object {
    try { $_.CommandLine -match 'BotNexus.Gateway.Api' } catch { $false }
} | Select-Object -First 1
# CommandLine is not always available; also look for the native apphost exe.
$apphost = @(Get-Process -Name 'BotNexus.Gateway.Api' -ErrorAction SilentlyContinue)
if ($apphost.Count -gt 0) {
    Add-Report "Native apphost 'BotNexus.Gateway.Api' running: PID $($apphost.Id -join ', ')"
    Write-Host "Native apphost running: PID $($apphost.Id -join ', ')" -ForegroundColor Green
} elseif ($gwProc) {
    Add-Report "Gateway (dotnet host) running: PID $($gwProc.Id)"
    Write-Host "Gateway (dotnet host) running: PID $($gwProc.Id)" -ForegroundColor Green
} else {
    Add-Report "No gateway process detected (neither native apphost nor 'dotnet BotNexus.Gateway.Api')."
    Write-Host "No gateway process detected." -ForegroundColor Red
}
try {
    $port = Get-GatewayPort -GatewayUrl $GatewayUrl
    $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) {
        Add-Report "Port ${port}: LISTENING (owning PID $($conn.OwningProcess))"
        Write-Host "Port ${port}: LISTENING (PID $($conn.OwningProcess))"
    } else {
        Add-Report "Port ${port}: not listening."
        Write-Host "Port ${port}: not listening." -ForegroundColor Yellow
    }
} catch { Add-Report "Port check failed: $($_.Exception.Message)" }
Add-Report ""

# --- 3. Newest log + error extraction ---------------------------------------
Write-Section 'Latest gateway log'
Add-Report "## Latest gateway log"
$latestLog = Get-ChildItem -Path $LogDir -Filter 'botnexus-*.log' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch 'bootstrap' } |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($latestLog) {
    Add-Report "File: $($latestLog.FullName)  (modified $($latestLog.LastWriteTime))"
    Write-Host "Log: $($latestLog.Name)"
    $content = Get-Content $latestLog.FullName -ErrorAction SilentlyContinue
    $errPattern = '\[ERR\]|\[FTL\]|Exception|FileNotFoundException|InvalidOperationException|Unhandled|Body was inferred|Could not load'
    $errLines = $content | Select-String -Pattern $errPattern
    if ($errLines) {
        Add-Report ""
        Add-Report "### Error / fatal lines (last 40)"
        Add-Report '```'
        $errLines | Select-Object -Last 40 | ForEach-Object { Add-Report $_.Line }
        Add-Report '```'
        Write-Host "Found $($errLines.Count) error/fatal lines." -ForegroundColor Yellow
        $errLines | Select-Object -Last 8 | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Red }
    } else {
        Add-Report "No ERR/FTL/exception lines found in newest log."
        Write-Host "No error lines in newest log."
    }
    Add-Report ""
    Add-Report "### Log tail (last $LogLines lines)"
    Add-Report '```'
    $content | Select-Object -Last $LogLines | ForEach-Object { Add-Report $_ }
    Add-Report '```'
} else {
    Add-Report "No gateway log files found under $LogDir."
    Write-Host "No gateway logs found under $LogDir." -ForegroundColor Yellow
}
Add-Report ""

# --- 4. Deployed extensions --------------------------------------------------
Write-Section 'Deployed extensions'
Add-Report "## Deployed extensions"
if (Test-Path $ExtDir) {
    $exts = Get-ChildItem -Path $ExtDir -Directory -ErrorAction SilentlyContinue
    Add-Report "Extension deploy dir: $ExtDir ($($exts.Count) extensions)"
    Write-Host "$($exts.Count) extensions deployed in $ExtDir"
    foreach ($e in $exts) {
        $dlls = @(Get-ChildItem -Path $e.FullName -Filter '*.dll' -ErrorAction SilentlyContinue)
        Add-Report "- $($e.Name) ($($dlls.Count) dll)"
    }
} else {
    Add-Report "No extension deploy dir at $ExtDir."
    Write-Host "No extension deploy dir at $ExtDir."
}
Add-Report ""

# --- 5. Git state ------------------------------------------------------------
Write-Section 'Git state'
Add-Report "## Git state (repo HEAD)"
try {
    Push-Location $RepoPath
    $head = git rev-parse HEAD 2>$null
    $branch = git rev-parse --abbrev-ref HEAD 2>$null
    $recent = git log --oneline -8 2>$null
    Add-Report "Branch: $branch"
    Add-Report "HEAD:   $head"
    Add-Report ""
    Add-Report "Recent commits:"
    Add-Report '```'
    $recent | ForEach-Object { Add-Report $_ }
    Add-Report '```'
    Write-Host "Branch $branch @ $head"
    # Deployed (running) repo, if different from this one
    $profileRepo = Join-Path (Split-Path -Parent $ConfigDir) 'botnexus'
    if ((Test-Path $profileRepo) -and ($profileRepo -ne $RepoPath)) {
        $phead = git -C $profileRepo rev-parse HEAD 2>$null
        Add-Report ""
        Add-Report "Profile/deployed repo ($profileRepo) HEAD: $phead"
    }
    Pop-Location
} catch {
    Add-Report "Git inspection failed: $($_.Exception.Message)"
    if ((Get-Location).Path -ne $RepoPath) { } else { Pop-Location -ErrorAction SilentlyContinue }
}
Add-Report ""

# --- Write report ------------------------------------------------------------
$reportPath = Join-Path ([System.IO.Path]::GetTempPath()) "botnexus-recovery-$((Get-Date).ToString('yyyyMMdd-HHmmss')).md"
$report -join [Environment]::NewLine | Set-Content -Path $reportPath -Encoding UTF8

Write-Section 'Diagnostic report written'
Write-Host $reportPath -ForegroundColor Green

if ($healthy) {
    Write-Host ''
    Write-Host "Gateway /health is returning 200 -- it appears healthy. Nothing to recover." -ForegroundColor Green
    Write-Host "Diagnostic report saved anyway at: $reportPath"
    return
}

# --- 6. Interactive Copilot handoff -----------------------------------------
if ($NoCopilot) {
    Write-Host ''
    Write-Host "Skipping Copilot handoff (-NoCopilot). Attach $reportPath to a GitHub issue for help." -ForegroundColor Yellow
    return
}

$copilot = Get-Command copilot -ErrorAction SilentlyContinue
if (-not $copilot) {
    Write-Host ''
    Write-Host "GitHub Copilot CLI ('copilot') not found on PATH -- cannot hand off." -ForegroundColor Yellow
    Write-Host "Install it, or attach $reportPath to a GitHub issue manually." -ForegroundColor Yellow
    return
}

Write-Host ''
if (-not $Yes) {
    $ans = Read-Host "Launch interactive GitHub Copilot CLI in the repo to help diagnose & fix? [y/N]"
    if ($ans -notmatch '^[yY]') {
        Write-Host "Not launching Copilot. Report is at $reportPath." -ForegroundColor Yellow
        return
    }
}

$gatewayPort = Get-GatewayPort -GatewayUrl $GatewayUrl
$prompt = New-RecoveryPrimingPrompt -RepoPath $RepoPath -ConfigDir $ConfigDir -HealthUrl $healthUrl -GatewayPort $gatewayPort -ReportPath $reportPath

# The handoff must be a session, not a one-shot: --prompt exits after completion, which
# cannot support the read -> hypothesise -> narrow loop recovery needs (issue #2455).
$copilotHelp = try { (& $copilot.Source --help 2>&1 | Out-String) } catch { '' }
if (-not (Test-CopilotInteractiveSupport -HelpText $copilotHelp)) {
    Write-Host ''
    Write-Host "The installed GitHub Copilot CLI ($($copilot.Source)) does not support the" -ForegroundColor Red
    Write-Host "--interactive flag, so it cannot host a recovery session. Upgrade the Copilot CLI," -ForegroundColor Red
    Write-Host "or work the report manually: $reportPath" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host "Launching an interactive Copilot session primed with a platform-aware prompt..." -ForegroundColor Cyan
Write-Host "(Report path: $reportPath)" -ForegroundColor DarkGray
Push-Location $RepoPath
try {
    & $copilot.Source @(Get-CopilotInteractiveArgument -ConfigDir $ConfigDir -Prompt $prompt)
} finally {
    Pop-Location
}
