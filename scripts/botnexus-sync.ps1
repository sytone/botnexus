#!/usr/bin/env pwsh
# botnexus-sync.ps1
# Manages two BotNexus instances via the BotNexus CLI:
#   PROD: ~/botnexus-prod  (sytone/botnexus main) -> ~/.botnexus/     -> port 5005
#   DEV:  ~/projects/botnexus (fork main)          -> ~/.botnexus-dev/ -> port 5006
#
# Runs every 15 min via cron. Logs to ~/logs/botnexus-sync.log

$ErrorActionPreference = 'Stop'

$env:PATH        = "$env:HOME/.dotnet:$env:HOME/.dotnet/tools:" + $env:PATH
$env:DOTNET_ROOT = "$env:HOME/.dotnet"
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'

$DOTNET  = "$env:HOME/.dotnet/dotnet"
$LogDir  = "$env:HOME/logs"
$LogFile = "$LogDir/botnexus-sync.log"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Write-Log {
    param([string]$Message)
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
    Write-Host $line
    Add-Content -Path $LogFile -Value $line
}

# Lock file — prevent concurrent runs
$LockFile   = '/tmp/botnexus-sync.lock'
$LockStream = $null
try {
    $LockStream = [System.IO.File]::Open($LockFile, 'OpenOrCreate', 'ReadWrite', 'None')
} catch {
    Write-Log "Already running — skipping"
    exit 0
}

function Invoke-Cli {
    param([string]$Repo, [string[]]$CliArgs)
    $CliDll = "$Repo/src/gateway/BotNexus.Cli/bin/Release/net10.0/BotNexus.Cli.dll"
    & $DOTNET $CliDll $CliArgs 2>&1 | Add-Content $LogFile
    return $LASTEXITCODE
}

function Sync-Instance {
    param([string]$Repo, [string]$Remote, [string]$Target, [int]$Port, [string]$Label)

    Set-Location $Repo

    & git fetch $Remote main 2>&1 | Add-Content $LogFile
    $LocalSha  = (git rev-parse HEAD).Trim()
    $RemoteSha = (git rev-parse "$Remote/main").Trim()

    if ($LocalSha -eq $RemoteSha) {
        Write-Log "[$Label] Up to date ($LocalSha)."
        $statusCode = Invoke-Cli $Repo @("gateway", "status", "--target", $Target)
        if ($statusCode -ne 0) {
            Write-Log "[$Label] Gateway not running — starting..."
            $startCode = Invoke-Cli $Repo @("gateway", "start", "--source", $Repo, "--target", $Target, "--port", "$Port")
            if ($startCode -ne 0) { Write-Log "[$Label] ERROR: gateway start failed." }
        }
        return
    }

    Write-Log "[$Label] New commits: $LocalSha -> $RemoteSha — pulling..."
    & git pull $Remote main 2>&1 | Add-Content $LogFile

    Write-Log "[$Label] Building (Release)..."
    & $DOTNET build "$Repo/dirs.proj" -c Release --nologo --tl:off 2>&1 | Add-Content $LogFile
    if ($LASTEXITCODE -ne 0) { Write-Log "[$Label] ERROR: Build failed — aborting."; return }

    Write-Log "[$Label] Running tests..."
    & $DOTNET test "$Repo/tests/dirs.proj" --nologo --tl:off 2>&1 | Add-Content $LogFile
    if ($LASTEXITCODE -ne 0) { Write-Log "[$Label] ERROR: Tests failed — aborting restart."; return }
    Write-Log "[$Label] Tests passed."

    Write-Log "[$Label] Restarting gateway via CLI..."
    Invoke-Cli $Repo @("gateway", "stop", "--target", $Target) | Out-Null
    $startCode = Invoke-Cli $Repo @("gateway", "start", "--source", $Repo, "--target", $Target, "--port", "$Port")
    if ($startCode -ne 0) { Write-Log "[$Label] ERROR: gateway start failed." }
    else { Write-Log "[$Label] Gateway restarted." }
}

try {
    Write-Log "=== BotNexus sync started ==="

    # PROD: sytone/botnexus -> ~/.botnexus -> port 5005
    Sync-Instance "$env:HOME/botnexus-prod" "origin" "$env:HOME/.botnexus" 5005 "prod"

    # DEV: fork -> ~/.botnexus-dev -> port 5006
    Sync-Instance "$env:HOME/projects/botnexus" "fork" "$env:HOME/.botnexus-dev" 5006 "dev"

    Write-Log "=== Done ==="
} finally {
    if ($LockStream) { $LockStream.Dispose() }
    Remove-Item $LockFile -Force -ErrorAction SilentlyContinue
}
