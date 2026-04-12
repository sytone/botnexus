---
id: feature-subagent-spawning
title: "Sub-Agent Spawning"
type: feature
priority: high
status: done
created: 2026-04-10
updated: 2026-04-10
author: nova
tags: [agents, subagents, delegation, parallel-work]
depends_on: []
---

# Design Spec: Sub-Agent Spawning

**Type**: Feature
**Priority**: High (unblocks parallel work, research delegation, context isolation)
**Status**: Draft
**Author**: Nova (via Jon)

## Overview

Add the ability for agents to spawn sub-agents - isolated LLM sessions that perform work independently and report results back to the parent session. This enables delegation of research, long-running tasks, and context-heavy operations without consuming the parent's context window.

## User Stories

1. **As Nova**, I want to spawn a research sub-agent so I can delegate deep-dive investigations while staying responsive to Jon.
2. **As Nova**, I want to use a cheaper model (gpt-4.1) for sub-tasks that don't need Opus-level reasoning.
3. **As Jon**, I want to see what sub-agents are running and their status.
4. **As Jon**, I want sub-agent results delivered to my conversation automatically when they complete.
5. **As an agent**, I want to restrict which tools a sub-agent can access for safety.

## Proposed Tool: `subagents`

### Actions

#### `spawn` - Create a new sub-agent

```json
{
  "action": "spawn",
  "task": "Research how context window compaction works across AI platforms",
  "agentId": "nova",
  "model": "gpt-4.1",
  "tools": ["read", "write", "bash", "web_search", "web_fetch", "grep", "glob"],
  "systemPrompt": "You are a research assistant. Be thorough and cite sources.",
  "workingDir": "C:\Users\jobullen\.botnexus\agents\nova\workspace",
  "background": true,
  "maxTurns": 20,
  "timeout": 300
}
```

**Parameters:**

| Parameter      | Type     | Required | Default              | Description                                    |
|----------------|----------|:--------:|----------------------|------------------------------------------------|
| `action`       | string   | Yes      |                      | `spawn`, `list`, `status`, `steer`, `kill`     |
| `task`         | string   | Yes*     |                      | Task description / initial prompt              |
| `agentId`      | string   | No       | parent agent         | Which agent config to use as base              |
| `model`        | string   | No       | agent default        | Override model for this sub-agent              |
| `tools`        | string[] | No       | parent tools - spawn | Tool allowlist                                 |
| `systemPrompt` | string   | No       | agent default        | Additional system prompt instructions          |
| `workingDir`   | string   | No       | parent workspace     | Working directory for the sub-agent            |
| `background`   | boolean  | No       | true                 | Run in background (false = foreground/blocking)|
| `maxTurns`     | integer  | No       | 30                   | Max conversation turns before auto-stop        |
| `timeout`      | integer  | No       | 600                  | Timeout in seconds                             |
| `name`         | string   | No       | auto-generated       | Human-readable label for the sub-agent         |

**Returns:**
```json
{
  "subagentId": "sa_abc123",
  "sessionId": "...",
  "status": "running",
  "name": "research-compaction"
}
```

#### `list` - List active sub-agents

```json
{
  "action": "list"
}
```

**Returns:**
```json
{
  "subagents": [
    {
      "subagentId": "sa_abc123",
      "name": "research-compaction",
      "status": "running",
      "model": "gpt-4.1",
      "startedAt": "2026-04-10T16:00:00Z",
      "turns": 5,
      "task": "Research how context window compaction works..."
    }
  ]
}
```

#### `status` - Get detailed status of a sub-agent

```json
{
  "action": "status",
  "subagentId": "sa_abc123"
}
```

#### `steer` - Send additional instructions to a running sub-agent

```json
{
  "action": "steer",
  "subagentId": "sa_abc123",
  "message": "Also look at how Windsurf handles context."
}
```

#### `kill` - Terminate a sub-agent

```json
{
  "action": "kill",
  "subagentId": "sa_abc123"
}
```

## Completion Flow

1. Sub-agent finishes its task (reaches conclusion, maxTurns, or timeout)
2. Sub-agent generates a **completion summary** (final assistant message)
3. BotNexus Gateway injects completion event into parent session:
   ```
   [Sub-agent "research-compaction" completed]
   <summary text from sub-agent's final response>
   ```
4. Parent agent wakes up and processes the result
5. Parent can relay findings to the user or take further action

## Architecture

### Session Model
```
Parent Session (nova, claude-opus-4.6)
  |
  +-- Sub-Agent Session (nova-subagent, gpt-4.1)
  |     task: "Research compaction..."
  |     tools: [read, write, bash, web_search, web_fetch]
  |     status: running -> completed
  |
  +-- Sub-Agent Session (nova-subagent, gpt-4.1)
        task: "Analyze ADO work items..."
        tools: [read, bash, invoke_mcp]
        status: running
```

### Database Schema Addition
```sql
-- New table for sub-agent tracking
CREATE TABLE subagent_sessions (
    subagent_id TEXT PRIMARY KEY,
    parent_session_id TEXT NOT NULL,
    child_session_id TEXT NOT NULL,
    name TEXT,
    task TEXT NOT NULL,
    model TEXT,
    status TEXT DEFAULT 'running',  -- running, completed, failed, killed
    started_at TEXT NOT NULL,
    completed_at TEXT,
    result_summary TEXT,
    FOREIGN KEY (parent_session_id) REFERENCES sessions(id),
    FOREIGN KEY (child_session_id) REFERENCES sessions(id)
);
```

### Key Implementation Points

1. **Session creation**: Reuse existing session infrastructure from `BotNexus.Session.dll`
2. **Tool scoping**: Filter available tools based on `tools` parameter
3. **Model routing**: Pass model override to provider when creating the sub-agent session
4. **Completion detection**: Sub-agent session ends when:
   - Agent outputs a final response with no tool calls
   - maxTurns reached
   - Timeout exceeded
   - Killed by parent
5. **Result injection**: Use existing session messaging to inject completion into parent
6. **Recursion guard**: Sub-agents cannot spawn sub-agents (depth limit = 1, configurable)

## Config Integration

### Pre-defined Sub-Agents (optional, future)
```json
{
  "agents": {
    "nova": {
      "subAgents": [
        {
          "name": "researcher",
          "description": "Deep-dive research on any topic",
          "model": "gpt-4.1",
          "tools": ["read", "write", "bash", "web_search", "web_fetch", "grep", "glob"],
          "systemPrompt": "You are a thorough research assistant."
        },
        {
          "name": "coder",
          "description": "Write and debug code",
          "model": "claude-opus-4.6",
          "tools": ["read", "write", "edit", "bash", "grep", "glob"],
          "systemPrompt": "You are a senior developer."
        }
      ]
    }
  }
}
```

### Agent Defaults
```json
{
  "gateway": {
    "subagents": {
      "defaultModel": "gpt-4.1",
      "maxConcurrent": 5,
      "maxTurns": 30,
      "timeoutSeconds": 600,
      "maxDepth": 1
    }
  }
}
```

## Phases

### Phase 1: Core Spawning (MVP)
- `spawn` action with background execution
- `list` and `kill` actions
- Completion notification to parent session
- Model override support
- Tool scoping

### Phase 2: Interaction
- `steer` action (send messages to running sub-agent)
- `status` action with detailed progress
- Foreground (blocking) mode

### Phase 3: Pre-defined Sub-Agents
- Config-based sub-agent definitions
- Auto-delegation based on task description matching
- Shared memory between parent and sub-agents

## Testing Plan

1. **Unit tests**: Subagent session creation, tool scoping, completion detection
2. **Integration tests**: Full spawn -> work -> complete -> notify cycle
3. **Edge cases**: Timeout handling, kill during work, recursion prevention
4. **Cost tests**: Verify cheaper model is actually used for sub-agent sessions

## Open Questions

1. Should sub-agents share the parent's workspace or get an isolated one?
   - **Recommendation**: Shared workspace (same working directory) for file access
2. Should sub-agent sessions persist in the session store like regular sessions?
   - **Recommendation**: Yes, tagge
