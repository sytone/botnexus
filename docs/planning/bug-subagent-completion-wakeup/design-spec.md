---
id: bug-subagent-completion-wakeup
title: "Sub-Agent Completion Signals Not Waking Parent Agent Session"
type: bug
priority: high
status: delivered
created: 2026-07-22
tags: [bug, subagent, gateway, session-wake, completion, async-delegation]
---

# Bug: Sub-Agent Completion Signals Not Waking Parent Agent Session

**Status:** Delivered
**Priority:** high
**Created:** 2026-07-22

## Problem

When a sub-agent completes its work, the parent agent session does not autonomously wake up to process the completion. The completion event is only processed when the user happens to send a subsequent message — at which point it appears as an injected `[Sub-Agent Completion]` system message bundled with the user's input. The parent agent cannot proactively review sub-agent output and report back to the user in real-time.

## Current Behavior

1. Parent agent spawns a sub-agent via `agent_converse`
2. Sub-agent runs and completes
3. **Nothing happens** — parent agent session remains idle
4. User sends an unrelated message (e.g., "good morning")
5. Parent agent sees the `[Sub-Agent Completion]` message bundled with the user's message
6. Parent agent finally reacts to the completion at that point

## Expected Behavior

1. Parent agent spawns a sub-agent
2. Sub-agent runs and completes
3. **Gateway delivers the completion signal to the parent agent session as a wake event**
4. Parent agent wakes, processes the completion (reviews output, summarizes results)
5. Parent agent proactively messages the user with the results — no user prompt needed

## Impact

- **High** — sub-agent delegation is a core workflow pattern
- Without real-time completion handling, the user must manually "check in" to trigger processing, which defeats the purpose of async delegation
- The parent agent appears unresponsive or lazy — it had the answer but didn't tell the user
- Breaks the mental model of "fire and forget" delegation that sub-agents are designed for

## Root Cause

The sub-agent completion event IS being generated (confirmed: it appears when the user next messages). The issue is that the event is **queued/buffered** in the parent session's pending messages rather than **triggering an active wake** of the parent session.

The gateway treats sub-agent completion as a passive injection rather than an active session wake trigger. Compare with cron jobs and incoming user messages, which DO wake idle sessions — sub-agent completion should behave the same way.

## Scope

This is a **gateway-level** issue, not a channel/UI issue. The completion signal routing should work regardless of which channel (SignalR, Blazor, etc.) is connected. The channel is just a view over the session — if the session wakes and the agent produces a response, the channel delivers it.

## Requirements

### Must Have
- Sub-agent completion triggers an active wake of the parent agent session
- Parent agent can process the completion and respond to the user without user prompting
- Works regardless of connected channel (SignalR, Blazor, Signal, Telegram, etc.)

### Should Have
- Wake semantics consistent with other wake triggers (cron, incoming messages)
- Completion event clearly distinguishable as a sub-agent wake (not confused with user input)

### Nice to Have
- Configurable behavior (e.g., option to batch completions vs. wake-per-completion)
- Telemetry/logging for completion wake events

## Relationship to Prior Work

> ⚠️ `improvement-subagent-completion-handling` (delivered Jul '26) addressed **reliable delivery** of completion events — ensuring they aren't lost. This bug is about the **wake trigger**: the event arrives but doesn't wake the session to process it.

## Investigation Steps

1. Trace the sub-agent completion event from generation through to parent session injection
2. Identify where cron/message wake triggers diverge from completion event handling
3. Determine whether the gateway's session wake mechanism can be invoked for completion events
4. Check for race conditions (e.g., completion arrives while parent session is mid-turn)

## Files to Investigate

- Gateway session wake/dispatch pipeline — where incoming messages and cron triggers wake sessions
- Sub-agent completion event routing — where the completion is generated and injected
- Session message queue — where the completion is buffered instead of triggering a wake
