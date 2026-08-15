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

Agents can be configured to converse on a cron schedule using the `agent-converse` action type. Jobs
live in `cron.sqlite` and are created either through the `cron` tool / `/api/cron`, or declaratively
under the `cron.jobs` section of `config.json`, which the scheduler syncs into the store at startup.
The declarative form — the same shape used throughout
[Cron & Scheduling](/cron-and-scheduling) — is:

```json
{
  "cron": {
    "jobs": {
      "morning-sync": {
        "name": "Morning sync",
        "schedule": "0 9 * * 1-5",
        "actionType": "agent-converse",
        "agentId": "analyst",
        "enabled": true,
        "metadata": {
          "targetAgentId": "reporter",
          "message": "Generate the morning status report.",
          "objective": "Get daily status",
          "maxTurns": "5"
        }
      }
    }
  }
}
```

The map key (`morning-sync`) is the job **ID** — the value every `botnexus cron` subcommand takes; the
`name` field is a display label only. `agentId` is the **initiator** and is required; `targetAgentId`
in `metadata` is the agent it converses with. `maxTurns` defaults to `5`
(`AgentConverseCronAction.DefaultMaxTurns`).

The `agent-converse` cron action respects budget enforcement — if a pair is in cooldown or at daily cap, the job is skipped and logged.

## Handoff Observability

An `agent_converse` call is blocking: the delegating agent waits for the target to finish. Without
further plumbing that means a silent gap - the exchange runs in its own conversation and session,
invisible from the thread that started it. Three mechanisms close that gap (#3176).

### 1. The tool result names the child exchange

The `agent_converse` result includes `conversationId` and `sessionId` for the exchange that was
created. A delegating agent asked "where is that work happening?" can answer from its own tool
result, without querying the target agent or the portal.

```json
{
  "sessionId": "...",
  "conversationId": "c_...",
  "status": "sealed",
  "turns": 2,
  "completionReason": "exchangeFinished"
}
```

### 2. Progress events land in the initiating conversation

While the exchange runs, status lines are published back into the conversation that initiated it,
over the same outbound fan-out path assistant replies use. They are **status lines, not a transcript
replay** - the child agent's output is never mirrored into the parent thread.

| Phase | When | Reason field |
|---|---|---|
| `started` | after the child session is pinned, before the first turn | - |
| `completed` | the target signalled completion, or a single-shot call returned | `exchangeFinished` / `singleShot` |
| `halted` | a guard stopped it rather than the target | `maxTurnsReached`, or the budget refusal message |
| `failed` | the exchange threw | the exception message |

`halted` is deliberately distinct from `completed`: "we ran out of turns" and "the target finished"
are different outcomes and a reader must be able to tell them apart. A budget or cooldown refusal
emits `halted` with no child ids, because no exchange was ever admitted.

The messages carry `MessageKind.AgentExchangeProgress`, so a channel or the portal can render or
filter them distinctly from a genuine assistant reply without parsing message text.

Emission is strictly advisory. A caller that ignores progress observes byte-identical behaviour and
result shape, and a failure in the delivery path degrades the handoff to silent - it can never fail
the exchange itself.

### 3. The child exchange is discoverable from its parent

The exchange handshake already registers both agents as participants on the child conversation, so
`IConversationStore.ListForCitizenAsync` returns it for either side. When the handoff came from a
known conversation, the child is additionally stamped with `metadata.parentConversationId`, which
narrows that list to the exchanges belonging to one specific parent thread.

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
