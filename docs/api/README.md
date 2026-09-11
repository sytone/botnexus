# REST API Reference

Reference for the BotNexus Gateway **REST API**. The gateway exposes an
HTTP/JSON surface (the same host that serves the WebUI) for managing agents,
conversations, cron jobs, webhooks, satellites, and portal tools.

> **Scope of this reference.** This is the first slice of the API documentation
> effort ([#219](https://github.com/Sytone/botnexus/issues/219)). It documents a
> small, code-verified subset of controllers, plus a full
> [SignalR hub reference](signalr.md). The .NET public API reference is tracked as
> a follow-up slice (see [Deferred](#deferred) below). Every route, verb, and status
> code on the linked pages was read directly from the controller source under
> `src/gateway/BotNexus.Gateway.Api/Controllers/`.

---

## Base URL

By default the gateway listens on:

```
http://localhost:5005
```

The management API generally uses the `/api` prefix (for example
`http://localhost:5005/api/conversations`). Channel extensions also register HTTP
routes outside it, including `/telegram/webhook/{botName}`, `/agent365/messages`
(the configurable default) and `/test-channel`. See the owning channel pages for
those contracts. The listen address and port are configurable through
`gateway.listenUrl`.

The `GET /health` endpoint is served at the root (`http://localhost:5005/health`)
and is unauthenticated.

---

## Authentication

Authentication is enforced by `GatewayAuthMiddleware`, which runs in front of every
request and delegates credential validation to `ApiKeyGatewayAuthHandler`.

### API key

When one or more API keys are configured (under `gateway.apiKeys`, or the legacy
top-level `apiKey`), requests that reach this middleware's credential check must
present the key using either header below. The bypasses listed later have separate
contracts; this is not a universal API-key requirement for every `/api/*` route:

```http
X-Api-Key: <key>
```

```http
Authorization: Bearer <key>
```

A nonblank `X-Api-Key` takes precedence over a Bearer value. Query-string API keys
are not read by this handler. See [Authentication](../api-reference.md#authentication)
for the detailed contract.

A request with a missing or unrecognised key is rejected with `401 Unauthorized`
and a JSON body of the form `{ "error": "...", "message": "..." }`. A caller whose
identity is not authorized for the requested agent is rejected with `403 Forbidden`.

### Development mode (no key configured)

When no API key is configured, the handler can grant a full admin identity without
an `X-Api-Key` or `Authorization` header. If the optional, off-by-default
`GatewayDevOriginEnforcement` flag is enabled, a supplied `Origin` must be on the
allow-list; a rejected origin causes authentication failure before admin identity
creation. A missing or blank Origin is allowed. This is a header-based check, not
browser detection: a CLI request that supplies a rejected Origin is rejected too.

### Paths that bypass the API-key check

`GatewayAuthMiddleware` skips the API-key check for:

| Path | Reason |
|------|--------|
| Exact `/health` path | Middleware bypass; the actual endpoint determines accepted verbs. |
| `/swagger` path segment and descendants | Swagger UI middleware bypass. |
| `/api/federation/cross-world` path segment and descendants | Cross-world federation retains its own authorization. |
| POST under `/api/webhooks`, except `/registrations` and `/runs` path segments | Inbound delivery uses the HMAC contract below; bypass alone does not establish that a route exists. |
| Existing non-directory web-root files outside `/api` | Only GET/HEAD requests bypass this API-key check. |

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
- **Sparse fieldsets.** Eligible MVC controller `GET` responses accept `?fields=`
  for top-level projection (comma-separated, case-insensitive). The registered result
  filter requires a successful, non-null `ObjectResult` that is not `ProblemDetails`;
  unsupported shapes pass through. Minimal-API endpoints and other result kinds do
  not acquire this behavior merely by returning JSON. See
  [sparse fieldsets](../api-reference.md#sparse-fieldsets-fields).

---

## Controller reference

| Controller | Base route | Reference |
|------------|------------|-----------|
| Agents | `api/agents` | [agents.md](agents.md) |
| Conversations | `api/conversations` | [conversations.md](conversations.md) |
| Cron | `api/cron` | [cron.md](cron.md) |
| Exchanges (agent-to-agent budget diagnostics) | `api/exchanges` | [exchanges.md](exchanges.md) |
| Satellites | `api/satellites` | [satellites.md](satellites.md) |
| Sessions + Sub-agents | `api/sessions`, `api/subagents` | [sessions.md](sessions.md) |
| Webhooks (management + inbound delivery) | `api/webhooks` | [webhooks.md](webhooks.md) |
| Tools | `api/tools` | [tools.md](tools.md) |

A checked-in [OpenAPI 3.0 snapshot](openapi.json) is available, but it is **partial**:
its selected operations do not enumerate the current controller and extension route
sets. Do not use absence from that file as evidence that a route does not exist.

> For additional channels, models, providers, memory and diagnostic surfaces, consult
> the [broader API reference](../api-reference.md) and the owning feature/channel pages.
> Verify current contracts against source; the snapshot is not a completeness gate.

---

## Routes registered outside the controller set

**The controller reference above is not a complete inventory of `/api/*`.** Some route groups are
registered through the `IEndpointContributor` seam - `MapGroup`/`MapGet`/`MapPut` minimal-API
registrations contributed by an extension or a gateway sub-assembly - rather than as a controller in
`src/gateway/BotNexus.Gateway.Api/Controllers/`. This is deliberate: a gateway project may not
reference an extension project (enforced by `GatewayProjectDependencyBoundaryTests`), so a surface
that needs extension types cannot live in a controller.

The practical consequence is that every controller-derived audit - including the table above - is
structurally blind to these routes. The groups below still pass through the gateway
API-key middleware; minimal-API registration is not an authentication exemption.
Apply the no-key Origin qualification and endpoint-specific rules above.

| Route group | Contributor | Reference |
|-------------|-------------|-----------|
| `api/plugins` | `PluginsEndpointContributor` | [Plugins Management](../api-reference.md#plugins-management) |
| `api/skills` | `SkillsEndpointContributor` | [Skills Management](../api-reference.md#skills-management) |
| `api/telemetry` | `TelemetryEndpointContributor` | [Telemetry Metrics](../api-reference.md#telemetry-metrics) |

---

## Deferred

The following are explicitly **out of scope** for this slice and tracked as
follow-up work under [#219](https://github.com/Sytone/botnexus/issues/219):

- **.NET public API reference** — generated from XML doc comments (e.g. DocFX).
- **Remaining REST controllers** - channels, models, providers, memory, reports,
  diagnostics, stats, and the rest of the `Controllers/` set.
