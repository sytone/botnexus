# Agents API Reference

Reference for the **Agents** endpoints — registration, lifecycle, instance status, and
context introspection.

All endpoints are served under the base route `api/agents` and require the gateway API key
(see [Authentication](README.md#authentication)).

Source: `src/gateway/BotNexus.Gateway.Api/Controllers/AgentsController.cs`.

---

## Endpoints

| Verb | Route | Purpose |
|------|-------|---------|
| GET | `/api/agents` | List registered agents. |
| GET | `/api/agents/{agentId}` | Get one agent descriptor. |
| POST | `/api/agents` | Register a new agent. |
| PUT | `/api/agents/{agentId}` | Update an existing agent descriptor. |
| DELETE | `/api/agents/{agentId}` | Unregister an agent. |
| GET | `/api/agents/instances` | List all active agent instances. |
| GET | `/api/agents/{agentId}/health` | Runtime health across an agent's instances. |
| GET | `/api/agents/{agentId}/sessions/{sessionId}/status` | Status of one running instance. |
| POST | `/api/agents/{agentId}/sessions/{sessionId}/stop` | Stop a running instance. |
| GET | `/api/agents/{agentId}/sessions/{sessionId}/context` | Context/token usage summary. |
| GET | `/api/agents/{agentId}/sessions/{sessionId}/context/system-prompt` | Full system prompt. |
| GET | `/api/agents/{agentId}/sessions/{sessionId}/context/tools` | Tool definitions in context. |
| POST | `/api/agents/{agentId}/sessions/{sessionId}/context/export` | Export full context to disk. |

---

## Registration & lifecycle

### `GET /api/agents`

| Parameter | In | Type | Default | Notes |
|-----------|----|------|---------|-------|
| `includeSubAgents` | query | bool | `false` | Include runtime-spawned sub-agent descriptors (e.g. "Farnsworth (coder)"). These are ephemeral children created via `spawn_subagent` and are hidden by default. |
| `includeBuiltin` | query | bool | `false` | Include built-in platform archetype agents (researcher, coder, planner, reviewer, writer, analyst). They are spawn/converse targets rather than top-level user-created agents. |

Returns `200 OK` with an array of `AgentListItem` - a **lean list projection**, not the full
`AgentDescriptor` (#2755). The domain model was previously serialised directly, putting 36
properties per agent on the portal's cold-boot path where consumers read at most seven, and
broadcasting `systemPrompt` / `fileAccess` / `toolPolicy` / `extensionConfig` on a call that is
unauthenticated by default. Clients needing the full shape use `GET /api/agents/{agentId}`.

| Field | Type | Notes |
|-------|------|-------|
| `agentId` | string | Stable agent identifier. |
| `displayName` | string | Human-readable name shown in pickers and the sidebar. |
| `emoji` | string? | Optional emoji rendered beside the display name. |
| `description` | string? | Optional short description shown in the agent list. |
| `isBuiltIn` | bool | Whether this is a built-in platform archetype agent. |
| `apiProvider` | string | Provider instance key. |
| `modelId` | string | Model identifier. |

By default only first-class, user-facing agents are returned, so the portal agent picker is not
cluttered with infrastructure descriptors.

### `GET /api/agents/{agentId}`

Returns `200 OK` with the `AgentDescriptor`, or `404 Not Found`.

### `POST /api/agents`

Body: an `AgentDescriptor`.

The create path is **failure-atomic**: the candidate descriptor is fully validated, then
persisted to `config.json` *before* the in-memory registry is mutated, so a disk failure
leaves no runtime/config divergence. If provisioning (heartbeat, skill review) fails after
the registry commit, both the registry entry and the freshly-written config are rolled back.

| Status | Condition |
|--------|-----------|
| `201 Created` | Registered. `Location` points at `GET /api/agents/{agentId}`; body is the descriptor. |
| `400 Bad Request` | `Kind = SubAgent` (reserved for runtime-spawned sub-agents), or descriptor validation failed. Body: `{ "error": "..." }`. |
| `409 Conflict` | An agent with that id is already registered. |
| `500` | Config persistence or provisioning failed (after rollback). |

### `PUT /api/agents/{agentId}`

Body: an `AgentDescriptor`. Same failure-atomic ordering as create — validate, persist,
then commit the registry; a provisioning failure restores both registry and config to the
previous descriptor.

| Status | Condition |
|--------|-----------|
| `200 OK` | Updated; body is the descriptor. |
| `400 Bad Request` | Route `agentId` does not match the payload `agentId`, `Kind = SubAgent`, or validation failed. |
| `404 Not Found` | No such agent. |
| `500` | Persistence or provisioning failed (after rollback). |

### `DELETE /api/agents/{agentId}`

Deletes the persisted config **before** dropping the registry entry, so a disk failure
leaves the agent both registered and persisted (consistent) rather than
live-in-registry-only.

Returns `204 No Content`, or `500` when config deletion fails.

> Successful create, update, and delete each publish an `AgentsChanged` notification
> (`changeType` = `added` / `updated` / `removed`) to registered change notifiers — see the
> [SignalR hub reference](signalr.md#agentschangedpayload).

---

## Instances & health

### `GET /api/agents/instances`

Returns `200 OK` with every active `AgentInstance` known to the supervisor.

### `GET /api/agents/{agentId}/sessions/{sessionId}/status`

Returns `200 OK` with the `AgentInstance`, or `404 Not Found` when no such instance is running.

### `POST /api/agents/{agentId}/sessions/{sessionId}/stop`

Stops the instance. Returns `204 No Content`.

### `GET /api/agents/{agentId}/health`

Returns `404 Not Found` when the agent is not registered. Otherwise `200 OK` with:

| Field | Type | Notes |
|-------|------|-------|
| `status` | string | `healthy`, `unhealthy`, or `unknown`. |
| `agentId` | string | The agent id. |
| `instanceCount` | int | Number of live instances found. |

`unknown` is returned when there are no instances, or when no instance handle supports a
health ping. A single failing ping makes the whole response `unhealthy`.

---

## Context introspection

These four endpoints require a live handle for `{agentId}/{sessionId}` that supports
diagnostics. Both preconditions return `404 Not Found` with a plain-text reason:
`No active handle.` or `Handle does not support diagnostics.`

### `GET /api/agents/{agentId}/sessions/{sessionId}/context`

`200 OK`, with a token-usage summary. `contextWindowTokens` is the context window of the model
the session is actually bound to (after the conversation > agent override stack), and
`usagePercent` is computed against it. When the window cannot be resolved - for example the
handle exposes no model binding - both fields are `null` rather than a placeholder value, so a
consumer can tell "unknown" apart from a real number (#3091).

```json
{
  "agentId": "farnsworth",
  "sessionId": "sess-123",
  "totalEstimatedTokens": 24310,
  "contextWindowTokens": 200000,
  "usagePercent": 12.2,
  "sections": {
    "systemPrompt": { "tokens": 4120, "chars": 16480 },
    "toolDefinitions": { "tokens": 8600, "toolCount": 42 },
    "conversationHistory": { "tokens": 11590, "entryCount": 87 }
  }
}
```

### `GET /api/agents/{agentId}/sessions/{sessionId}/context/system-prompt`

`200 OK` with `{ "systemPrompt": "...", "chars": 16480, "estimatedTokens": 4120 }`.

### `GET /api/agents/{agentId}/sessions/{sessionId}/context/tools`

`200 OK` with `{ "toolCount": 42, "tools": [ ... ] }`.

### `POST /api/agents/{agentId}/sessions/{sessionId}/context/export`

Serializes the full context diagnostics to
`<BotNexus home>/logs/context-export-{agentId}-{yyyyMMddHHmmss}.json` and returns
`200 OK` with `{ "exported": "<absolute path>" }`.

---

## See also

- [Sessions API](sessions.md) — the session-side counterpart of these routes
- [SignalR hub reference](signalr.md) — `GetAgentStatus` is the hub equivalent of the status route
- [REST API overview](README.md)
