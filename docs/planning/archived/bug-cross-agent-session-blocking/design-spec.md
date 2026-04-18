---
type: bug
priority: high
status: delivered
created: 2026-07-16
title: "Cross-Agent Session Blocking — Response Delivery Stalls While Another Agent Runs"
---

# Cross-Agent Session Blocking

## Problem

When one agent (e.g., Aurum) is actively running, another agent's (e.g., Nova's) completed responses are not delivered to the user until the first agent's run finishes. Messages from the user to the blocked agent also appear to queue without triggering a run.

## Observed Behavior

1. User sends message to Nova
2. Aurum is actively running on a separate session
3. Nova's response is delayed — not delivered until Aurum completes
4. User's follow-up messages to Nova ("hello?", "You seem to be stuck????") also don't trigger runs

## Expected Behavior

Agent sessions should be independent. A completed response on Session A should be delivered immediately regardless of whether Session B has an active run.

## Impact

- User thinks agent is broken/stuck
- Time-sensitive responses delayed unpredictably
- Undermines multi-agent UX — the whole point is parallel agents

## Likely Cause

Gateway appears to serialize agent runs or SignalR message dispatch, creating a bottleneck where only one agent can actively process/deliver at a time.

## Root Cause

`GatewayHub.SendMessage` **awaits** `DispatchAsync`, which blocks until the full agent run completes. SignalR processes hub invocations **sequentially per connection**. So when Aurum's run takes minutes, Nova's `SendMessage` can't even start.

Session queues and agent locks are correctly per-instance — the bottleneck is purely the hub await + SignalR's per-connection serialization.

## Recommended Fix

Fire-and-forget `DispatchMessageAsync` in `SendMessage` (and `Steer`, `FollowUp`). The streaming event path already delivers all content — the await is redundant for SignalR clients.

See `research.md` for full analysis with code references.
