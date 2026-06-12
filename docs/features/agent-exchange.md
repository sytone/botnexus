# Agent-to-Agent Communication

BotNexus supports direct agent-to-agent communication through an exchange system with configurable access policies and budget limits to prevent runaway loops.

## Overview

Agents can converse with each other using the `agent_converse` tool or via scheduled cron jobs. Communication is governed by:

1. **Access Policy** — who can talk to whom
2. **Budget System** — daily caps, loop detection, and cooldown enforcement

## Access Policies

Configure the exchange access policy in the gateway section:

```json
{
  "gateway": {
    "agentExchange": {
      "accessPolicy": "open"
    }
  }
}
```

| Policy | Behavior |
|--------|----------|
| `open` | Any agent can converse with any other agent (default). |
| `whitelist` | Only agents listed in `subAgentIds` on the initiator can be contacted. |

When `open` is set, the `ListAgents` tool shows `canConverse: true` for all agents. Under `whitelist`, the legacy `SubAgentIds` / `SubAgentRoles` restrictions apply.

## Budget System

The budget system prevents runaway agent loops and excessive resource consumption:

```json
{
  "gateway": {
    "agentExchange": {
      "budget": {
        "dailyCap": 200,
        "loopWindowSeconds": 60,
        "loopThreshold": 3,
        "cooldownSeconds": 300
      }
    }
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `dailyCap` | integer | 200 | Maximum exchanges per agent pair per day. |
| `loopWindowSeconds` | integer | 60 | Time window for loop detection. |
| `loopThreshold` | integer | 3 | Exchanges within the window that trigger cooldown. |
| `cooldownSeconds` | integer | 300 | Seconds a pair must wait after loop detection. |

### How It Works

- Each agent pair (A→B) has an independent budget tracker
- When a pair exceeds `loopThreshold` exchanges within `loopWindowSeconds`, a cooldown is applied
- During cooldown, further exchanges between that pair are rejected
- The daily cap resets at UTC midnight

## Scheduled Agent Conversations

Agents can be configured to converse on a cron schedule using the `agent-converse` action:

```json
{
  "BotNexus.Cron.Jobs": [
    {
      "id": "morning-sync",
      "schedule": "0 9 * * 1-5",
      "action": "agent-converse",
      "metadata": {
        "targetAgentId": "reporter",
        "message": "Generate the morning status report.",
        "objective": "Get daily status",
        "maxTurns": 5
      }
    }
  ]
}
```

The `agent-converse` cron action respects budget enforcement — if a pair is in cooldown or at daily cap, the job is skipped and logged.

## Diagnostics

### REST Endpoint

```
GET /api/exchanges/budget
GET /api/exchanges/budget?initiator=agent-a&target=agent-b
```

Returns current budget state for all or specific agent pairs, including:
- Exchanges today
- Remaining daily quota
- Cooldown status and expiry
- Loop detection window state

## Related

- [Built-in Agents](/features/built-in-agents) — Agents available out of the box
- [Sub-Agent Spawning](/features/sub-agent-spawning) — Ephemeral sub-agent delegation
- [Cron & Scheduling](/cron-and-scheduling) — Scheduled job configuration
