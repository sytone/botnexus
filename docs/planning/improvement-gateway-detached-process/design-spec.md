---
id: improvement-gateway-detached-process
title: "Launch Gateway as Detached Process from CLI"
type: improvement
priority: medium
status: delivered
created: 2026-07-28
updated: 2026-07-28
author: nova
depends_on: []
tags: [cli, gateway, process-management, windows, developer-experience]
---

# Improvement: Launch Gateway as Detached Process from CLI

**Status:** Delivered
**Priority:** medium
**Created:** 2026-07-28

## Problem

When using `botnexus gateway start`, the gateway process runs in the same console window as the CLI. This blocks the console — the user can't run further CLI commands until the gateway is stopped. The CLI effectively becomes a gateway log viewer rather than remaining a command-line tool.

### Current Behavior

```
> botnexus gateway start
[13:42:01 INF] BotNexus Gateway starting...
[13:42:01 INF] Loading configuration from C:\Users\jobullen\.botnexus\config.json
[13:42:02 INF] Gateway listening on https://localhost:5001
█  ← cursor blocked here, no further CLI input possible
```

The user must open a new terminal to run `botnexus gateway status` or any other command.

### Expected Behavior

```
> botnexus gateway start
✓ Gateway started (PID 12345)
  Logs: C:\Users\jobullen\.botnexus\logs\gateway.log
  Stop: botnexus gateway stop

> botnexus gateway status
● Gateway is running (PID 12345, uptime 2m 14s)

> botnexus gateway stop
✓ Gateway stopped
```

The gateway runs in its own console window. The CLI returns control immediately.

## Requirements

### Must Have

- `botnexus gateway start` launches the gateway as a separate/detached process in its own console window
- CLI prints the gateway PID, confirms successful start, and returns control to the user
- PID is persisted to a file (`~/.botnexus/gateway.pid`) so the CLI can find it across sessions
- `botnexus gateway status` reads the PID file, checks if the process is alive, reports status
- `botnexus gateway stop` reads the PID file, gracefully stops the process, cleans up PID file
- `botnexus gateway restart` performs stop + start
- Stale PID file handling — if the process died but the PID file remains, detect and clean up

### Should Have

- Gateway writes logs to `~/.botnexus/logs/` even when running detached (stdout/stderr redirected to log files)
- Startup health check — after spawning, the CLI briefly polls the gateway's health endpoint to confirm it's actually ready before printing success
- Graceful shutdown via SIGTERM / `Process.Kill(graceful)` before falling back to hard kill

### Nice to Have

- `botnexus gateway logs` tails the gateway log file for ad-hoc viewing
- `botnexus gateway start --attached` flag to preserve current foreground behavior for debugging
- Prevent double-start — if PID file exists and process is alive, refuse to start a second instance

## Design

### Process Spawning (Windows)

On Windows, use `System.Diagnostics.Process.Start()` with:

```csharp
var startInfo = new ProcessStartInfo
{
    FileName = gatewayExecutablePath,
    Arguments = "--daemon", // or equivalent flag
    UseShellExecute = true, // required for new window on Windows
    CreateNoWindow = false, // open in its own console window
    WindowStyle = ProcessWindowStyle.Normal,
};

var process = Process.Start(startInfo);
```

`UseShellExecute = true` with `CreateNoWindow = false` opens the gateway in a new console window on Windows. The gateway's own window shows its log output independently.

### PID File

**Location:** `~/.botnexus/gateway.pid`

**Format:** Plain text, single line containing the PID as an integer.

```
12345
```

**Lifecycle:**
1. `gateway start` — spawns process, writes PID file
2. `gateway status` — reads PID file, checks `Process.GetProcessById(pid)` exists
3. `gateway stop` — reads PID file, kills process, deletes PID file
4. On any command — if PID file exists but process is dead, delete stale PID file and report accordingly

### Stale PID Detection

```csharp
bool IsGatewayRunning(int pid)
{
    try
    {
        var process = Process.GetProcessById(pid);
        // Optionally verify it's actually the gateway by checking process name
        return !process.HasExited && process.ProcessName.Contains("BotNexus");
    }
    catch (ArgumentException)
    {
        return false; // PID doesn't exist
    }
}
```

Checking the process name prevents false positives if the OS has recycled the PID for an unrelated process.

### Log Routing

When running detached, the gateway should continue writing to its standard log files in `~/.botnexus/logs/` via Serilog (or whichever logging framework is configured). The new console window will show real-time output; the log files provide persistent history.

If `UseShellExecute = true`, stdout/stderr cannot be redirected programmatically. This is acceptable because:
- Serilog file sinks handle persistent logging regardless of console attachment
- The gateway's own console window provides real-time visibility
- `botnexus gateway logs` (nice-to-have) can tail the log file

### Startup Health Check

After spawning the process, the CLI should:

1. Wait briefly (e.g., 500ms) for the process to initialize
2. Poll the gateway's health endpoint (`GET /health` or similar) up to ~10 seconds
3. If healthy → print success with PID
4. If process exited → print error, clean up PID file
5. If timeout → print warning ("Gateway started but health check timed out, check logs")

### Command Flow

#### `botnexus gateway start`

```
1. Check PID file — if exists and process alive, print "Gateway already running (PID X)" and exit
2. Resolve gateway executable path
3. Spawn detached process
4. Write PID file
5. Run startup health check
6. Print result
```

#### `botnexus gateway stop`

```
1. Read PID file — if missing, print "Gateway is not running" and exit
2. Get process by PID — if dead, clean up PID file, print "Gateway is not running (cleaned stale PID)"
3. Send graceful shutdown signal
4. Wait up to 10s for exit
5. If still alive, force kill
6. Delete PID file
7. Print result
```

#### `botnexus gateway status`

```
1. Read PID file — if missing, print "Gateway is not running"
2. Check if process alive — if dead, clean up PID file, print "Gateway is not running"
3. Optionally hit health endpoint for richer status
4. Print "Gateway is running (PID X, uptime Y)"
```

## Files to Change

### CLI Layer
- `src/cli/BotNexus.Cli/Commands/GatewayCommand.cs` (or equivalent) — refactor `start` to spawn detached, add PID file management
- `src/cli/BotNexus.Cli/Services/GatewayProcessManager.cs` (new) — encapsulate PID file read/write, process spawn, health check, stale detection

### Gateway Layer
- Verify Serilog file sink is configured and works when gateway runs without a parent console
- Consider adding a `--daemon` flag to suppress interactive prompts or banner output

### Cross-Cutting
- `~/.botnexus/gateway.pid` — new runtime artifact (should be in `.gitignore` if applicable)

## Risks and Open Questions

1. **Cross-platform support** — `UseShellExecute = true` behaves differently on Linux/macOS. If BotNexus needs to support non-Windows platforms, the spawning logic needs platform-specific branches (e.g., `nohup` + `&` on Linux).
2. **Multiple gateway instances** — should the PID file approach support multiple gateways (e.g., different ports)? For now, single-instance is sufficient.
3. **Windows service** — long-term, the gateway could be a Windows Service (`sc.exe`) instead of a detached console process. This spec covers the simpler detached-process approach; a service-based approach could be a follow-up.
4. **Signal handling** — .NET on Windows doesn't support POSIX signals natively. Graceful shutdown may need to use `Process.CloseMainWindow()` or a named pipe/event for signaling.

## Success Criteria

- [x] `botnexus gateway start` launches gateway in a separate console window and returns control immediately
- [x] PID file written to `~/.botnexus/gateway.pid`
- [x] `botnexus gateway status` correctly reports running/stopped state
- [x] `botnexus gateway stop` gracefully stops the gateway and cleans up PID file
- [x] `botnexus gateway restart` works end-to-end
- [x] Stale PID files are detected and cleaned up
- [ ] Gateway logs continue writing to `~/.botnexus/logs/` when running detached
