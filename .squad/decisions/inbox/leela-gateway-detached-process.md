# Leela Design Review: Gateway Detached Process (2026-07-28)

**Decision Date:** 2026-07-28  
**Decided By:** Leela (Lead/Architect)  
**Status:** Approved — Ready for Implementation

**Context:** `botnexus serve gateway` blocks the console. Users want the gateway to run in its own window with CLI control (`start`, `stop`, `status`, `restart`).

## Architectural Decisions

### 1. `IGatewayProcessManager` Interface Location

**Decision:** Lives in `src/gateway/BotNexus.Cli/Services/`  
**Rationale:** Process management is a CLI concern, not a gateway runtime concern. The gateway itself doesn't need to know how it was spawned.

### 2. PID File Format

**Decision:** Plain text file at `~/.botnexus/gateway.pid` containing only the integer PID  
**Rationale:** Simplicity. No JSON overhead. Uptime derivable from process start time.

### 3. Default Mode

**Decision:** Detached mode is the default; `--attached` flag for foreground debugging  
**Rationale:** Matches user expectation that a "start" command returns control. Developers needing logs use `--attached`.

### 4. Windows-Only for v1

**Decision:** Guard with `OperatingSystem.IsWindows()`, print helpful error on other platforms  
**Rationale:** `UseShellExecute = true` + new console window is Windows-specific. Cross-platform (nohup/setsid) is future work.

### 5. Hard Kill for Stop

**Decision:** Use `Process.Kill()` without attempting graceful shutdown via `CloseMainWindow()`  
**Rationale:** Console apps on Windows don't respond to `CloseMainWindow()`. Named event signaling is over-engineered for v1.

### 6. Health Check Timeout

**Decision:** 10 seconds, exponential backoff starting at 200ms  
**Rationale:** Matches spec. Fast-fail on process exit; warn (not fail) on timeout.

### 7. Stale PID Cleanup

**Decision:** Automatic on any PID file read  
**Rationale:** No separate "clean" command needed. Commands self-heal.

## Contracts

**New Interface:** `IGatewayProcessManager` with `StartAsync`, `StopAsync`, `GetStatusAsync`, `IsRunning`  
**Supporting Types:** `GatewayStartOptions`, `GatewayStartResult`, `GatewayStopResult`, `GatewayStatus`  
**Internal Contract:** `IHealthChecker.WaitForHealthyAsync`

## Wave Plan

| Wave | Agent | Deliverable | Depends On |
|------|-------|-------------|------------|
| 1 | Bender | Core process manager + health checker | None |
| 2 | Farnsworth | CLI command refactor + DI registration | Wave 1 |
| 3 | Hermes | Unit + integration tests (18-24 tests) | Wave 1 interfaces |
| 4 | Kif | Documentation + spec archive | Wave 2 |

**Parallelization:** Waves 3 and 2 can run concurrently (Hermes starts with interface definitions).

## Risks Accepted

1. **Hard kill only** — No graceful shutdown signal for v1 (acceptable)
2. **Windows-only** — Cross-platform is future work
3. **PID recycling** — Mitigated by process name check

## Files to Create

- `src/gateway/BotNexus.Cli/Services/IGatewayProcessManager.cs`
- `src/gateway/BotNexus.Cli/Services/GatewayProcessTypes.cs`
- `src/gateway/BotNexus.Cli/Services/GatewayProcessManager.cs`
- `src/gateway/BotNexus.Cli/Services/IHealthChecker.cs`
- `src/gateway/BotNexus.Cli/Services/HttpHealthChecker.cs`
- `src/gateway/BotNexus.Cli/Commands/GatewaySubcommands.cs`
- `tests/BotNexus.Cli.Tests/Services/GatewayProcessManagerTests.cs`
- `tests/BotNexus.Cli.Tests/Services/HttpHealthCheckerTests.cs`
- `tests/BotNexus.Cli.Tests/Commands/GatewayCommandTests.cs`

## Files to Modify

- `src/gateway/BotNexus.Cli/Commands/ServeCommand.cs`
- `src/gateway/BotNexus.Cli/Program.cs`
- `src/gateway/README.md`
- `src/gateway/BotNexus.Cli/README.md`
