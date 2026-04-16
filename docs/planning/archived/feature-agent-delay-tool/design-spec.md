---
id: feature-agent-delay-tool
title: "Agent Delay/Wait Tool"
type: feature
priority: medium
status: draft
created: 2026-07-17
updated: 2026-07-17
author: leela
tags: [tool, agent-loop, async, scheduling]
---

# Design Spec: Agent Delay/Wait Tool

**Type**: Feature
**Priority**: Medium
**Status**: Draft
**Author**: Leela (via Jon)

## Overview

Agents need the ability to pause mid-session for a specified duration, then
resume their work. This is a simple, in-session delay — not a scheduled job or
cron trigger.

The delay tool is a standard `IAgentTool` implementation. When an agent calls
it, `ExecuteAsync` awaits an async delay (backed by `Task.Delay`) using the
existing `CancellationToken` pipeline. The agent loop pauses at the tool-call
step; no threads are held, no new sessions are created, and no external
scheduler is involved.

### User Stories

| # | Story | Delay |
|---|-------|-------|
| 1 | "Wait 5 minutes then do X" — agent delays, then performs the action. | 300 s |
| 2 | "Review this document every few minutes" — agent reads, gives feedback, delays, loops. | 180 s |
| 3 | "Check the build status in 30 seconds" — agent delays, then checks. | 30 s |

## Tool Definition

### Name & Labels

| Property | Value |
|----------|-------|
| `Name` | `delay` |
| `Label` | `Delay / Wait` |
| Description | Pause execution for a specified number of seconds, then continue. |

### Parameters (JSON Schema)

```json
{
  "type": "object",
  "properties": {
    "seconds": {
      "type": "integer",
      "description": "Number of seconds to wait. Must be between 1 and the configured maximum (default 1800).",
      "minimum": 1,
      "maximum": 1800
    },
    "reason": {
      "type": "string",
      "description": "Optional human-readable reason for the delay (logged and surfaced in UI)."
    }
  },
  "required": ["seconds"]
}
```

### Return Value

On successful completion the tool returns a text result:

```json
{
  "content": [
    {
      "type": "text",
      "text": "Waited 30 seconds. Resuming."
    }
  ]
}
```

If the delay is cancelled (user message, steering, or abort), the tool returns
an informational result — **not** an exception — so the agent can react:

```json
{
  "content": [
    {
      "type": "text",
      "text": "Delay cancelled after 12 of 30 seconds (reason: user sent a message). Resuming."
    }
  ]
}
```

### Prompt Snippet

```
delay — Pause execution for N seconds before continuing. Use when told to wait, poll, or check back later.
```

## Implementation Details

### Class: `DelayTool`

```
Namespace:  BotNexus.Gateway.Tools
File:       src/gateway/BotNexus.Gateway/Tools/DelayTool.cs
Implements: IAgentTool
```

The implementation is intentionally minimal — the core of `ExecuteAsync` is a
single `Task.Delay` call:

```csharp
public async Task<AgentToolResult> ExecuteAsync(
    string toolCallId,
    IReadOnlyDictionary<string, object?> arguments,
    CancellationToken cancellationToken = default,
    AgentToolUpdateCallback? onUpdate = null)
{
    var seconds = ReadInt(arguments, "seconds");
    var reason  = ReadString(arguments, "reason");
    var clamped = Math.Clamp(seconds, 1, _maxDelaySeconds);
    var delay   = TimeSpan.FromSeconds(clamped);

    var sw = Stopwatch.StartNew();

    // Surface progress to the UI while waiting
    onUpdate?.Invoke(new AgentToolUpdate(
        toolCallId,
        $"Waiting {clamped}s{(reason is not null ? $" — {reason}" : "")}…"));

    try
    {
        await Task.Delay(delay, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        var elapsed = (int)sw.Elapsed.TotalSeconds;
        return TextResult(
            $"Delay cancelled after {elapsed} of {clamped} seconds. Resuming.");
    }

    return TextResult($"Waited {clamped} seconds. Resuming.");
}
```

Key design decisions:

1. **`Task.Delay`, not `Thread.Sleep`** — zero thread cost during the wait.
2. **Uses the existing `CancellationToken`** that the agent loop already
   propagates through every tool call. No new cancellation plumbing is needed.
3. **Catches `OperationCanceledException` gracefully** — returns a descriptive
   text result instead of bubbling an error, so the agent can decide what to do
   next (e.g., answer the user's new message).
4. **`onUpdate` callback** — pushes a status line to the UI so the user can see
   the delay is in progress.
5. **Clamped, not rejected** — if the model asks for a delay larger than the
   configured max, we clamp silently to avoid a retry loop.

### Registration

`DelayTool` is a workspace-scoped tool like the file I/O tools. It is created
inside `DefaultAgentToolFactory.CreateTools`:

```csharp
// In DefaultAgentToolFactory.CreateTools
new DelayTool(maxDelaySeconds: options.MaxDelaySeconds)
```

The factory reads `MaxDelaySeconds` from `BotNexusOptions` (see Configuration
below). The tool requires no filesystem access, so its constructor only takes
the max-delay config value.

### Integration with the Agent Loop

No changes to the agent loop are required. The loop already:

1. Calls `ExecuteAsync` for each tool call.
2. Passes a `CancellationToken` that is cancelled on abort or steering.
3. Awaits the `Task<AgentToolResult>` — so the loop naturally pauses while the
   delay is in flight.
4. On cancellation, catches the error result and feeds it back to the model.

The delay tool is just another awaitable tool execution from the loop's
perspective.

### Cancellation Scenarios

| Trigger | Mechanism | Delay Tool Behavior |
|---------|-----------|---------------------|
| User sends a new message | `SteerAsync` cancels the token | `OperationCanceledException` caught → returns informational result |
| User aborts | `AbortAsync` cancels the token | Same as above |
| Session timeout | Gateway disposes the handle, token is cancelled | Same as above |
| Sub-agent completes | Does not cancel the parent token | Delay runs to completion; sub-agent result is queued |
| Process shutdown | Host cancellation propagates | `OperationCanceledException` — session is torn down normally |

In all cancellation cases, the delay tool returns a friendly text result so the
model can adjust its plan.

## Configuration

### Global Default

Added to the existing `BotNexus:Tools` configuration section:

```json
{
  "BotNexus": {
    "Tools": {
      "Delay": {
        "MaxSeconds": 1800,
        "Enabled": true
      }
    }
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `MaxSeconds` | `int` | `1800` (30 min) | Maximum delay any agent can request. Clamped, not rejected. |
| `Enabled` | `bool` | `true` | Set `false` to remove the tool from all agents. |

### Per-Agent Override

Agents already support a `tools` allow/deny list in their config. The delay
tool respects the same mechanism:

```json
{
  "BotNexus": {
    "Agents": {
      "my-agent": {
        "Tools": {
          "Deny": ["delay"]
        }
      }
    }
  }
}
```

A per-agent `MaxSeconds` override could be added later if needed; for now the
global cap is sufficient.

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| **`seconds` < 1** | Clamped to 1. |
| **`seconds` > MaxSeconds** | Clamped to MaxSeconds. |
| **`seconds` is not an integer** | `PrepareArgumentsAsync` throws `ArgumentException` → error result to model. |
| **Missing `seconds`** | `PrepareArgumentsAsync` throws `ArgumentException`. |
| **User sends message during delay** | Steering cancels the token. Delay returns early with elapsed time. Agent processes the user message. |
| **User aborts during delay** | Abort cancels the token. Delay returns early. Agent loop terminates. |
| **Session idle timeout fires during delay** | Handle is disposed, token cancelled. Delay returns early. |
| **Multiple delays in parallel** (model calls delay twice) | Both execute concurrently via `Task.Delay`. Both respect the same cancellation token. Unusual but harmless. |
| **Sub-agent finishes while parent is delayed** | Sub-agent result is queued via `FollowUpAsync`. Parent delay is **not** interrupted — the result is delivered after the delay completes or after cancellation. |
| **Delay tool disabled globally** | Tool is not registered. Model cannot call it. |
| **Delay of 0** | Clamped to 1 second — prevents a no-op tool call that wastes a model round-trip. |

## Testing Plan

### Unit Tests

| Test | Description |
|------|-------------|
| `DelayTool_WaitsRequestedDuration` | Pass `seconds: 2`, assert elapsed ≥ 2 s. |
| `DelayTool_ClampsToMax` | Configure max = 5, request 60, assert waited ≤ 5 s. |
| `DelayTool_ClampsMinimumTo1` | Request 0, assert waited ≥ 1 s. |
| `DelayTool_CancellationReturnsEarly` | Cancel token after 500 ms on a 10 s delay. Assert result text contains "cancelled". |
| `DelayTool_ReturnsElapsedOnCancel` | Cancel at ~2 s of a 10 s delay. Assert result contains "2 of 10". |
| `DelayTool_MissingSecondsThrows` | Omit `seconds` → `ArgumentException`. |
| `DelayTool_InvalidSecondsThrows` | Pass `seconds: "abc"` → `ArgumentException`. |
| `DelayTool_ReasonIncludedInUpdate` | Pass `reason`, assert `onUpdate` callback receives it. |
| `DelayTool_DefinitionMatchesSchema` | Assert `Definition` JSON schema matches expected shape. |

### Integration Tests

| Test | Description |
|------|-------------|
| `AgentLoop_DelayTool_PausesAndResumes` | Send a prompt that triggers a delay call, verify the agent resumes and produces output after the delay. |
| `AgentLoop_DelayTool_CancelledBySteering` | Start a delay, send a steering message, verify the agent processes the steering message. |
| `AgentLoop_DelayTool_CancelledByAbort` | Start a delay, call `AbortAsync`, verify graceful teardown. |
| `AgentLoop_DelayTool_DisabledConfig` | Set `Enabled: false`, verify tool is not in the tool list. |

### Location

```
tests/gateway/BotNexus.Gateway.Tests/Tools/DelayToolTests.cs
tests/gateway/BotNexus.Gateway.IntegrationTests/Tools/DelayToolIntegrationTests.cs
```

## Work Breakdown

| Phase | Task | Owner | Est. |
|-------|------|-------|------|
| 1 | Implement `DelayTool` class | Dev (Kara/Remy) | 0.5 d |
| 2 | Register in `DefaultAgentToolFactory` | Dev (Kara/Remy) | 0.5 d |
| 3 | Add `Delay` config section + `BotNexusOptions` binding | Dev (Kara/Remy) | 0.5 d |
| 4 | Unit tests | Dev (Kara/Remy) | 0.5 d |
| 5 | Integration tests (agent loop + steering cancel) | Dev (Kara/Remy) | 1 d |
| 6 | UI: surface "waiting" state in chat | UI (if applicable) | 0.5 d |
| 7 | Documentation update (tool reference, config guide) | Nova | 0.5 d |
| **Total** | | | **~4 d** |

## Open Questions

1. **Should the delay be visible in the WebSocket stream?** — Recommend yes:
   emit an `agent.tool.progress` event with the reason and remaining time so
   the UI can render a countdown or spinner.
2. **Per-agent max override** — deferred. The global cap is adequate for v1. If
   a use case arises (e.g., a monitor agent that needs 60-min waits), we can
   add `Tools:Delay:MaxSeconds` to per-agent config.
3. **Should cancellation from steering also deliver the steering message to the
   model?** — Yes, this is already how the agent loop works: on steering the
   token is cancelled, the tool result is fed back, and the steering message is
   injected.
