---
id: message-queue-injection-timing
title: "Message Queue Injection Timing"
type: bug
priority: medium
status: planning
created: 2025-07-17
---

# Message Queue Injection Timing

## Status: planning

## Problem

When an agent is in a multi-tool-call turn (e.g., a delay/sleep followed by another tool call), user messages sent during the wait are not visible to the LLM until after the current turn fully completes. This means the agent cannot react to user interrupts mid-turn.

## Current Behavior

1. LLM turn → calls blocking tool (e.g., `sleep 60`)
2. Tool completes → result returned to LLM
3. LLM generates response + next tool call (e.g., reload file) — **without seeing queued user messages**
4. After full LLM interaction completes → queued messages are delivered
5. Next LLM turn now sees those messages — too late to interrupt the prior action

## Desired Behavior

1. LLM turn → calls blocking tool (e.g., `delay 60`)
2. Tool completes
3. **Before** sending tool result to LLM for next inference, gateway flushes any queued user messages into context
4. LLM sees both the tool result AND the user messages, can react accordingly (e.g., stop a review loop)

## Prerequisite

Need a clear architecture document for the steering and message queueing system — how messages flow, where they queue, and where in the code/logical flow injection happens. Without that visibility, it's hard to know where to make the change safely.

## Use Case

Collaborative review loops — agent reviews a file, waits for user edits, reloads and reviews again. User needs to be able to say "stop" during a wait and have the agent respect it on the next inference call.

## Discovered

2025-07-17 — During a collaborative file review session. The `delay` tool also errored unexpectedly (separate issue).

## Related

- `delay` tool — intended for this pattern but threw parameter validation errors
- Steer/queue message architecture — needs documenting first
