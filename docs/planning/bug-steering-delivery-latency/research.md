# Research: Steering Messages Delivered Late to Agent

## Problem Statement
Steering messages (including sub-agent completion notifications) are arriving significantly later than expected. The agent does not see them in a timely manner, which defeats the purpose of mid-turn steering.

## Observed Behavior (2026-04-10)
1. Three sub-agents were spawned in parallel for spec update work
2. Sub-agent 3e82f94f completed its work
3. Nova manually checked status via manage_subagent and found it complete (status=1)
4. Nova reported the results to Jon
5. The completion steering message (Sub-agent 3e82f94f completed. Summary: ...) arrived MUCH LATER - after Nova had already moved on
6. Same pattern for sub-agents 2ab34695 and 800762ec - Nova had already checked, reported, and moved on before the completion notifications arrived
7. Jon observed: steering messages arrive far later than expected

## Why This Matters
- Steering is a core interaction pattern - the user sends corrections/additions while the agent is working
- If steering messages are delayed, the agent completes work before seeing the correction, wasting time and tokens
- Sub-agent completion notifications are system-generated steering messages - if these are late, user steering messages are likely late too
- The whole point of steering is real-time course correction. Delayed delivery makes it useless

## Possible Causes

### 1. Message Queue Batching
The gateway may batch steering messages and only deliver them at certain checkpoints (e.g., between tool calls, after assistant response completes). If the agent is mid-tool-execution, the steering message sits in a queue until the current turn finishes.

### 2. LLM API Call Blocking
If the gateway is waiting for an LLM API response, it may not inject steering messages until the response stream completes. The message is queued on the server but not injected into the conversation until the next turn boundary.

### 3. SignalR Delivery vs LLM Injection
The steering message may arrive at the gateway quickly (SignalR is fast) but the injection into the active LLM conversation context may be delayed. There could be a gap between when the message hits the server and when it is actually presented to the agent.

### 4. Turn Boundary Limitation
Some agent frameworks only allow new messages to be injected between turns (after the current assistant response is complete). If BotNexus works this way, steering messages are inherently delayed until the agent finishes its current response - which could be many tool calls later.

### 5. Sub-Agent Completion Event Processing
The sub-agent completion may trigger an event that needs to be processed by the gateway, formatted into a steering message, and injected. Each step adds latency. If the event processing is async with low priority, it could queue behind other work.

## Relationship to bug-steering-message-visibility
That bug covers steering messages not DISPLAYING in the UI conversation timeline.
This bug covers steering messages not being DELIVERED to the agent promptly.
They may share root causes (e.g., the steering injection pipeline has issues) but the symptoms and impact are different.

## Questions for the Squad
1. When is a steering message actually injected into the LLM conversation? Between turns only, or can it interrupt mid-tool-execution?
2. Is there a message queue for steering? What is its delivery mechanism?
3. For sub-agent completion events specifically - what is the event flow from sub-agent completion to steering message delivery?
4. Is there any batching or debouncing of steering messages?
5. Can we add timestamps to steering messages (queued_at, delivered_at, seen_by_agent_at) for diagnostics?
