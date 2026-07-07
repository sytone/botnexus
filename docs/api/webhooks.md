# Webhooks API Reference

Complete reference for the BotNexus webhook endpoints. For concepts, response
modes, and worked examples see the [Webhooks guide](../guides/webhooks.md).

All endpoints are served under the base route `api/webhooks`. There are two
controllers:

- **Registration management** — CRUD over webhook registrations and run status
  polling.
- **Inbound delivery** — the signed endpoint external systems POST messages to.

---

## Data types

### Response mode

`WebhookResponseMode` is one of:

| Value      | Meaning |
|------------|---------|
| `async`    | 202 immediately + `Location` poll URL; agent runs in background (default). |
| `sync`     | Holds the connection open until the agent completes (≤120s), returns inline. |
| `callback` | 202 immediately; POSTs the result to a `callbackUrl` on completion. |

### Run status

`WebhookRunStatus` is one of: `Pending`, `Running`, `Completed`, `Failed`,
`Timeout`.

---

## Registration management

Base route: `api/webhooks`.

### Create registration

```
POST /api/webhooks/registrations
```

Request body (`CreateWebhookRegistrationRequest`):

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `agentId` | string | yes | Target agent ID. |
| `label` | string | yes | Human-readable label for portal display. |
| `conversationId` | string \| null | no | Pin all messages to this conversation. |
| `defaultResponseMode` | `"async"` \| `"sync"` \| `"callback"` \| null | no | Defaults to `async`. |

Responses:

- `201 Created` — [`WebhookRegistrationResponse`](#webhookregistrationresponse).
  The `secret` field is populated **only** here.
- `400 Bad Request` — `{ "error": "agentId is required." }` or
  `{ "error": "label is required." }`.

Example:

```bash
curl -X POST https://your-host/api/webhooks/registrations \
  -H "Content-Type: application/json" \
  -d '{ "agentId": "my-agent", "label": "Alertmanager bridge", "defaultResponseMode": "async" }'
```

```json
{
  "webhookId": "wh_9f2c...",
  "label": "Alertmanager bridge",
  "agentId": "my-agent",
  "conversationId": null,
  "enabled": true,
  "defaultResponseMode": "async",
  "url": "https://your-host/api/webhooks/my-agent/wh_9f2c...",
  "secret": "whsec_3a1b...64hex...",
  "createdAt": "2026-07-06T12:00:02Z",
  "lastUsedAt": null
}
```

### Get registration

```
GET /api/webhooks/registrations/{webhookId}
```

Responses:

- `200 OK` — [`WebhookRegistrationResponse`](#webhookregistrationresponse) with
  `secret: null`.
- `404 Not Found` — `{ "error": "Webhook registration '{webhookId}' not found." }`.

### List registrations

```
GET /api/webhooks/registrations
GET /api/webhooks/registrations?agentId={agentId}
```

Optional `agentId` query filter. Returns `200 OK` with an array of
[`WebhookRegistrationResponse`](#webhookregistrationresponse); secrets are never
included in list responses.

### Update registration

```
PUT /api/webhooks/registrations/{webhookId}
```

Request body (`UpdateWebhookRegistrationRequest`) — any omitted/null field
preserves its existing value:

| Field | Type | Notes |
|-------|------|-------|
| `label` | string \| null | New label. |
| `enabled` | bool \| null | Enable/disable inbound delivery. |
| `defaultResponseMode` | `"async"` \| `"sync"` \| `"callback"` \| null | New default. |

The `agentId` and `secret` are immutable after creation.

Responses:

- `200 OK` — updated [`WebhookRegistrationResponse`](#webhookregistrationresponse)
  (`secret: null`).
- `404 Not Found` — `{ "error": "Webhook registration '{webhookId}' not found." }`.

### Delete registration

```
DELETE /api/webhooks/registrations/{webhookId}
```

Responses:

- `204 No Content` — deleted. Pending runs complete but no new inbound POSTs are
  accepted.
- `404 Not Found` — `{ "error": "Webhook registration '{webhookId}' not found." }`.

---

## Inbound delivery

```
POST /api/webhooks/{agentId}/{webhookId}
```

External systems POST a message here. Requires a valid HMAC signature header.

### Headers

| Header | Required | Notes |
|--------|----------|-------|
| `Content-Type` | yes | `application/json`. |
| `X-BotNexus-Signature-256` | yes | `sha256=<hex>` — HMAC-SHA256 of the raw body keyed by the secret. |

### Request body (`WebhookInboundRequest`)

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `message` | string | yes | The message to deliver to the agent. |
| `responseMode` | `"async"` \| `"sync"` \| `"callback"` \| null | no | Overrides the registration default. |
| `agentAction` | bool \| null | no | Default `true`. `false` records the message without running the agent. |
| `callbackUrl` | string \| null | conditionally | Required when `responseMode` is `callback`. |

### Responses

**Async / callback / store-only** → `202 Accepted`,
[`WebhookAcceptedResponse`](#webhookacceptedresponse). For async, a
`Location: <pollUrl>` header is also set.

```json
{
  "runId": "whr_51ab...",
  "pollUrl": "https://your-host/api/webhooks/runs/whr_51ab...",
  "conversationId": "conv_77de..."
}
```

**Sync (completed within timeout)** → `200 OK`,
[`WebhookSyncResponse`](#webhooksyncresponse).

```json
{
  "runId": "whr_51ab...",
  "agentResponse": "Acknowledged. Opened incident INC-4821.",
  "conversationId": "conv_77de..."
}
```

**Sync (timed out at 120s)** → downgrades to `202 Accepted` with a `Location`
poll URL; the run status becomes `Timeout`.

### Error responses

| Status | Body |
|--------|------|
| `400 Bad Request` | `{ "error": "Invalid JSON body." }` or `{ "error": "message is required." }` |
| `401 Unauthorized` | `{ "error": "Invalid signature." }` |
| `404 Not Found` | `{ "error": "Webhook '{webhookId}' not found or disabled." }` |
| `503 Service Unavailable` | `{ "error": "Agent run did not complete." }` (sync mode) |

### Signature computation

```
signature = "sha256=" + lowercase_hex( HMAC_SHA256(secret_utf8, raw_body_bytes) )
```

Sign the exact bytes you transmit. See the
[guide's worked example](../guides/webhooks.md#security-hmac-sha256-signing).

### Callback payload

When `responseMode` is `callback`, on completion the gateway POSTs this JSON to the
provided `callbackUrl` (validated against SSRF rules first):

```json
{
  "runId": "whr_51ab...",
  "webhookId": "wh_9f2c...",
  "status": "Completed",
  "agentResponse": "The overnight batch processed 12,400 records...",
  "conversationId": "conv_77de...",
  "completedAt": "2026-07-06T12:03:10Z"
}
```

---

## Runs

### Get run status

```
GET /api/webhooks/runs/{runId}
```

Poll target after a `202`. Responses:

- `200 OK` — [`WebhookRunResponse`](#webhookrunresponse).
- `404 Not Found` — `{ "error": "Webhook run '{runId}' not found." }`.

### List runs for a registration

```
GET /api/webhooks/registrations/{webhookId}/runs
GET /api/webhooks/registrations/{webhookId}/runs?limit=20
```

`limit` defaults to 20 and is clamped to the range 1–100. Returns `200 OK` with an
array of [`WebhookRunResponse`](#webhookrunresponse).

---

## Response schemas

### WebhookRegistrationResponse

| Field | Type | Notes |
|-------|------|-------|
| `webhookId` | string | Stable registration identifier. |
| `label` | string | Human-readable label. |
| `agentId` | string | Target agent ID. |
| `conversationId` | string \| null | Pinned conversation, if any. |
| `enabled` | bool | Whether inbound POSTs are accepted. |
| `defaultResponseMode` | string | `async` \| `sync` \| `callback`. |
| `url` | string | Inbound POST URL. |
| `secret` | string \| null | Plaintext secret — **only** on the create response. |
| `createdAt` | string (ISO-8601) | Creation timestamp. |
| `lastUsedAt` | string \| null | Last inbound POST timestamp. |

### WebhookAcceptedResponse

| Field | Type | Notes |
|-------|------|-------|
| `runId` | string | Run identifier for polling. |
| `pollUrl` | string | URL to GET for run status. |
| `conversationId` | string | Conversation the message routed to. |

### WebhookSyncResponse

| Field | Type | Notes |
|-------|------|-------|
| `runId` | string | Run identifier. |
| `agentResponse` | string \| null | The agent's response text. |
| `conversationId` | string | Conversation the message routed to. |

### WebhookRunResponse

| Field | Type | Notes |
|-------|------|-------|
| `runId` | string | Run identifier. |
| `webhookId` | string | Registration that triggered the run. |
| `status` | string | `Pending` \| `Running` \| `Completed` \| `Failed` \| `Timeout`. |
| `acceptedAt` | string (ISO-8601) | When the inbound POST was accepted. |
| `startedAt` | string \| null | When agent execution started. |
| `completedAt` | string \| null | When agent execution completed. |
| `agentResponse` | string \| null | Response text; populated on `Completed` (null for store-only). |
| `error` | string \| null | Error message; populated on `Failed`. |
| `conversationId` | string \| null | Conversation used for the run. |
| `sessionId` | string \| null | Session in which the agent executed. |

---

## See also

- [Webhooks guide](../guides/webhooks.md)
- [Cron & scheduling](../cron-and-scheduling.md)
