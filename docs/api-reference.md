# BotNexus API Reference

Complete reference for BotNexus REST API endpoints, including agents, sessions, providers, skills, and system status.

## Table of Contents

1. [Overview](#overview)
2. [Authentication](#authentication)
3. [Agent Management](#agent-management)
4. [Skills Management](#skills-management)
5. [Session Management](#session-management)
6. [System & Status](#system--status)
7. [Error Handling](#error-handling)

---

## Overview

**Base URL:** `http://localhost:18790/api`

All endpoints follow REST conventions and return JSON responses. The default port is **18790** (configurable via `config.json`).

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

**Exemptions:** `/health` and `/ready` health check endpoints do not require authentication.

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
    "name": "assistant",
    "systemPrompt": "You are a helpful assistant",
    "model": "gpt-4o",
    "provider": "openai",
    "maxTokens": 2000,
    "temperature": 0.7,
    "disallowedTools": []
  },
  {
    "name": "analyzer",
    "systemPrompt": "You are a data analyst",
    "model": "claude-3-5-sonnet",
    "provider": "anthropic",
    "maxTokens": null,
    "temperature": null,
    "disallowedTools": ["shell"]
  }
]
```

---

### Get Agent Details

**Endpoint:** `GET /api/agents/{name}`

**Description:** Retrieve configuration for a specific agent.

**Parameters:**
- `name` (string, path) — Agent name (normalized to lowercase with dashes)

**Request:**
```http
GET /api/agents/my-agent
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
{
  "name": "my-agent",
  "systemPrompt": "You are helpful",
  "model": "gpt-4o",
  "provider": "openai",
  "maxTokens": 2000,
  "temperature": 0.7,
  "disallowedTools": ["filesystem"]
}
```

**Error Responses:**
- `404 Not Found` — Agent does not exist
- `400 Bad Request` — Invalid agent name

---

### Create Agent

**Endpoint:** `POST /api/agents`

**Description:** Create a new agent with the specified configuration.

**Request Body:**
```json
{
  "name": "my-agent",
  "systemPrompt": "You are a helpful assistant",
  "model": "gpt-4o",
  "provider": "openai",
  "maxTokens": 2000,
  "temperature": 0.7,
  "disallowedTools": []
}
```

**Field Descriptions:**
- `name` (string, required) — Agent name (will be normalized)
- `systemPrompt` (string, optional) — System instruction for the agent
- `model` (string, optional) — Model name (e.g., "gpt-4o", "claude-3-5-sonnet")
- `provider` (string, optional) — Provider name (e.g., "openai", "anthropic", "copilot")
- `maxTokens` (integer, optional) — Max tokens per response. When null, provider uses its default
- `temperature` (number, optional) — Temperature for randomness (0.0-2.0). When null, provider uses its default
- `disallowedTools` (array of strings, optional) — Tools to disable for this agent (e.g., ["shell", "filesystem"])

**Request:**
```http
POST /api/agents
X-Api-Key: your-api-key
Content-Type: application/json

{
  "name": "analyzer",
  "systemPrompt": "Analyze data professionally",
  "model": "gpt-4o",
  "provider": "openai",
  "maxTokens": 4000,
  "temperature": 0.5,
  "disallowedTools": ["shell"]
}
```

**Response:** 201 Created
```json
{
  "name": "analyzer",
  "systemPrompt": "Analyze data professionally",
  "model": "gpt-4o",
  "provider": "openai",
  "maxTokens": 4000,
  "temperature": 0.5,
  "disallowedTools": ["shell"]
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

**Endpoint:** `PUT /api/agents/{name}`

**Description:** Update an existing agent's configuration.

**Parameters:**
- `name` (string, path) — Agent name

**Request Body:** (same as POST, all fields optional)
```json
{
  "systemPrompt": "Updated instructions",
  "temperature": 0.9
}
```

**Request:**
```http
PUT /api/agents/my-agent
X-Api-Key: your-api-key
Content-Type: application/json

{
  "model": "gpt-4-turbo",
  "temperature": 0.8
}
```

**Response:** 200 OK
```json
{
  "name": "my-agent",
  "systemPrompt": "You are helpful",
  "model": "gpt-4-turbo",
  "provider": "openai",
  "maxTokens": 2000,
  "temperature": 0.8,
  "disallowedTools": []
}
```

**Side Effects:**
- Config is backed up to `config.json.bak` before update
- Config is hot-reloaded (no restart needed)

**Error Responses:**
- `404 Not Found` — Agent does not exist
- `400 Bad Request` — Invalid configuration
- `500 Internal Server Error` — Update failed

---

### Delete Agent

**Endpoint:** `DELETE /api/agents/{name}`

**Description:** Remove an agent from the configuration.

**Parameters:**
- `name` (string, path) — Agent name

**Request:**
```http
DELETE /api/agents/my-agent
X-Api-Key: your-api-key
```

**Response:** 204 No Content

**Side Effects:**
- Config is backed up to `config.json.bak`
- Agent workspace directory is preserved (not deleted)
- Config is hot-reloaded

**Error Responses:**
- `404 Not Found` — Agent does not exist
- `500 Internal Server Error` — Deletion failed

---

## Skills Management

Skills are modular knowledge packages that enhance agent reasoning. Learn more in the [Skills Guide](./skills.md).

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

## Session Management

### List Sessions

**Endpoint:** `GET /api/sessions`

**Description:** Retrieve all conversation sessions.

**Query Parameters:**
- `hidden` (boolean, optional) — Filter by hidden status (true/false)

**Request:**
```http
GET /api/sessions
X-Api-Key: your-api-key
```

**Response:** 200 OK
```json
[
  {
    "key": "session-abc123",
    "agentName": "my-agent",
    "title": "Conversation about AI",
    "hidden": false,
    "createdAt": "2026-01-15T10:30:00Z",
    "updatedAt": "2026-01-15T11:45:00Z",
    "messageCount": 12
  }
]
```

---

### Hide/Unhide Session

**Endpoint:** `PATCH /api/sessions/{key}`

**Description:** Hide or unhide a session in the WebUI.

**Parameters:**
- `key` (string, path) — Session key/ID

**Request Body:**
```json
{
  "hidden": true
}
```

**Request:**
```http
PATCH /api/sessions/session-abc123
X-Api-Key: your-api-key
Content-Type: application/json

{
  "hidden": true
}
```

**Response:** 200 OK
```json
{
  "key": "session-abc123",
  "agentName": "my-agent",
  "title": "Conversation about AI",
  "hidden": true,
  "createdAt": "2026-01-15T10:30:00Z",
  "updatedAt": "2026-01-15T11:50:00Z",
  "messageCount": 12
}
```

**Side Effects:**
- Session is removed from WebUI sidebar when `hidden: true`
- Session is restored to sidebar when `hidden: false`

**Error Responses:**
- `404 Not Found` — Session does not exist
- `400 Bad Request` — Invalid request

---

## System & Status

### Health Check

**Endpoint:** `GET /health`

**Description:** Basic health check (no authentication required).

**Response:** 200 OK
```json
{
  "status": "healthy",
  "timestamp": "2026-01-15T11:50:00Z"
}
```

---

### Readiness Check

**Endpoint:** `GET /ready`

**Description:** Check if the system is ready to accept requests (no authentication required).

**Response:** 200 OK
```json
{
  "ready": true,
  "agents": 3,
  "providers": 2,
  "timestamp": "2026-01-15T11:50:00Z"
}
```

---

### Doctor/Diagnostics

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
  "error": "Agent not found",
  "code": "AGENT_NOT_FOUND",
  "statusCode": 404,
  "timestamp": "2026-01-15T11:50:00Z"
}
```

### Common Status Codes

| Code | Meaning |
|------|---------|
| 200 | Success |
| 201 | Created |
| 204 | No Content (success with no body) |
| 400 | Bad Request (invalid input) |
| 401 | Unauthorized (missing/invalid API key) |
| 404 | Not Found |
| 409 | Conflict (duplicate name, etc.) |
| 500 | Internal Server Error |

### Error Codes

| Code | HTTP | Meaning |
|------|------|---------|
| INVALID_INPUT | 400 | Missing or invalid field |
| AGENT_NOT_FOUND | 404 | Agent does not exist |
| DUPLICATE_AGENT | 409 | Agent name already exists |
| SESSION_NOT_FOUND | 404 | Session does not exist |
| UNAUTHORIZED | 401 | Invalid or missing API key |
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
curl -X POST http://localhost:18790/api/agents \
  -H "X-Api-Key: your-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "secure-agent",
    "disallowedTools": ["shell", "filesystem"]
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
  "BotNexus": {
    "Agents": {
      "Model": "gpt-4o",
      "Named": {
        "my-agent": {
          "Model": "gpt-4-turbo",
          "MaxTokens": 4000,
          "DisallowedTools": ["shell"]
        }
      }
    },
    "Providers": {
      "openai": {
        "Auth": "api-key",
        "ApiKey": "sk-...",
        "DefaultModel": "gpt-4o"
      }
    },
    "Tools": {
      "Exec": {
        "Enable": true
      }
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

## See Also

- [Configuration Guide](configuration.md) — Detailed configuration options
- [Getting Started](getting-started.md) — Quick start guide
- [Extension Development](extension-development.md) — Build custom tools and providers
