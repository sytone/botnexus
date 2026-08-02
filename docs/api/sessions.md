# Sessions API Reference

Reference for the **Sessions** endpoints — listing, inspection, history, metadata,
lifecycle transitions, and sub-agent runs.

All endpoints are served under the base route `api/sessions` and require the gateway API key
(see [Authentication](README.md#authentication)). A second, top-level `api/subagents`
controller is documented at the [bottom of this page](#sub-agent-runs-apisubagents).

Sources:

- `src/gateway/BotNexus.Gateway.Api/Controllers/SessionsController.cs`
- `src/gateway/BotNexus.Gateway.Api/Controllers/SubAgentsController.cs`

---

## Per-session caller authorization

Several endpoints apply a per-session caller check in addition to the gateway API key. The
caller identity established by `GatewayAuthMiddleware` is compared against the session's
`CallerId`; a mismatch returns:

```
403 Forbidden
{ "error": "Caller is not authorized for this session." }
```

and emits a security event with a hashed caller id. The check is applied to
`GET/PATCH .../metadata`, `DELETE /{sessionId}`, `PATCH .../suspend`, `PATCH .../resume`,
and `PATCH .../seal`.

---

## Endpoints

| Verb | Route | Purpose |
|------|-------|---------|
| GET | `/api/sessions` | List sessions. |
| GET | `/api/sessions/stats` | Aggregate session statistics. |
| GET | `/api/sessions/{sessionId}` | Get one session. |
| GET | `/api/sessions/{sessionId}/history` | Paginated conversation history. |
| GET | `/api/sessions/{sessionId}/debug` | Debug snapshot (prompt + history + metadata). |
| GET | `/api/sessions/{sessionId}/export/markdown` | Download the transcript as Markdown. |
| GET | `/api/sessions/{sessionId}/metadata` | Read session metadata. |
| PATCH | `/api/sessions/{sessionId}/metadata` | Merge metadata entries. |
| DELETE | `/api/sessions/{sessionId}` | Delete a session. |
| PATCH | `/api/sessions/{sessionId}/suspend` | Suspend an active session. |
| PATCH | `/api/sessions/{sessionId}/resume` | Resume a suspended session. |
| PATCH | `/api/sessions/{sessionId}/seal` | Seal a finished sub-agent session. |
| GET | `/api/sessions/{sessionId}/subagents` | Live sub-agents of a session. |
| GET | `/api/sessions/{sessionId}/subagents/history` | Persisted sub-agent runs of a session. |
| DELETE | `/api/sessions/{sessionId}/subagents/{subAgentId}` | Kill a sub-agent. |
| GET | `/api/subagents` | List persisted sub-agent runs across all sessions. |

---

## Listing & stats

### `GET /api/sessions`

| Parameter | In | Type | Default | Notes |
|-----------|----|------|---------|-------|
| `agentId` | query | string | — | Filter to one agent. Applied in the **store query**, not post-hoc on a page. |
| `conversationId` | query | string | — | When set, only sessions linked to this conversation are returned. |
| `includeInactive` | query | bool | `false` | When `true`, includes sealed and expired sessions. When `false`, only `Active` and `Suspended` are returned. |
| `offset` | query | int | `0` | Zero-based offset into the newest-first **filtered** set. |
| `limit` | query | int | `50` | Clamped to a server maximum of `200`. |

Returns `200 OK` with a paging envelope:

| Field | Type | Notes |
|-------|------|-------|
| `sessions` | array | The projected summaries for this page (see below). |
| `totalCount` | int | Total rows matching the filters, across all pages. |
| `hasMore` | bool | Authoritative termination signal. |
| `offset` | int | The offset that was applied. |
| `limit` | int | The **clamped** limit that was applied. |

> **Do not infer exhaustion from a short page.** The server clamps `limit` to its own maximum,
> so `sessions.length < limit` says nothing about whether more rows exist. Terminate on
> `hasMore === false`.

Each entry in `sessions` is a projected summary:

| Field | Type | Notes |
|-------|------|-------|
| `sessionId` | string | |
| `agentId` | string | |
| `channelType` | string \| null | |
| `conversationId` | string \| null | `null` for the unset sentinel, so clients see a stable shape. |
| `status` | string | `Active`, `Suspended`, `Sealed`, … |
| `sessionType` | string | Typed session discriminator. |
| `isInteractive` | bool | |
| `messageCount` | int | Derived from a `COUNT(*)` aggregate, not a transcript hydration. |
| `createdAt` | timestamp | |
| `updatedAt` | timestamp | |

Returns `503 Service Unavailable` with `{ "error": "Session store temporarily unavailable. Please retry." }`
when the session store is briefly unavailable.

> This route deliberately uses the transcript-free summary read so the portal sidebar never
> pays for hydrating every session's full transcript just to render metadata. The `agentId`,
> `conversationId` and status predicates are pushed into the store query so that `offset`
> addresses exactly the set the client is consuming — filtering a page *after* it came back
> put `limit`/`offset` in a different coordinate space from the returned rows, so a client
> advancing by the row count crept forward one row at a time and only terminated by walking
> the entire global session table.

### `GET /api/sessions/stats`

| Parameter | In | Type | Notes |
|-----------|----|------|-------|
| `agentId` | query | string | Optional agent filter. |

Returns `200 OK` with the store's stats object, or `404 Not Found` with
`Session statistics not supported by the current store implementation.` when the configured
store cannot compute them.

### `GET /api/sessions/{sessionId}`

Returns `200 OK` with the `GatewaySession`, or `404 Not Found`.

---

## History, debug & export

### `GET /api/sessions/{sessionId}/history`

| Parameter | In | Type | Default | Notes |
|-----------|----|------|---------|-------|
| `offset` | query | int | `0` | Zero-based. Must be `>= 0`. |
| `limit` | query | int | `50` | Must be `> 0`. |

Returns `200 OK` with a `SessionHistoryResponse`. Invalid paging returns `400 Bad Request`
with `{ "error": "offset must be greater than or equal to zero." }` or the equivalent
`limit` message.

### `GET /api/sessions/{sessionId}/debug`

| Parameter | In | Type | Default | Notes |
|-----------|----|------|---------|-------|
| `offset` | query | int | `0` | Clamped to `>= 0`. |
| `limit` | query | int | `50` | Clamped to the range 1–200. |

Returns `200 OK` with a debug snapshot (session fields, system prompt, paginated history,
metadata) or `404 Not Found`. Unlike `/history`, out-of-range paging values are clamped
rather than rejected.

### `GET /api/sessions/{sessionId}/export/markdown`

Renders the transcript via `SessionTranscriptRenderer`.

| Status | Condition |
|--------|-----------|
| `200 OK` | `text/markdown` file download named `session-{sessionId}.md`. |
| `204 No Content` | The session exists but renders no transcript. |
| `404 Not Found` | No such session. |

Secret redaction follows the gateway's transcript-export configuration.

---

## Metadata

### `GET /api/sessions/{sessionId}/metadata`

Returns `200 OK` with the session's metadata dictionary, `403` (see
[caller authorization](#per-session-caller-authorization)), or `404 Not Found`.

### `PATCH /api/sessions/{sessionId}/metadata`

Body: a JSON **object**. Each property is merged into the session metadata; a property whose
value is `null` **removes** that key.

| Status | Condition |
|--------|-----------|
| `200 OK` | Body is the updated metadata dictionary. |
| `400 Bad Request` | Body was not a JSON object: `{ "error": "Metadata patch body must be a JSON object." }`. |
| `403 Forbidden` | Caller is not authorized for this session. |
| `404 Not Found` | No such session. |

---

## Lifecycle

### `DELETE /api/sessions/{sessionId}`

Returns `204 No Content` — **including when the session does not exist**. Surfacing `404`
here would create an existence-disclosure oracle for authenticated probes and would break
DELETE retry idempotency. Returns `403 Forbidden` when the caller does not own the session.

### `PATCH /api/sessions/{sessionId}/suspend`

| Status | Condition |
|--------|-----------|
| `200 OK` | Body is the updated `GatewaySession` with `status = Suspended`. |
| `403` / `404` | Not authorized / no such session. |
| `409 Conflict` | `{ "error": "Cannot suspend session in '<status>' state." }` — only an `Active` session can be suspended. |

### `PATCH /api/sessions/{sessionId}/resume`

Mirror of suspend: only a `Suspended` session can be resumed, otherwise `409 Conflict` with
`Cannot resume session in '<status>' state.`

### `PATCH /api/sessions/{sessionId}/seal`

Seals a finished sub-agent session so it cannot be reused.

| Status | Condition |
|--------|-----------|
| `200 OK` | `{ "sessionId": "...", "status": "Sealed", "updatedAt": "..." }`. |
| `204 No Content` | Already sealed (idempotent). |
| `400 Bad Request` | `{ "error": "Only sub-agent sessions can be sealed" }`. |
| `409 Conflict` | `{ "error": "Cannot seal an active session" }` — the session is `Active` or `Suspended`. |
| `403` / `404` | Not authorized / no such session. |

Sub-agent eligibility is driven by the typed `SessionType` discriminator, not by an id
substring check.

---

## Sub-agents of a session

### `GET /api/sessions/{sessionId}/subagents`

Returns `200 OK` with `SubAgentInfo[]` for the live sub-agents of the parent session, or
`404 Not Found` when the session is unknown.

### `GET /api/sessions/{sessionId}/subagents/history`

Returns `200 OK` with `SubAgentSessionSummary[]` — the persisted runs for that parent
session — or `404 Not Found`.

### `DELETE /api/sessions/{sessionId}/subagents/{subAgentId}`

| Status | Condition |
|--------|-----------|
| `204 No Content` | Killed. |
| `403 Forbidden` | The requesting session does not own the sub-agent. |
| `404 Not Found` | Unknown session or sub-agent. |

---

## Sub-agent runs (`/api/subagents`)

Source: `SubAgentsController` (`[Route("api/subagents")]`).

### `GET /api/subagents`

Lists persisted sub-agent runs across **all** parent sessions, newest-started first.

| Parameter | In | Type | Default | Notes |
|-----------|----|------|---------|-------|
| `status` | query | string | - | Case-insensitive status filter, e.g. `Active`, `Completed`, `Failed`, `Killed`, `TimedOut`, `BudgetExhausted`. Omit for all statuses. |
| `limit` | query | int | `200` | Must be `> 0`; values above `500` are clamped to `500`. |

Returns `200 OK` with `SubAgentSessionSummary[]`, or `400 Bad Request` with
`{ "error": "limit must be greater than zero." }`.

---

## See also

- [Agents API](agents.md)
- [SignalR hub reference](signalr.md) — live `SubAgentSpawned` / `SubAgentCompleted` / `SubAgentFailed` / `SubAgentKilled` events
- [REST API overview](README.md)
