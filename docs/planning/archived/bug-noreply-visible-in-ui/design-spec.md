---
id: bug-noreply-visible-in-ui
title: "NO_REPLY Sentinel Visible as Literal Text in Blazor UI"
type: bug
priority: high
status: draft
created: 2026-04-20
tags: [bug, blazor, webui, signalr, no-reply, ux]
---

# Bug: NO_REPLY Sentinel Visible as Literal Text in Blazor UI

**Status:** Draft
**Priority:** High
**Created:** 2026-04-20

## Problem

When an agent responds with `NO_REPLY` — a sentinel value meaning "no user-visible reply needed" — the Blazor UI renders it as literal text in the chat. Users see a message containing just "NO_REPLY", which looks broken and confusing.

`NO_REPLY` is a convention used by agents for:
- Silent housekeeping tasks
- No-op heartbeat/ambient wakeups
- Cases where a messaging tool already delivered the reply
- Deliberate suppression of a response

The SignalR channel currently passes these messages through to the UI without filtering.

## Impact

- **High** — user-facing; every `NO_REPLY` appears as a broken-looking message
- Erodes trust ("why is the bot saying NO_REPLY?")
- Clutters conversation history with meaningless entries
- Occurs frequently with cron-triggered heartbeats and sub-agent housekeeping

## Reproduction

1. Open the Blazor Web UI
2. Trigger any scenario that produces a `NO_REPLY` response (e.g., a cron heartbeat, or steer a sub-agent with a no-op)
3. Observe: the chat displays a message bubble containing the literal text "NO_REPLY"

## Expected Behavior

`NO_REPLY` messages should be **silently dropped** — no message bubble, no chat entry, no visual artifact. The user should see nothing.

## Root Cause Analysis

The SignalR channel (`SignalRChannelAdapter` or equivalent) receives agent responses and pushes them to connected Blazor clients. It does not check for the `NO_REPLY` sentinel before forwarding.

## Proposed Fix

### Option A: Filter at SignalR Channel (Recommended)

Intercept `NO_REPLY` in the SignalR channel adapter before broadcasting to clients.

**Where:** `SignalRChannelAdapter.SendMessageAsync` (or equivalent outbound path)

```csharp
// Before sending to SignalR clients:
if (message.Content?.Trim() == "NO_REPLY")
{
    _logger.LogDebug("Suppressed NO_REPLY message from agent {AgentId}", message.AgentId);
    return; // Do not forward to UI
}
```

**Pros:**
- Single point of filtering for the Blazor UI
- Other channels (Signal, Telegram) can implement their own filtering independently
- Clean separation of concerns

**Cons:**
- String-matching on content is fragile if the sentinel format changes

### Option B: Filter at Message Bus / Orchestrator Level

Intercept earlier in the pipeline, before messages reach any channel.

**Pros:**
- All channels get filtering for free
- Single implementation

**Cons:**
- May suppress messages that some channels want to log or handle differently
- Harder to customize per-channel behavior

### Recommendation

**Option A** — filter at the SignalR channel level. This matches the architectural principle that channels own their delivery semantics. Other channels (Signal, Telegram) already handle this by simply not sending the message.

## Implementation Notes

1. **Constant, not magic string** — define `NO_REPLY` as a constant (e.g., `AgentConstants.NoReply`) rather than hardcoding the string in multiple places
2. **Trim and normalize** — the agent may include trailing whitespace or newlines; normalize before comparing
3. **Do not persist as visible** — if messages are stored before channel delivery, consider marking `NO_REPLY` messages as `hidden` or `system` so they don't appear in session history either
4. **Logging** — log suppressed messages at `Debug` level for diagnostics

## Scope

| In Scope | Out of Scope |
|----------|--------------|
| Filter `NO_REPLY` in SignalR channel | Changing the `NO_REPLY` convention itself |
| Define shared constant for sentinel | Filtering in Signal/Telegram channels (already handled) |
| Suppress from chat rendering | Retroactively hiding past `NO_REPLY` messages |
| Debug-level logging of suppressed messages | |

## Testing

| Scenario | Expected |
|----------|----------|
| Agent responds with `NO_REPLY` | No message appears in Blazor UI |
| Agent responds with `NO_REPLY\n` (trailing whitespace) | No message appears |
| Agent responds with `NO_REPLY` after sending a real message via tool | No message appears |
| Agent responds with text containing "NO_REPLY" in a sentence | Message renders normally (not suppressed) |
| Agent responds with normal text | Message renders normally |
| Cron heartbeat produces `NO_REPLY` | No message appears |

## Files Likely Affected

- `src/BotNexus.Gateway/Channels/SignalR/SignalRChannelAdapter.cs` — add sentinel check
- `src/BotNexus.Core/Constants/AgentConstants.cs` (or similar) — define `NoReply` constant
- Blazor session history rendering (if messages are stored before filtering)
