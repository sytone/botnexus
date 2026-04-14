---
id: bug-exec-process-disconnect
title: "ExecTool/ProcessTool Disconnect - Research"
type: research
created: 2026-07-18
author: nova
---

# Research: ExecTool and ProcessTool State Disconnect

## Code Evidence

### ExecTool Background Process Tracking

From `extensions/tools/exec/BotNexus.Extensions.ExecTool/ExecTool.cs`:

```csharp
private static readonly ConcurrentDictionary<int, ProcessInfo> BackgroundProcesses = new();
```

On background launch:
```csharp
BackgroundProcesses[pid] = new ProcessInfo(pid, command[0], DateTime.UtcNow);
```

`ProcessInfo` is a simple data record:
```csharp
internal sealed record ProcessInfo(int Pid, string Command, DateTime StartedUtc);
```

Note: `BackgroundProcesses` is only ever written to — never read by any other code. It is dead state.

### ProcessTool Process Registry

From `extensions/tools/process/BotNexus.Extensions.ProcessTool/ProcessTool.cs`:

```csharp
private readonly ProcessManager _manager;
public ProcessTool() : this(ProcessManager.Instance) { }
```

All operations go through ProcessManager:
```csharp
var process = _manager.Get(pid.Value);  // Always returns null for exec-launched processes
```

### ProcessManager Registry

From `extensions/tools/process/BotNexus.Extensions.ProcessTool/ProcessManager.cs`:

```csharp
public static ProcessManager Instance { get; } = new();
private readonly ConcurrentDictionary<int, ManagedProcess> _processes = new();
```

`Register()` is public but never called by ExecTool.

### ManagedProcess Capabilities

From `extensions/tools/process/BotNexus.Extensions.ProcessTool/ManagedProcess.cs`:

ManagedProcess wraps a live Process handle and provides:
- Output capture (circular buffer, 100KB max)
- `GetOutput(tailLines)` — read captured output
- `WriteInput(content)` — write to stdin
- `Kill()` — graceful then force kill
- `WaitForExit(ms)` — timeout-based wait
- `IsRunning` / `ExitCode` — status queries

Constructor is `internal`:
```csharp
internal ManagedProcess(Process process, string command, DateTimeOffset startedAt)
```

This would need to be `public` for ExecTool to create instances (or use `InternalsVisibleTo`).

### Extension Assembly Structure

```
extensions/
  tools/
    exec/
      BotNexus.Extensions.ExecTool/
        ExecTool.cs              <- Has its own process tracking
    process/
      BotNexus.Extensions.ProcessTool/
        ProcessTool.cs           <- Queries ProcessManager
        ProcessManager.cs        <- Singleton registry, never populated
        ManagedProcess.cs        <- Full process wrapper, never instantiated
```

These are separate assemblies with no project references between them.

### Process Handle Lifecycle Bug

ExecTool uses `using var process`:

```csharp
using var process = new Process { StartInfo = startInfo };
if (!process.Start()) { ... }

if (background)
{
    var pid = process.Id;
    BackgroundProcesses[pid] = new ProcessInfo(pid, command[0], DateTime.UtcNow);
    // writes stdin if provided
    var result = JsonSerializer.Serialize(new { pid, status = "running" });
    return new AgentToolResult(...);
}
```

When the method returns for background mode, the `using` disposes the Process handle. The OS process continues running, but:
- Stdout/stderr streams are closed on the .NET side
- No way to read further output
- No way to write to stdin
- `Process.Kill()` no longer available (disposed)
- The PID exists but is unmanageable

## Agent Experience

What the agent sees:

1. `exec(command: ["node", "server.js"], background: true)` → `{"pid": 5432, "status": "running"}`
2. `process(action: "list")` → "No tracked processes."
3. `process(action: "output", pid: 5432)` → "No tracked process with PID 5432."
4. `process(action: "kill", pid: 5432)` → "No tracked process with PID 5432."

The agent is told a process is running but has no way to interact with it. The only recourse is `bash("kill 5432")` which is a workaround, not a solution.

## Cross-Extension Dependency Options

| Approach | Pros | Cons |
|----------|------|------|
| ExecTool references ProcessTool assembly | Simple, direct | Tight coupling between extensions |
| Shared abstractions package | Clean separation | New project, more build complexity |
| Merge into single extension | No cross-refs | Larger single assembly |
| Interface in AgentCore | Most extensible | Adds to core surface area |
| Static ProcessManager in shared location | Minimal change | Static singletons are harder to test |

ProcessManager is already a static singleton (`ProcessManager.Instance`), so the simplest fix is making ExecTool reference ProcessTool and call `ProcessManager.Instance.Register()`. The architectural purity of a shared interface can come later.

## bash vs exec: Redundancy Note

Both tools exist and are registered simultaneously. `exec` is a strict superset of `bash`:

| Feature | bash (ShellTool) | exec (ExecTool) |
|---------|------------------|-----------------|
| Simple command string | Yes | No (array only) |
| Background mode | No | Yes |
| Stdin piping | No | Yes |
| No-output timeout | No | Yes |
| Env var merging | No | Yes |
| Working dir override | No | Yes |
| Windows cmd resolution | Git Bash → PowerShell | .cmd/.bat via cmd.exe |
| Output cap | 50KB | 100KB |

Consider whether both should remain or if `bash` should be deprecated in favor of `exec` with a convenience string mode. Separate discussion from this bug.
