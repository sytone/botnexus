---
type: bug
priority: critical
status: in-progress
created: 2026-07-16
title: "Internal Channel Adapter Missing — Sub-Agent Completions and Cross-Agent Messages Silently Drop"
---

# Internal Channel Adapter Missing

## Problem

There is no channel adapter registered for type `internal`. Sub-agent completion follow-ups, cross-agent messages, and any internal routing that uses this channel type silently fail — the wake-up fires, the LLM runs, but the response has no delivery path and is dropped.

## Evidence

Gateway logs (`botnexus-2026041612.log`):

```
12:50:22.045 [INF] Waking idle parent agent 'nova' session '...' after sub-agent '969a3c91...' completion.
12:50:22.049 [WRN] No channel adapter found for type 'internal'. Available: cross-world, signalr
12:50:22.063 [INF] Channel resolution: type='internal' found=false streaming=null streamEvents=false
```

The wake-up succeeds, the agent run starts (LLM API calls visible in logs), tools execute, but the final response cannot be delivered. The warning appears twice — once for the completion wake-up dispatch and once for the agent's response output.

## Impact

- **Sub-agent completion notifications silently drop** — parent agent runs but response goes nowhere
- **Cross-agent messaging broken** — any internal routing between agents fails
- **User sees agent as "stuck"** — has to send another message on `signalr` to trigger a fresh run that can deliver
- This is the root cause behind the observed "Nova doesn't react to sub-agent completions" behavior

## Root Cause

The `internal` channel type is used by `FollowUpAsync` and sub-agent completion wake-ups, but no `InternalChannelAdapter` is registered in the DI container. The channel resolution falls through to null, and the response is silently discarded.

Registered adapters: `cross-world`, `signalr`
Missing: `internal`

## Expected Behavior

An `internal` channel adapter should:
1. Be registered at gateway startup
2. Route messages to the correct session regardless of the originating channel
3. For parent session wake-ups: deliver via the parent session's original channel adapter (e.g., `signalr`)
4. Support cross-agent messaging for future multi-agent orchestration

## Recommended Fix

Register an `InternalChannelAdapter` that:
- Resolves the target session's active channel adapter and delegates delivery to it
- Falls back to `signalr` if the session has no prior channel context
- Supports streaming if the target channel supports it

This is likely a small adapter class + one DI registration line.

## Related Items

- `improvement-subagent-completion-handling` (archived/delivered) — implemented the wake-up, but didn't catch the missing adapter
- `bug-cross-agent-session-blocking` (active) — separate SignalR serialization issue, but this bug compounds it
