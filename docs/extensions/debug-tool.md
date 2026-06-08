# Debug Tool Extension

The Debug Tool provides agents with read-only access to the platform's `sessions.db` database and gateway runtime state. It enables agents to inspect their own sessions, conversations, history entries, and sub-agent relationships for debugging and introspection.

## Overview

| Property | Value |
|----------|-------|
| Extension ID | `botnexus-debug-tool` |
| Tool name | `platform_debug` |
| Access | Read-only (SQLite `Mode=ReadOnly`) |
| Scope | Agent-scoped by default |

## Enabling

The Debug Tool is loaded via extension discovery. Add it to your agent's tool list in `config.json`:

```json
{
  "agents": {
    "my-agent": {
      "tools": ["platform_debug"]
    }
  }
}
```

## Actions

The tool supports the following actions:

| Action | Description |
|--------|-------------|
| `query_sessions` | List sessions for the current agent (filterable by conversation, status) |
| `query_conversations` | List conversations (filterable by status, kind) |
| `query_history` | Retrieve session history entries (filterable by role, kind) |
| `query_sub_agents` | List sub-agent sessions spawned from a parent session |
| `session_info` | Get detailed information about a specific session |
| `conversation_info` | Get detailed information about a specific conversation |
| `runtime_status` | Query gateway runtime state (active sessions, memory, uptime) |
| `raw_sql` | Execute arbitrary SELECT statements (gated, must be enabled) |

## Parameters

Common parameters available across actions:

| Parameter | Type | Description |
|-----------|------|-------------|
| `action` | string | Required. The action to perform. |
| `session_id` | string | Session ID filter |
| `conversation_id` | string | Conversation ID filter |
| `agent_id` | string | Agent ID filter (defaults to current agent) |
| `status` | string | Status filter |
| `kind` | string | Kind filter (for conversations) |
| `role` | string | Role filter (for history entries) |
| `limit` | integer | Max results (default: 50, max: 500) |
| `offset` | integer | Pagination offset |
| `sql` | string | SQL query (for `raw_sql` action only) |

## Usage Examples

### List recent sessions

```
platform_debug action=query_sessions limit=10
```

### Inspect a session's history

```
platform_debug action=query_history session_id=sess_abc123 role=assistant
```

### Check runtime status

```
platform_debug action=runtime_status
```

### Raw SQL (when enabled)

```
platform_debug action=raw_sql sql="SELECT COUNT(*) as total FROM sessions WHERE agent_id = 'my-agent'"
```

## Security

- All database access is **read-only** — no writes are possible through this tool
- Queries are **agent-scoped** by default — agents can only see their own data unless `agent_id` is explicitly specified
- The `raw_sql` action is **gated** behind a configuration flag and only allows SELECT statements
- No access to credentials, API keys, or other sensitive configuration

## Configuration

```json
{
  "extensions": {
    "debugTool": {
      "enabled": true,
      "allowRawSql": false
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `enabled` | `true` | Whether the debug tool is available |
| `allowRawSql` | `false` | Whether the `raw_sql` action is permitted |
