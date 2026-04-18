---
id: improvement-subagent-completion-handling
title: "Sub-Agent Completion Handling — Reliable Parent Wake-Up"
type: improvement
priority: high
status: delivered
created: 2026-07-26
---

# Improvement: Sub-Agent Completion Handling

**Status:** delivered
**Priority:** high
**Created:** 2026-07-26

## Problem

When a sub-agent completes, `FollowUpAsync` enqueues a completion message to the parent session's follow-up queue. If the parent agent is idle (no active run), nobody drains that queue. The completion sits orphaned until the next user message arrives — forcing the user to manually nudge the agent.

This is a platform-level reliability bug, not an agent prompt issue.

## Root Cause

`FollowUpAsync` writes to the queue but doesn't trigger a new agent run. The parent session only processes queued follow-ups when it's already running (i.e., draining the queue during an active turn).

## Requirements

### Must Have
- Idle parent agent wakes up automatically when a sub-agent completion follow-up arrives
- Completion is processed within seconds, not on next user message
- No duplicate processing if parent is already mid-turn

### Should Have
- Typed completion messages (not plain text) so the agent can distinguish them from user messages
- Deduplication — if the same completion is delivered twice, only process once

### Nice to Have
- agents-as-tools pattern (long-term) — sub-agent call is a tool call that returns when done, guaranteeing LLM processes the result

## Proposed Fix

1. **Wake-up mechanism:** When `FollowUpAsync` is called on an idle session, dispatch a synthetic inbound message through `GatewayHost.DispatchAsync` (or equivalent) to trigger a new agent run that drains the queue
2. **Guard against re-entrancy:** If the parent is already in an active run, just enqueue normally (existing behavior is fine for this case)
3. **Typed messages:** Use a structured completion payload instead of plain text so agents can reliably identify and act on completions

## See Also

- [research.md](research.md) — full cross-platform comparison (AutoGen, CrewAI, LangGraph, OpenAI SDK, Semantic Kernel, OpenClaw)
