# BotNexus Gateway

The **Gateway** is the central orchestrator of the BotNexus platform. It manages multi-agent execution, session persistence, real-time WebSocket streaming, and extensibility via pluggable strategies and channel adapters.

## Architecture

The Gateway is composed of **5 core projects**:

| Project | Purpose |
|---------|---------|
| **BotNexus.Gateway.Abstractions** | Interface contracts (IAgentConfigurationSource, IIsolationStrategy, IChannelAdapter, ISessionStore) |
| **BotNexus.Gateway** | Main host — message routing, agent supervision, hot reload, channel management |
| **BotNexus.Gateway.Api** | REST API (agents, sessions, chat, config) + WebSocket handler + SignalR hub |
| **BotNexus.Gateway.Sessions** | Session persistence implementations (InMemory, FileSessionStore) |
| **BotNexus.Cli** | Command-line interface for Gateway management and interaction |

### Message Flow

```
External Channel (Slack, Discord, etc.)
         ↓
    IChannelAdapter
         ↓
  ┌─────────────────────────┐
  │   BotNexus.Gateway      │
  │  [Message Router]       │  ← Hot reload watches config
  └──────────┬──────────────┘
             ↓
    IAgentConfigurationSource
    (PlatformConfigLoader)
             ↓
   ┌─────────────────────────┐
   │  IAgentSupervisor       │
   │ [Agent Registry & Pool] │
   └──────────┬──────────────┘
              ↓
   IIsolationStrategy
   (in-process/sandbox/container)
              ↓
   Agent executes with LLM provider
              ↓
   ISessionStore
   (Persists conversation history)
```

## Extension Points

The Gateway is **fully extensible** through pluggable interfaces:

### 1. IIsolationStrategy
Controls how agent code is executed and isolated.

```csharp
public interface IIsolationStrategy
{
    string Name { get; }
    Task<IAgentHandle> CreateAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext context,
        CancellationToken cancellationToken = default);
}
```

**Built-in strategies:**
- **in-process** — Runs agent directly in Gateway process (default, fastest)
- **sandbox** — Restricted AppDomain or process with limited permissions (Phase 2)
- **container** — Docker container isolation (Phase 2)
- **remote** — HTTP/gRPC delegation to remote service (Phase 2)

**To add a custom strategy:**
1. Implement `IIsolationStrategy`
2. Register via dependency injection in `Program.cs`
3. Set `isolationStrategy: "your-strategy"` in agent config

### 2. IChannelAdapter
Connects external communication platforms (Slack, Discord, Telegram, etc.) to the Gateway.

```csharp
public interface IChannelAdapter
{
    string ChannelType { get; }      // "slack", "discord", "telegram"
    string DisplayName { get; }
    bool SupportsStreaming { get; }

    // Capability flags — declare what your channel supports
    bool SupportsSteering { get; }          // Can inject mid-run steering messages
    bool SupportsFollowUp { get; }          // Can queue follow-up messages
    bool SupportsThinkingDisplay { get; }   // Can render thinking/reasoning deltas
    bool SupportsToolDisplay { get; }       // Can render tool call start/end events

    bool IsRunning { get; }
    
    Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);
    Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default);
}
```

#### Channel Capability Flags

Channels declare which interaction features they support. The Gateway uses these flags to decide what events to send:

| Flag | Purpose | Example |
|------|---------|---------|
| `SupportsStreaming` | Real-time token streaming | WebSocket: ✅, REST: ❌ |
| `SupportsSteering` | Mid-run steering injection | WebUI: ✅, Telegram: ❌ |
| `SupportsFollowUp` | Queued follow-up messages | WebUI: ✅, Slack: ✅ |
| `SupportsThinkingDisplay` | Display agent reasoning | WebUI: ✅, Discord: ❌ |
| `SupportsToolDisplay` | Display tool call events | WebUI: ✅, SMS: ❌ |

If a channel sets `SupportsThinkingDisplay = false`, the Gateway suppresses `thinking_delta` events for that channel. The same applies to tool events and steering support.

**To add a custom channel:**
1. Implement `IChannelAdapter` with your protocol (e.g., Slack Events API)
2. Register in `Program.cs` and add to dependency injection
3. Enable in `config.json`:
   ```json
   {
     "channels": {
       "my-platform": {
         "type": "my-platform",
         "enabled": true,
         "settings": {
           "token": "your-secret",
           "workspace": "your-workspace"
         }
       }
     }
   }
   ```

### 3. ISessionStore
Persists conversation history. Swap implementations for different backends.

```csharp
public interface ISessionStore
{
    Task<GatewaySession?> GetAsync(string sessionId, CancellationToken ct = default);
    Task<GatewaySession> GetOrCreateAsync(string sessionId, string agentId, CancellationToken ct = default);
    Task SaveAsync(GatewaySession session, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<GatewaySession>> ListAsync(string? agentId = null, CancellationToken ct = default);
}
```

**Built-in implementations:**
- **InMemorySessionStore** — Fast, non-durable (development/testing)
- **FileSessionStore** — JSONL files with `.meta.json` sidecar

**To use a database backend:**
1. Implement `ISessionStore` (e.g., `SqlSessionStore`)
2. Register in `Program.cs`:
   ```csharp
   builder.Services.AddSingleton<ISessionStore, SqlSessionStore>();
   ```

### 4. IAgentConfigurationSource
Loads and watches agent definitions from external sources.

```csharp
public interface IAgentConfigurationSource
{
    Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken ct = default);
    IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged);
}
```

**Built-in implementation:**
- **PlatformConfigLoader** — Loads from `~/.botnexus/config.json` with file watching for hot reload

**To add a dynamic config source (e.g., Azure AppConfig):**
1. Implement `IAgentConfigurationSource`
2. Register in `Program.cs`
3. Implement `Watch()` to notify on config changes

## Configuration

### PlatformConfig (~/.botnexus/config.json)

The Gateway reads from `~/.botnexus/config.json`. Configure via nested `gateway` section:

```json
{
  "version": 1,
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "assistant",
    "sessionsDirectory": "workspace/sessions",
    "logLevel": "Information",
    "apiKeys": {
      "user-key-1": {
        "apiKey": "sk-...",
        "tenantId": "tenant-1",
        "displayName": "User #1 API Key",
        "allowedAgents": ["assistant"],
        "permissions": ["chat:send", "sessions:read"],
        "isAdmin": false
      }
    }
  },
  "agents": {
    "assistant": {
      "provider": "copilot",
      "model": "gpt-4.1",
      "systemPromptFile": "prompts/assistant.txt",
      "isolationStrategy": "in-process",
      "enabled": true
    }
  },
  "providers": {
    "copilot": {
      "apiKey": "copilot",
      "baseUrl": "https://api.githubcopilot.com",
      "defaultModel": "gpt-4.1"
    }
  },
  "channels": {
    "discord": {
      "type": "discord",
      "enabled": true,
      "settings": {
        "token": "discord-bot-token"
      }
    }
  }
}
```

### Environment Variable Overrides

Use `BotNexus__Path__To__Property` format:

```bash
export BotNexus__Gateway__ListenUrl="http://0.0.0.0:8080"
export BotNexus__Gateway__DefaultAgentId="researcher"
export BotNexus__LogLevel="Debug"
```

### Legacy Root-Level Form (Deprecated)

For backward compatibility, Gateway settings can also be at the root:

```json
{
  "listenUrl": "http://localhost:5005",
  "defaultAgentId": "assistant",
  "sessionsDirectory": "workspace/sessions"
}
```

The Gateway prefers the nested `gateway` section if both exist.

## Authentication

The Gateway has two authentication layers: **provider authentication** (how the Gateway authenticates with upstream LLM APIs) and **gateway endpoint protection** (how callers authenticate with the Gateway itself).

### Provider Authentication

Controls how the Gateway authenticates with upstream LLM providers (Copilot, OpenAI, etc.).

#### Resolution Order

1. **auth.json** — `~/.botnexus/auth.json` (OAuth tokens, enterprise endpoints).  
   For local repo-driven development, `./.botnexus-agent/auth.json` is also accepted as a fallback.
2. **Environment Variables** — `BOTNEXUS_COPILOT_APIKEY`, `BOTNEXUS_OPENAI_APIKEY`, etc.
3. **Platform Config** — `config.json` provider section (`apiKey` field)

#### Example: Copilot OAuth

Store in `~/.botnexus/auth.json`:
```json
{
  "github-copilot": {
    "type": "oauth",
    "access": "ghu_...",
    "refresh": "ghr_...",
    "expires": 1234567890000,
    "endpoint": "https://api.githubcopilot.com"
  }
}
```

Then in `config.json`:
```json
{
  "providers": {
    "copilot": {
      "apiKey": "auth:github-copilot",  // Reference to auth.json entry
      "baseUrl": "https://api.githubcopilot.com"
    }
  }
}
```

The `GatewayAuthManager` auto-refreshes expired OAuth tokens when needed.

### Gateway Endpoint Protection (Auth Middleware)

The `GatewayAuthMiddleware` protects the Gateway's HTTP and WebSocket endpoints. It delegates validation to `ApiKeyGatewayAuthHandler`.

#### How It Works

1. Every incoming request passes through `GatewayAuthMiddleware`
2. The middleware checks for an API key via `X-Api-Key` header or `Authorization: Bearer {key}`
3. Valid keys map to a caller identity with tenant ID, allowed agents, and permissions
4. Invalid or missing keys receive a `401 Unauthorized` response

#### Excluded Endpoints

These paths bypass authentication entirely:

| Path | Reason |
|------|--------|
| `/health` | Health checks for load balancers and monitoring |
| `/swagger` | API documentation browser |

#### Development Mode

When **no API keys are configured** in `gateway.apiKeys`, the middleware operates in development mode:

- All requests are allowed without authentication
- Every caller receives an admin-level identity
- No `X-Api-Key` or `Authorization` header is required

This lets you develop locally without configuring keys. Add at least one key to activate enforcement.

#### Multi-Tenant API Keys

Configure per-caller API keys with granular permissions:

```json
{
  "gateway": {
    "apiKeys": {
      "key-1": {
        "apiKey": "sk-...",
        "tenantId": "tenant-1",
        "displayName": "User #1 API Key",
        "allowedAgents": ["assistant"],
        "permissions": ["chat:send", "sessions:read"],
        "isAdmin": false
      }
    }
  }
}
```

| Field | Purpose |
|-------|---------|
| `apiKey` | The secret key callers provide |
| `tenantId` | Logical grouping for multi-tenant deployments |
| `displayName` | Human-readable label for logs |
| `allowedAgents` | Restrict which agents this key can access |
| `permissions` | Granular operation scopes |
| `isAdmin` | Full access when `true` |

## Session Lifecycle

Sessions track conversation state between callers and agents. Each session has a status that progresses through a defined lifecycle.

### Session Status

| Status | Meaning |
|--------|---------|
| `Active` | Session is in use and accepting messages |
| `Suspended` | Temporarily paused (e.g., user idle) |
| `Expired` | TTL exceeded — no longer accepting new messages |
| `Closed` | Explicitly ended by user or system |

### Session Cleanup Service

The `SessionCleanupService` runs as a background hosted service. It periodically scans sessions and transitions expired ones.

**Default behavior:**

| Setting | Default | Description |
|---------|---------|-------------|
| `CheckInterval` | 5 minutes | How often the cleanup service runs |
| `SessionTtl` | 24 hours | Time-to-live for active sessions |
| `ClosedSessionRetention` | `null` (keep forever) | Optional: auto-delete closed sessions after this period |

Configure via the `gateway` section in `config.json`:

```json
{
  "gateway": {
    "sessionCleanup": {
      "checkInterval": "00:05:00",
      "sessionTtl": "1.00:00:00",
      "closedSessionRetention": "7.00:00:00"
    }
  }
}
```

### Session Locking (WebSocket)

Each session allows **one active WebSocket connection** at a time. When a second WebSocket attempts to connect to the same session:

1. The duplicate connection is accepted briefly
2. Immediately closed with status code **4409** and message `"Session already has an active connection"`
3. The original connection continues unaffected

This prevents race conditions from multiple browser tabs or clients writing to the same session simultaneously.

### MaxConcurrentSessions

Each agent can limit how many sessions run concurrently. Set `maxConcurrentSessions` in the agent config:

```json
{
  "agents": {
    "assistant": {
      "maxConcurrentSessions": 10
    }
  }
}
```

When the limit is reached:
- REST API returns **HTTP 429 Too Many Requests**
- Error message: `"Agent '{agentId}' has reached MaxConcurrentSessions ({limit})."`

Set to `0` (default) for unlimited concurrent sessions.

## Agent Workspace

Each agent gets a persistent workspace directory at `~/.botnexus/agents/{name}/`. The workspace is created automatically the first time an agent is referenced.

### Directory Structure

```
~/.botnexus/agents/{agent-name}/
├── SOUL.md           # Core personality and values
├── IDENTITY.md       # Role, style, and behavioral constraints
├── USER.md           # User-specific preferences
└── MEMORY.md         # Long-term distilled knowledge
```

### How Context Is Built

When an agent starts a conversation, the Gateway reads these workspace files and injects them into the system prompt:

1. **SOUL.md** — Loaded first. Defines who the agent *is* at its core.
2. **IDENTITY.md** — Role-specific instructions, interaction style, constraints.
3. **USER.md** — Preferences about the user (timezone, communication style, etc.).
4. **MEMORY.md** — Accumulated knowledge from past conversations.

Files are created empty on first use. Edit them to customize agent behavior — changes take effect on the **next conversation** without requiring a Gateway restart.

### Isolation Strategy Validation

When an agent is created or loaded, the Gateway validates that its `isolationStrategy` matches a registered strategy. If it doesn't, the agent fails fast with a descriptive error:

```
Isolation strategy 'kubernetes' is not registered for agent 'assistant'.
Available strategies: container, in-process, remote, sandbox.
```

This prevents silent misconfiguration.

## Configuration Hot Reload

The Gateway watches `~/.botnexus/config.json` for changes and automatically reloads configuration without restarting.

### How It Works

1. `PlatformConfigLoader` sets up a `FileSystemWatcher` on `config.json`
2. When a change is detected, a **500ms debounce timer** starts
3. After 500ms of no further changes, the config is reloaded
4. Updated agent definitions, provider settings, and channel configurations take effect immediately

The debounce prevents rapid-fire reloads when editors write files in multiple steps (e.g., write temp file → rename).

> **Note:** Agent configuration files (`FileAgentConfigurationSource`) use a separate watcher with a 250ms debounce.

### What Reloads Automatically

- Agent definitions (new agents, updated models, disabled agents)
- Provider configuration (API keys, base URLs, model defaults)
- Channel settings (enabled/disabled, connection parameters)
- Gateway settings (API keys, default agent)

### What Requires a Restart

- Changes to `listenUrl` (port binding)
- New isolation strategy registrations
- Extension DLL additions

## OpenAPI / Swagger

The Gateway exposes an interactive API documentation browser at `/swagger`.

### Accessing Swagger

Start the Gateway and navigate to:

```
http://localhost:5005/swagger
```

The Swagger UI lets you:
- Browse all REST API endpoints
- View request/response schemas
- Send test requests directly from the browser
- See XML documentation comments from source code

### Configuration

Swagger is enabled automatically. The API title and version are pulled from the assembly metadata:

```
Title:   BotNexus Gateway
Version: {assembly version}
```

XML documentation comments are included when the `BotNexus.Gateway.Api.xml` doc file is present (generated during build).

### Exported OpenAPI Specification

A version-controlled copy of the spec lives at [`docs/api/openapi.json`](../../docs/api/openapi.json). To regenerate it after API changes:

```powershell
./scripts/export-openapi.ps1
```

The script builds the Gateway, starts it on a temporary port, fetches `/swagger/v1/swagger.json`, and saves to `docs/api/openapi.json`. Use `-SkipBuild` if already built, or `-Port 15200` to change the temp port.

## API Endpoints

### Health
- **GET `/health`** — Returns `{ "status": "ok" }`

### Agents
- **GET `/api/agents`** — List all registered agents
  ```json
  [
    {
      "agentId": "assistant",
      "provider": "copilot",
      "model": "gpt-4.1"
    }
  ]
  ```
- **GET `/api/agents/{agentId}`** — Get agent details
- **POST `/api/agents`** — Register a new agent
- **PUT `/api/agents/{agentId}`** — Update an agent (returns 400 on ID mismatch)
- **DELETE `/api/agents/{agentId}`** — Unregister an agent
- **GET `/api/agents/instances`** — List active agent instances
- **GET `/api/agents/{agentId}/sessions/{sessionId}/status`** — Check instance status
- **POST `/api/agents/{agentId}/sessions/{sessionId}/stop`** — Stop an instance

### Chat (REST, non-streaming)
- **POST `/api/chat`**
  ```json
  {
    "agentId": "assistant",
    "message": "What is the weather?",
    "sessionId": "optional-session-id"
  }
  ```
  Response:
  ```json
  {
    "sessionId": "...",
    "content": "The weather...",
    "usage": {
      "inputTokens": 50,
      "outputTokens": 100
    }
  }
  ```
- **POST `/api/chat/steer`** — Inject steering message into active agent run
- **POST `/api/chat/follow-up`** — Queue follow-up for next run

### Sessions
- **GET `/api/sessions`** — List all sessions
  - Query param: `?agentId=assistant` (optional filter)
- **GET `/api/sessions/{sessionId}`** — Get session history
  ```json
  {
    "sessionId": "...",
    "agentId": "assistant",
    "entries": [
      { "role": "user", "content": "..." },
      { "role": "assistant", "content": "..." }
    ]
  }
  ```
- **GET `/api/sessions/{sessionId}/history`** — Paginated history (`?offset=0&limit=50`)
- **PATCH `/api/sessions/{sessionId}/suspend`** — Suspend an active session
- **PATCH `/api/sessions/{sessionId}/resume`** — Resume a suspended session
- **DELETE `/api/sessions/{sessionId}`** — Delete a session

### WebSocket (Real-time Streaming)
- **GET/WebSocket `/ws`**
  - Query params: `?agent={agentId}&session={sessionId}`
  - See [WebSocket Protocol](#websocket-protocol) section in root README

## WebSocket Protocol

### Connection
```
ws://localhost:5005/ws?agent=assistant&session={sessionId}
```

If `session` is omitted, a new session ID is auto-generated.

### Client → Server Messages

**Send a message:**
```json
{ "type": "message", "content": "What is 2+2?" }
```

**Abort active execution:**
```json
{ "type": "abort" }
```

**Inject steering message into active run:**
```json
{ "type": "steer", "content": "Focus on the main point." }
```

**Queue a follow-up for the next run:**
```json
{ "type": "follow_up", "content": "And what about 3+3?" }
```

**Keepalive ping:**
```json
{ "type": "ping" }
```

### Server → Client Messages

**Connection established:**
```json
{ "type": "connected", "connectionId": "...", "sessionId": "...", "sequenceId": 1 }
```

**Agent started processing:**
```json
{ "type": "message_start", "messageId": "uuid-..." }
```

**Thinking delta (streaming agent reasoning):**
```json
{ "type": "thinking_delta", "delta": "Let me think about...", "messageId": "uuid-..." }
```

**Content delta (streaming response text):**
```json
{ "type": "content_delta", "delta": "2+2 is", "messageId": "uuid-..." }
```

**Tool execution started:**
```json
{
  "type": "tool_start",
  "toolCallId": "call_...",
  "toolName": "calculate",
  "messageId": "uuid-..."
}
```

**Tool result received:**
```json
{
  "type": "tool_end",
  "toolCallId": "call_...",
  "toolName": "calculate",
  "toolResult": "4",
  "toolIsError": false,
  "messageId": "uuid-..."
}
```

**Agent completed:**
```json
{
  "type": "message_end",
  "messageId": "uuid-...",
  "usage": {
    "inputTokens": 50,
    "outputTokens": 100
  }
}
```

**Error occurred:**
```json
{
  "type": "error",
  "message": "Agent not found",
  "code": "NOT_FOUND"
}
```

**Keepalive pong:**
```json
{ "type": "pong" }
```

## Development Quick Start

### Prerequisites
- .NET 10 SDK
- PowerShell (Windows) or bash (Linux/macOS)

### Build the Gateway
```bash
dotnet build src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj
```

### Run the Dev Server
```bash
# Option 1: Full dev loop (build + test + run)
.\scripts\dev-loop.ps1

# Option 2: Dev loop with watch mode (auto-rebuild on changes)
.\scripts\dev-loop.ps1 -Watch

# Option 3: Start gateway only (skips tests)
.\scripts\start-gateway.ps1

# Option 4: Start on a custom port
.\scripts\start-gateway.ps1 -Port 8080

# Option 5: Direct dotnet
dotnet run --project src/gateway/BotNexus.Gateway.Api
```

The Gateway starts on `http://localhost:5005` by default.

### Set Up Authentication

In development mode (no API keys configured), all endpoints are open. To enable auth:

1. Add API keys to `~/.botnexus/config.json`:
   ```json
   {
     "gateway": {
       "apiKeys": {
         "dev-key": {
           "apiKey": "sk-dev-secret",
           "isAdmin": true,
           "displayName": "Dev API Key"
         }
       }
     }
   }
   ```

2. Include the key in requests:
   ```bash
   curl -H "X-Api-Key: sk-dev-secret" http://localhost:5005/api/agents
   ```

### Create a Test Agent
Edit `~/.botnexus/config.json`:
```json
{
  "gateway": {
    "listenUrl": "http://localhost:5005",
    "defaultAgentId": "test-agent"
  },
  "agents": {
    "test-agent": {
      "provider": "copilot",
      "model": "gpt-4.1",
      "isolationStrategy": "in-process",
      "enabled": true
    }
  },
  "providers": {
    "copilot": {
      "apiKey": "your-api-key",
      "baseUrl": "https://api.githubcopilot.com"
    }
  }
}
```

### Test via REST
```bash
# Send a message
curl -X POST http://localhost:5005/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "agentId": "test-agent",
    "message": "Hello, what is your name?"
  }'
```

### Test via WebSocket
```bash
# Use websocat, wscat, or any WebSocket client
wscat -c "ws://localhost:5005/ws?agent=test-agent&session=test-session-1"
# Type: { "type": "message", "content": "Hello!" }
```

### Explore the API

- **Blazor WebUI:** `http://localhost:5005` — Chat and configuration interface
- **Swagger:** `http://localhost:5005/swagger` — Interactive API docs
- **Health:** `http://localhost:5005/health` — Status check (no auth required)

### Run Tests
```bash
dotnet test tests/BotNexus.Gateway.Api.Tests/BotNexus.Gateway.Api.Tests.csproj
```

## File Structure

```
src/gateway/
├── BotNexus.Gateway.Abstractions/
│   ├── Agents/
│   │   ├── IAgentConfigurationSource.cs
│   │   ├── IAgentRegistry.cs
│   │   ├── IAgentSupervisor.cs
│   │   ├── AgentConcurrencyLimitExceededException.cs
│   │   └── ...
│   ├── Channels/
│   │   ├── IChannelAdapter.cs         # Includes capability flags
│   │   └── IChannelManager.cs
│   ├── Isolation/
│   │   └── IIsolationStrategy.cs
│   ├── Sessions/
│   │   └── ISessionStore.cs
│   ├── Models/
│   │   ├── AgentDescriptor.cs
│   │   ├── GatewaySession.cs
│   │   ├── SessionStatus.cs           # Active/Suspended/Expired/Closed
│   │   └── ...
│   └── ...
├── BotNexus.Gateway/
│   ├── Configuration/
│   │   ├── PlatformConfig.cs
│   │   ├── PlatformConfigLoader.cs    # Hot reload with 500ms debounce
│   │   ├── GatewayAuthManager.cs
│   │   ├── BotNexusHome.cs            # Home dir + agent workspace scaffolding
│   │   ├── SessionCleanupOptions.cs
│   │   └── ...
│   ├── Agents/
│   │   ├── DefaultAgentSupervisor.cs  # MaxConcurrentSessions enforcement
│   │   ├── AgentRegistry.cs
│   │   └── AgentDescriptorValidator.cs # Isolation strategy validation
│   ├── Channels/
│   │   └── ChannelManager.cs
│   ├── Security/
│   │   └── ApiKeyGatewayAuthHandler.cs
│   ├── SessionCleanupService.cs       # Background TTL cleanup
│   └── ...
├── BotNexus.Gateway.Api/
│   ├── Program.cs                     # Swagger/OpenAPI setup
│   ├── GatewayAuthMiddleware.cs       # Auth pipeline middleware
│   ├── RateLimitingMiddleware.cs      # Per-client rate limiting (429)
│   ├── CorrelationIdMiddleware.cs     # X-Correlation-Id tracing
│   ├── Controllers/
│   │   ├── ChatController.cs          # 429 handling for concurrency limits
│   │   ├── AgentsController.cs
│   │   ├── SessionsController.cs
│   │   └── ConfigController.cs
│   └── WebSocket/
│       └── GatewayHub.cs              # SignalR hub + session groups
├── BotNexus.Gateway.Sessions/
│   ├── InMemorySessionStore.cs
│   ├── FileSessionStore.cs
│   └── ...
└── README.md (this file)
```

## Troubleshooting

### Gateway won't start
1. Check `~/.botnexus/config.json` is valid JSON
2. Verify providers have valid credentials (check `~/.botnexus/auth.json`)
3. Check the port is not in use: `netstat -an | grep 5005`
4. Check for isolation strategy errors — an unknown strategy causes a fail-fast exit

### Agent not responding
1. Confirm agent is registered: `curl http://localhost:5005/api/agents`
2. Check provider credentials: `curl http://localhost:5005/api/config/providers`
3. Verify LLM provider is accessible (check firewall, VPN, etc.)

### 401 Unauthorized on API calls
1. Check if API keys are configured in `gateway.apiKeys` — if yes, include `X-Api-Key` header
2. In dev mode (no keys configured), all requests should pass — verify your config
3. Endpoints `/health`, `/swagger`, and the Blazor WebUI are always exempt

### 429 Too Many Requests
1. Agent has reached its `maxConcurrentSessions` limit
2. Wait for existing sessions to complete, or increase the limit in agent config
3. Set `maxConcurrentSessions: 0` for unlimited

### WebSocket 4409 (Session Already Connected)
1. Another WebSocket client is already connected to this session
2. Close the existing connection first, or use a different session ID
3. Each session allows exactly one active WebSocket connection

### Session history lost
1. Verify `sessionsDirectory` is writable
2. Check disk space availability
3. Confirm `ISessionStore` is not in-memory for production
4. Check if `SessionCleanupService` TTL is too aggressive — default is 24 hours

### WebSocket connection drops
1. Check browser WebSocket support (console for errors)
2. Verify firewall allows WebSocket connections
3. Check Gateway logs for timeout or protocol errors

### Config changes not taking effect
1. Verify the file was saved (some editors use write-rename which the watcher detects)
2. Check Gateway logs for reload messages
3. Changes to `listenUrl` require a full restart — hot reload doesn't rebind ports

## Further Reading

- [Developer Guide](../../docs/getting-started-dev.md) — Local development setup and workflow
- [BotNexus Architecture Overview](../../docs/architecture.md)
- [Configuration Guide](../../docs/configuration.md)
- [Extension Development](../../docs/extension-development.md)
- [API Reference](../../docs/api-reference.md)
- [OpenAPI Spec](../../docs/api/openapi.json) — Machine-readable API specification
