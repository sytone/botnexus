---
id: feature-file-watcher-tool
title: "File Watcher Tool"
type: feature
priority: medium
status: draft
created: 2026-07-19
updated: 2026-07-19
author: leela
tags: [tool, agent-loop, file-system, reactive]
---

# Design Spec: File Watcher Tool

**Type**: Feature
**Priority**: Medium
**Status**: Draft
**Author**: Leela (via Jon)

## Overview

Agents need the ability to watch a file and resume when it changes — enabling
reactive workflows like "give me feedback every time I save." The `watch_file`
tool is a blocking `IAgentTool` that waits for a filesystem event (or timeout),
then returns so the agent can act on the change.

This is the event-driven counterpart to `delay` — instead of waiting for a
fixed duration, the agent waits for a file modification.

### User Stories

| # | Story | Behavior |
|---|-------|----------|
| 1 | "Watch this file and give me feedback when I save" | Agent calls `watch_file(path)`, blocks until modified, reads file, gives feedback. |
| 2 | "Review my doc every time I save" | Agent loops: `watch_file` → read → review → `watch_file` again. |
| 3 | "Tell me when the build output appears" | Agent calls `watch_file(path, event: "created")`, blocks until file is created. |

## Tool Definition

### Name & Labels

| Property | Value |
|----------|-------|
| `Name` | `watch_file` |
| `Label` | `Watch File` |
| Description | Watch a file for changes and resume when it is modified, created, or deleted. |

### Parameters (JSON Schema)

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "Path to the file to watch (absolute or relative to workspace)."
    },
    "timeout": {
      "type": "integer",
      "description": "Maximum seconds to wait before returning a timeout result.",
      "minimum": 1,
      "maximum": 1800,
      "default": 300
    },
    "event": {
      "type": "string",
      "enum": ["modified", "created", "deleted", "any"],
      "description": "What filesystem event to watch for.",
      "default": "modified"
    }
  },
  "required": ["path"]
}
```

### Return Value

On file event:

```json
{
  "content": [{ "type": "text", "text": "File modified: /workspace/doc.md (after 12 seconds)." }]
}
```

On timeout:

```json
{
  "content": [{ "type": "text", "text": "Timeout after 300 seconds — no change detected on /workspace/doc.md." }]
}
```

On cancellation (steering/abort):

```json
{
  "content": [{ "type": "text", "text": "Watch cancelled after 8 seconds. No change detected." }]
}
```

## Implementation Details

### Class: `FileWatcherTool`

```
Namespace:  BotNexus.Gateway.Tools
File:       src/gateway/BotNexus.Gateway/Tools/FileWatcherTool.cs
Implements: IAgentTool
```

Core approach: `FileSystemWatcher` + `TaskCompletionSource<string>` + linked
cancellation token for timeout.

```csharp
// Pseudocode for ExecuteAsync:
var dir = Path.GetDirectoryName(fullPath);
var file = Path.GetFileName(fullPath);
using var watcher = new FileSystemWatcher(dir!, file);
// Configure NotifyFilter based on event type
watcher.EnableRaisingEvents = true;

var tcs = new TaskCompletionSource<string>();
Timer? debounceTimer = null;

watcher.Changed += (s, e) => {
    debounceTimer?.Dispose();
    debounceTimer = new Timer(_ => tcs.TrySetResult("modified"), null, 500, Timeout.Infinite);
};

using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult("timeout"));

var result = await tcs.Task;
debounceTimer?.Dispose();
```

Key design decisions:

1. **`FileSystemWatcher`** — OS-native file events, no polling.
2. **Debounce (500ms)** — rapid saves (editor auto-save, format-on-save) produce
   one event, not a burst.
3. **Linked cancellation** — combines the tool's cancellation token (steering/abort)
   with a timeout CTS. Both paths complete the TCS gracefully.
4. **Graceful results, not exceptions** — same pattern as `DelayTool`. Timeout and
   cancellation return informational text so the agent can decide what to do next.
5. **Dispose in finally** — `FileSystemWatcher` and debounce timer are always cleaned up.

### Registration

Same as `DelayTool` — registered in `InProcessIsolationStrategy.BuildToolSet`
so all agents get it:

```csharp
var fileWatcherOptions = _serviceProvider.GetService<IOptions<FileWatcherToolOptions>>()
    ?? Options.Create(new FileWatcherToolOptions());
tools.Add(new FileWatcherTool(fileWatcherOptions));
```

Options bound from config in `GatewayServiceCollectionExtensions`:

```csharp
services.AddOptions<FileWatcherToolOptions>();
services.Configure<FileWatcherToolOptions>(config.GetSection("gateway:fileWatcherTool"));
```

## Security

- **Path validation**: Resolve to absolute path, verify it is under the agent's
  allowed workspace directory. Reject paths outside workspace with a clear error.
- **No system directories**: Paths like `C:\Windows`, `/etc`, `/proc` are blocked
  by the workspace check.
- **No directory watching**: Only single-file watches are supported.

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| **File doesn't exist yet** | If `event` is `"created"` or `"any"`, watch the parent directory for creation. Otherwise return an error. |
| **File deleted during watch** | If watching for `"modified"`, return `"deleted"` event so the agent knows. |
| **Rapid saves** | Debounce (500ms quiet period) coalesces into a single event. |
| **Timeout expires** | Return informational timeout result — not an exception. |
| **User steers/aborts** | Cancellation token fires, TCS completes, returns cancellation result. |
| **Parent directory doesn't exist** | Return error result (cannot watch non-existent directory). |
| **Path outside workspace** | `PrepareArgumentsAsync` throws `ArgumentException`. |
| **Timeout > MaxTimeoutSeconds** | Clamped to max (same pattern as DelayTool). |
| **Timeout < 1** | Clamped to 1. |

## Configuration

### `FileWatcherToolOptions`

```csharp
public sealed class FileWatcherToolOptions
{
    public int MaxTimeoutSeconds { get; set; } = 1800;     // 30 minutes
    public int DefaultTimeoutSeconds { get; set; } = 300;  // 5 minutes
    public int DebounceMilliseconds { get; set; } = 500;
}
```

### Config JSON

```json
{
  "gateway": {
    "fileWatcherTool": {
      "maxTimeoutSeconds": 1800,
      "defaultTimeoutSeconds": 300,
      "debounceMilliseconds": 500
    }
  }
}
```

## Testing Plan

### Unit Tests

| Test | Description |
|------|-------------|
| `WatchFile_DetectsModification` | Modify file during watch, assert returns "modified". |
| `WatchFile_DetectsCreation` | Create file during watch with `event: "created"`, assert returns "created". |
| `WatchFile_DetectsDeletion` | Delete file during watch with `event: "deleted"`, assert returns "deleted". |
| `WatchFile_TimeoutReturnsGracefully` | Set short timeout, don't modify, assert timeout result. |
| `WatchFile_CancellationReturnsEarly` | Cancel token, assert cancellation result. |
| `WatchFile_DebounceCoalesces` | Rapid modifications produce single event. |
| `WatchFile_PathOutsideWorkspaceThrows` | Path validation rejects `../../etc/passwd`. |
| `WatchFile_MissingPathThrows` | Omit `path` → `ArgumentException`. |
| `WatchFile_ClampsTimeout` | Request timeout > max, assert clamped. |

### Location

```
tests/BotNexus.Gateway.Tests/Tools/FileWatcherToolTests.cs
```

## Work Breakdown

| Phase | Task | Est. |
|-------|------|------|
| 1 | `FileWatcherToolOptions` + config binding | 0.25 d |
| 2 | `FileWatcherTool` implementation | 1 d |
| 3 | Registration in DI + isolation strategy | 0.25 d |
| 4 | Unit tests | 1 d |
| 5 | Integration tests | 0.5 d |
| 6 | Documentation update | 0.25 d |
| **Total** | | **~3.25 d** |
