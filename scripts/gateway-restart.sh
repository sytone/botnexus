#!/usr/bin/env bash
#
# gateway-restart.sh — restart the BotNexus gateway, port first.
#
# WHY THIS EXISTS
#
# `botnexus gateway` tracks its process by PID file and refuses to signal anything it cannot
# positively identify as its own binary. That is the right call — it is what stops it killing an
# unrelated `dotnet` — but it means a gateway started from ANOTHER build directory is invisible to
# it. It stops the one it manages, says "Gateway stopped", and the start that follows dies with:
#
#     Failed to bind to address http://...:5005: address already in use
#
# buried under a page of cancelled background services, a minidump, and exit code 134. The stop was
# honest; it was just about a different process.
#
# So this script asks the only question that cannot lie: WHO HOLDS THE PORT. It then verifies that
# process really is a gateway before signalling it, and names the build directory it came from, so
# a stray from another worktree is obvious rather than mysterious.
#
# Three traps it encodes, each of which has cost real time:
#
#   1. `pgrep -f BotNexus.Gateway.Api` matches its OWN command line. A liveness check written that
#      way reports the gateway is still running forever. Liveness here is the port and /proc.
#   2. A gateway suspended with Ctrl+Z (state T) still holds the port and ignores SIGTERM. It has
#      to be SIGCONT'd before it can die.
#   3. The gateway binds the address in its config, NOT necessarily the one in --urls. Started with
#      `--urls http://localhost:5005` it may listen on a LAN address, so a readiness probe against
#      localhost times out on a perfectly healthy gateway. This probes the address it actually bound.
#
# USAGE
#
#   gateway-restart.sh                 # restart whatever is running, from the same build
#   gateway-restart.sh --status        # show who holds the port and every gateway process
#   gateway-restart.sh --stop          # stop only
#   gateway-restart.sh --from ~/bn-wt-design
#   gateway-restart.sh --port 5006
#   gateway-restart.sh --force         # signal a port holder that is NOT a gateway
#
set -uo pipefail

PORT="${BOTNEXUS_GATEWAY_PORT:-5005}"
BUILD_DIR="${BOTNEXUS_GATEWAY_DIR:-}"
HOME_DIR="${BOTNEXUS_HOME:-$HOME/.botnexus}"
ACTION="restart"
FORCE=0
STOP_TIMEOUT=25
READY_TIMEOUT=60
BINARY_SUFFIX="src/gateway/BotNexus.Gateway.Api/bin/Release/net10.0/BotNexus.Gateway.Api"

while [ $# -gt 0 ]; do
  case "$1" in
    --status)      ACTION="status" ;;
    --stop)        ACTION="stop" ;;
    --restart)     ACTION="restart" ;;
    --force)       FORCE=1 ;;
    --port)        PORT="$2"; shift ;;
    --from)        BUILD_DIR="$2"; shift ;;
    -h|--help)     sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)             echo "unknown option: $1 (try --help)" >&2; exit 2 ;;
  esac
  shift
done

say()  { printf '%s\n' "$*"; }
warn() { printf '%s\n' "$*" >&2; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

# ── Facts ────────────────────────────────────────────────────────────────────────────────────
#
# The port, not a process name, is the ground truth. Everything else is derived from it.

port_holder_pid() {
  # -H drops the header; the users:(("name",pid=N,fd=N)) field carries the owner.
  ss -ltnpH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | head -1 | cut -d= -f2
}

port_bound_address() {
  ss -ltnH "sport = :$PORT" 2>/dev/null | awk '{print $4}' | head -1
}

# The full command line, NUL-separated in /proc, rendered readable.
cmdline_of() {
  tr '\0' ' ' < "/proc/$1/cmdline" 2>/dev/null
}

process_state() {
  ps -o stat= -p "$1" 2>/dev/null | tr -d ' '
}

is_gateway() {
  case "$(cmdline_of "$1")" in
    *BotNexus.Gateway.Api*) return 0 ;;
    *) return 1 ;;
  esac
}

# The build a running gateway came from, so a stray from another worktree names itself.
build_dir_of() {
  local exe
  exe=$(readlink -f "/proc/$1/exe" 2>/dev/null)

  case "$exe" in
    */dotnet)
      # Launched as `dotnet <dll>`; the build is the directory of the dll argument.
      exe=$(cmdline_of "$1" | tr ' ' '\n' | grep -m1 'BotNexus.Gateway.Api.dll')
      ;;
  esac

  [ -n "$exe" ] || return 1
  printf '%s\n' "${exe%/$BINARY_SUFFIX*}" | sed 's#/src/gateway/.*##'
}

every_gateway_pid() {
  # Deliberately NOT pgrep -f: that matches this script's own command line and reports a gateway
  # that does not exist. /proc is read directly instead.
  local pid
  for pid in /proc/[0-9]*; do
    pid=${pid#/proc/}
    is_gateway "$pid" && printf '%s\n' "$pid"
  done
}

# ── Reporting ────────────────────────────────────────────────────────────────────────────────

status() {
  local pid addr
  pid=$(port_holder_pid)
  addr=$(port_bound_address)

  if [ -n "$pid" ]; then
    say "port $PORT is held by pid $pid  (listening on ${addr:-unknown})"

    if is_gateway "$pid"; then
      say "  build:   $(build_dir_of "$pid" || echo unknown)"
    else
      say "  NOTE:    this is NOT a BotNexus gateway"
    fi

    say "  state:   $(process_state "$pid")"
    say "  cmdline: $(cmdline_of "$pid" | tr '\n' ' ' | cut -c1-160)"
  else
    say "port $PORT is free"
  fi

  say ""
  say "gateway processes on this host:"

  local found=0 marked=0 other
  for other in $(every_gateway_pid); do
    found=1
    local mark="     "

    if [ "$other" = "${pid:-}" ]; then
      mark="  >> "
      marked=1
    fi

    say "${mark}pid $other  $(build_dir_of "$other" || echo unknown)"
  done

  [ "$found" = 1 ] || say "     (none)"

  # Only explain the marker if one was actually printed. A legend for a symbol that is not on
  # screen reads as though something was missed.
  [ "$marked" = 1 ] && { say ""; say "  >> holds port $PORT"; }

  return 0
}

# ── Stopping ─────────────────────────────────────────────────────────────────────────────────

stop_gateway() {
  local pid
  pid=$(port_holder_pid)

  if [ -z "$pid" ]; then
    say "port $PORT already free; nothing to stop"
    return 0
  fi

  if ! is_gateway "$pid" && [ "$FORCE" != 1 ]; then
    warn "port $PORT is held by pid $pid, which is NOT a BotNexus gateway:"
    warn "  $(cmdline_of "$pid" | tr '\n' ' ' | cut -c1-160)"
    die "refusing to signal it. Re-run with --force if you are certain."
  fi

  say "stopping pid $pid  ($(build_dir_of "$pid" || echo 'unknown build'))"

  # A process suspended with Ctrl+Z still holds the port and cannot act on SIGTERM until it is
  # resumed. Signalling it and waiting would look exactly like a gateway that ignores TERM.
  case "$(process_state "$pid")" in
    T*) say "  it is SUSPENDED; resuming it so it can shut down"; kill -CONT "$pid" 2>/dev/null ;;
  esac

  kill -TERM "$pid" 2>/dev/null

  # Wait for the PORT to free, not for the PID to disappear. A process can linger in shutdown
  # after releasing its listener, and a released port is what the next start actually needs.
  local waited=0
  while [ "$waited" -lt "$STOP_TIMEOUT" ]; do
    sleep 1
    waited=$((waited + 1))
    [ -z "$(port_holder_pid)" ] && { say "  stopped; port $PORT free after ${waited}s"; return 0; }
  done

  warn "  still holding the port after ${STOP_TIMEOUT}s; escalating to SIGKILL"
  kill -KILL "$pid" 2>/dev/null

  waited=0
  while [ "$waited" -lt 10 ]; do
    sleep 1
    waited=$((waited + 1))
    [ -z "$(port_holder_pid)" ] && { say "  killed; port $PORT free"; return 0; }
  done

  die "port $PORT is STILL held after SIGKILL. Run --status; something else may have taken it."
}

# ── Starting ─────────────────────────────────────────────────────────────────────────────────

resolve_build_dir() {
  # An explicit --from wins. Otherwise restart what was running, from the same build, which is the
  # least surprising thing "restart" can mean.
  if [ -n "$BUILD_DIR" ]; then
    printf '%s\n' "$BUILD_DIR"
    return 0
  fi

  local pid
  pid=$(port_holder_pid)

  if [ -n "$pid" ] && is_gateway "$pid"; then
    build_dir_of "$pid"
    return 0
  fi

  [ -d "$HOME/botnexus" ] && { printf '%s\n' "$HOME/botnexus"; return 0; }

  return 1
}

start_gateway() {
  local dir binary log
  dir="$1"
  binary="$dir/$BINARY_SUFFIX"

  [ -x "$binary" ] || die "no gateway binary at $binary — build it first, or pass --from"

  local env_file="$HOME_DIR/botnexus.env"
  if [ -f "$env_file" ]; then
    set -a
    # shellcheck disable=SC1090
    . "$env_file"
    set +a
  else
    warn "no $env_file; starting without it"
  fi

  [ -n "${ANTHROPIC_API_KEY:-}" ] || warn "ANTHROPIC_API_KEY is empty; the gateway will start but agents will fail"

  log="$HOME_DIR/logs/gateway-console.log"
  mkdir -p "$(dirname "$log")"

  # --urls must be passed, and must carry the CONFIGURED address rather than a guess.
  #
  # Two ways to get this wrong, both of which happened here. Hardcoding
  # --urls http://localhost:PORT bound the gateway to loopback and made the portal unreachable
  # from the LAN on a host whose config says 192.168.168.10 - earlier builds ignored the flag,
  # which hid it until an upstream change made the command line win. Dropping --urls entirely is
  # no better: the gateway does not hand gateway.listenUrl to Kestrel, so it fell back to the
  # framework default and bound 127.0.0.1:5000, a port nothing was looking at.
  #
  # So: read listenUrl from config and pass that.
  local listen
  listen=$(python3 -c "import json,sys
try:
    print(json.load(open(\"$HOME_DIR/config.json\"))[\"gateway\"][\"listenUrl\"])
except Exception:
    sys.exit(1)" 2>/dev/null)

  if [ -z "$listen" ]; then
    warn "  could not read gateway.listenUrl from $HOME_DIR/config.json; falling back to port $PORT on all interfaces"
    listen="http://0.0.0.0:$PORT"
  fi

  say "starting from $dir"
  say "  listen:  $listen  (from gateway.listenUrl)"
  nohup "$binary" --urls "$listen" --environment Development >"$log" 2>&1 &
  local pid=$!
  say "  pid $pid, console log -> $log"

  # Readiness is the health endpoint on the address the gateway ACTUALLY bound. Probing localhost
  # would time out on a healthy gateway that bound a LAN address instead.
  local waited=0 addr probe
  while [ "$waited" -lt "$READY_TIMEOUT" ]; do
    sleep 1
    waited=$((waited + 1))

    if ! [ -d "/proc/$pid" ]; then
      warn "  the gateway exited after ${waited}s. Last lines:"
      tail -20 "$log" >&2
      die "gateway failed to start"
    fi

    addr=$(port_bound_address)
    [ -n "$addr" ] || continue

    # 0.0.0.0 means every interface; probe it as loopback.
    probe="http://${addr/0.0.0.0/127.0.0.1}/health"

    if curl -fsS -o /dev/null --max-time 3 "$probe" 2>/dev/null; then
      say "  healthy after ${waited}s, listening on $addr"
      return 0
    fi
  done

  warn "  listening but not healthy after ${READY_TIMEOUT}s; it may still be starting"
  warn "  check: tail -f $log"
  return 1
}

# ── Main ─────────────────────────────────────────────────────────────────────────────────────

case "$ACTION" in
  status)
    status
    ;;
  stop)
    stop_gateway
    ;;
  restart)
    dir=$(resolve_build_dir) || die "nothing is running and no default build found; pass --from <dir>"
    stop_gateway
    start_gateway "$dir"
    ;;
esac
