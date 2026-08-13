# BotNexus API Reference

Complete reference for BotNexus REST API endpoints, including agents, sessions, providers, skills, and system status.

## Table of Contents

1. [Overview](#overview)
2. [Sparse Fieldsets](#sparse-fieldsets-fields)
3. [Authentication](#authentication)
3. [Agent Management](#agent-management)
4. [Skills Management](#skills-management)
5. [Channels Management](#channels-management)
6. [Extensions Management](#extensions-management)
7. [Chat](#chat)
8. [Commands](#commands)
9. [Session Management](#session-management)
10. [System & Status](#system--status)
11. [Error Handling](#error-handling)
12. [Webhooks](#webhooks)

---

## Overview

**Base URL:** `http://localhost:5005/api`

All endpoints follow REST conventions and return JSON responses. The default port is **5005** (configurable via `config.json`).

**Authentication:** All endpoints require API key authentication (see [Authentication](#authentication) below).

---

## Sparse Fieldsets (`?fields=`)

Any `GET` endpoint accepts an optional `?fields=` query parameter that projects each returned
object down to only the requested **top-level** fields. This is useful for reducing payload size
and bandwidth when a client only needs a few properties.

**Default is everything.** When `?fields=` is omitted (or empty/whitespace), the response is the
full object, byte-for-byte identical to the behaviour before this feature existed. Sparse fieldsets
are strictly additive and never change the default response shape.

### Rules

- **Comma-separated:** `?fields=agentId,displayName`.
- **Case-insensitive:** field names are matched case-insensitively against the JSON property names
  (`?fields=agentid,DISPLAYNAME` works).
- **Lenient / unknown fields ignored:** unknown field names are silently dropped and never produce a
  `400`. If none of the requested names match, an empty object `{}` is returned per item.
- **Empty parameter = full object:** `?fields=` or `?fields=%20%20` returns the full object.
- **Applies to lists and single items:** both array responses (list endpoints) and single-object
  responses (get-by-id endpoints) are projected. For arrays, every element is projected.
- **Top-level only:** nested field selection (e.g. `?fields=metadata.builtin`) is **not** supported
  in v1. Only top-level properties are projected.
- **Errors pass through:** non-2xx responses and `ProblemDetails` error bodies are never projected.

### Examples

```http
GET /api/agents?fields=agentId,displayName
X-Api-Key: your-api-key
```

```json
[
  { "agentId": "farnsworth", "displayName": "Farnsworth" },
  { "agentId": "nibbler", "displayName": "Nibbler" }
]
```

```http
GET /api/agents/farnsworth?fields=agentId,displayName
X-Api-Key: your-api-key
```

```json
{ "agentId": "farnsworth", "displayName": "Farnsworth" }
```

The same parameter works on `GET /api/conversations` and `GET /api/conversations/{id}` and, by
virtue of being a global result filter, on all other `GET` endpoints.

---

## Authentication

### X-Api-Key Header

Include your API key in the `X-Api-Key` request header:

```http
GET /api/agents
X-Api-Key: your-api-key-here
```

Or pass it as a query parameter:

```http
GET /api/agents?apiKey=your-api-key-here
```

**Exemptions:** `/health` and `/swagger` are exempt from authentication. The Blazor WebUI is also exempt when running in development mode.

### Request & Response Headers

#### Correlation ID (`X-Correlation-Id`)

Every request/response carries a correlation identifier for end-to-end tracing:

- **Request:** Optionally include `X-Correlation-Id` to propagate your own trace ID.
- **Response:** The header is always returned. If not provided on the request, the server generates a new UUID.

```http
GET /api/agents
X-Api-Key: your-api-key
X-Correlation-Id: my-trace-id-123
```

Use this header to correlate client requests with server-side logs.

#### Rate Limiting

HTTP REST requests are rate-limited per client (authenticated caller ID or IP address). Default: **60 requests per 60-second window** (configurable via `gateway.rateLimit` in `config.json`).

When the limit is exceeded, the server returns:

| Status | Header | Description |
|--------|--------|-------------|
| `429 Too Many Requests` | `Retry-After` | Seconds until the current window resets |

The `/health` endpoint is exempt from rate limiting.

**Configuration:**
```json
{
  "gateway": {
    "rateLimit": {
      "requestsPerMinute": 60,
      "windowSeconds": 60
    }
  }
}
```

### CORS

Cross-Origin Resource Sharing is environment-aware:

| Environment | Behavior |
|---|---|
| **Development** | All origins, methods, and headers allowed (`AllowAny*`) |
| **Production** | Origins restricted to `gateway.cors.allowedOrigins` config (defaults to `http://localhost:5005`); methods restricted to `GET`, `POST`, `PUT`, `DELETE`, `OPTIONS` |

Configure production origins in `config.json`:
```json
{
  "gateway": {
    "cors": {
      "allowedOrigins": ["https://your-domain.com"]
    }
  }
}
```

---

## Agent Management

### List All Agents

**Endpoint:** `GET /api/agents`

**Description:** Retrieve a list of registered agents. By default only first-class,
user-facing agents are returned. Runtime-spawned sub-agents and built-in platform
archetype agents (researcher, coder, planner, reviewer, writer, analyst) are excluded
so the portal agent picker is not cluttered with infrastructure descriptors.

**Query parameters:**
- `includeSubAgents` (bool, optional, default `false`) — include runtime-spawned
  sub-agent descriptors (e.g. `"Farnsworth (coder)"`). Useful for diagnostics.
- `includeBuiltin` (bool, optional, default `false`) — include built-in platform
  archetype agents that serve as `spawn_subagent` / `agent_converse` targets.

**Request:**
```http
GET /api/agents
X-Api-Key: your-api-key
```

To include everything (parity with the previous unfiltered behaviour):
```http
GET /api/agents?includeSubAgents=true&includeBuiltin=true
```

**Response:** 200 OK
```json
[
  {
    "agentId": "assistant",
    "displayName": "Assistant",
    "modelId": "gpt-4.1",
    "apiProvider": "copilot",
    "systemPrompt": null,
    "isolationStrategy": "in-process",
    "toolIds": [],
    "subAgentIds": [],
    "maxConcurrentSessions": 0
  },
  {
    "agentId": "analyzer",
    "displayName": "Analyzer",
    "modelId": "claude-sonnet-4-5",
    "apiProvider": "anthropic",
    "systemPrompt": null,
    "isolationStrategy": "in-process",
    "toolIds": [],
    "subAgentIds": [],
    "maxConcurrentSessions": 0
  }
]
```

---

### Get Agent Details

**Endpoint:** `GET /api/agents/{agentId}`

**Description:** Retrieve a specific agent by ID.

**Parameters:**
- `agentId` (string, path) — Agent ID

**Request:**
```http
GET /api/agents/my-agent
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "agentId": "my-agent",
  "displayName": "My Agent",
  "modelId": "gpt-4.1",
  "apiProvider": "copilot",
  "systemPrompt": "You are helpful",
  "isolationStrategy": "in-process",
  "toolIds": [],
  "subAgentIds": [],
  "maxConcurrentSessions": 0
}
```

**Error Responses:**
- `404 Not Found` — Agent does not exist

---

### Create Agent

**Endpoint:** `POST /api/agents`

**Description:** Create a new agent with the specified configuration.

**Request Body:**
```json
{
  "agentId": "my-agent",
  "displayName": "My Agent",
  "modelId": "gpt-4.1",
  "apiProvider": "copilot",
  "systemPrompt": "You are a helpful assistant",
  "isolationStrategy": "in-process"
}
```

**Field Descriptions:**
- `agentId` (string, required) — Unique agent identifier
- `displayName` (string, required) — Human-readable display name
- `modelId` (string, required) — Model identifier (e.g., "gpt-4.1", "claude-sonnet-4-5")
- `apiProvider` (string, required) — Provider name (e.g., "copilot", "openai", "anthropic")
- `systemPrompt` (string, optional) — System instruction for the agent
- `systemPromptFile` (string, optional) — Path to an external system prompt file
- `isolationStrategy` (string, optional) — Execution strategy (default: "in-process")
- `toolIds` (array of strings, optional) — Tool identifiers the agent can access
- `subAgentIds` (array of strings, optional) — Agent IDs this agent can call as sub-agents
- `maxConcurrentSessions` (integer, optional) — Max concurrent sessions (0 = unlimited)

**Request:**
```http
POST /api/agents
X-Api-Key: your-api-key
Content-Type: application/json

{
  "agentId": "analyzer",
  "displayName": "Analyzer",
  "modelId": "gpt-4.1",
  "apiProvider": "copilot",
  "systemPrompt": "Analyze data professionally",
  "isolationStrategy": "in-process"
}
```

**Response:** 201 Created
```json
{
  "agentId": "analyzer",
  "displayName": "Analyzer",
  "modelId": "gpt-4.1",
  "apiProvider": "copilot",
  "systemPrompt": "Analyze data professionally",
  "isolationStrategy": "in-process",
  "toolIds": [],
  "subAgentIds": [],
  "maxConcurrentSessions": 0
}
```

**Side Effects:**
- Config is backed up to `config.json.bak`
- Agent workspace is bootstrapped with template files (SOUL.md, IDENTITY.md, etc.)
- Agent name is normalized to lowercase with dashes (e.g., "My Agent" → my-agent")

**Error Responses:**
- `400 Bad Request` — Invalid configuration or duplicate agent name
- `500 Internal Server Error` — Workspace creation failed

---

### Update Agent

**Endpoint:** `PUT /api/agents/{agentId}`

**Description:** Update an existing agent descriptor in-place. If the request body omits `agentId`, the route value is used. If both are provided, they must match — a mismatch returns 400 Bad Request.

**Parameters:**
- `agentId` (string, path) — The registered agent ID to update

**Request Body:** Same schema as [Create Agent](#create-agent).

**Request:**
```http
PUT /api/agents/my-agent
X-Api-Key: your-api-key
Content-Type: application/json

{
  "agentId": "my-agent",
  "displayName": "My Updated Agent",
  "modelId": "claude-sonnet-4-5",
  "apiProvider": "anthropic",
  "systemPrompt": "You are a refined assistant"
}
```

**Response:** 200 OK
```json
{
  "agentId": "my-agent",
  "displayName": "My Updated Agent",
  "modelId": "claude-sonnet-4-5",
  "apiProvider": "anthropic",
  "systemPrompt": "You are a refined assistant",
  "isolationStrategy": "in-process",
  "toolIds": [],
  "subAgentIds": [],
  "maxConcurrentSessions": 0
}
```

**Error Responses:**
- `400 Bad Request` — Body `agentId` does not match the route `agentId`
- `404 Not Found` — Agent with the given ID is not registered

---

### Delete Agent

**Endpoint:** `DELETE /api/agents/{agentId}`

**Description:** Unregister an agent.

**Parameters:**
- `agentId` (string, path) — Agent ID

**Request:**
```http
DELETE /api/agents/my-agent
X-Api-Key: your-api-key
```

**Response:** 204 No Content

**Note:** This operation is idempotent — no error is returned if the agent does not exist.

---

### List Active Instances

**Endpoint:** `GET /api/agents/instances`

**Description:** List all active agent instances (running agents with live sessions).

**Request:**
```http
GET /api/agents/instances
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "agentId": "assistant",
    "sessionId": "abc123",
    "status": "running",
    "startedAt": "2026-01-15T10:30:00Z"
  }
]
```

---

### Get Instance Status

**Endpoint:** `GET /api/agents/{agentId}/sessions/{sessionId}/status`

**Description:** Check the status of a specific running agent instance.

**Parameters:**
- `agentId` (string, path) — Agent ID
- `sessionId` (string, path) — Session ID

**Request:**
```http
GET /api/agents/assistant/sessions/abc123/status
X-Api-Key: your-api-key
```

**Response:** 200 OK — Returns the agent instance details.

**Error Responses:**
- `404 Not Found` — No active instance for the given agent/session

---

### Stop Instance

**Endpoint:** `POST /api/agents/{agentId}/sessions/{sessionId}/stop`

**Description:** Stop a running agent instance.

**Parameters:**
- `agentId` (string, path) — Agent ID
- `sessionId` (string, path) — Session ID

**Request:**
```http
POST /api/agents/assistant/sessions/abc123/stop
X-Api-Key: your-api-key
```

**Response:** 204 No Content

**Error Responses:**
- `404 Not Found` — No active instance for the given agent/session

---

## Skills Management

Skills are modular knowledge packages that enhance agent reasoning. Learn more in the [Skills Guide](./skills.md).

> **Note:** Skills endpoints are provided by the main BotNexus application host, not the Gateway API project. They are included here for completeness.

### List Global Skills

**Endpoint:** `GET /api/skills`

**Description:** Retrieve all global skills available to all agents.

**Request:**
```http
GET /api/skills
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "name": "git-workflow",
    "description": "Git workflow and commit conventions for BotNexus",
    "version": "1.0.0",
    "scope": "Global",
    "alwaysLoad": false,
    "sourcePath": "/home/user/.botnexus/skills/git-workflow/SKILL.md"
  },
  {
    "name": "testing-standards",
    "description": "Testing patterns and best practices",
    "version": "1.0.0",
    "scope": "Global",
    "alwaysLoad": false,
    "sourcePath": "/home/user/.botnexus/skills/testing-standards/SKILL.md"
  }
]
```

**Response Fields:**
- `name` (string) — Skill identifier (folder name)
- `description` (string) — Human-readable skill description
- `version` (string) — Semantic version of the skill
- `scope` (string) — Scope: `"Global"` or `"Agent"`
- `alwaysLoad` (boolean) — Reserved for future use (always false currently)
- `sourcePath` (string) — File path for debugging

---

### List Agent Skills

**Endpoint:** `GET /api/agents/{name}/skills`

**Description:** Retrieve all skills (global + per-agent) loaded for a specific agent, respecting `DisabledSkills` configuration.

**Parameters:**
- `name` (string, path) — Agent name

**Request:**
```http
GET /api/agents/code-reviewer/skills
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "name": "code-review-criteria",
    "description": "Code review standards for this project",
    "version": "1.0.0",
    "scope": "Agent",
    "alwaysLoad": false,
    "sourcePath": "/home/user/.botnexus/agents/code-reviewer/skills/code-review-criteria/SKILL.md",
    "contentPreview": "# Code Review Criteria\n\nReviewers should check:\n1. Functionality\n2. Code style\n3. Tests\n..."
  },
  {
    "name": "git-workflow",
    "description": "Git workflow and commit conventions for BotNexus",
    "version": "1.0.0",
    "scope": "Global",
    "alwaysLoad": false,
    "sourcePath": "/home/user/.botnexus/skills/git-workflow/SKILL.md"
  }
]
```

**Response Fields:**
- All fields from [List Global Skills](#list-global-skills), plus:
- `contentPreview` (string) — First 200 characters of skill markdown content (agent endpoint only)

**Skill Resolution Order:**
1. Global skills from `~/.botnexus/skills/`
2. Per-agent skills from `~/.botnexus/agents/{name}/skills/` (override global if same name)
3. Filtered by agent's `DisabledSkills` configuration
4. Sorted alphabetically by name

**Error Responses:**
- `404 Not Found` — Agent does not exist

**Notes:**
- Agent skills override global skills with the same name
- Use `DisabledSkills` in agent config to exclude specific skills or patterns
- See [Skills Guide: Disabling Skills](./skills.md#disabling-skills) for pattern syntax

---

## Channels Management

> **Added in Wave 1.** Channels are adapter plugins that connect BotNexus to external surfaces (SignalR, Telegram, TUI, etc.).

### List Channel Adapters

**Endpoint:** `GET /api/channels`

**Description:** List all registered channel adapters and their runtime capabilities.

**Request:**
```http
GET /api/channels
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "name": "signalr",
    "displayName": "SignalR",
    "isRunning": true,
    "supportsStreaming": true,
    "supportsSteering": true,
    "supportsFollowUp": true,
    "supportsThinking": true,
    "supportsToolDisplay": true
  },
  {
    "name": "telegram",
    "displayName": "Telegram",
    "isRunning": false,
    "supportsStreaming": false,
    "supportsSteering": false,
    "supportsFollowUp": false,
    "supportsThinking": false,
    "supportsToolDisplay": false
  }
]
```

**Response Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Channel type identifier (e.g., `"signalr"`, `"telegram"`, `"tui"`) |
| `displayName` | string | Human-readable channel name |
| `isRunning` | boolean | Whether the adapter is currently active |
| `supportsStreaming` | boolean | Whether the adapter supports streamed content deltas |
| `supportsSteering` | boolean | Whether the adapter supports real-time steering messages |
| `supportsFollowUp` | boolean | Whether the adapter supports follow-up message queuing |
| `supportsThinking` | boolean | Whether the adapter renders thinking/progress output |
| `supportsToolDisplay` | boolean | Whether the adapter renders tool call activity |

### Channel Startup Health

**Endpoint:** `GET /api/channels/health`

**Description:** Reports how many *configured* channel adapters actually reached the *started*
state, naming any that did not. Adapter startup is retried with bounded backoff for transient
failures (5xx, timeout, socket) and abandoned immediately for terminal ones (invalid token,
malformed config). Previously the gateway logged `Gateway started with N channel adapter(s)`
using the configured count even after a failure, so a permanently dead channel was invisible to
operators - the only symptom was silence on that channel. See issue #2447.

**Request:**
```http
GET /api/channels/health
X-Api-Key: your-api-key
```

**Response:** 200 OK when every configured adapter started
```json
{
  "status": "ok",
  "configured": 5,
  "started": 5,
  "failed": []
}
```

**Response:** 503 Service Unavailable when any configured adapter did not start
```json
{
  "status": "degraded",
  "configured": 5,
  "started": 4,
  "failed": [
    {
      "channelType": "telegram",
      "failureKind": "Transient",
      "error": "Telegram API call 'deleteWebhook' failed (502)",
      "attempts": 4
    }
  ]
}
```

**Response Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `status` | string | `"ok"` when every configured adapter started, otherwise `"degraded"` |
| `configured` | integer | Number of channel adapters configured on this gateway |
| `started` | integer | Number of those adapters that actually reached the started state |
| `failed` | array | One entry per configured adapter that did not start |
| `failed[].channelType` | string | The channel type identifier that failed |
| `failed[].failureKind` | string | `Transient`, `Terminal`, or `NotAttempted` (startup pass incomplete) |
| `failed[].error` | string | The final error message from the failed start |
| `failed[].attempts` | integer | How many start attempts were made before giving up |

---

## Extensions Management

> **Added in Wave 1.** Extensions are assembly-based plugins loaded at runtime from `~/.botnexus/extensions/`.

### List Loaded Extensions

**Endpoint:** `GET /api/extensions`

**Description:** List all loaded runtime extensions and their declared types.

**Request:**
```http
GET /api/extensions
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "name": "GitHub Tools",
    "version": "1.0.0",
    "type": "tool",
    "assemblyPath": "BotNexus.Extensions.GitHub.dll"
  },
  {
    "name": "Custom Provider",
    "version": "0.5.0",
    "type": "provider",
    "assemblyPath": "MyCustomProvider.dll"
  }
]
```

**Response Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Extension display name from the manifest |
| `version` | string | Extension version from the manifest |
| `type` | string | Extension type (e.g., `"tool"`, `"provider"`, `"channel"`, `"unknown"`) |
| `assemblyPath` | string | Entry assembly filename (not full path) |

**Notes:**
- An extension that declares multiple types appears once per type in the response.
- Extensions with no declared types use `"unknown"` as the type.

### Extension Details

**Endpoint:** `GET /api/extensions/details`

**Description:** List loaded extensions with their full manifest details, including the configuration field schema each extension declares in its `botnexus-extension.json`. Unlike the flat `GET /api/extensions` listing, each extension appears exactly once regardless of how many types it declares.

**Request:**
```http
GET /api/extensions/details
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "id": "botnexus-qmd",
    "name": "QMD",
    "version": "1.0.0",
    "enabled": true,
    "extensionTypes": ["tool"],
    "registeredServices": ["IQmdSearchService"],
    "configSchema": [
      { "id": "enabled", "type": "bool", "default": "false", "required": false }
    ],
    "assemblyFileName": "BotNexus.Extensions.Qmd.dll"
  }
]
```

**Response Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | string | Extension ID from the manifest |
| `name` | string | Extension display name |
| `version` | string | Extension version |
| `enabled` | boolean | Whether the extension is enabled; a disabled extension is discovered but not loaded |
| `extensionTypes` | string[] | Extension type identifiers declared in the manifest |
| `registeredServices` | string[] | Service contract names registered by this extension |
| `configSchema` | object[] | Configuration field schema declared by this extension (`id`, `type`, `default`, `required`, ...), used to validate operator config and apply defaults at startup |
| `assemblyFileName` | string | Entry assembly filename (not full path) |

### Extension Load Health

**Endpoint:** `GET /api/extensions/health`

**Description:** Report the outcome of the startup extension-load pass. This is the boot smoke gate's assertion surface: it turns a silent extension-assembly-load regression - which previously left `/health` green and surfaced only as a generic timeout - into an explicit, named failure.

**Request:**
```http
GET /api/extensions/health
X-Api-Key: your-api-key
```

**Response:** 200 OK - every attempted extension loaded
```json
{
  "status": "ok",
  "loadedCount": 7,
  "failedCount": 0,
  "failed": []
}
```

**Response:** 503 Service Unavailable - at least one extension failed to load
```json
{
  "status": "failed",
  "loadedCount": 6,
  "failedCount": 1,
  "failed": [
    {
      "id": "botnexus-qmd",
      "error": "Could not load file or assembly 'BotNexus.Extensions.Qmd'"
    }
  ]
}
```

**Response Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `status` | string | `"ok"` when every attempted extension loaded; otherwise `"failed"` |
| `loadedCount` | int | Number of extensions that loaded successfully at boot |
| `failedCount` | int | Number of extensions that failed to load at boot |
| `failed` | object[] | Per-extension load failures; each entry carries the `id` that failed and the actual `error` (typically naming the missing or diverged assembly/type) |

**Notes:**
- The report reflects the **startup** load pass, so it is stable for the lifetime of the process.
- The non-200 status code makes this endpoint usable directly as a container or deployment readiness probe.

---

## Chat

### Send Message

**Endpoint:** `POST /api/chat`

**Description:** Send a message to an agent and receive the complete response (non-streaming). For real-time streaming, use the SignalR hub at `/hub/gateway`.

**Request Body:**
```json
{
  "agentId": "assistant",
  "message": "Hello, how can you help?",
  "sessionId": "optional-session-id"
}
```

**Field Descriptions:**
- `agentId` (string, required) — Target agent ID
- `message` (string, required) — Message content to send
- `sessionId` (string, optional) — Session ID to continue a conversation. If omitted, a new session is created.

**Request:**
```http
POST /api/chat
X-Api-Key: your-api-key
Content-Type: application/json

{
  "agentId": "assistant",
  "message": "What is BotNexus?"
}
```

**Response:** 200 OK
```json
{
  "sessionId": "abc123",
  "content": "BotNexus is a modular AI agent execution platform...",
  "usage": {
    "inputTokens": 50,
    "outputTokens": 120
  }
}
```

**Error Responses:**
- `429 Too Many Requests` — Agent concurrency limit exceeded

---

### Steer Active Run

**Endpoint:** `POST /api/chat/steer`

**Description:** Inject a steering message into an agent's active run. This allows mid-stream guidance without aborting the current response.

**Request Body:**
```json
{
  "agentId": "assistant",
  "sessionId": "abc123",
  "message": "Focus on the security aspects"
}
```

**Request:**
```http
POST /api/chat/steer
X-Api-Key: your-api-key
Content-Type: application/json

{
  "agentId": "assistant",
  "sessionId": "abc123",
  "message": "Focus on the security aspects"
}
```

**Response:** 202 Accepted

**Error Responses:**
- `404 Not Found` — Agent session not found
- `429 Too Many Requests` — Agent concurrency limit exceeded

---

### Queue Follow-Up

**Endpoint:** `POST /api/chat/follow-up`

**Description:** Queue a follow-up message for an active agent session. The follow-up is processed after the current run completes.

**Request Body:**
```json
{
  "agentId": "assistant",
  "sessionId": "abc123",
  "message": "Also summarize the key points"
}
```

**Request:**
```http
POST /api/chat/follow-up
X-Api-Key: your-api-key
Content-Type: application/json

{
  "agentId": "assistant",
  "sessionId": "abc123",
  "message": "Also summarize the key points"
}
```

**Response:** 202 Accepted

**Error Responses:**
- `404 Not Found` — Agent session not found
- `429 Too Many Requests` — Agent concurrency limit exceeded

---

## Commands

The Commands API allows clients to discover and execute slash commands contributed by extensions and built-in handlers. Commands are backend-driven, enabling a unified command palette across WebUI, TUI, and future client surfaces.

### List Commands

**Endpoint:** `GET /api/commands`

**Description:** Retrieve all available commands (built-in + extension-contributed). Used by client surfaces to populate command palettes and autocomplete.

**Request:**
```http
GET /api/commands
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "name": "/help",
    "description": "Show available commands",
    "category": "System",
    "clientSideOnly": false,
    "subCommands": null
  },
  {
    "name": "/agents",
    "description": "List available agents",
    "category": "System",
    "clientSideOnly": false,
    "subCommands": null
  },
  {
    "name": "/skills",
    "description": "Manage skills for the current agent",
    "category": "Extension",
    "clientSideOnly": false,
    "subCommands": [
      {
        "name": "list",
        "description": "Show loaded, available, and denied skills"
      },
      {
        "name": "info",
        "description": "Show skill metadata and size"
      },
      {
        "name": "add",
        "description": "Load a skill into the current session"
      },
      {
        "name": "remove",
        "description": "Unload a skill from the current session"
      },
      {
        "name": "reload",
        "description": "Re-discover skills from disk"
      }
    ]
  },
  {
    "name": "/reset",
    "description": "Clear chat and reset current session",
    "category": "System",
    "clientSideOnly": true,
    "subCommands": null
  }
]
```

**Response Fields:**
- `name` (string) — Command name including slash (e.g., "/skills")
- `description` (string) — Short description for the command palette
- `category` (string) — Command category: "System" (built-in) or "Extension" (contributor)
- `clientSideOnly` (boolean) — If true, client handles execution without backend call (e.g., `/reset` manages DOM/reconnect). If false, execution requires POST to `/api/commands/execute`
- `subCommands` (array of objects, nullable) — Sub-command definitions (e.g., "/skills list", "/skills info &lt;name&gt;"). Null if command has no sub-commands
  - `name` (string) — Sub-command name (without slash)
  - `description` (string) — Sub-command description

**Notes:**
- Client-side commands (where `clientSideOnly: true`) execute locally without backend involvement
- Backend commands are executed via `POST /api/commands/execute`
- Extensions register commands via `ICommandContributor` interface during gateway startup
- Command name collisions are resolved by first-registered-wins; duplicates are logged as warnings

---

### Execute Command

**Endpoint:** `POST /api/commands/execute`

**Description:** Execute a slash command and return the result. Routes to the appropriate handler (built-in or extension-contributed) based on command name.

**Request Body:**
```json
{
  "input": "/skills list",
  "agentId": "my-agent",
  "sessionId": "sess-123abc"
}
```

**Field Descriptions:**
- `input` (string, required) — Full command text including slash and arguments (e.g., "/skills list", "/skills info my-skill")
- `agentId` (string, optional) — Agent context for the command. Required for agent-aware commands like `/skills`
- `sessionId` (string, optional) — Session context for the command

**Request:**
```http
POST /api/commands/execute
X-Api-Key: your-api-key
Content-Type: application/json

{
  "input": "/skills list",
  "agentId": "my-agent",
  "sessionId": "sess-123abc"
}
```

**Response:** 200 OK
```json
{
  "title": "📚 Skills for my-agent",
  "body": "Loaded (3):\n  ado-work-management          Unified ADO work management...\n  m365-communication           Microsoft 365 communication...\n  reference-bank               Shared reference data...\n\nAvailable (8):\n  calendar-interaction         Calendar management...\n  datetime-helper              Date/time utilities...\n  ...\n\nConfig: max 20 loaded, ~25K token budget, ~10.5K used",
  "isError": false
}
```

**Response Fields:**
- `title` (string) — Display title for the result block (e.g., "📚 Skills for my-agent")
- `body` (string) — Result content (plain text, rendered in a preformatted block)
- `isError` (boolean) — True if the command execution failed. When true, the client should render the body as an error message

**Example: /skills info**

**Request:**
```http
POST /api/commands/execute
X-Api-Key: your-api-key
Content-Type: application/json

{
  "input": "/skills info ado-work-management",
  "agentId": "my-agent",
  "sessionId": "sess-123abc"
}
```

**Response:** 200 OK
```json
{
  "title": "Skill: ado-work-management",
  "body": "Name:         ado-work-management\nDescription:  Unified ADO work management...\nSource:       Global (~/.botnexus/skills/ado-work-management/)\nStatus:       Loaded (auto-load)\nSize:         ~3,200 tokens\nLicense:      MIT\nFiles:        SKILL.md, reference/features.md, workflows/",
  "isError": false
}
```

**Example: Error Response**

**Request:**
```http
POST /api/commands/execute
X-Api-Key: your-api-key
Content-Type: application/json

{
  "input": "/skills add nonexistent-skill",
  "agentId": "my-agent",
  "sessionId": "sess-123abc"
}
```

**Response:** 200 OK
```json
{
  "title": "Error",
  "body": "Skill 'nonexistent-skill' not found. Available skills: ado-work-management, m365-communication, ...",
  "isError": true
}
```

**Error Responses:**
- `400 Bad Request` — Malformed input (e.g., empty string, missing required fields)
- `404 Not Found` — Command not recognized (no handler found for the command name)
- `400 Bad Request` — Invalid arguments (e.g., sub-command not found for a command with sub-commands)

**Notes:**
- All command errors return HTTP 200 with `isError: true` in the response body, not HTTP error codes. This allows clients to consistently handle command results (success or failure) in a unified way
- Exception: Malformed requests or unrecognized commands return HTTP 4xx errors
- Built-in commands (e.g., `/help`, `/status`, `/agents`) are implemented in the Gateway as built-in handlers
- Extension commands (e.g., `/skills`) are implemented by extensions that implement the `ICommandContributor` interface
- Commands are agent-aware when applicable (e.g., `/skills list` shows skills for the specified agent)
- Sub-command parsing is case-insensitive (e.g., "/skills LIST" and "/skills list" are equivalent)

---

## Session Management

### List Sessions

**Endpoint:** `GET /api/sessions`

**Description:** Retrieve a page of conversation sessions, newest first.

**Query Parameters:**
- `agentId` (string, optional) — Filter sessions by agent ID
- `conversationId` (string, optional) — Filter sessions linked to one conversation
- `includeInactive` (bool, optional, default: false) — Include sealed and expired sessions
- `offset` (integer, optional, default: 0) — Zero-based offset into the **filtered** set
- `limit` (integer, optional, default: 50) — Clamped to a server maximum of 200

**Request:**
```http
GET /api/sessions?limit=50&offset=0
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "sessions": [
    {
      "sessionId": "abc123",
      "agentId": "assistant",
      "channelType": "signalr",
      "conversationId": "conv-1",
      "status": "Active",
      "createdAt": "2026-01-15T10:30:00Z",
      "updatedAt": "2026-01-15T11:45:00Z"
    }
  ],
  "totalCount": 120,
  "hasMore": true,
  "offset": 0,
  "limit": 50
}
```

> **Terminate on `hasMore`, not on a short page.** The server clamps `limit` to its own
> maximum, so a page shorter than requested does not mean the set is exhausted.

See the [Sessions API reference](api/sessions.md#get-apisessions) for the full field table.

---

### Get Session

**Endpoint:** `GET /api/sessions/{sessionId}`

**Description:** Retrieve a specific session by ID, including conversation history.

**Parameters:**
- `sessionId` (string, path) — Session ID

**Request:**
```http
GET /api/sessions/session-abc123
X-Api-Key: your-api-key
```

**Response:** 200 OK — Returns the session with full history.

**Error Responses:**
- `404 Not Found` — Session does not exist

---

### Get Session Debug Snapshot

**Endpoint:** `GET /api/sessions/{sessionId}/debug`

**Description:** Returns a diagnostic snapshot containing session fields, the current rendered system prompt, paginated history, and metadata.

**Query Parameters:**
- `offset` (integer, optional, default: 0) — Zero-based history offset; negative values are clamped to zero.
- `limit` (integer, optional, default: 50, max: 200) — Number of history entries; values are clamped to 1-200.

The response includes `isExecutionLive`, which reports whether an in-memory handle is currently running. For active cron sessions, `lifecycleDiagnostic` is `live-execution` when that handle is running or `stale-persisted-active` when only the persisted status remains active; it is `null` for other sessions.

**Error Responses:**
- `404 Not Found` — Session does not exist

---

### Get Session History (Paginated)

**Endpoint:** `GET /api/sessions/{sessionId}/history`

**Description:** Get paginated session history for long-running conversations.

**Parameters:**
- `sessionId` (string, path) — Session ID

**Query Parameters:**
- `offset` (integer, optional, default: 0) — Zero-based offset into history
- `limit` (integer, optional, default: 50, max: 200) — Number of entries to return

**Request:**
```http
GET /api/sessions/session-abc123/history?offset=0&limit=50
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "offset": 0,
  "limit": 50,
  "totalCount": 120,
  "entries": [
    {
      "role": "user",
      "content": "Hello",
      "timestamp": "2026-01-15T10:30:00Z"
    },
    {
      "role": "assistant",
      "content": "Hi there!",
      "timestamp": "2026-01-15T10:30:01Z"
    }
  ]
}
```

**Error Responses:**
- `400 Bad Request` — Invalid offset or limit
- `404 Not Found` — Session does not exist

---

### Export Session Transcript (Markdown)

**Endpoint:** `GET /api/sessions/{sessionId}/export/markdown`

**Description:** Render the session's history as a downloadable markdown transcript document. When `gateway.transcriptExport.redactSecrets` is enabled, secret-shaped values are redacted during rendering.

**Parameters:**
- `sessionId` (string, path) — Session ID

**Request:**
```http
GET /api/sessions/session-abc123/export/markdown
X-Api-Key: your-api-key
```

**Response:** 200 OK — `text/markdown` file attachment named `session-{sessionId}.md`.

**Error Responses:**
- `204 No Content` — Session exists but has no renderable transcript
- `404 Not Found` — Session does not exist

---

### List Live Sub-Agents for a Session

**Endpoint:** `GET /api/sessions/{sessionId}/subagents`

**Description:** List the **live runtime** sub-agent state for one parent session. Contrast with `GET /api/sessions/{sessionId}/subagents/history` (persisted rows, including finished runs) and the platform-wide `GET /api/subagents` feed.

**Parameters:**
- `sessionId` (string, path) — Parent session ID

**Request:**
```http
GET /api/sessions/session-abc123/subagents
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "subAgentId": "sub-abc123",
    "parentSessionId": "session-abc123",
    "childSessionId": "session-sub-abc123",
    "name": "docs-audit",
    "parentAgentId": "farnsworth",
    "childAgentId": "farnsworth",
    "childConversationId": "conv-9f2",
    "task": "Audit the API reference for missing endpoints",
    "model": "claude-opus-5",
    "archetype": "Researcher",
    "status": "Running",
    "startedAt": "2026-07-26T10:30:00Z",
    "completedAt": null,
    "turnsUsed": 4,
    "resultSummary": null
  }
]
```

A sub-agent run owns its own conversation (`childConversationId`), linked back to the supervisor by `Conversation.ParentConversationId` rather than by sharing the parent's identity. `status` is one of `Running`, `Completed`, `Failed`, `Killed`, `TimedOut`, `BudgetExhausted`.

**Error Responses:**
- `404 Not Found` — Session does not exist

---

### Get Sub-Agent History for a Session

**Endpoint:** `GET /api/sessions/{sessionId}/subagents/history`

**Description:** Return persisted sub-agent session rows for the given parent session, ordered by start time ascending. Unlike the live listing above, this includes completed and failed runs.

**Parameters:**
- `sessionId` (string, path) — Parent session ID

**Request:**
```http
GET /api/sessions/session-abc123/subagents/history
X-Api-Key: your-api-key
```

**Response:** 200 OK — an array of the same `SubAgentSessionSummary` shape returned by [List Sub-Agent Runs](#list-sub-agent-runs), scoped to this parent session.

**Error Responses:**
- `404 Not Found` — Session does not exist

---

### Kill a Sub-Agent

**Endpoint:** `DELETE /api/sessions/{sessionId}/subagents/{subAgentId}`

**Description:** Terminate a sub-agent run owned by the specified session. This is a state mutation and enforces the same per-session caller-identity gate as Delete and Suspend, so a caller cannot terminate a run owned by a different caller.

**Parameters:**
- `sessionId` (string, path) — Parent session ID
- `subAgentId` (string, path) — Sub-agent ID to terminate

**Request:**
```http
DELETE /api/sessions/session-abc123/subagents/sub-abc123
X-Api-Key: your-api-key
```

**Response:** 204 No Content

**Error Responses:**
- `403 Forbidden` — Caller does not own the session
- `404 Not Found` — Session or sub-agent does not exist

---

### Get Session Metadata

**Endpoint:** `GET /api/sessions/{sessionId}/metadata`

**Description:** Retrieve metadata key-value pairs for a session.

**Parameters:**
- `sessionId` (string, path) — Session ID

**Request:**
```http
GET /api/sessions/session-abc123/metadata
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "environment": "production",
  "userId": "user-42",
  "customTag": "priority"
}
```

**Error Responses:**
- `404 Not Found` — Session does not exist

---

### Patch Session Metadata

**Endpoint:** `PATCH /api/sessions/{sessionId}/metadata`

**Description:** Merge metadata entries into a session. Keys with `null` values are removed.

**Parameters:**
- `sessionId` (string, path) — Session ID

**Request:**
```http
PATCH /api/sessions/session-abc123/metadata
X-Api-Key: your-api-key
Content-Type: application/json

{
  "environment": "staging",
  "customTag": null
}
```

**Response:** 200 OK — Returns the updated metadata dictionary.

**Error Responses:**
- `400 Bad Request` — Request body is not a JSON object
- `404 Not Found` — Session does not exist

---

### Delete Session

**Endpoint:** `DELETE /api/sessions/{sessionId}`

**Description:** Delete a session and its conversation history.

**Parameters:**
- `sessionId` (string, path) — Session ID

**Request:**
```http
DELETE /api/sessions/session-abc123
X-Api-Key: your-api-key
```

**Response:** 204 No Content

---

### Suspend Session

**Endpoint:** `PATCH /api/sessions/{sessionId}/suspend`

**Description:** Suspend an active session. Only sessions with `Active` status can be suspended.

**Parameters:**
- `sessionId` (string, path) — Session ID

**Request:**
```http
PATCH /api/sessions/session-abc123/suspend
X-Api-Key: your-api-key
```

**Response:** 200 OK — Returns the updated session.

**Error Responses:**
- `404 Not Found` — Session does not exist
- `409 Conflict` — Session is not in `Active` state

---

### Resume Session

**Endpoint:** `PATCH /api/sessions/{sessionId}/resume`

**Description:** Resume a suspended session. Only sessions with `Suspended` status can be resumed.

**Parameters:**
- `sessionId` (string, path) — Session ID

**Request:**
```http
PATCH /api/sessions/session-abc123/resume
X-Api-Key: your-api-key
```

**Response:** 200 OK — Returns the updated session.

**Error Responses:**
- `404 Not Found` — Session does not exist
- `409 Conflict` — Session is not in `Suspended` state

---

### Seal Session

**Endpoint:** `PATCH /api/sessions/{sessionId}/seal`

**Description:** Seal a finished **sub-agent** session so it can never be reused. Only sessions whose type is a sub-agent session are eligible, and only once the run has ended — an `Active` or `Suspended` session cannot be sealed. The transition is a compare-and-set, so a concurrent transcript append is not rolled back and a competing seal/reset is reported rather than silently overwritten. Enforces the same per-session caller-identity gate as the metadata endpoints.

**Parameters:**
- `sessionId` (string, path) — Sub-agent session ID

**Request:**
```http
PATCH /api/sessions/session-sub-abc123/seal
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "sessionId": "session-sub-abc123",
  "status": "Sealed",
  "updatedAt": "2026-07-26T10:42:00Z"
}
```

**Response:** 204 No Content — Session is already sealed (idempotent).

**Error Responses:**
- `400 Bad Request` — Session is not a sub-agent session
- `403 Forbidden` — Caller does not own the session
- `404 Not Found` — Session does not exist
- `409 Conflict` — Session is `Active`/`Suspended`, or is in another state that cannot transition to `Sealed`

---

### List Sub-Agent Runs

**Endpoint:** `GET /api/subagents`

**Description:** Read-only, platform-wide sub-agent observability feed. Lists persisted sub-agent runs across **all** parent sessions, newest-started first, so an operator can review what sub-agents did after the fact — including whether a run genuinely completed or bailed. This surface is strictly read-only: it never spawns, kills, or mutates sub-agent state. It reads the same persisted `sub_agent_sessions` rows that the parent-scoped session history exposes, but aggregated across every parent session.

**Query Parameters:**
- `status` (string, optional) - Case-insensitive status filter (e.g. `Active`, `Completed`, `Failed`, `Killed`, `TimedOut`, `BudgetExhausted`). When omitted, runs of every status are returned.
- `limit` (int, optional, default `200`) — Maximum number of rows to return. Bounded to `1`–`500`; values above `500` are clamped.

**Request:**
```http
GET /api/subagents?status=Completed&limit=50
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "subAgentId": "sub-abc123",
    "parentSessionId": "session-xyz789",
    "parentAgentId": "farnsworth",
    "childAgentId": "farnsworth",
    "archetype": "coder",
    "startedAt": "2026-07-15T10:30:00Z",
    "endedAt": "2026-07-15T10:42:00Z",
    "status": "Completed"
  }
]
```

Each summary carries the run's task lineage (`parentAgentId` / `childAgentId`), behavioral `archetype` (or `null` if unset), lifecycle `status`, and start/end timestamps (`endedAt` is `null` while a run is still active).

**Error Responses:**
- `400 Bad Request` — `limit` is not greater than zero

---

## System & Status

### Gateway Information

**Endpoint:** `GET /api/gateway/info`

**Description:** Returns runtime and build information for the running gateway. The portal also uses `defaultAgentId` to select the configured default agent instead of assuming a hard-coded agent ID.

**Response:** 200 OK
```json
{
  "startedAt": "2026-07-18T01:00:00Z",
  "uptimeSeconds": 3600,
  "commitSha": "7068c75528fc144277ebe9759929ea512598fb24",
  "commitShort": "7068c755",
  "version": "0.28.0",
  "defaultAgentId": "assistant"
}
```

---

### Health Check

**Endpoint:** `GET /health`

**Description:** Basic health check (no authentication required).

**Response:** 200 OK
```json
{
  "status": "ok"
}
```

---

### Config Snapshot (with revision)

**Endpoint:** `GET /api/config/snapshot`

**Description:** Returns the raw configuration document (secrets redacted) together with the
revision token it was read at. Pair with `PATCH /api/config` to save with optimistic concurrency.

**Response:** 200 OK
```json
{
  "revision": "9F2A...C41",
  "config": { "gateway": { "logLevel": "Information" } }
}
```

---

### Config Patch (atomic dirty-path save)

**Endpoint:** `PATCH /api/config`

**Description:** Applies a batch of addressed changes as one all-or-nothing write. Only the paths
you send are written, so a section nobody edited cannot be clobbered by a stale snapshot. When
`expectedRevision` is supplied the save is a compare-and-swap.

This is the endpoint the settings UI uses. It replaced a per-section `PUT /api/config/{section}`
loop that rewrote every materialized section on every save.

**Request:**
```http
PATCH /api/config
X-Api-Key: your-api-key
Content-Type: application/json

{
  "expectedRevision": "9F2A...C41",
  "operations": [
    { "path": "gateway.logLevel", "value": "Debug" },
    { "path": "providers.openai", "value": { "enabled": true } },
    { "path": "channels.telegram.bots.ops", "remove": true }
  ]
}
```

- `path` — dotted path with optional `[index]` array segments.
- `value` — the value to write; ignored when `remove` is `true`.
- `remove` — delete the addressed node instead of setting it.
- Intermediate objects are created as needed, so a change can materialize a section that is absent
  from the file on disk.
- A secret submitted as the redaction placeholder `***` never overwrites the real stored value.
- `agents` is not addressable here — use `/api/agents`.

**Response (committed):** 200 OK
```json
{ "success": true, "revision": "11B7...9E0", "errors": [] }
```

**Response (stale revision):** 409 Conflict — nothing was written. Reload the snapshot, re-apply
your changes, and retry.
```json
{ "success": false, "revision": "11B7...9E0", "errors": ["Configuration '...' was modified by another writer since it was read..."] }
```

**Response (rejected batch):** 400 Bad Request — nothing was written; no operation from the batch
is partially committed.

---

### Config Validation

**Endpoint:** `GET /api/config/validate`

**Description:** Validates the platform configuration file and returns any errors.

**Query Parameters:**
- `path` (string, optional) — Explicit path to a config file. Defaults to `~/.botnexus/config.json`.

**Request:**
```http
GET /api/config/validate
X-Api-Key: your-api-key
```

**Response (valid config):** 200 OK
```json
{
  "isValid": true,
  "configPath": "/home/user/.botnexus/config.json",
  "errors": []
}
```

**Response (invalid config):** 200 OK
```json
{
  "isValid": false,
  "configPath": "/home/user/.botnexus/config.json",
  "errors": [
    "agents.assistant.provider is required (example: 'copilot').",
    "agents.assistant.model is required (example: 'gpt-4.1')."
  ]
}
```

**Validation checks include:**
- `gateway.listenUrl` must be a valid absolute HTTP/HTTPS URL
- Each provider must have `apiKey` or `baseUrl`
- Each agent must have `provider` and `model`
- Each channel must have a `type`
- API keys must have `apiKey`, `tenantId`, and at least one permission

---

### Doctor/Diagnostics

> **Note:** The diagnostics endpoint is provided by the main BotNexus application host, not the Gateway API project.

**Endpoint:** `GET /api/doctor`

**Description:** Run comprehensive health diagnostics with auto-fix recommendations.

**Request:**
```http
GET /api/doctor
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "timestamp": "2026-01-15T11:50:00Z",
  "checks": [
    {
      "name": "Configuration File",
      "category": "startup",
      "status": "healthy",
      "message": "Config file exists and is valid"
    },
    {
      "name": "OAuth Tokens",
      "category": "authentication",
      "status": "warning",
      "message": "Copilot token expires in 2 days",
      "suggestedFix": "Run 'botnexus login' to refresh"
    }
  ],
  "summary": {
    "healthy": 11,
    "warnings": 1,
    "errors": 0
  }
}
```

---

### Diagnostics: Thread Pool

**Endpoint:** `GET /api/diagnostics/threadpool`

**Description:** Returns thread pool health metrics including pending work items, thread counts, and a health assessment.

**Request:**
```http
GET /api/diagnostics/threadpool
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "pendingWorkItems": 3,
  "workerThreads": { "available": 28, "max": 32 },
  "completionPortThreads": { "available": 8, "max": 8 },
  "health": "healthy"
}
```

Returns `404` if the threadpool watchdog service is not registered.

---

### Diagnostics: Activity

**Endpoint:** `GET /api/diagnostics/activity`

**Description:** Returns the gateway's last activity timestamp and inactivity duration.

**Request:**
```http
GET /api/diagnostics/activity
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "lastActivityUtc": "2026-06-10T14:30:00Z",
  "inactivitySeconds": 120,
  "health": "healthy"
}
```

Returns `404` if the activity tracking service is not registered.

---

### Memory Statistics

**Endpoint:** `GET /api/memory`

**Description:** Returns memory store statistics for all agents.

**Request:**
```http
GET /api/memory
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "agentId": "assistant",
    "entryCount": 42,
    "totalSizeBytes": 128000
  }
]
```

---

### Memory Statistics (Per Agent)

**Endpoint:** `GET /api/memory/{agentId}`

**Description:** Returns memory store statistics for a specific agent.

**Parameters:**
- `agentId` (string, path) — Agent ID

**Response:** 200 OK
```json
{
  "agentId": "assistant",
  "entryCount": 42,
  "totalSizeBytes": 128000
}
```

---

### Memory Search (Per Agent)

**Endpoint:** `GET /api/memory/{agentId}/entries?query=`

**Description:** Search memory entries for an agent.

**Parameters:**
- `agentId` (string, path) — Agent ID
- `query` (string, query) — Search text

**Response:** 200 OK — Returns matching memory entries.

---

### Satellites

**Endpoint:** `GET /api/satellites`

**Description:** List all registered satellite nodes with their connection status.

**Request:**
```http
GET /api/satellites
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "id": "sat_desktop_home",
    "displayName": "Home Desktop",
    "owner": "jon",
    "platform": "windows",
    "capabilities": ["notify", "canvas"],
    "status": "online",
    "lastSeenUtc": "2026-06-10T14:30:00Z"
  }
]
```

---

### Get Satellite

**Endpoint:** `GET /api/satellites/{id}`

**Description:** Get details for a specific satellite.

**Parameters:**
- `id` (string, path) — Satellite ID

**Response:** 200 OK — Returns the satellite details.

**Error Responses:**
- `404 Not Found` — Satellite does not exist

---

### Provider Health Check

**Endpoint:** `GET /api/providers/{id}/health`

**Description:** Check the health of a configured provider (model registration + credential validation).

**Parameters:**
- `id` (string, path) — Provider ID

**Response:** 200 OK (healthy)
```json
{
  "providerId": "anthropic",
  "status": "healthy",
  "checkedAtUtc": "2026-06-12T10:00:00Z"
}
```

**Response:** 503 Service Unavailable (unhealthy)
```json
{
  "providerId": "anthropic",
  "status": "unhealthy",
  "reason": "API key not configured",
  "checkedAtUtc": "2026-06-12T10:00:00Z"
}
```

---

### Session Statistics

**Endpoint:** `GET /api/sessions/stats`

**Description:** Get aggregate session metrics.

**Query Parameters:**
- `agentId` (string, optional) — Filter to a specific agent

**Response:** 200 OK
```json
{
  "totalSessions": 1542,
  "activeSessions": 23,
  "averageTurns": 8.3,
  "oldestSessionUtc": "2026-01-15T09:00:00Z",
  "newestSessionUtc": "2026-06-12T09:55:00Z"
}
```

---

### Exchange Budget Diagnostics

**Endpoint:** `GET /api/exchanges/budget`

**Description:** Get current agent exchange budget state for all or specific agent pairs.

**Query Parameters:**
- `initiator` (string, optional) — Filter by initiating agent
- `target` (string, optional) — Filter by target agent

**Response:** 200 OK
```json
[
  {
    "initiator": "coordinator",
    "target": "reporter",
    "exchangesToday": 12,
    "dailyCap": 200,
    "inCooldown": false,
    "cooldownExpiresUtc": null
  }
]
```

---

### Rate Limit Headers

All non-exempt API responses include standard rate limit headers:

| Header | Description |
|--------|-------------|
| `X-RateLimit-Limit` | Maximum requests per window |
| `X-RateLimit-Remaining` | Remaining requests in current window |
| `X-RateLimit-Reset` | Unix timestamp when the window resets |

On `429 Too Many Requests`, the same headers are returned with `Remaining=0`.

Headers are absent when rate limiting is disabled or on exempt paths (e.g., health check).

---

### Nav Order (Portal Sidebar)

The portal left-nav ordering model. The gateway stores per-item order overrides server-side so
they roam with the user across browsers and devices; the effective order is the built-in defaults
layered with those overrides. Lower order numbers render higher in the sidebar.

#### List Nav Order

```http
GET /api/nav-order
```

Returns every built-in nav item with its effective order, ascending.

```json
[
  { "key": "conversations", "order": 10 },
  { "key": "tools", "order": 20 }
]
```

#### Set a Nav Order Override

```http
PUT /api/nav-order/{key}
PATCH /api/nav-order/{key}
Content-Type: application/json

{ "order": 5 }
```

`{key}` is the stable nav key (for example `tools`). Returns the full updated ordered list.
An empty or whitespace key returns `400 Bad Request`.

#### Reset a Nav Key

```http
DELETE /api/nav-order/{key}
```

Removes the user override for `{key}`, restoring its built-in default. Returns the full updated
ordered list.

---

## Error Handling

### Error Response Format

All error responses follow a standard format:

```json
{
  "error": "unauthenticated",
  "message": "API key is missing or invalid."
}
```

**Note:** Controller-level errors may return a simplified format:

```json
{
  "error": "Agent not found"
}
```

### Common Status Codes

| Code | Meaning |
|------|---------|
| 200 | Success |
| 201 | Created |
| 202 | Accepted (async operation queued) |
| 204 | No Content (success with no body) |
| 400 | Bad Request (invalid input) |
| 401 | Unauthorized (missing/invalid API key) |
| 404 | Not Found |
| 409 | Conflict (duplicate name, state mismatch) |
| 429 | Too Many Requests (rate limit exceeded or agent concurrency limit) |
| 500 | Internal Server Error |

### Error Codes

| Code | HTTP | Meaning |
|------|------|---------|
| INVALID_INPUT | 400 | Missing or invalid field |
| AGENT_NOT_FOUND | 404 | Agent does not exist |
| DUPLICATE_AGENT | 409 | Agent name already exists |
| SESSION_NOT_FOUND | 404 | Session does not exist |
| UNAUTHORIZED | 401 | Invalid or missing API key |
| CONCURRENCY_LIMIT | 429 | Agent concurrency limit exceeded |
| RATE_LIMITED | 429 | Per-client request rate limit exceeded |
| STATE_CONFLICT | 409 | Invalid state transition (e.g., suspend non-active session) |
| INTERNAL_ERROR | 500 | Server error |

---

## Tools Auto-Registration & DisallowedTools

### Internal Tools

Every agent automatically gets the following tools by default:

| Tool | Purpose | Default Status | Can Disable |
|------|---------|-----------------|------------|
| `filesystem` | Read/write files on disk | Enabled | Yes |
| `web_fetch` | Fetch content from URLs | Enabled | Yes |
| `send_message` | Send messages via channels | Enabled | Yes |
| `cron` | Schedule periodic tasks | Enabled | Yes |
| `get_datetime` | Get current UTC and timezone-aware local date/time | Enabled | Yes |
| `shell` | Execute shell commands | Enabled if `tools.exec.enable=true` | Yes |

### Disabling Tools

To disable specific tools for an agent, add them to the `disallowedTools` array:

**Config Example:**
```json
{
  "BotNexus": {
    "Agents": {
      "Named": {
        "secure-agent": {
          "DisallowedTools": ["shell", "filesystem"]
        }
      }
    }
  }
}
```

**API Example:**
```bash
curl -X POST http://localhost:5005/api/agents \
  -H "X-Api-Key: your-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "agentId": "secure-agent",
    "displayName": "Secure Agent",
    "modelId": "gpt-4.1",
    "apiProvider": "copilot"
  }'
```

---

## Model Selector & Nullable Parameters

### Using Model Selector in WebUI

1. Open a session in the WebUI
2. Look for the **"Model:"** dropdown at the top
3. Select a model from the dropdown (or leave it blank for default)
4. Send a message — the selected model will be used for that request

### Nullable MaxTokens & Temperature

If `maxTokens` or `temperature` are not specified (null), the provider uses its own defaults:

```json
{
  "name": "my-agent",
  "model": "gpt-4o",
  "provider": "openai",
  "maxTokens": null,      // Provider uses OpenAI default
  "temperature": null     // Provider uses OpenAI default
}
```

**Fallback Order:**
1. Agent-specific setting (if set)
2. Default agent config (if set)
3. Provider's default setting

---

## Configuration Files & Backups

### Config Structure

Configuration is stored in `~/.botnexus/config.json`:

```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant"
  },
  "agents": {
    "my-agent": {
      "provider": "openai",
      "model": "gpt-4.1",
      "isolationStrategy": "in-process",
      "enabled": true
    }
  },
  "providers": {
    "openai": {
      "apiKey": "sk-...",
      "baseUrl": "https://api.openai.com/v1",
      "defaultModel": "gpt-4.1"
    }
  }
}
```

### Config Backups & Audit Logging

- **Automatic Backups:** Every config write creates `config.json.bak`
- **Token Logging:** OAuth token operations are logged with WARNING level
- **Model Logging:** The actual model used (resolved from config or provider default) is logged per provider call

Example log output:
```text
[Information] Calling provider OpenAiProvider for agent my-agent, model=gpt-4o, contextWindowTokens=128000
[Information] Provider OpenAiProvider responded in 1234ms
[Warning] OAuth token saved for provider 'copilot' at ~/.botnexus/tokens/copilot.json. Expires at 2026-02-15T10:30:00Z
```

---

## WebUI Features

### Command Palette

Type `/` in the chat input to open the command palette:

- `/help` — Show available commands and their descriptions
- `/reset` — Reset the current conversation session
- `/status` — Show system status and last heartbeat time
- `/models` — List all available models by provider (alias: `/model`)

**Usage:**
1. Type `/hel` in the chat input
2. Palette shows matching commands
3. Press Tab/Enter or click to select
4. Command is inserted into the input

### Tool Call Visibility Toggle

Use the **🔧 Tools** toggle in the toolbar to show/hide tool calls in the chat.

- **Enabled:** Tool calls are shown as collapsible summaries
- **Disabled:** Tool calls are hidden (arguments still processed normally)

Each tool call shows:
- Tool name (e.g., "filesystem")
- Action performed (e.g., "read_file")
- Arguments preview (truncated to 80 chars)
- Click to expand full details in a modal

---

## Loop Detection & Iteration Limits

### Configuration-Only Feature

Agent loop detection and iteration limits are configured via `config.json` and the [Configuration Guide](configuration.md) — they are not exposed through REST API endpoints. This is intentional to ensure consistency and prevent accidental runtime misconfigurations.

### Settings

Two settings control the agent loop behavior:

| Setting | Default | Purpose |
|---------|---------|---------|
| `MaxToolIterations` | 40 | Max number of LLM calls in a single agent cycle |
| `MaxRepeatedToolCalls` | 2 | Max times the same tool can be called with identical arguments |

### Configuration Example

```json
{
  "BotNexus": {
    "Agents": {
      "MaxToolIterations": 40,
      "MaxRepeatedToolCalls": 2,
      "Named": {
        "careful-agent": {
          "MaxToolIterations": 10,
          "MaxRepeatedToolCalls": 1
        },
        "researcher": {
          "MaxToolIterations": 100,
          "MaxRepeatedToolCalls": 5
        }
      }
    }
  }
}
```

### How Loop Detection Works

When an agent repeatedly calls the same tool with identical arguments:

1. **First call:** Tool signature computed (`tool_name + normalized_arguments`)
2. **Second call:** Signature compared to first; counter incremented to 2
3. **Threshold reached:** If counter ≥ `MaxRepeatedToolCalls`, execution is blocked
4. **LLM receives error:** Error returned to agent: `"Tool 'X' called N times with identical arguments"`
5. **Agent recovers:** Agent can now try a different tool or modify arguments

**Example:**

```text
Iteration 0: search_files("") → executes (count: 1)
Iteration 1: search_files("") → executes (count: 2)
Iteration 2: search_files("") → BLOCKED (count: 2 >= MaxRepeatedToolCalls: 2)
```

### Best Practices

- **Most agents (10-40 iterations):** Default settings work well
- **Safety-critical agents:** Use `MaxToolIterations=10-20, MaxRepeatedToolCalls=1`
- **Exploratory/research agents:** Use `MaxToolIterations=50+, MaxRepeatedToolCalls=3-5`

For detailed configuration guidance, see [Configuration Guide § Agent Iteration & Loop Detection](configuration.md#agent-iteration--loop-detection).

---

## Webhooks

Webhooks let external systems deliver a message to an agent over signed HTTP. All
routes live under pi/webhooks. This section is a summary; see the full
[Webhooks API reference](api/webhooks.md) and the [Webhooks guide](guides/webhooks.md)
for concepts, response modes, and worked HMAC signing examples.

### Registration management

| Method & path | Description |
|---------------|-------------|
| POST /api/webhooks/registrations | Create a registration. Returns the secret **once**. |
| GET /api/webhooks/registrations/{webhookId} | Get a registration (no secret). |
| GET /api/webhooks/registrations | List registrations (optional ?agentId=). |
| PUT /api/webhooks/registrations/{webhookId} | Update label / enabled / default response mode. |
| DELETE /api/webhooks/registrations/{webhookId} | Delete a registration (204). |
| GET /api/webhooks/runs/{runId} | Poll a run's status. |
| GET /api/webhooks/registrations/{webhookId}/runs | List recent runs (?limit=, 1–100, default 20). |

### Inbound delivery

`	ext
POST /api/webhooks/{agentId}/{webhookId}
X-BotNexus-Signature-256: sha256=<hex>
`

Body: `{ "message": string, "responseMode": "async"|"sync"|"callback"|null,
"agentAction": bool|null, "callbackUrl": string|null }`.

- **async** (default): 202 + Location poll URL; agent runs in background.
- **sync**: 200 with the agent response inline (≤120s, else downgrades to 202).
- **callback**: 202; result POSTed to callbackUrl on completion.

The signature is sha256= + lowercase hex of HMAC-SHA256(secret, rawBody). An
invalid or missing signature returns 401 { "error": "Invalid signature." }.

---

## Real-Time Streaming (SignalR Hub)

Real-time agent interaction uses the SignalR hub at `/hub/gateway`. There is no raw WebSocket endpoint -- all streaming goes through ASP.NET Core SignalR.

**Hub URL:**
```text
http://localhost:5005/hub/gateway
```

**Transport:** SignalR negotiation (WebSocket / Server-Sent Events / Long Polling as available).

**Authentication:** Subject to `GatewayAuthMiddleware` rules. In development mode (no API keys configured), connections are allowed without auth.

For the full hub method contract (client-to-server invocations and server-to-client events), see the [SignalR Hub Contract](./signalr-hub-contract.md).

---
## See Also

- [Developer Guide](getting-started-dev.md) — Local development setup and workflow
- [Configuration Guide](configuration.md) — Detailed configuration options
- [Getting Started](getting-started.md) — Quick start guide
- [Extension Development](extension-development.md) — Build custom tools and providers
