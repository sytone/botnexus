# Gateway Crash Diagnostics

When the gateway process dies, BotNexus now leaves behind enough evidence to root-cause
the failure — even for a **silent hard exit** (stack overflow, `Environment.FailFast`,
native abort) that raises no catchable managed exception and produces no WER dump under the
default process configuration.

This is the observability layer added for issue #1901.

## What gets captured

There are three independent, layered artifacts. Any one of them may be enough to explain a
death; together they cover the full spectrum from graceful stop to silent hard kill.

### 1. Minidump on hard exit

The CLI launcher sets the .NET runtime crash-dump environment variables **on the gateway
child process** before it starts:

| Variable | Value | Meaning |
|---|---|---|
| `DOTNET_DbgEnableMiniDump` | `1` | Enable the runtime minidump writer. |
| `DOTNET_DbgMiniDumpType` | `2` | Heap dump (managed objects + threads, without a full-memory dump's size). |
| `DOTNET_DbgMiniDumpName` | `<home>/dumps/botnexus-gateway.%d.dmp` | Dump path; `%d` is replaced with the PID so crashes never overwrite each other. |

These variables are only honoured by the CLR when present **at process startup**, which is
why they are applied by the launcher (`GatewayProcessManager` for detached start,
`ServeCommand` / `GatewayCommand` for foreground dev mode) rather than from inside the
already-running gateway. Because the runtime writes the dump, it fires even for a stack
overflow or `FailFast` — the exact cases that leave no managed exception.

**Where dumps land:** `~/.botnexus/dumps/` (or `<BOTNEXUS_HOME>/dumps/`). The directory is
created automatically on gateway start.

### 2. Last-chance fault breadcrumb (`[FTL]`)

An in-process last-chance handler is installed at startup and hooks:

- `AppDomain.CurrentDomain.UnhandledException`
- `TaskScheduler.UnobservedTaskException`
- `AppDomain.CurrentDomain.ProcessExit`

The instant the process is about to die, it flushes a single-line structured record to the
log so that even a **dump-less** exit leaves a trail:

```
[FTL] gateway fault breadcrumb reason=UnhandledException exitCode=134 agents=5 sessions=unknown threads=88 ws=1.0 GB terminating=true detail=System.StackOverflowException: ...
```

Fields: `reason`, `exitCode`, active `agents`/`sessions` counts (when the registry/session
store are still reachable), managed `threads` count, process working set (`ws`), whether the
runtime reported it is `terminating`, and a whitespace-collapsed `detail` (exception type +
message). The record is deliberately a single line so log parsers cannot split it.

> A clean `ProcessExit` (graceful shutdown) is logged at *information* level, not as a
> fatal `[FTL]` — only genuine fault paths claim fatality.

### 3. Unclean-termination warning on next boot

The gateway maintains a clean-shutdown marker file at
`~/.botnexus/.gateway-clean-shutdown`:

- **On graceful shutdown** (`IHostApplicationLifetime.ApplicationStopped`) it writes the
  marker containing the shutdown timestamp.
- **On boot** it reads the marker to learn how the previous run ended, then immediately
  clears it for the current run. If the marker is **absent**, the previous run did not reach
  graceful shutdown — a silent death — and the gateway logs:

```
[WRN] previous gateway run terminated uncleanly (last-known clean-shutdown timestamp: <timestamp-or-unknown>)
```

This makes a silent death visible in the log directly, without cross-referencing wall-clock
gaps between log files.

## How to read a dump

1. Locate the dump under `~/.botnexus/dumps/` (newest `botnexus-gateway.<pid>.dmp`).
2. Open it with `dotnet-dump`:
   ```shell
   dotnet tool install -g dotnet-dump   # once
   dotnet-dump analyze ~/.botnexus/dumps/botnexus-gateway.<pid>.dmp
   ```
3. Useful SOS commands inside the analyzer session:
   - `clrstack -all` — managed stacks for every thread (find a runaway recursion → stack overflow).
   - `threads` / `clrthreads` — thread count and state (thread-pool starvation).
   - `dumpheap -stat` — managed heap by type (memory blow-up).
   - `pe` — print the current exception on the faulting thread.
4. Cross-reference the dump's PID and timestamp with the `[FTL]` breadcrumb and the
   `previous gateway run terminated uncleanly` warning in
   `~/.botnexus/logs/botnexus-*.log`.

## Files

| Concern | Location |
|---|---|
| Crash-dump env var builder | `src/gateway/BotNexus.Gateway/Diagnostics/CrashDumpEnvironment.cs` |
| Fault breadcrumb model + formatter | `src/gateway/BotNexus.Gateway/Diagnostics/FaultBreadcrumbFormatter.cs` |
| Last-chance fault handler | `src/gateway/BotNexus.Gateway/Diagnostics/LastChanceFaultHandler.cs` |
| Clean-shutdown marker | `src/gateway/BotNexus.Gateway/Diagnostics/CleanShutdownMarker.cs` |
| Host wiring | `src/gateway/BotNexus.Gateway.Api/Program.cs` (`InstallCrashObservability`) |
| Launcher env-var injection | `src/gateway/BotNexus.Cli/Services/GatewayProcessManager.cs`, `src/gateway/BotNexus.Cli/Commands/ServeCommand.cs` |

All wiring is strictly additive and fully defensive: any failure in the diagnostics path is
swallowed so it can never prevent the gateway from starting or serving.
