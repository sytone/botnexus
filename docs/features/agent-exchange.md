# Agent-to-Agent Communication

BotNexus supports direct agent-to-agent communication through an exchange system with configurable access policies and budget limits to prevent runaway loops.

## Overview

Agents can converse with each other using the `agent_converse` tool or via scheduled cron jobs. Communication is governed by:

1. **Target Resolution** — how the supplied target string maps to an agent
2. **Access Policy** — who can talk to whom
3. **Budget System** — daily caps, loop detection, and cooldown enforcement

## Target Resolution

The `agentId` argument of `agent_converse` is resolved in strict precedence order:

1. **Exact agent id** — always wins, so an id-addressed call can never change meaning.
2. **Cross-world reference** — a `world:agent` target is routed to the peer world.
3. **Display name** — a case-insensitive match against every registered agent's `displayName`.

Display-name resolution exists because display names drift from ids as a matter of course: an agent
created for one purpose and later renamed keeps its original id forever, since ids key workspaces,
cron jobs and session history. Addressing the agent shown everywhere as *Sentinel* should not require
knowing that its id is `ub-warning-cleanup`.

Resolution rules:

- **Exactly one display-name match** — resolves to that agent and the exchange proceeds.
- **Two or more matches** — fails with an ambiguity error listing every candidate id. The target is
  never guessed, because dispatching to the wrong same-named peer is worse than failing.
- **No match** — fails as a target-resolution failure, explicitly distinguished from a policy denial.

The access-policy check below runs against the **resolved** agent, so addressing an agent by display
name is never a way around a `whitelist` restriction.

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

Agents can be configured to converse on a cron schedule using the `agent-converse` action. Cron jobs
are not a configuration-file section - they live in `cron.sqlite` and are created through the `cron`
tool or `botnexus debug cron`. A single job's stored shape is:

```json
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
```

The `agent-converse` cron action respects budget enforcement — if a pair is in cooldown or at daily cap, the job is skipped and logged.

## Diagnostics

### REST Endpoint

```
GET /api/exchanges/budget
GET /api/exchanges/budget?initiator=agent-a&target=agent-b
```

Returns current budget state for all or specific agent pairs, including:
- Turns used today and the cap they are measured against
- Cooldown status and the seconds remaining
- Loop detection counter
- The end of the pair's last exchange

The authoritative request and response shape — verified against `ExchangesController` — lives in
the [Exchanges API reference](/api/exchanges). Note in particular that the response is an object
wrapping a `pairs` array, not a bare array.

## Related

- [Exchanges API reference](/api/exchanges) — controller-verified shape of `/api/exchanges/budget`
- [Built-in Agents](/features/built-in-agents) — Agents available out of the box
- [Sub-Agent Spawning](/features/sub-agent-spawning) — Ephemeral sub-agent delegation
- [Cron & Scheduling](/cron-and-scheduling) — Scheduled job configuration
