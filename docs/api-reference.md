# BotNexus API Reference

Complete reference for BotNexus REST API endpoints, including agents, sessions, providers, skills, and system status.

## Table of Contents

1. [Overview](#overview)
2. [Authentication](#authentication)
3. [Agent Management](#agent-management)
4. [Skills Management](#skills-management)
5. [Chat](#chat)
6. [Session Management](#session-management)
7. [System & Status](#system--status)
8. [Error Handling](#error-handling)

---

## Overview

**Base URL:** `http://localhost:5005/api`

All endpoints follow REST conventions and return JSON responses. The default port is **5005** (configurable via `config.json`).

**Authentication:** All endpoints require API key authentication (see [Authentication](#authentication) below).

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

**Exemptions:** `/health`, `/webui`, and `/swagger` are exempt from authentication.

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

**Description:** Retrieve a list of all configured agents.

**Request:**
```http
GET /api/agents
X-Api-Key: your-api-key
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
- Agent name is normalized to lowercase with dashes (e.g., "My Agent" → "my-agent")

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

## Chat

### Send Message

**Endpoint:** `POST /api/chat`

**Description:** Send a message to an agent and receive the complete response (non-streaming). For real-time streaming, use the WebSocket endpoint at `/ws`.

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

## Session Management

### List Sessions

**Endpoint:** `GET /api/sessions`

**Description:** Retrieve all conversation sessions.

**Query Parameters:**
- `agentId` (string, optional) — Filter sessions by agent ID

**Request:**
```http
GET /api/sessions
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "sessionId": "abc123",
    "agentId": "assistant",
    "channelType": "websocket",
    "callerId": "user-1",
    "status": "Active",
    "createdAt": "2026-01-15T10:30:00Z",
    "updatedAt": "2026-01-15T11:45:00Z"
  }
]
```

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

## System & Status

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
| 429 | Too Many Requests (agent concurrency limit exceeded) |
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
```
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

```
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

## WebSocket Protocol

### Agent WebSocket (`/ws`)

Real-time streaming connection for interacting with agents.

**Connection URL:**
```
ws://localhost:5005/ws?agent={agentId}&session={sessionId}
```

| Parameter | Required | Description |
|-----------|----------|-------------|
| `agent` | Yes | Target agent ID |
| `session` | No | Session ID (auto-generated if omitted) |

**Authentication:** Subject to `GatewayAuthMiddleware` rules. In development mode (no API keys configured), connections are allowed without auth.

**Session locking:** Each session allows one active WebSocket at a time. A second connection to the same session is closed with status code **4409** (`"Session already has an active connection"`).

**Rate limiting:** WebSocket connections are rate-limited per client IP + agent. Default: 20 attempts per 300-second window. Excess connections receive HTTP 429 with a `Retry-After` header.

#### Client → Server Messages

**Send a message:**
```json
{ "type": "message", "content": "What is 2+2?" }
```

**Abort current execution:**
```json
{ "type": "abort" }
```

**Inject steering message into active run:**
```json
{ "type": "steer", "content": "Focus on the main point." }
```

**Queue a follow-up for the next run:**
```json
{ "type": "follow_up", "content": "And what about 3+3?" }
```

**Keepalive ping:**
```json
{ "type": "ping" }
```

#### Server → Client Messages

**Connection established:**
```json
{ "type": "connected", "connectionId": "abc123", "sessionId": "def456" }
```

**Agent started processing:**
```json
{ "type": "message_start", "messageId": "uuid-..." }
```

**Thinking delta (streaming agent reasoning):**
```json
{ "type": "thinking_delta", "delta": "Let me think about...", "messageId": "uuid-..." }
```

**Content delta (streaming response text):**
```json
{ "type": "content_delta", "delta": "2+2 is 4", "messageId": "uuid-..." }
```

**Tool execution started:**
```json
{ "type": "tool_start", "toolCallId": "call_...", "toolName": "calculate", "messageId": "uuid-..." }
```

**Tool result received:**
```json
{ "type": "tool_end", "toolCallId": "call_...", "toolResult": "4", "messageId": "uuid-..." }
```

**Agent completed:**
```json
{ "type": "message_end", "messageId": "uuid-...", "usage": { "inputTokens": 50, "outputTokens": 100 } }
```

**Error occurred:**
```json
{ "type": "error", "message": "Agent not found", "code": "NOT_FOUND" }
```

**Keepalive pong:**
```json
{ "type": "pong" }
```

#### WebSocket Message Flow

```
Client                          Server
  │                               │
  │──── { type: "message" } ─────►│
  │                               │
  │◄──── { type: "message_start" }│
  │◄──── { type: thinking_delta } │  (if thinking enabled)
  │◄──── { type: content_delta }  │  (streamed chunks)
  │◄──── { type: content_delta }  │
  │◄──── { type: tool_start }     │  (if tool called)
  │◄──── { type: tool_end }       │
  │◄──── { type: content_delta }  │  (post-tool response)
  │◄──── { type: message_end }    │
  │                               │
```

---

### Activity Stream WebSocket (`/ws/activity`)

Real-time stream of gateway activity events for monitoring.

**Connection URL:**
```
ws://localhost:5005/ws/activity?agent={agentFilter}
```

| Parameter | Required | Description |
|-----------|----------|-------------|
| `agent` | No | Filter events to a specific agent ID. Omit for all agents. |

**Behavior:**
- Subscribes to `IActivityBroadcaster` and streams all gateway activity events
- Events are JSON-serialized with `camelCase` naming
- Connection stays open until the client disconnects or the server shuts down
- When an `agent` filter is provided, only events for that agent are sent

---

## See Also

- [Developer Guide](dev-guide.md) — Local development setup and workflow
- [Configuration Guide](configuration.md) — Detailed configuration options
- [Getting Started](getting-started.md) — Quick start guide
- [Extension Development](extension-development.md) — Build custom tools and providers
