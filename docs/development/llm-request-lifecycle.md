# LLM Request Lifecycle

> How a user message becomes an AI provider API call, and why every call is stateless.

## Overview

AI provider APIs (OpenAI, Anthropic, GitHub Copilot) are **completely stateless**. They have zero memory of any prior call. Every request must include the full conversation context — system prompt, message history, tool definitions — as a self-contained payload. The provider processes it, returns a response, and forgets everything.

BotNexus manages the statefulness on the gateway side: it maintains an in-memory conversation timeline, persists it to the session store, and assembles the full context payload before each LLM call.

## The Request Flow

```text
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

```text
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

When the estimated token count of the **LLM-visible** projection exceeds a threshold (default: 60% of context window), the compactor:

1. **Projects** the session history to the LLM-visible subset: everything that is not already historical (`SessionEntry.IsHistory == false`) and not a crash sentinel. Already-historical entries from earlier compactions are not re-summarised.
2. **Splits** the visible portion into old entries and recent entries (preserves last N user turns, default: 3).
3. **Summarises** the old entries using a cheaper model (e.g., `gpt-4.1-mini`) with a structured prompt requesting decisions, TODOs, constraints, and key identifiers.
4. **Marks** every summarised entry with `IsHistory = true` and **inserts** the new summary entry at the historical→preserved boundary. The original turns remain in the session store for the full-fidelity transcript.
5. Subsequent LLM calls send: system prompt + latest compaction summary + recent messages. Historical entries are excluded from the projection.

```text
Before compaction (stored history):
  [u1] [a1] [u2] [a2] ... [u47] [a47] [u48] [a48]

After compaction (stored history — full transcript preserved):
  [u1*] [a1*] ... [u45*] [a45*] [summary] [u46] [a46] [u47] [a47] [u48] [a48]
  *IsHistory = true; excluded from LLM context but visible in the UI transcript

After compaction (LLM-visible projection):
  [system prompt] + [summary] + [u46] [a46] [u47] [a47] [u48] [a48]  → ~25K tokens
```

On the next compaction cycle the previous `summary` entry is itself folded into the new summary and marked `IsHistory = true` — only the latest summary is ever sent to the LLM, but the chain of summaries (and every original turn) stays in the store.

#### Resilience: model fallback and the circuit breaker

The summary call can fail transiently (e.g. an intermittent HTTP 421 from the Copilot endpoint, or a timeout). Two mechanisms keep a single failure from permanently wedging a session:

- **Model fallback** — the compactor tries the configured/primary summary model first, then falls through the cheaper default summary models (`gpt-4.1-mini`, `gpt-5-mini`, `claude-haiku-4.5`, `gpt-4.1`) in order, stopping at the first that returns a usable summary. A transient outage on one model therefore does not abort the whole compaction. (The transport layer also retries 421 on a fresh connection first — see [Retry and Error Handling](#retry-and-error-handling).)
- **Time-based circuit breaker** — after `MaxConsecutiveFailures` (3) consecutive failures the breaker opens for that session, but **only for a cooldown window** (`gateway:compaction:circuitBreakerCooldownSeconds`, default 600s) rather than until the gateway restarts. Once the cooldown elapses the breaker auto-resets and compaction is attempted again, so a session recovers on its own when the provider issue clears. A successful compaction resets the breaker immediately.

When the LLM ultimately returns no usable summary, compaction **aborts without mutating history** — the session is left intact and retried later rather than corrupted.

### 1a. Concurrent additions during the LLM summary window (`HistoryReplaceOutcome`)

The summary LLM call runs **outside** the runtime lock — otherwise every new inbound message would block waiting for compaction to finish. To stay safe under concurrent `AddEntry` (a new user/assistant turn) or `RemoveCrashSentinels` (post-restart cleanup) calls that land while the summary is in flight, the compactor uses optimistic concurrency:

1. **Snapshot**: `GatewaySession.SnapshotHistoryForCompaction()` takes a defensive copy of the entries plus the destructive-mutation version counter, all under the lock. The compactor operates on the immutable snapshot from this point on — `session.History` is not read again.
2. **Summarise off-lock**: the LLM call happens in user space using the snapshot.
3. **Apply**: the caller invokes `session.TryApplyCompactionResult(result)`, which calls `TryReplaceHistoryFromSnapshot`. The runtime decides between three outcomes:
   - **`Applied`** — fast path: no mutations happened during the summary window. The new history is swapped in verbatim.
   - **`Rebased`** — only additions happened (one or more `AddEntry` calls). The compacted history is applied with the concurrent tail appended afterwards so no new turns are dropped. `TokensAfter` becomes approximate.
   - **`Aborted`** — a destructive change happened (another compaction, or a crash-sentinel removal that actually removed something). The apply is refused; live history is left unchanged. Callers should log the conflict and may retry from a fresh snapshot.

The destructive-version counter is bumped on `ReplaceHistory` always, and on `RemoveCrashSentinels` only when it actually removed at least one entry — so common no-op sentinel scrubs after every clean turn do **not** cause spurious aborts.

### 1b. Where the projection lives (`SessionContextProjector`)

The "which session entries reach the LLM" rule is **owned by a single type**: `SessionContextProjector` in `BotNexus.Gateway.Sessions`. Two predicates make explicit what was previously two divergent inline filters:

- **`IsVisibleOnResume`** — the strict filter used by isolation strategies when hydrating a cold-resumed session into the initial LLM message list. Excludes `IsHistory`, `IsCrashSentinel`, raw `System` entries, and (importantly) `Tool` entries. Tool entries are dropped on resume because the persisted Assistant `SessionEntry` only carries response text, not the structured `tool_use` blocks that would pair with the following `tool_result`. Without the pair, the `tool_result` becomes orphaned and the Anthropic Messages API rejects the request.
- **`IsVisibleInLiveContext`** — the broader filter used by `LlmSessionCompactor` for sizing the token budget. Counts `Tool` and non-summary `System` entries because in continuous mid-session operation those *are* sent to the LLM — the agent's in-memory message list still has the tool calls paired with their Assistant `tool_use` blocks, and the provider client serialises them correctly.

Future isolation strategies (sandbox, container, remote) and any other code that turns session history into LLM messages must route through `SessionContextProjector`. An architecture test (`SessionContextProjectorArchitectureTests`) fails the build if a file outside the small allowlist contains both `IsHistory` and `IsCrashSentinel` — that is the canonical signature of an inline re-implementation drifting from the projector.

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

## Conversation Reset (`IConversationResetService`)

In-session compaction keeps the conversation in the **same session**. A user-initiated `/reset` (REST `POST /api/conversations/{id}/reset`, SignalR `ResetSession`, or REST archive) is different: it **seals the active session in place** and lets the next inbound message start a **fresh empty session in the same conversation**, with the system prompt naturally reloaded from workspace files. Memory persists across the boundary via an explicit memory-bridge step.

The canonical reset sequence is owned by **`IConversationResetService.ResetActiveSessionAsync`** (default impl `DefaultConversationResetService`) so REST and SignalR cannot drift. The five steps are:

1. **Stop the agent supervisor handle** for the active session — best-effort. Drops any in-memory handle so the next `GetOrCreateAsync` starts a fresh isolation strategy with a freshly-built system prompt.
2. **Flush memory via `ISessionEndMemoryFlusher`** — best-effort. The agent gets one final synthetic turn to write a memory bridge (what it learned, decisions made, follow-ups) before the session is sealed, so the next session inherits the agent's intent without inheriting the raw turn history.
3. **Cancel pending ask-user prompts** — any conversation-scoped `ask_user` waiters are cancelled so they don't block the new session.
4. **Seal the active session in place** — set `Status = Sealed` and persist. Sessions are *not* archived (`ArchiveAsync` would delete from `InMemorySessionStore` and rename out of lookup in `FileSessionStore`); they remain readable via the session store for transcript/audit, just no longer Active.
5. **Clear `Conversation.ActiveSessionId`** — persist the conversation. The next inbound through `DefaultConversationRouter` sees no active session and creates a new one.

REST `Archive` runs the same canonical sequence on the active session before archiving the conversation, so both archive and reset paths get the memory-flush bridge.

An architecture fitness function — `NoDirect_ISessionEndMemoryFlusher_FlushAsync_OutsideAllowlist` — fails the build if any file in `src/gateway/` outside the 3-file allowlist (`DefaultConversationResetService.cs` + `SessionEndMemoryFlusher.cs` + the `ISessionEndMemoryFlusher.cs` interface declaration) references both `ISessionEndMemoryFlusher` and `FlushAsync(`. New callers that need to reset must route through `IConversationResetService`.

## System Prompt Assembly

The system prompt is the largest fixed cost per LLM call. `SystemPromptBuilder.Build()` assembles it from:

1. **Base instructions** — tooling guidance, execution bias, safety rules, CLI reference
2. **Workspace context files** — AGENTS.md, SOUL.md, IDENTITY.md, USER.md, TOOLS.md (ordered by priority)
3. **Skills context** — available skills and their descriptions
4. **Runtime metadata** — agent ID, host, OS, provider, model, channel
5. **Dynamic context** — HEARTBEAT.md and other frequently-changing files (placed after a cache boundary marker)
6. **Behavioral rules** — silent reply rules, reply tags, messaging guidance

A **cache boundary marker** (`<!-- BOTNEXUS_CACHE_BOUNDARY -->`) separates stable content from dynamic content. Providers that support prompt caching (Anthropic) can cache the stable portion across calls, reducing cost for the repeated system prompt.

### When the system prompt is (re)loaded

`GatewayHost.ShouldInitializeSystemPrompt(session)` returns true exactly when `session.History.Count == 0`. That's the only signal needed — and it's safe because:

- **In-session compaction** marks older entries as `IsHistory = true` rather than deleting them, so a compacted session still has `History.Count > 0` and the prompt is *not* re-initialised mid-conversation.
- **Conversation reset** (see above) creates a brand-new session in the same conversation with empty `History`, so the prompt *is* re-loaded on the next dispatch, picking up any changes to workspace files (AGENTS.md, SOUL.md, etc.).

There is intentionally no metadata flag: the natural invariant on `Session.History` is the source of truth, enforced by the `NoCode_References_systemPromptInitialized_Literal` architecture fence.

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

- [Architecture Overview](../architecture/overview.md) — Full system architecture
- [Workspace and Memory](workspace-and-memory.md) — Agent workspace, memory, and context files
- [Message Flow](message-flow.md) — Channel dispatch and routing
