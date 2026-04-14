# LLM Request Lifecycle

> How a user message becomes an AI provider API call, and why every call is stateless.

## Overview

AI provider APIs (OpenAI, Anthropic, GitHub Copilot) are **completely stateless**. They have zero memory of any prior call. Every request must include the full conversation context — system prompt, message history, tool definitions — as a self-contained payload. The provider processes it, returns a response, and forgets everything.

BotNexus manages the statefulness on the gateway side: it maintains an in-memory conversation timeline, persists it to the session store, and assembles the full context payload before each LLM call.

## The Request Flow

```
User Message (e.g., SignalR)
    │
    ▼
┌─────────────────────────┐
│  1. Message Router       │  Resolves which agent handles this message
│     DefaultMessageRouter │  Priority: explicit target → session binding → default agent
└─────────┬───────────────┘
          │
          ▼
┌─────────────────────────┐
│  2. Agent Supervisor     │  Gets or creates an agent instance for the session
│  DefaultAgentSupervisor  │  Key: agentId + sessionId
└─────────┬───────────────┘
          │
          ▼
┌─────────────────────────┐
│  3. Agent Handle         │  In-process wrapper around the Agent core
│  InProcessAgentHandle    │  Calls Agent.PromptAsync(message)
└─────────┬───────────────┘
          │
          ▼
┌─────────────────────────┐
│  4. Agent Loop Runner    │  Core turn loop: call LLM → execute tools → repeat
│     AgentLoopRunner      │  Maintains the in-memory message timeline
└─────────┬───────────────┘
          │
          ▼
┌─────────────────────────┐
│  5. Context Conversion   │  Converts agent timeline → provider Context
│     ContextConverter     │  AgentMessage[] → Message[] + Tool[] + SystemPrompt
└─────────┬───────────────┘
          │
          ▼
┌─────────────────────────┐
│  6. LLM Client           │  Resolves the API provider and delegates
│     LlmClient            │  StreamSimple(model, context, options)
└─────────┬───────────────┘
          │
          ▼
┌─────────────────────────┐
│  7. API Provider          │  Builds and sends the HTTP request
│     (Anthropic/OpenAI/   │  One stateless POST with full context
│      Copilot)            │
└─────────┬───────────────┘
          │
          ▼
┌─────────────────────────┐
│  8. SSE Stream Response   │  Streamed back token-by-token
│     StreamAccumulator    │  Accumulated into an AssistantMessage
└─────────────────────────┘
```

## What Gets Sent to the Provider

Every LLM call is a single HTTP POST containing:

| Component        | Source                       | Description                                                                                                                            |
|------------------|------------------------------|----------------------------------------------------------------------------------------------------------------------------------------|
| **System Prompt** | `SystemPromptBuilder.Build()` | Agent identity, workspace files (AGENTS.md, SOUL.md, USER.md, TOOLS.md), skills, runtime info, behavioral rules                       |
| **Messages**     | In-memory timeline           | The **entire conversation history** — every user message, assistant response, and tool result, converted to provider format             |
| **Tools**        | Tool registry                | JSON schemas for all available tools                                                                                                   |
| **Model ID**     | Agent config                 | Which model to use (e.g., `claude-opus-4.6`)                                                                                           |
| **Stream flag**  | Always `true`                | Responses stream back via SSE                                                                                                          |

The provider receives this as a single self-contained request. It has no knowledge of prior calls, sessions, or agent state.

### Example: Anthropic Messages API Request

```json
{
  "model": "claude-opus-4.6",
  "system": [
    {
      "type": "text",
      "text": "You are a personal assistant running inside BotNexus...[full system prompt]...",
      "cache_control": { "type": "ephemeral" }
    }
  ],
  "messages": [
    { "role": "user", "content": "What's on my calendar today?" },
    { "role": "assistant", "content": "...", "tool_use": [{ "id": "call_1", "name": "calendar_today", "input": {} }] },
    { "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "call_1", "content": "..." }] },
    { "role": "assistant", "content": "You have 3 meetings today..." },
    { "role": "user", "content": "Cancel the last one" }
  ],
  "tools": [ { "name": "calendar_today", "description": "...", "input_schema": {} } ],
  "max_tokens": 21333,
  "stream": true
}
```

**Every message in the conversation is sent every time.** The 5th message in a session includes all 4 prior messages. The 50th includes all 49.

## The Agent Loop

The `AgentLoopRunner` is the core orchestration loop. It runs until the LLM stops requesting tool calls:

```
while (hasMoreToolCalls || pendingSteeringMessages):
    1. Drain any steering messages (injected mid-turn)
    2. Optionally run TransformContext (compaction-aware trimming)
    3. Convert full timeline → provider Context
    4. Call LLM (full context sent)
    5. Accumulate streamed response → AssistantMessage
    6. If assistant requested tool calls:
       a. Execute tools in parallel
       b. Append tool results to timeline
       c. Continue loop (another LLM call with updated timeline)
    7. If no tool calls: check for follow-up messages, then exit
```

Each iteration of this loop is a separate stateless HTTP call to the provider. A single user message might trigger 1 call (simple response) or 10+ calls (complex multi-tool workflow).

### Key Source Files

| File                          | Role                                                                                    |
|-------------------------------|-----------------------------------------------------------------------------------------|
| `AgentLoopRunner.cs`          | Core turn loop — drain steering → call LLM → execute tools → repeat                    |
| `ContextConverter.cs`         | Transforms `AgentContext` (agent messages) into provider `Context` (provider messages + tools) |
| `DefaultMessageConverter.cs`  | Default conversion from `AgentMessage[]` to provider `Message[]`                        |
| `LlmClient.cs`               | Resolves API provider from registry and delegates streaming call                        |
| `StreamAccumulator.cs`        | Collects SSE stream chunks into a complete `AssistantMessage`                           |
| `AnthropicRequestBuilder.cs`  | Builds the JSON body for Anthropic Messages API                                         |

## Context Growth and Compaction

Since the full timeline is sent every call, context grows with every turn. BotNexus has two mechanisms to manage this:

### 1. Session Compaction (`LlmSessionCompactor`)

When the estimated token count exceeds a threshold (default: 60% of context window), the compactor:

1. **Splits** the history into old entries and recent entries (preserves last N user turns, default: 3)
2. **Summarizes** the old entries using a cheaper model (e.g., `gpt-4.1-mini`) with a structured prompt requesting decisions, TODOs, constraints, and key identifiers
3. **Replaces** the old entries with a single system message containing the summary
4. Subsequent LLM calls send: system prompt + compaction summary + recent messages

```
Before compaction:
  [system prompt] + [msg1] + [msg2] + ... + [msg47] + [msg48]  → ~90K tokens

After compaction:
  [system prompt] + [compaction summary] + [msg46] + [msg47] + [msg48]  → ~25K tokens
```

### 2. Emergency Overflow Recovery

If the provider rejects a call with a context overflow error (detected by `ContextOverflowDetector`), `AgentLoopRunner.ExecuteWithRetryAsync` does an emergency compact: keeps the last 1/3 of messages (minimum 8) and retries.

```csharp
private static IReadOnlyList<AgentMessage> CompactForOverflow(IReadOnlyList<AgentMessage> messages)
{
    if (messages.Count <= 12) return messages.ToList();
    var keep = Math.Max(8, messages.Count / 3);
    return messages.Skip(messages.Count - keep).ToList();
}
```

## System Prompt Assembly

The system prompt is the largest fixed cost per LLM call. `SystemPromptBuilder.Build()` assembles it from:

1. **Base instructions** — tooling guidance, execution bias, safety rules, CLI reference
2. **Workspace context files** — AGENTS.md, SOUL.md, IDENTITY.md, USER.md, TOOLS.md (ordered by priority)
3. **Skills context** — available skills and their descriptions
4. **Runtime metadata** — agent ID, host, OS, provider, model, channel
5. **Dynamic context** — HEARTBEAT.md and other frequently-changing files (placed after a cache boundary marker)
6. **Behavioral rules** — silent reply rules, reply tags, messaging guidance

A **cache boundary marker** (`<!-- BOTNEXUS_CACHE_BOUNDARY -->`) separates stable content from dynamic content. Providers that support prompt caching (Anthropic) can cache the stable portion across calls, reducing cost for the repeated system prompt.

## Session Warmup and Resumption

The `SessionWarmupService` pre-loads session summaries into a cache when the gateway starts. This enables fast session lookup when a channel connects, but the actual agent context (the in-memory timeline) is only created when the agent is instantiated.

For session resumption after a gateway restart, the system needs to reconstruct the timeline from the session store — injecting the compaction summary and recent messages — so the next LLM call has meaningful history to send.

## Retry and Error Handling

`AgentLoopRunner.ExecuteWithRetryAsync` handles transient failures:

- **Transient errors** (rate limits, timeouts, 429/502/503/504): Exponential backoff, up to 4 attempts
- **Context overflow**: Emergency compaction (keep last 1/3 of messages), then retry once
- **Non-transient errors**: Fail immediately

The retry logic restores the message list to its pre-stream state before retrying, preventing partial streamed messages from corrupting the timeline.

## Key Insight: Cost Implications

Because every call includes the full context:

- **Early messages are sent repeatedly** — the system prompt and first few messages are included in every single API call for the life of the session
- **Tool-heavy turns are expensive** — a turn with 5 tool calls means 6 LLM calls (initial + 5 follow-ups), each sending the growing timeline
- **Compaction is critical** — without it, a long session would hit context limits and become increasingly expensive
- **Prompt caching helps** — providers that support it (Anthropic) can skip re-processing the stable system prompt portion on subsequent calls

This is fundamentally how all current LLM APIs work — the statefulness is entirely on the client side.

## See Also

- [Architecture Overview](overview.md) — Full system architecture
- [Workspace and Memory](workspace-and-memory.md) — Agent workspace, memory, and context files
- [System Layers](system-layers.md) — Layer-by-layer breakdown
