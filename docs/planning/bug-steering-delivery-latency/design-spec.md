---
id: bug-steering-delivery-latency
title: Steering Messages Delivered Late to Agent
type: bug
priority: high
status: draft
created: 2026-04-10
updated: 2026-04-10
author: nova
related: [bug-steering-message-visibility]
tags: [steering, gateway, latency, subagent]
---

# Design Spec: Steering Message Delivery Latency

Type: Bug
Priority: High (steering is useless if delayed)
Status: Draft
Author: Nova (via Jon)

## Overview
Steering messages (user mid-turn messages and sub-agent completion notifications) are delivered to the agent significantly later than expected, often after the agent has already completed the work the steering was meant to influence.

## Requirements

### Must Have
1. Steering messages are delivered to the agent within the current turn, not queued until the next turn
2. Sub-agent completion notifications are delivered promptly (within seconds of completion)
3. The agent can see and act on steering messages between tool calls, not only between full response turns

### Should Have
4. Diagnostic timestamps on steering messages (queued_at, delivered_at, processed_at) for latency measurement
5. Configurable urgency levels for steering (e.g., completion notifications could be lower priority than user messages)

### Nice to Have
6. Visual indicator in UI showing steering message delivery status (queued, delivered, seen)

## Proposed Investigation

### Step 1: Instrument the Pipeline
Add timestamps at each stage of steering message flow:
- T0: Message received by gateway (from SignalR client or sub-agent completion event)
- T1: Message queued for injection
- T2: Message injected into LLM conversation context
- T3: Agent processes/acknowledges the message

The delta between T0 and T2 is the delivery latency. The delta between T0 and T3 is the end-to-end latency.

### Step 2: Identify Injection Points
Determine when the gateway can inject steering messages:
- Between full turns only? (current suspected behavior)
- Between tool calls within a turn?
- Mid-LLM-response (interrupt the stream)?

The ideal is injection between tool calls. Mid-stream interruption is complex and may not be worth it.

### Step 3: Fix Injection Timing
If messages are only delivered between turns, change the injection point to between tool calls:
- After a tool returns its result, before the next LLM call, check the steering queue
- If messages are waiting, inject them into the context before the next LLM call
- This gives the agent a chance to change course without waiting for the full turn to complete

## Architecture Consideration
The steering injection point determines the responsiveness ceiling:
- Between turns: Agent may execute 5-10 tool calls before seeing steering (minutes)
- Between tool calls: Agent sees steering before the next action (seconds)
- Mid-stream: Agent sees steering immediately (sub-second, but complex)

Between tool calls is the right balance of responsiveness and implementation complexity.

## Testing Plan
1. Send a steering message while agent is executing a multi-tool-call turn. Measure time from send to agent acknowledgment
2. Spawn a sub-agent, wait for completion. Measure time from completion to parent agent seeing the notification
3. Send multiple steering messages in rapid succession. Verify all are delivered, in order
4. Send steering during LLM response generation (not tool execution). Verify delivery timing
5. Compare delivery latency before and after fix

## Success Criteria
- Steering messages delivered to agent within 5 seconds of being sent
- Sub-agent completion notifications arrive before the parent agent's next tool call (not next turn)
- User does not need to repeat steering messages because the first one was ignored/delayed
