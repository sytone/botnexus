# REST API Reference

Reference for the BotNexus Gateway **REST API**. The gateway exposes an
HTTP/JSON surface (the same host that serves the WebUI) for managing agents,
conversations, cron jobs, webhooks, satellites, and portal tools.

> **Scope of this reference.** This is the first slice of the API documentation
> effort ([#219](https://github.com/Sytone/botnexus/issues/219)). It documents a
> small, code-verified subset of controllers. The SignalR hub/event reference and
> the .NET public API reference are tracked as follow-up slices (see
> [Deferred](#deferred) below). Every route, verb, and status code on the linked
> pages was read directly from the controller source under
> `src/gateway/BotNexus.Gateway.Api/Controllers/`.

---

## Base URL

By default the gateway listens on:

```
http://localhost:5005
```

All REST endpoints are served under the `/api` path prefix (for example
`http://localhost:5005/api/conversations`). The listen address and port are
configurable via `config.json` (`gateway.listenUrl`).

The `GET /health` endpoint is served at the root (`http://localhost:5005/health`)
and is unauthenticated.

---

## Authentication

Authentication is enforced by `GatewayAuthMiddleware`, which runs in front of every
request and delegates credential validation to `ApiKeyGatewayAuthHandler`.

### API key

When one or more API keys are configured (under `gateway.apiKeys`, or the legacy
top-level `apiKey`), every `/api/*` request must present the key using **either**
header:

```http
X-Api-Key: <key>
```

```http
Authorization: Bearer <key>
```

A request with a missing or unrecognised key is rejected with `401 Unauthorized`
and a JSON body of the form `{ "error": "...", "message": "..." }`. A caller whose
identity is not authorized for the requested agent is rejected with `403 Forbidden`.

### Development mode (no key configured)

When **no** API key is configured, the handler runs in development mode and grants
a full admin identity to every caller — no `X-Api-Key` or `Authorization` header is
required. (An optional, off-by-default feature flag,
`GatewayDevOriginEnforcement`, can additionally require browser requests to carry an
allow-listed `Origin` header; non-browser callers such as `curl` and the CLI are
unaffected.)

### Paths that bypass the API-key check

`GatewayAuthMiddleware` skips the API-key check for:

| Path | Reason |
|------|--------|
| `GET /health` | Liveness probe, intentionally unauthenticated. |
| `/swagger/*` | Swagger UI. |
| `/api/federation/cross-world/*` | Cross-world federation (own auth). |
| `POST /api/webhooks/{agentId}/{webhookId}` | **HMAC webhook exception** — see below. |
| Static WebUI files under the web root | Served directly. |

### The HMAC webhook exception

Inbound webhook delivery (`POST /api/webhooks/{agentId}/{webhookId}`) does **not**
use the gateway API key. Instead each request is authenticated per-registration with
an HMAC-SHA256 signature. The caller computes:

```
X-BotNexus-Signature-256: sha256=HMAC_SHA256(rawRequestBody, registrationSecret)
```

The gateway recomputes the signature from the raw body and the registration's stored
secret and compares them in constant time (`WebhookSecretHelper.VerifySignature`). A
missing or invalid signature yields `401 Unauthorized`. The registration **management**
endpoints (`/api/webhooks/registrations/*` and `/api/webhooks/runs/*`) are *not* part of
this exception — they go through the normal API-key check.

---

## Response conventions

- **Content type.** Request and response bodies are JSON unless noted otherwise.
- **Success codes.** `200 OK` for reads and updates, `201 Created` (with a `Location`
  header) for resource creation, `202 Accepted` for asynchronous webhook delivery,
  and `204 No Content` for deletes.
- **Not found.** A missing resource returns `404 Not Found`, sometimes with a JSON
  body `{ "error": "<resource> '<id>' not found." }`.
- **Validation errors.** Invalid input returns `400 Bad Request` with a JSON body
  `{ "error": "<message>" }`.
- **Sparse fieldsets.** Many `GET` endpoints accept an optional `?fields=` query
  parameter that projects each returned object down to the requested top-level fields
  (comma-separated, case-insensitive). Omitting it returns the full object.

---

## Controller reference

| Controller | Base route | Reference |
|------------|------------|-----------|
| Conversations | `api/conversations` | [conversations.md](conversations.md) |
| Cron | `api/cron` | [cron.md](cron.md) |
| Satellites | `api/satellites` | [satellites.md](satellites.md) |
| Webhooks (management + inbound delivery) | `api/webhooks` | [webhooks.md](webhooks.md) |
| Tools | `api/tools` | [tools.md](tools.md) |

A machine-readable OpenAPI 3.0 description of the full surface is also available at
[openapi.json](openapi.json).

> The gateway hosts additional controllers (agents, sessions, channels, models,
> providers, memory, stats, and more) that are not yet documented as hand-written
> pages in this slice; they do appear in `openapi.json`.

---

## Deferred

The following are explicitly **out of scope** for this slice and tracked as
follow-up work under [#219](https://github.com/Sytone/botnexus/issues/219):

- **SignalR hub & event reference** — the real-time hub methods and server→client
  events used by the WebUI.
- **.NET public API reference** — generated from XML doc comments (e.g. DocFX).
- **Remaining REST controllers** — agents, sessions, channels, models, providers,
  memory, reports, diagnostics, stats, sub-agents, and the rest of the
  `Controllers/` set.
