# Sub-Agent Spawning

For agents and developers delegating bounded tasks and retrieving their results.

**Status:** Describes the tool-result completion contract introduced for [#4793](https://github.com/sytone/botnexus/issues/4793). An installed gateway must include this change to use awaited spawning and explicit joining.

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

```json
{
  "tool": "spawn_subagent",
  "parameters": {
    "background": true,
    "task": "Research how context window compaction works across AI platforms. Summarize findings with sources.",
    "name": "research-compaction",
    "model": "gpt-4.1",
    "tools": ["read", "web_search", "web_fetch", "grep", "glob"]
  }
}
```

This example requires the listed tools and a configured model. With `background: true`, the tool returns admission information, including `subAgentId`, while the child continues working. Admission is not a completed result. Save that ID and explicitly join the child when you need its output:

```json
{
  "tool": "manage_subagent",
  "parameters": {
    "subAgentId": "<subAgentId from spawn_subagent>",
    "action": "wait"
  }
}
```

`wait` returns the terminal result through its own tool response. There is no automatic completion message or extra parent turn. Omit `background` (or set it to `false`) to await the terminal result in the original `spawn_subagent` tool response instead.

Check what's running:

```json
{
  "tool": "list_subagents"
}
```

---

## 2. Overview

Sub-agent spawning lets an agent delegate work to independent child sessions. By default, the calling tool awaits the child's terminal result. Explicit `background: true` lets the parent continue before joining the child. Each sub-agent runs in its own isolated session with its own context window, model, and tool set.

### Why Sub-Agents?

| Problem | Solution |
|---------|----------|
| Deep-dive research consumes parent's context window | Sub-agent gets a fresh context window |
| Expensive model used for simple sub-tasks | Sub-agent can use a cheaper model (e.g., `gpt-4.1` instead of `claude-opus-4.6`) |
| Parent needs to continue while slow work runs | Explicit `background: true` returns admission; parent joins with `manage_subagent` / `wait` |
| Unrestricted tool access for delegated tasks | Sub-agent's tool set is explicitly scoped |

### User Stories

1. **As an agent**, I want to spawn a research sub-agent so I can delegate deep-dive investigations while staying responsive.
2. **As an agent**, I want to use a cheaper model for sub-tasks that don't need high-end reasoning.
3. **As a user**, I want to see what sub-agents are running and their status.
4. **As a user**, I want sub-agent results in the parent tool response, without an unsolicited completion message.
5. **As an agent**, I want to restrict which tools a sub-agent can access for safety.

---

## 3. Architecture

Sub-agent spawning extends the existing BotNexus session infrastructure. Sub-agents are full `GatewaySession` objects — not a parallel system.

### Parent–Child Session Model

```text
Parent Session (agent: my-agent, model: claude-opus-4.6)
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

| Interface/Class | Project | Purpose |
|---|---|---|
| `ISubAgentManager` | `BotNexus.Gateway.Contracts` | Core orchestration contract for spawning, joining, consuming results, listing, and stopping children |
| `SubAgentSpawnRequest` | `BotNexus.Domain` | Parameters for spawning a sub-agent |
| `SubAgentInfo` | `BotNexus.Domain` | Status and metadata for a running or completed sub-agent |
| `SubAgentStatus` | `BotNexus.Domain` | Enum: `Running`, `Completed`, `Failed`, `Killed`, `TimedOut`, `BudgetExhausted` |
| `DefaultSubAgentManager` | `BotNexus.Gateway` | Orchestrator with session supervision, persisted result retrieval, activity broadcasting, and timeout management |
| `SubAgentSpawnTool` | `BotNexus.Gateway` | `IAgentTool` implementation for `spawn_subagent` |
| `SubAgentListTool` | `BotNexus.Gateway` | `IAgentTool` implementation for `list_subagents` |
| `SubAgentManageTool` | `BotNexus.Gateway` | `IAgentTool` implementation for `manage_subagent` |

### Relationship to Existing Sub-Agent Calls

BotNexus already has **synchronous** sub-agent calls via `IAgentCommunicator.CallSubAgentAsync()`. The spawning system builds *on top of* this existing infrastructure:

| Aspect | `CallSubAgentAsync` (existing) | `spawn_subagent` (new) |
|--------|-------------------------------|------------------------|
| Execution | Synchronous — parent waits | Parent waits by default; explicit `background: true` returns admission |
| Session creation | Same (`IAgentSupervisor.GetOrCreateAsync`) | Same |
| Session ID format | `{parentSessionId}::sub::{childAgentId}` | `{parentSessionId}::subagent::{uniqueId}` |
| Result delivery | Return value | Original tool result by default; `manage_subagent` / `wait` for background work |
| Use case | Simple delegation | Long-running research, parallel work |

---

## 4. Tools Reference

Sub-agent functionality is exposed through three focused tools, following the BotNexus convention of single-purpose tools (like `MemorySearchTool` / `MemoryGetTool`).

### `spawn_subagent`

Spawns a child session and, by default, awaits its terminal result.

**Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|:---:|---|---|
| `task` | string | Yes | — | Task description and initial prompt for the sub-agent |
| `background` | boolean | No | `false` | Return admission immediately only when `true`; otherwise await the terminal result in this tool call |
| `name` | string | No | auto-generated | Human-readable label for this **run** (not the agent). Accepted in every mode, including alongside `targetAgentId` |
| `model` | string | No | parent's model | LLM model override (e.g., `gpt-4.1`, `claude-sonnet-4.5`) |
| `apiProvider` | string | No | parent's provider | API provider override for the sub-agent run |
| `tools` | string[] | No | parent's tools minus sub-agent management tools | Explicit tool allowlist; see Tool Scoping below |
| `systemPrompt` | string | No | parent's system prompt | Override system prompt instructions |
| `archetype` | string | No | `general` | Behavioural role profile. One of `researcher`, `coder`, `planner`, `reviewer`, `writer`, `general`. Narrows the sub-agent's tool set to the role unless `tools` is supplied. See [Built-in Agent Archetypes](/features/built-in-agents) |
| `targetAgentId` | string | No | - | Run the sub-agent as an already-registered named agent, using that agent's descriptor verbatim ("mirror" mode) instead of cloning the parent |
| `maxTurns` | integer | No | `30` | Maximum conversation turns before auto-stop |
| `timeoutSeconds` | integer | No | `600` | Timeout in seconds |
| `shareWorkspace` | boolean | No | `false` | Grants the sub-agent **read and write** access to the parent agent's whole workspace |
| `grantedPaths` | string[] | No | — | Grants **read-only** access to specific absolute paths beyond the sub-agent's own workspace. Writes and edits to these paths are refused |
| `grantedWritePaths` | string[] | No | — | Grants **read and write** access to specific absolute paths beyond the sub-agent's own workspace |

#### File access: which parameter confers write?

A sub-agent can always read and write inside its own temporary workspace. Beyond that:

| Grant | Read | Write | Scope |
|---|:---:|:---:|---|
| `grantedPaths` | Yes | **No** | The listed paths only |
| `grantedWritePaths` | Yes | **Yes** | The listed paths only |
| `shareWorkspace: true` | Yes | Yes | The parent agent's entire workspace |

Use `grantedWritePaths` when the sub-agent must produce files in a specific directory (for
example a git worktree) — it is narrower than `shareWorkspace`, which hands over the whole
parent workspace. Handing a write-capable sub-agent only `grantedPaths` logs a warning at
spawn time, because every `write`/`edit` outside its own workspace would otherwise be
refused mid-run (#2650).

#### Embody vs mirror: `targetAgentId` is exclusive

A spawn is either **embody** (a role, optionally customised) or **mirror** (an existing named
agent, verbatim) - never both. Supplying `targetAgentId` together with any embody-only
**descriptor** field (`model`, `apiProvider`, `tools`, `systemPrompt`, `archetype`) is rejected
with an error naming only the conflicting fields actually supplied. Mirror mode is strict
pass-through of the target's descriptor; only `task` differs from that agent's normal operation.
Reserved archetype ids are never valid `targetAgentId` values.

`name` is **not** a descriptor field and is accepted in mirror mode (#3570): it labels the *run*
(titling the child conversation and coming back on the run's `name`), exactly like the
run-scoped `maxTurns` and `timeoutSeconds`, and changes nothing about the mirrored agent.

#### Archetypes and shell access

Archetypes carry different toolsets. Notably, `coder` includes `shell`, `exec` and `process`;
`writer` deliberately does **not** — it has `read`, `write`, `edit`, `glob`, `grep`,
`web_search`, `web_fetch` and `memory_search` only. Delegating work that needs to run
commands (git, builds, test runs) to a `writer` will fail for lack of those tools regardless
of the file grants; use `coder`, or pass an explicit `tools` allowlist.

**Returns:** By default, the terminal outcome and child output are returned in the original tool response. Identity and budget disclosures remain available. Failed, timed-out, cancelled, or budget-exhausted work is not presented as successful completion; read any available partial output together with its outcome.

With explicit `background: true`, the response is admission information. Illustrative admission response (not terminal output):

```json
{
  "subAgentId": "a1b2c3d4e5f6...",
  "sessionId": "parent-session-id::subagent::a1b2c3d4e5f6...",
  "status": "Running",
  "name": "research-compaction"
}
```

**On a spawn failure (#2633):** a spawn that fails for a configuration reason — most commonly
the descriptor naming a `model` that is not registered for its `apiProvider` — is returned to the
calling agent as a tool error rather than escaping as a host fault:

```json
{
  "error": "Model 'gpt-4.1' for provider 'github-copilot-messages' is not registered",
  "Status": "failed"
}
```

The underlying message names both the model and the provider, so the requesting agent can correct
the override and retry. Cancellation is *not* treated as a failure — it keeps propagating so the
executor can unwind the turn.

**Example — spawn a research agent on a configured cheaper model and await its result:**

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
    "timeoutSeconds": 300
  }
}
```

### `list_subagents`

Lists active and completed sub-agents for the current session.

**Parameters:** None.

**Returns:**

```json
{
  "subAgents": [
    {
      "subAgentId": "a1b2c3d4e5f6...",
      "name": "research-compaction",
      "status": "Running",
      "model": "gpt-4.1",
      "startedAt": "2026-04-10T16:00:00Z",
      "turnsUsed": 5,
      "task": "Research how context window compaction works..."
    },
    {
      "subAgentId": "d4e5f6a1b2c3...",
      "name": "ado-analysis",
      "status": "Completed",
      "model": "gpt-4.1",
      "startedAt": "2026-04-10T15:50:00Z",
      "completedAt": "2026-04-10T15:55:30Z",
      "turnsUsed": 12,
      "task": "Analyze ADO work items for sprint planning..."
    }
  ]
}
```

### `manage_subagent`

Performs management actions on a specific sub-agent.

**Parameters:**

| Parameter | Type | Required | Description |
|---|---|:---:|---|
| `subAgentId` | string | Yes | The ID of the sub-agent to manage |
| `action` | string | Yes | Action to perform: `"kill"`, `"status"`, or `"wait"` |

**Actions:**

| Action | Description |
|--------|-------------|
| `kill` | Terminate a running sub-agent. Only the parent session can kill its own children. |
| `status` | Return current details without waiting for a running child. If the child is terminal, retrieve and consume its result through the same path as `wait`. |
| `wait` | Join a child: await its terminal outcome and return its result to the owning parent. |

Terminal child output is consumed once across the default spawn response and management calls. A later `wait` or terminal `status` returns identity and an `alreadyConsumed` indication, not another copy of child output. Listing children is for discovery and monitoring, not a replacement for joining or consuming results.

**Returns (kill):**

```json
{
  "subAgentId": "a1b2c3d4e5f6...",
  "killed": true
}
```

**Illustrative response (status while running):**

```json
{
  "subAgentId": "a1b2c3d4e5f6...",
  "status": "Running",
  "resultSummary": null,
  "startedAt": "2026-04-10T16:00:00Z",
  "completedAt": null
}
```

---

## 5. Configuration Reference

Sub-agent behavior is configured via `SubAgentOptions`, nested under the `gateway` configuration section.

### `SubAgentOptions` Fields

| Field | Type | Default | Description |
|---|---|---|---|
| `maxConcurrentPerSession` | int | `5` | Maximum number of sub-agents a single session can run simultaneously |
| `defaultMaxTurns` | int | `30` | Default turn limit for sub-agents (overridable per spawn) |
| `maxTurnsCeiling` | int | `30` | Hard upper bound for a spawn-supplied `maxTurns`. Requests above this are clamped down. `0` disables the ceiling |
| `advisoryMaxTurns` | int | `30` | Effective turn budget above which staging guidance is returned and logged. `0` disables this advisory |
| `defaultTimeoutSeconds` | int | `600` | Default timeout in seconds (overridable per spawn) |
| `maxTimeoutSeconds` | int | `1800` | Hard upper bound for a spawn-supplied `timeoutSeconds`. Requests above this are clamped down. `0` disables the ceiling |
| `advisoryTimeoutSeconds` | int | `1500` | Effective timeout above which staging guidance is returned and logged. `0` disables this advisory |
| `maxDepth` | int | `1` | Maximum nesting depth. `1` = sub-agents cannot spawn sub-agents |
| `defaultModel` | string | `""` | Default model for sub-agents. Empty string means inherit parent's model |

### Configuration Example

```json
{
  "gateway": {
    "subAgents": {
      "maxConcurrentPerSession": 5,
      "defaultMaxTurns": 30,
      "maxTurnsCeiling": 30,
      "advisoryMaxTurns": 30,
      "defaultTimeoutSeconds": 600,
      "maxTimeoutSeconds": 1800,
      "advisoryTimeoutSeconds": 1500,
      "maxDepth": 1,
      "defaultModel": ""
    }
  }
}
```

### Overriding Defaults at Spawn Time

The `maxTurns` and `timeoutSeconds` parameters on `spawn_subagent` override `defaultMaxTurns` and `defaultTimeoutSeconds` respectively. If not specified at spawn time, the configured defaults apply.

Both are bounded by hard ceilings: a spawn-supplied `maxTurns` is clamped to at most `maxTurnsCeiling` (default `30`) and `timeoutSeconds` to at most `maxTimeoutSeconds` (default `1800`). This prevents a single `spawn_subagent` call from requesting a runaway turn budget or an effectively unbounded wall-clock timeout. Set a ceiling to `0` to disable it.

Keep delegated tasks narrow and staged instead of raising these ceilings. When exploratory work reaches
the reserved boundary, the last turn within `maxTurns` is used once for tool-free synthesis; it is not an
extra turn. Exploration also stops before the absolute timeout, reserving 10% of the configured duration
(bounded to 100 ms through 30 seconds) for that attempt. Finalization retains the original deadline, and
late text is discarded. Separate investigation, implementation, and validation spawns are easier to bound and usually
produce more reliable handoffs than one large task with a larger turn or timeout budget.

#### Clamp disclosure on the tool result

When a ceiling actually reduces the request, the `spawn_subagent` result carries a `budgetClamp` object in both default and background modes so the
calling agent can re-scope the delegated task against the budget it really has (issue #2789). The field is
emitted **only** when something was clamped - its presence is the signal, so a result without it needs no
interpretation.

Illustrative background admission response with a clamp:

```json
{
  "subAgentId": "sub_abc123",
  "sessionId": "...",
  "conversationId": "...",
  "status": "Running",
  "name": "researcher",
  "budgetClamp": {
    "policyTier": "...",
    "maxTurnsClamped": true,
    "requestedMaxTurns": 100,
    "effectiveMaxTurns": 30,
    "timeoutSecondsClamped": false,
    "requestedTimeoutSeconds": 600,
    "effectiveTimeoutSeconds": 600,
    "notice": "Your requested budget exceeded a configured ceiling and was reduced. Scope the delegated task to the effective values above, not the requested ones."
  }
}
```

When an effective budget is above an enabled advisory threshold, the result also carries a
`budgetAdvisory` warning that names the threshold and effective value and recommends delegating one
coherent stage. This is guidance only: it does not prove that a task is well scoped, and it never
changes, rejects, or exempts the budget from the hard ceiling. A request above a ceiling can therefore
carry both `budgetClamp` and `budgetAdvisory`, with the advisory always reporting the effective values.

The clamp itself is unchanged by this disclosure - only its visibility. The reduction is still recorded in the
gateway log as well.

The `model` parameter at spawn time overrides `defaultModel`. If neither is set, the sub-agent inherits the parent agent's model.

---

## 6. Completion Flow

Completion is a tool result, not a new inbound message to the parent.

### How It Works

1. **Spawn admits the child** — the manager creates the isolated child session with its tool grants and effective budgets.
2. **Choose when to join** — by default, `spawn_subagent` waits. With explicit `background: true`, it returns admission and the parent later calls `manage_subagent` with `action: "wait"`.
3. **Child reaches a terminal outcome** — it finishes, exhausts its budget, times out, fails, or is stopped. Available output and outcome evidence are retained together.
4. **Return through the waiting tool** — the default spawn or explicit join returns the terminal result. A `status` call that finds a terminal child may consume it through the same path.
5. **Consume once** — repeated calls retain the run's identity and report `alreadyConsumed`; they do not repeat child output. No synthetic inbound completion message is dispatched, and completion does not start an extra parent turn.

The child's complete history remains in its own transcript. In the parent conversation, expand the normal tool result to inspect the returned outcome; open the child session for detailed messages and tool history.

### Retrieval After a Manager Restart

An unconsumed, persisted completed result remains retrievable by the owning parent after a cold manager restart. Consumption state must also survive the restart so previously consumed output is not delivered again.

A persisted running record does not prove that execution survived a restart. When no live run exists, retrieval returns `Failed` with an interruption diagnostic rather than waiting indefinitely or silently rerunning the task. Inspect the child transcript and any artifacts before deciding whether to start a separate replacement run.

Consumption and the original parent tool-result row are retained together. Retrying the same tool call returns its retained result; a different call receives `alreadyConsumed` without repeating the child summary. Cancelling a join before consumption leaves the child result available. The child continues under its own enforced budget.

### Completion Statuses

| Status | Trigger | Result Summary |
|--------|---------|----------------|
| `Completed` | Agent finishes naturally, including an audit-backed recovered tool attempt | Last assistant message; recovered runs include a `completed-with-recovered-errors` evidence header |
| `TimedOut` | Effective `timeoutSeconds` elapsed | Timeout diagnostic and available structured partial evidence |
| `Failed` | Terminal provider error or an unrecovered tool failure | Error description |
| `Killed` | Parent called `manage_subagent` with `action: "kill"` | Cancellation diagnostic and any retained evidence |
| `BudgetExhausted` | `maxTurns` reached before a final response | Budget diagnostic and available structured partial evidence |

A tool failure is classified as recovered only when the ordered audit timeline shows that the
immediately following invocation used the same tool and succeeded. The terminal tool result names the
tool and both call IDs, while the child transcript retains every failed and successful audit row.
Final prose, a later unrelated success, or a non-adjacent invocation cannot convert an unresolved
failure into success. Terminal provider errors remain failures even when tool retries recovered.

### First-Limit-Wins

If both `maxTurns` and `timeout` are set, whichever limit is hit first terminates the sub-agent. A `CancellationToken` from the timeout and a turn counter are both checked at the start of each loop iteration.

The two limits report **different terminal statuses** so the cause is never ambiguous: running
out of wall clock is `TimedOut`, running out of turns is `BudgetExhausted`. The remedy differs
accordingly - a longer `timeoutSeconds` for the former, a larger `maxTurns` or a narrower task
for the latter. `BudgetExhausted` is latched before cancellation, so a budget stop is never
misreported as a timeout.

---

## 7. Security

### Tool Scoping

Sub-agents receive an **explicit tool allowlist** from the spawn request. If no tools are specified, the sub-agent inherits the parent's tool set *minus* `spawn_subagent`, `list_subagents`, and `manage_subagent` (recursion prevention).

All tool IDs in the allowlist are validated against the tool registry at spawn time. Invalid tool IDs cause the spawn to fail.

### No Working Directory Override

Sub-agents use their own isolated workspace by default. There is no `workingDir` spawn parameter. Access beyond that workspace requires `grantedPaths`, `grantedWritePaths`, or `shareWorkspace`, with the read/write boundaries described above. Background mode and joining do not widen those grants.

### Recursion Prevention

Sub-agents cannot spawn sub-agents in Phase 1. This is enforced at two levels:

1. **Tool exclusion** — Sub-agent spawning tools are excluded from child session tool sets by `InProcessIsolationStrategy`.
2. **Depth limit** — `SubAgentOptions.MaxDepth` defaults to `1`. Even if a sub-agent somehow gets the spawn tool, the manager rejects the request.

### Session Isolation

Sub-agent sessions start with a **clean context** — only the `task` parameter and system prompt. No conversation history is carried over from the parent session. This prevents accidental context leaks.

### Ownership Enforcement

Only the parent session that spawned a sub-agent can kill, join, or consume its result. Retrieval after a restart must enforce the same ownership boundary.

---

## 8. Phase 1 Limitations

The following capabilities are **not included** in Phase 1 and are planned for future phases:

| Limitation | Future Phase | Notes |
|---|---|---|
| **No `steer` action** | Phase 2 | Cannot send additional instructions to a running sub-agent |
| **No detailed `status` progress** | Phase 2 | Status shows state but not intermediate progress |
| **Depth limit = 1** | Phase 2 (configurable) | Sub-agents cannot spawn sub-agents |
| **No pre-defined sub-agent templates** | Phase 3 | No config-based sub-agent definitions |
| **No auto-delegation** | Phase 3 | No automatic task routing to sub-agents |
| **No shared memory** | Phase 3 | Parent and sub-agent don't share memory |

---

## 9. API Endpoints

Sub-agents are also accessible via the REST API:

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/sessions/{sessionId}/subagents` | List active sub-agents for a session |
| `DELETE` | `/api/sessions/{sessionId}/subagents/{subAgentId}` | Kill a specific sub-agent |

### SignalR Events

The following SignalR events are emitted on the parent session's group for UI activity updates. These are not inbound model messages, do not consume the terminal tool result, and do not start a parent turn:

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
    "background": true,
    "model": "gpt-4.1",
    "tools": ["web_search", "web_fetch"],
    "maxTurns": 25,
    "timeoutSeconds": 300
  }
}
```

After admission, continue independent parent work and then join with `manage_subagent` / `wait` as shown in Quick Start. Do not wait for an automatic completion message.

### Cost-Optimize Bulk Analysis

Spawn a sub-agent using a configured cheaper model for routine analysis. This example requires read access to the source tree (grant its absolute path through `grantedPaths` if it is outside the child's workspace). With `background` omitted, the spawn tool awaits the result:

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
    "subAgentId": "a1b2c3d4e5f6...",
    "action": "kill"
  }
}
```

Check a specific sub-agent's status. If it is already terminal and unconsumed, this call consumes its result:

```json
{
  "tool": "manage_subagent",
  "parameters": {
    "subAgentId": "a1b2c3d4e5f6...",
    "action": "status"
  }
}
```
