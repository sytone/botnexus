---
id: bug-tool-execution-timeout
title: "No Tool Execution Timeout or Stuck-Turn Recovery"
type: bug
priority: critical
status: in-progress
created: 2026-04-20
tags: [tools, timeout, resilience, session-recovery, agent-lifecycle]
---

# Bug: No Tool Execution Timeout or Stuck-Turn Recovery

**Status:** in-progress
**Priority:** critical
**Created:** 2026-04-20

## Problem

When a tool call hangs indefinitely, the entire agent session becomes permanently unresponsive. There is no timeout, no error propagation, no recovery mechanism, and no way for the user to interrupt or cancel the stuck turn short of restarting the gateway.

## Incident

- **Agent:** aurum
- **Session:** `67f4198aad8d45c69899d4496b8cf80c`
- **Date:** 2026-04-20 at 17:15 PT
- **Trigger:** User asked Aurum to run a semester planning review
- **Sequence:**
  1. Aurum fired 10+ bash tool calls in rapid succession
  2. Last `ToolStart` event at 17:15:58 — never received a corresponding `ToolEnd`
  3. Session hung permanently
  4. User messages at 17:25 and 17:27 received no response
  5. No error logged, no timeout triggered, no recovery attempted

## Root Cause

The tool execution pipeline has no timeout mechanism. Once a `ToolStart` is emitted and the tool executor begins awaiting a result, it waits forever. The agent turn loop is blocked on the tool result, so no further messages can be processed. The session is effectively dead.

### Why This Is Critical

- **No recovery path** — session is permanently dead without gateway restart
- **Silent failure** — no error logged, no user notification
- **User messages lost** — messages sent during the hang are never processed
- **Cascading impact** — if the agent is a parent with sub-agents, those are orphaned

## Impact

- Sessions permanently killed by a single hung tool call
- User has no indication of what happened or how to recover
- Gateway restart required, which disrupts all active sessions

## Requirements

### Must Have

1. **Configurable tool execution timeout** — default 120 seconds, configurable per-tool and per-agent
2. **Timeout produces error result** — when a tool times out, return a structured error to the LLM (e.g. `ToolResult { IsError = true, Content = "Tool execution timed out after 120s" }`) so it can recover gracefully
3. **Session remains usable after timeout** — the session must not be permanently dead; subsequent user messages must be processable
4. **Stuck-turn detection** — if an agent turn has been running for X minutes (configurable, default 5min) with no progress (no tool completions, no streaming tokens), flag it as stuck
5. **User interrupt/cancel** — user can cancel a stuck turn from the UI (e.g. a "Stop" button or `/cancel` command)

### Should Have

6. **Auto-cancel stuck turns** — after the stuck-turn threshold, automatically cancel the turn and notify the user
7. **Logging and diagnostics** — log tool timeout events with full context (tool name, arguments, duration, session, agent)
8. **Per-tool timeout configuration** — some tools (e.g. `exec` with `timeoutMs`) already have their own timeout semantics; the system timeout should be a safety net above those

### Nice to Have

9. **Health indicator** — surface stuck sessions in the Blazor UI with a warning badge
10. **Metrics** — track tool execution durations for observability (P50, P95, P99)

## Proposed Design

### Tool Execution Timeout

```csharp
// In ToolExecutor or equivalent
using var cts = CancellationTokenSource.CreateLinkedTokenSource(turnCancellation);
cts.CancelAfter(toolTimeout);  // default 120s

try
{
    var result = await tool.ExecuteAsync(args, cts.Token);
    return result;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    return ToolResult.Error($"Tool '{tool.Name}' timed out after {toolTimeout.TotalSeconds}s");
}
```

### Stuck-Turn Detection

A background monitor (or timer per active turn) checks:
- Time since turn started
- Time since last progress event (ToolEnd, streaming chunk, etc.)
- If exceeded threshold → mark turn as stuck → allow cancellation

### User Cancellation

- Blazor UI: "Stop" button appears when a turn is in-progress
- CLI/Signal: `/cancel` command kills the active turn
- Cancellation triggers the turn's `CancellationToken`, which propagates to the active tool

### Configuration

```json
{
  "AgentDefaults": {
    "ToolTimeoutSeconds": 120,
    "StuckTurnTimeoutMinutes": 5,
    "AutoCancelStuckTurns": true
  }
}
```

## Edge Cases

- **Tool that completes just after timeout** — result is discarded; LLM already got the error
- **Multiple parallel tool calls** — timeout applies per-tool; one timeout doesn't kill siblings
- **User cancel during streaming** — cancel should abort the LLM stream too
- **Exec with explicit timeoutMs** — system timeout is a safety net; whichever fires first wins

## Testing

- Unit test: tool timeout returns error result
- Unit test: session processable after tool timeout
- Integration test: stuck-turn detection triggers after threshold
- Integration test: user cancel aborts active tool and turn
- Manual: reproduce the aurum incident scenario and verify recovery

## References

- Incident session: `67f4198aad8d45c69899d4496b8cf80c`
- Related: [message-queue-injection-timing](../message-queue-injection-timing/design-spec.md) — messages during active turns
