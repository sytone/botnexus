# BotNexus.Gateway.Api

> ASP.NET Core API surface — REST controllers and WebSocket middleware for the BotNexus Gateway.

## Overview

This package provides the public HTTP and WebSocket API for the BotNexus Gateway. All REST endpoints (`/api/*`) and WebSocket connections (`/ws`) are served here. The Blazor WebUI (`BotNexus.WebUI`) is hosted as a separate project. This package contains no orchestration logic — that is in `BotNexus.Gateway` — it only translates between HTTP/WebSocket and the gateway's internal interfaces.

## API Endpoints

### REST API

All REST endpoints are in the `/api/` path and require authentication (if configured).

#### Agents (`/api/agents`)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/agents` | List all registered agents |
| `GET` | `/api/agents/{agentId}` | Get a specific agent descriptor |
| `POST` | `/api/agents` | Register a new agent |
| `PUT` | `/api/agents/{agentId}` | Update an agent descriptor |
| `DELETE` | `/api/agents/{agentId}` | Unregister an agent |
| `GET` | `/api/agents/instances` | List all active agent instances |
| `GET` | `/api/agents/{agentId}/sessions/{sessionId}/status` | Get an agent instance status |
| `POST` | `/api/agents/{agentId}/sessions/{sessionId}/stop` | Stop an agent instance |

#### Chat (`/api/chat`)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/chat` | Send a message to an agent (non-streaming) |
| `POST` | `/api/chat/steer` | Inject a steering message into an active agent run |
| `POST` | `/api/chat/follow-up` | Queue a follow-up message for an active agent session |

#### Channels (`/api/channels`)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/channels` | List registered channel adapters and their capabilities |

#### Extensions (`/api/extensions`)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/extensions` | List loaded runtime extensions and their declared types |

#### Sessions (`/api/sessions`)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/sessions` | List sessions (optionally filtered by `?agentId=...`) |
| `GET` | `/api/sessions/{sessionId}` | Get a specific session |
| `GET` | `/api/sessions/{sessionId}/history` | Get paginated session history (offset/limit) |
| `GET` | `/api/sessions/{sessionId}/metadata` | Get session metadata key-value pairs |
| `PATCH` | `/api/sessions/{sessionId}/metadata` | Merge metadata entries (null removes keys) |
| `DELETE` | `/api/sessions/{sessionId}` | Delete a session |
| `PATCH` | `/api/sessions/{sessionId}/suspend` | Suspend a session |
| `PATCH` | `/api/sessions/{sessionId}/resume` | Resume a suspended session |

### WebSocket

**Endpoint:** `ws://localhost:5005/ws?agent=<agentId>&session=<sessionId>`

The WebSocket connection provides streaming chat access to an agent session. Messages are JSON:

```json
{
  "type": "message",
  "content": "Hello!"
}
```

Responses stream as `GatewayStreamEvent` JSON events:

```json
{
  "type": "content_delta",
  "content": "Hello! I'm"
}
```

**Activity Stream:** `ws://localhost:5005/ws/activity?agent=<agentId>`

Subscribes to real-time activity events (agent started, tool called, streaming ended, etc.).

### Health & Documentation

| Path | Description |
|------|-------------|
| `/health` | Health check (no auth required) — returns `{"status":"ok"}` |
| `/swagger` | Interactive OpenAPI documentation (no auth required) |
| `/swagger/v1/swagger.json` | OpenAPI spec in JSON format |

## Middleware

### Authentication (`GatewayAuthMiddleware`)

Validates all requests using a route-based allowlist. The following paths skip authentication:

- `/health` — Health check endpoint
- `/swagger/**` — OpenAPI documentation
- Blazor WebUI routes — served by the `BotNexus.WebUI` project

All other requests require:

1. **API Key header:** `X-Api-Key: <key>`
2. **Bearer token:** `Authorization: Bearer <token>`

Failures return 401 (unauthenticated) or 403 (unauthorized). The middleware also enforces per-caller agent access restrictions.

### CORS

Development mode allows any origin. Production mode restricts to origins configured in `config.json` (default: `http://localhost:5005`).

### Rate Limiting (`RateLimitingMiddleware`)

Applies per-client request rate limiting to all HTTP requests (except `/health`). Clients are identified by authenticated caller ID or IP address.

- **Default:** 60 requests per 60-second window (configurable via `gateway.rateLimit` in `config.json`)
- **Exceeded:** Returns `429 Too Many Requests` with a `Retry-After` header
- **Skips:** `/health` endpoint

### Correlation ID (`CorrelationIdMiddleware`)

Adds an `X-Correlation-Id` header to every request/response for end-to-end tracing:

- Accepts an existing `X-Correlation-Id` from the request
- Generates a new UUID if none is provided
- Returns the correlation ID in the response header
- Stores it in `HttpContext.Items["CorrelationId"]` for downstream middleware and controllers

## Key Types

### Controllers

| Type | Namespace | Description |
|------|-----------|-------------|
| `AgentsController` | Controllers | Agent registration and lifecycle endpoints |
| `ChannelsController` | Controllers | Channel adapter introspection endpoint |
| `ChatController` | Controllers | Chat and message steering endpoints |
| `ConfigController` | Controllers | Configuration validation endpoint (`/api/config/validate`) |
| `ExtensionsController` | Controllers | Loaded extension introspection endpoint |
| `SessionsController` | Controllers | Session management endpoints |

### Models

| Type | Namespace | Description |
|------|-----------|-------------|
| `ChatRequest` | Controllers | Request to `/api/chat` — agent ID, message, optional session ID |
| `ChatResponse` | Controllers | Response from `/api/chat` — session ID, content, usage |
| `AgentControlRequest` | Controllers | Request to `/api/chat/steer` or `/follow-up` — agent ID, session ID, message |
| `ChannelAdapterResponse` | Controllers | Channel adapter capability and status DTO |
| `ExtensionResponse` | Controllers | Loaded extension metadata DTO |
| `SessionHistoryResponse` | Abstractions.Models | Paginated history response — offset, limit, total count, entries |

### Middleware & Security

| Type | Namespace | Description |
|------|-----------|-------------|
| `GatewayAuthMiddleware` | - | ASP.NET Core middleware for authentication and authorization |
| `RateLimitingMiddleware` | - | Per-client HTTP request rate limiting (429 + Retry-After) |
| `CorrelationIdMiddleware` | - | Adds X-Correlation-Id header for end-to-end request tracing |
| `ActivityWebSocketHandler` | WebSocket | Handles WebSocket connections for activity stream and chat |

## Development

### Running the API

```powershell
# Via dev-loop script
.\scripts\dev-loop.ps1

# Or start directly
.\scripts\start-gateway.ps1
```

The API starts at `http://localhost:5005` by default.

### OpenAPI/Swagger

The API is fully documented with OpenAPI annotations. XML documentation from source code appears in Swagger. To export the spec:

```powershell
.\scripts\export-openapi.ps1
# Saves to docs/api/openapi.json
```

### Testing with curl

```bash
# Health check
curl http://localhost:5005/health

# List agents
curl http://localhost:5005/api/agents

# Send a message (if auth is off)
curl -X POST http://localhost:5005/api/chat \
  -H "Content-Type: application/json" \
  -d '{"agentId":"assistant","message":"Hello"}'
```

## Configuration

API behavior is controlled by `~/.botnexus/config.json`:

- `gateway.listenUrl` — HTTP listen address and port
- `gateway.apiKeys` — API key definitions for authentication
- `gateway.corsAllowedOrigins` — CORS allowed origins in production

See [Configuration Guide](../../docs/configuration.md) for full reference.

## Further Reading

- [BotNexus.Gateway](../BotNexus.Gateway/README.md) — Orchestration runtime
- [BotNexus.Gateway.Abstractions](../BotNexus.Gateway.Abstractions/README.md) — Contract surface
- [API Reference](../../docs/api-reference.md) — Detailed endpoint documentation
- [Development Loop](../../docs/dev-loop.md) — Build and run guide
