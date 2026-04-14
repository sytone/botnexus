---
id: bug-exec-process-disconnect
title: "Background Processes Launched by ExecTool Are Unmanageable"
type: bug
priority: high
status: ready
created: 2026-07-18
updated: 2026-07-18
author: nova
tags: [tools, extensions, exec, process, background-processes]
ddd_types: [IAgentTool, ExecTool, ProcessTool, IProcessRegistry]
---

# Bug: Background Processes Launched by ExecTool Are Unmanageable

**Type**: Bug
**Priority**: High
**Status**: Ready
**Author**: Nova

## Problem

When ExecTool launches a background process, the process is unmanageable:

1. The .NET `Process` handle is disposed immediately (via `using var`) — stdout/stderr, stdin, and kill are all lost
2. The PID is recorded in a static dictionary internal to ExecTool that nothing else reads — dead state
3. ProcessTool queries `ProcessManager.Instance` which is never populated by anything

Each tool works in isolation for its own concerns, but there is no shared infrastructure for background process lifecycle.

**Steps to reproduce:**
1. Agent calls `exec` with `background: true` — gets `{"pid": 12345, "status": "running"}`
2. Agent calls `process` with `action: "list"` — returns "No tracked processes"
3. The OS process runs but the agent cannot monitor, read output, send input, or kill it

## Root Cause

Two independent issues:

### 1. ExecTool Disposes the Process Handle

```csharp
using var process = new Process { StartInfo = startInfo };
// ...
if (background)
{
    BackgroundProcesses[pid] = new ProcessInfo(pid, command[0], DateTime.UtcNow);
    return result; // 'using' disposes Process here
}
```

The `using` disposes the Process on return. The OS process keeps running but the .NET handle is gone — no way to read output, write stdin, or kill gracefully.

### 2. No Shared Process Registry

ExecTool has its own `ConcurrentDictionary<int, ProcessInfo>` (write-only, never read).
ProcessTool has `ProcessManager` with `ConcurrentDictionary<int, ManagedProcess>` (never populated).
These are completely disconnected.

## Design Principles

- **exec and process are independent tools** — exec works without process, process works without exec
- **exec** is a *producer* of background processes
- **process** is a *consumer/manager* of background processes
- They share infrastructure (a process registry), not a dependency on each other
- Future tools could also produce or consume background processes through the same registry

## Solution

### Shared Process Registry Abstraction

Introduce `IProcessRegistry` and `IBackgroundProcess` in a shared location (AgentCore or a lightweight shared package):

```csharp
public interface IProcessRegistry
{
    void Register(int pid, IBackgroundProcess process);
    IBackgroundProcess? Get(int pid);
    IReadOnlyList<BackgroundProcessInfo> List();
    bool Remove(int pid);
}

public interface IBackgroundProcess : IDisposable
{
    int Pid { get; }
    string Command { get; }
    bool IsRunning { get; }
    int? ExitCode { get; }
    DateTimeOffset StartedAt { get; }
    string GetOutput(int? tailLines = null);
    void WriteInput(string content);
    void Kill();
    bool WaitForExit(int milliseconds);
}
```

### ExecTool Changes

1. Accept `IProcessRegistry` (optional — exec works without it, just loses manageability)
2. For background mode: wrap Process in an `IBackgroundProcess` implementation, register it, do NOT dispose
3. For foreground mode: no change (keep `using var`)
4. Remove internal `BackgroundProcesses` dictionary

### ProcessTool Changes

1. Accept `IProcessRegistry` instead of `ProcessManager.Instance`
2. All operations delegate to the registry
3. `ProcessManager` either becomes the `IProcessRegistry` implementation or is replaced by it

### ManagedProcess

Already implements the right capabilities (output capture, stdin, kill, wait). Needs to:
1. Implement `IBackgroundProcess`
2. Constructor changed from `internal` to `public`
3. Lives wherever makes sense — could stay in ProcessTool extension or move to the shared package

### Registration in DI

```csharp
// Single IProcessRegistry instance shared across all tools
services.AddSingleton<IProcessRegistry, ProcessManager>();
```

Extension loader passes `IProcessRegistry` to tools that request it via constructor injection.

## Impact

- **Shared abstraction**: 2 interfaces + 1 record in AgentCore or shared package (~30 lines)
- **ExecTool**: Remove internal dict, accept optional registry, wrap Process for background mode
- **ProcessTool**: Accept registry interface instead of static singleton
- **ManagedProcess**: Implement IBackgroundProcess, make constructor public
- **Risk**: Low — foreground exec unchanged, background exec gains proper lifecycle
- **Testing**: Launch background via exec → list/output/input/kill via process
