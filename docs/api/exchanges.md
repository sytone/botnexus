# Exchanges API

Base route: `api/exchanges` — implemented by `ExchangesController`
(`src/gateway/BotNexus.Gateway.Api/Controllers/ExchangesController.cs`).

This controller exposes read-only diagnostics for the **agent exchange budget**: the
daily turn caps, loop counters, and cooldowns that govern agent-to-agent conversation.
See [Agent-to-Agent Communication](../features/agent-exchange.md) for the feature
overview and the configuration that produces these numbers.

Every route below goes through the normal gateway API-key check described in the
[REST API overview](README.md#authentication).

---

## `GET /api/exchanges/budget`

Returns the budget state of every tracked initiator/target agent pair.

### Query parameters

| Parameter | Type | Notes |
|-----------|------|-------|
| `initiator` | string | Optional. Keeps only pairs whose initiator matches, compared **case-insensitively**. |
| `target` | string | Optional. Keeps only pairs whose target matches, compared **case-insensitively**. |

Both filters are applied together (logical AND). An unknown agent id is not an error —
it simply matches nothing and yields an empty `pairs` array.

### Response

Always `200 OK`. The body is an **object**, not a bare array:

| Field | Type | Notes |
|-------|------|-------|
| `pairs` | array | One entry per tracked pair that survived the filters. |
| `totalPairs` | int | The number of entries in `pairs` — the count **after** filtering, not the total tracked. |
| `timestamp` | string (ISO-8601) | UTC instant the snapshot was taken. Cooldown fields are relative to this. |

Each element of `pairs`:

| Field | Type | Notes |
|-------|------|-------|
| `initiator` | string | Initiating agent id. |
| `target` | string | Target agent id. |
| `dailyTurnsUsed` | int | Turns consumed by this pair in the current budget day. |
| `dailyTurnCap` | int | The configured cap the pair is measured against. |
| `cooldownActive` | bool | `true` only when a cooldown is set **and** still in the future at `timestamp`. |
| `cooldownRemainingSeconds` | int | Seconds until the cooldown expires, rounded **up**. `0` when `cooldownActive` is `false`. |
| `loopCounter` | int | Loop-detection counter for the pair. |
| `lastInteraction` | string (ISO-8601) or `null` | End of the pair's most recent exchange; `null` when the pair has never completed one. |

```json
{
  "pairs": [
    {
      "initiator": "coordinator",
      "target": "reporter",
      "dailyTurnsUsed": 12,
      "dailyTurnCap": 200,
      "cooldownActive": false,
      "cooldownRemainingSeconds": 0,
      "loopCounter": 0,
      "lastInteraction": "2026-08-13T09:41:12.4180000+00:00"
    }
  ],
  "totalPairs": 1,
  "timestamp": "2026-08-13T09:55:03.1027451+00:00"
}
```

### When exchange budgeting is not running

The controller takes its budget tracker as an **optional** dependency. When no tracker
is registered, the endpoint still returns `200 OK` with an empty snapshot
(`pairs: []`, `totalPairs: 0`, a current `timestamp`) rather than `404` or `503`.

> A response of `totalPairs: 0` therefore means either "no pairs matched" or "budget
> tracking is not active". The two are not distinguishable from this endpoint alone.

### Pairs appear only after they are used

The tracker holds pair state in memory, keyed `"{initiator}:{target}"`, and creates an
entry the first time that pair exchanges. A configured-but-never-used pair does not
appear, and the whole set is lost on gateway restart. Treat this endpoint as a live
diagnostic, not as a durable record.

---

## See also

- [Agent-to-Agent Communication](../features/agent-exchange.md) — the feature and its configuration
- [REST API overview](README.md)
