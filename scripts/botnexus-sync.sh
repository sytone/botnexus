#!/usr/bin/env bash
# botnexus-sync.sh
# Manages two BotNexus instances:
#   PROD: ~/botnexus-prod  (sytone/botnexus main) → ~/.botnexus/     → port 5005
#   DEV:  ~/projects/botnexus (fork main)          → ~/.botnexus-dev/ → port 5006
#
# Runs every 15 min via cron. Logs to ~/logs/botnexus-sync.log

set -euo pipefail

export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

DOTNET="$HOME/.dotnet/dotnet"
LOG_DIR="$HOME/logs"
LOG_FILE="$LOG_DIR/botnexus-sync.log"
mkdir -p "$LOG_DIR"

# Lock file — prevent concurrent runs
LOCK="/tmp/botnexus-sync.lock"
exec 9>"$LOCK"
flock -n 9 || { echo "[$(date '+%Y-%m-%d %H:%M:%S')] Already running — skipping" >> "$LOG_FILE"; exit 0; }

log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*" | tee -a "$LOG_FILE"; }

gateway_is_running() {
    local pidfile="$1"
    [[ -f "$pidfile" ]] && kill -0 "$(cat "$pidfile")" 2>/dev/null
}

gateway_stop() {
    local pidfile="$1" label="$2"
    if [[ -f "$pidfile" ]]; then
        local pid; pid=$(cat "$pidfile")
        log "[$label] Stopping gateway (pid $pid)..."
        kill "$pid" 2>/dev/null || true
        sleep 2; kill -9 "$pid" 2>/dev/null || true
        rm -f "$pidfile"
    fi
}

gateway_start() {
    local dll="$1" home="$2" port="$3" pidfile="$4" logfile="$5" label="$6"
    log "[$label] Starting gateway (port $port)..."
    # setsid fully detaches from the terminal session — nohup alone isn't sufficient
    # DOTNET_SYSTEM_GLOBALIZATION_INVARIANT avoids CultureNotFoundException on Linux
    BOTNEXUS_HOME="$home" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 setsid "$DOTNET" "$dll" --urls "http://0.0.0.0:$port" \
        >> "$logfile" 2>&1 </dev/null &
    local pid=$!
    echo "$pid" > "$pidfile"
    disown
    sleep 4
    if kill -0 "$pid" 2>/dev/null; then
        log "[$label] Started (pid $pid)"
    else
        log "[$label] ERROR: Exited immediately — check $logfile"
        rm -f "$pidfile"; return 1
    fi
}

sync_instance() {
    local repo="$1" remote="$2" home="$3" port="$4" label="$5"
    local pidfile="$home/gateway.pid"
    local dll="$repo/src/gateway/BotNexus.Gateway.Api/bin/Debug/net10.0/BotNexus.Gateway.Api.dll"
    local gwlog="$LOG_DIR/botnexus-gateway-${label}.log"

    cd "$repo"
    git fetch "$remote" main >> "$LOG_FILE" 2>&1
    local local_sha remote_sha
    local_sha=$(git rev-parse HEAD)
    remote_sha=$(git rev-parse "$remote/main")

    if [[ "$local_sha" == "$remote_sha" ]]; then
        log "[$label] Up to date ($local_sha)."
        if ! gateway_is_running "$pidfile"; then
            log "[$label] Gateway not running — starting..."
            gateway_start "$dll" "$home" "$port" "$pidfile" "$gwlog" "$label"
        fi
        return
    fi

    log "[$label] New commits: $local_sha → $remote_sha — pulling..."
    git pull "$remote" main >> "$LOG_FILE" 2>&1

    log "[$label] Building..."
    if ! "$DOTNET" build dirs.proj --nologo --tl:off >> "$LOG_FILE" 2>&1; then
        log "[$label] ERROR: Build failed — aborting."
        return 1
    fi

    log "[$label] Running tests..."
    if ! "$DOTNET" test tests/dirs.proj --nologo --tl:off >> "$LOG_FILE" 2>&1; then
        log "[$label] ERROR: Tests failed — aborting restart."
        return 1
    fi
    log "[$label] Tests passed."

    # Deploy extensions — run CLI deploy then copy to the correct home
    log "[$label] Deploying extensions..."
    # --source/--skip-build, not --path/--dev: the CLI renamed these and rejects the old names.
    # The `|| true` below is deliberate for a genuinely failed start, but it also swallowed the
    # argument error, so this step silently deployed nothing at all for as long as the names were
    # stale. Log the failure rather than discarding it.
    if ! BOTNEXUS_HOME="$home" "$DOTNET" "$repo/src/gateway/BotNexus.Cli/bin/Debug/net10.0/BotNexus.Cli.dll" \
        gateway start --source "$repo" --skip-build >> "$LOG_FILE" 2>&1; then
        log "[$label] WARNING: gateway start reported failure — see $LOG_FILE."
    fi
    # CLI always deploys to ~/.botnexus — copy to the right home if different
    if [[ "$home" != "$HOME/.botnexus" ]] && [[ -d "$HOME/.botnexus/extensions" ]]; then
        mkdir -p "$home/extensions"
        cp -r "$HOME/.botnexus/extensions/"* "$home/extensions/"
    fi
    # Publish Blazor client to get fresh static assets (index.html + _framework)
    "$DOTNET" publish "$repo/src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient" \
        -c Release --nologo >> "$LOG_FILE" 2>&1 || true
    BLAZOR_PUBLISH="$repo/src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/bin/Release/net10.0/publish/wwwroot"
    if [[ -d "$BLAZOR_PUBLISH" ]]; then
        rm -rf "$home/extensions/botnexus-signalr/blazor"
        cp -r "$BLAZOR_PUBLISH" "$home/extensions/botnexus-signalr/blazor"
    fi
    log "[$label] Extensions deployed."

    if gateway_is_running "$pidfile"; then
        gateway_stop "$pidfile" "$label"
    fi
    gateway_start "$dll" "$home" "$port" "$pidfile" "$gwlog" "$label"
}

log "=== BotNexus sync started ==="

# PROD: sytone/botnexus → ~/.botnexus → port 5005
sync_instance "$HOME/botnexus-prod" "origin" "$HOME/.botnexus" "5005" "prod"

# DEV: fork → ~/.botnexus-dev → port 5006
sync_instance "$HOME/projects/botnexus" "fork" "$HOME/.botnexus-dev" "5006" "dev"

log "=== Done ==="
