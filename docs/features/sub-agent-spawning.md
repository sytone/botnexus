# Sub-Agent Spawning

**Version:** 1.0 (Draft)
**Last Updated:** 2026-04-11
**Status:** Phase 1 — Implementation in progress

---

## Table of Contents

1. [Quick Start](#1-quick-start)
2. [Overview](#2-overview)
3. [Architecture](#3-architecture)
4. [Tools Reference](#4-tools-reference)
   - [spawn_subagent](#spawn_subagent)
   - [list_subagents](#list_subagents)
   - [manage_subagent](#manage_subagent)
5. [Configuration Reference](#5-configuration-reference)
6. [Completion Flow](#6-completion-flow)
7. [Security](#7-security)
8. [Phase 1 Limitations](#8-phase-1-limitations)
9. [API Endpoints](#9-api-endpoints)
10. [Examples](#10-examples)

---

## 1. Quick Start

Spawn a background sub-agent to perform research while you continue working:

<!-- DRAFT: verify against implementation -->

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "task": "Research how context window compaction works across AI platforms. Summarize findings with sources.",
    "name": "research-compaction",
    "model": "gpt-4.1",
    "tools": ["read", "web_search", "web_fetch", "grep", "glob"]
  }
}
```

The sub-agent runs in the background. When it finishes, its result summary is automatically delivered to your session.

Check what's running:

```json
{
  "tool": "list_subagents"
}
```

---

## 2. Overview

Sub-agent spawning lets an agent delegate work to independent background sessions. Each sub-agent runs in its own isolated session with its own context window, model, and tool set.

### Why Sub-Agents?

| Problem | Solution |
|---------|----------|
| Deep-dive research consumes parent's context window | Sub-agent gets a fresh context window |
| Expensive model used for simple sub-tasks | Sub-agent can use a cheaper model (e.g., `gpt-4.1` instead of `claude-opus-4.6`) |
| Parent agent blocked while waiting for slow work | Sub-agent runs in the background; parent stays responsive |
| Unrestricted tool access for delegated tasks | Sub-agent's tool set is explicitly scoped |

### User Stories

1. **As an agent**, I want to spawn a research sub-agent so I can delegate deep-dive investigations while staying responsive.
2. **As an agent**, I want to use a cheaper model for sub-tasks that don't need high-end reasoning.
3. **As a user**, I want to see what sub-agents are running and their status.
4. **As a user**, I want sub-agent results delivered to my conversation automatically when they complete.
5. **As an agent**, I want to restrict which tools a sub-agent can access for safety.

---

## 3. Architecture

Sub-agent spawning extends the existing BotNexus session infrastructure. Sub-agents are full `GatewaySession` objects — not a parallel system.

### Parent–Child Session Model

```
Parent Session (agent: nova, model: claude-opus-4.6)
  │
  ├── Sub-Agent Session (model: gpt-4.1)
  │     task: "Research compaction strategies..."
  │     tools: [read, web_search, web_fetch]
  │     status: running → completed
  │
  └── Sub-Agent Session (model: gpt-4.1)
        task: "Analyze ADO work items..."
        tools: [read, bash, invoke_mcp]
        status: running
```

### Key Interfaces

<!-- DRAFT: verify against implementation -->

| Interface/Class | Project | Purpose |
|---|---|---|
| `ISubAgentManager` | `BotNexus.Gateway.Abstractions` | Core orchestration contract — `SpawnAsync`, `ListAsync`, `GetAsync`, `KillAsync`, `OnCompletedAsync` |
| `SubAgentSpawnRequest` | `BotNexus.Gateway.Abstractions` | Parameters for spawning a sub-agent |
| `SubAgentInfo` | `BotNexus.Gateway.Abstractions` | Status and metadata for a running or completed sub-agent |
| `SubAgentStatus` | `BotNexus.Gateway.Abstractions` | Enum: `Running`, `Completed`, `Failed`, `Killed`, `TimedOut` |
| `DefaultSubAgentManager` | `BotNexus.Gateway` | Implementation with session supervisor, store, and handle dependencies |
| `SubAgentSpawnTool` | `BotNexus.Gateway` | `IAgentTool` implementation for `spawn_subagent` |
| `SubAgentListTool` | `BotNexus.Gateway` | `IAgentTool` implementation for `list_subagents` |
| `SubAgentManageTool` | `BotNexus.Gateway` | `IAgentTool` implementation for `manage_subagent` |
| `SubAgentCompletionHook` | `BotNexus.Gateway` | Hook that fires when a sub-agent session ends, delivering results to parent |

### Relationship to Existing Sub-Agent Calls

BotNexus already has **synchronous** sub-agent calls via `IAgentCommunicator.CallSubAgentAsync()`. The new background spawning system builds *on top of* this existing infrastructure:

| Aspect | `CallSubAgentAsync` (existing) | `spawn_subagent` (new) |
|--------|-------------------------------|------------------------|
| Execution | Synchronous — parent waits | Asynchronous — parent continues |
| Session creation | Same (`IAgentSupervisor.GetOrCreateAsync`) | Same |
| Session ID format | `{parentSessionId}::sub::{childAgentId}` | `{parentSessionId}::sub::{childAgentId}::{uniqueId}` |
| Result delivery | Return value | `FollowUpAsync` into parent session |
| Use case | Simple delegation | Long-running research, parallel work |

---

## 4. Tools Reference

Sub-agent functionality is exposed through three focused tools, following the BotNexus convention of single-purpose tools (like `MemorySearchTool` / `MemoryGetTool`).

### `spawn_subagent`

Spawns a new background sub-agent session.

<!-- DRAFT: verify against implementation -->

**Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|:---:|---|---|
| `task` | string | Yes | — | Task description and initial prompt for the sub-agent |
| `name` | string | No | auto-generated | Human-readable label for identification |
| `model` | string | No | parent's model | LLM model override (e.g., `gpt-4.1`, `claude-sonnet-4.5`) |
| `tools` | string[] | No | parent's tools minus `spawn_subagent` | Explicit tool allowlist |
| `systemPrompt` | string | No | parent's system prompt | Override system prompt instructions |
| `maxTurns` | integer | No | `30` | Maximum conversation turns before auto-stop |
| `timeout` | integer | No | `600` | Timeout in seconds |

**Returns:**

```json
{
  "subAgentId": "sa_abc123",
  "sessionId": "session_xyz::sub::nova::sa_abc123",
  "status": "Running",
  "name": "research-compaction"
}
```

**Example — spawn a research agent on a cheaper model:**

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "task": "Research how major AI platforms (OpenAI, Anthropic, Google) handle context window compaction and summarization. Compare approaches, cite sources, and recommend an approach for BotNexus.",
    "name": "research-compaction",
    "model": "gpt-4.1",
    "tools": ["read", "web_search", "web_fetch", "grep", "glob"],
    "systemPrompt": "You are a thorough research assistant. Be comprehensive and cite all sources.",
    "maxTurns": 20,
    "timeout": 300
  }
}
```

### `list_subagents`

Lists all active sub-agents for the current session.

<!-- DRAFT: verify against implementation -->

**Parameters:** None.

**Returns:**

```json
{
  "subagents": [
    {
      "subAgentId": "sa_abc123",
      "name": "research-compaction",
      "status": "Running",
      "model": "gpt-4.1",
      "startedAt": "2026-04-10T16:00:00Z",
      "turnsUsed": 5,
      "task": "Research how context window compaction works..."
    },
    {
      "subAgentId": "sa_def456",
      "name": "ado-analysis",
      "status": "Completed",
      "model": "gpt-4.1",
      "startedAt": "2026-04-10T15:50:00Z",
      "completedAt": "2026-04-10T15:55:30Z",
      "turnsUsed": 12,
      "task": "Analyze ADO work items for sprint planning...",
      "resultSummary": "Found 14 active work items across 3 areas..."
    }
  ]
}
```

### `manage_subagent`

Performs management actions on a specific sub-agent.

<!-- DRAFT: verify against implementation -->

**Parameters:**

| Parameter | Type | Required | Description |
|---|---|:---:|---|
| `subAgentId` | string | Yes | The ID of the sub-agent to manage |
| `action` | string | Yes | Action to perform: `"kill"` or `"status"` |

**Actions:**

| Action | Description |
|--------|-------------|
| `kill` | Terminate a running sub-agent. Only the parent session can kill its own children. |
| `status` | Get detailed status of a specific sub-agent (same as the entry in `list_subagents`). |

**Returns (kill):**

```json
{
  "subAgentId": "sa_abc123",
  "status": "Killed",
  "resultSummary": null
}
```

**Returns (status):**

```json
{
  "subAgentId": "sa_abc123",
  "name": "research-compaction",
  "childSessionId": "session_xyz::sub::nova::sa_abc123",
  "status": "Running",
  "model": "gpt-4.1",
  "startedAt": "2026-04-10T16:00:00Z",
  "turnsUsed": 8,
  "task": "Research how context window compaction works..."
}
```

---

## 5. Configuration Reference

Sub-agent behavior is configured via `SubAgentOptions`, nested under the `gateway` configuration section.

<!-- DRAFT: verify against implementation -->

### `SubAgentOptions` Fields

| Field | Type | Default | Description |
|---|---|---|---|
| `maxConcurrentPerSession` | int | `5` | Maximum number of sub-agents a single session can run simultaneously |
| `defaultMaxTurns` | int | `30` | Default turn limit for sub-agents (overridable per spawn) |
| `defaultTimeoutSeconds` | int | `600` | Default timeout in seconds (overridable per spawn) |
| `maxDepth` | int | `1` | Maximum nesting depth. `1` = sub-agents cannot spawn sub-agents |
| `defaultModel` | string | `""` | Default model for sub-agents. Empty string means inherit parent's model |

### Configuration Example

```json
{
  "gateway": {
    "subAgents": {
      "maxConcurrentPerSession": 5,
      "defaultMaxTurns": 30,
      "defaultTimeoutSeconds": 600,
      "maxDepth": 1,
      "defaultModel": ""
    }
  }
}
```

### Overriding Defaults at Spawn Time

The `maxTurns` and `timeout` parameters on `spawn_subagent` override `defaultMaxTurns` and `defaultTimeoutSeconds` respectively. If not specified at spawn time, the configured defaults apply.

The `model` parameter at spawn time overrides `defaultModel`. If neither is set, the sub-agent inherits the parent agent's model.

---

## 6. Completion Flow

When a sub-agent finishes its work, results are automatically delivered to the parent session.

### How It Works

1. **Sub-agent completes** — it reaches a natural conclusion (final response with no tool calls), hits `maxTurns`, times out, or is killed.
2. **Completion hook fires** — `SubAgentCompletionHook` detects the session end and extracts the last assistant message as a result summary.
3. **Result delivered** — `ISubAgentManager.OnCompletedAsync()` is called, which uses `IAgentHandle.FollowUpAsync()` to inject the result into the parent session.
4. **Parent wakes** — If the parent is idle (waiting for user input), the follow-up triggers a new agent run. If the parent is mid-run, it processes the result between turns via the existing `PendingMessageQueue`.
5. **Parent processes** — The parent agent sees a message like:

```
[Sub-agent "research-compaction" completed]
<summary text from sub-agent's final response>
```

### Completion Statuses

| Status | Trigger | Result Summary |
|--------|---------|----------------|
| `Completed` | Agent finishes naturally (no more tool calls) | Last assistant message |
| `TimedOut` | `timeout` seconds elapsed | Last assistant message before timeout |
| `Failed` | Unrecoverable error during execution | Error description |
| `Killed` | Parent called `manage_subagent` with `action: "kill"` | `null` |

### First-Limit-Wins

If both `maxTurns` and `timeout` are set, whichever limit is hit first terminates the sub-agent. A `CancellationToken` from the timeout and a turn counter are both checked at the start of each loop iteration.

---

## 7. Security

### Tool Scoping

Sub-agents receive an **explicit tool allowlist** from the spawn request. If no tools are specified, the sub-agent inherits the parent's tool set *minus* `spawn_subagent`, `list_subagents`, and `manage_subagent` (recursion prevention).

All tool IDs in the allowlist are validated against the tool registry at spawn time. Invalid tool IDs cause the spawn to fail.

### No Working Directory Override

Sub-agents always use the parent agent's workspace directory. There is no `workingDir` parameter in Phase 1. This prevents agents from escaping their workspace sandbox.

### Recursion Prevention

Sub-agents cannot spawn sub-agents in Phase 1. This is enforced at two levels:

1. **Tool exclusion** — Sub-agent spawning tools are excluded from child session tool sets by `InProcessIsolationStrategy`.
2. **Depth limit** — `SubAgentOptions.MaxDepth` defaults to `1`. Even if a sub-agent somehow gets the spawn tool, the manager rejects the request.

### Session Isolation

Sub-agent sessions start with a **clean context** — only the `task` parameter and system prompt. No conversation history is carried over from the parent session. This prevents accidental context leaks.

### Ownership Enforcement

Only the parent session that spawned a sub-agent can kill or manage it. The `KillAsync` method on `ISubAgentManager` validates the `requestingSessionId` against the sub-agent's `parentSessionId`.

---

## 8. Phase 1 Limitations

The following capabilities are **not included** in Phase 1 and are planned for future phases:

| Limitation | Future Phase | Notes |
|---|---|---|
| **No `steer` action** | Phase 2 | Cannot send additional instructions to a running sub-agent |
| **No foreground (blocking) mode** | Phase 2 | All sub-agents run in the background |
| **No detailed `status` progress** | Phase 2 | Status shows state but not intermediate progress |
| **Depth limit = 1** | Phase 2 (configurable) | Sub-agents cannot spawn sub-agents |
| **No pre-defined sub-agent templates** | Phase 3 | No config-based sub-agent definitions |
| **No auto-delegation** | Phase 3 | No automatic task routing to sub-agents |
| **No shared memory** | Phase 3 | Parent and sub-agent don't share memory |

---

## 9. API Endpoints

<!-- DRAFT: verify against implementation -->

Sub-agents are also accessible via the REST API:

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/sessions/{sessionId}/subagents` | List active sub-agents for a session |
| `DELETE` | `/api/sessions/{sessionId}/subagents/{subAgentId}` | Kill a specific sub-agent |

### WebSocket Events

The following SignalR events are emitted on the parent session's group:

| Event | Payload | Triggered When |
|-------|---------|----------------|
| `subagent_spawned` | `SubAgentInfo` | A new sub-agent session starts |
| `subagent_completed` | `SubAgentInfo` (with `resultSummary`) | A sub-agent finishes successfully |
| `subagent_failed` | `SubAgentInfo` (with error details) | A sub-agent fails or times out |
| `subagent_killed` | `SubAgentInfo` (status `Killed`) | A sub-agent is explicitly killed by its parent session |

---

## 10. Examples

### Delegate Research While Staying Responsive

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "task": "Research the top 5 vector database solutions (Pinecone, Weaviate, Qdrant, Milvus, ChromaDB). Compare: pricing, performance benchmarks, .NET SDK availability, and hosted vs self-hosted options. Produce a comparison table.",
    "name": "vectordb-research",
    "model": "gpt-4.1",
    "tools": ["web_search", "web_fetch"],
    "maxTurns": 25,
    "timeout": 300
  }
}
```

### Cost-Optimize Bulk Analysis

Spawn a sub-agent using a cheaper model for routine analysis:

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "task": "Read all C# files in src/gateway/ and produce a summary of every public interface, listing method signatures and XML doc summaries.",
    "name": "interface-audit",
    "model": "gpt-4.1",
    "tools": ["read", "grep", "glob"]
  }
}
```

### Monitor and Manage Active Sub-Agents

List running sub-agents:

```json
{
  "tool": "list_subagents"
}
```

Kill a sub-agent that's taking too long:

```json
{
  "tool": "manage_subagent",
  "parameters": {
    "subAgentId": "sa_abc123",
    "action": "kill"
  }
}
```

Check a specific sub-agent's status:

```json
{
  "tool": "manage_subagent",
  "parameters": {
    "subAgentId": "sa_abc123",
    "action": "status"
  }
}
```
