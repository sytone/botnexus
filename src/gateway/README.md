# BotNexus Gateway

The **Gateway** is the central orchestrator of the BotNexus platform. It manages multi-agent execution, session persistence, outbound-stream broadcasting, and extensibility via pluggable strategies and channel adapters. The gateway itself only hosts a REST API — real-time delivery to end-user surfaces (browser, Teams, TUI, …) is owned by channel extensions that subscribe to gateway events and bring their own transport (e.g. the bundled SignalR channel extension mounts a SignalR hub).

## Architecture

The Gateway is composed of **6 core projects**:

| Project | Purpose |
|---------|---------|
| **BotNexus.Gateway.Abstractions** | Interface contracts (IAgentConfigurationSource, IIsolationStrategy, IChannelAdapter, ISessionStore) |
| **BotNexus.Gateway** | Main host — message routing, agent supervision, hot reload, channel management |
| **BotNexus.Gateway.Api** | REST API (agents, sessions, chat, config) and middleware (auth, rate-limit, correlation-id). Channel extensions mount their own additional endpoints (e.g. the SignalR channel mounts a hub) via `MapExtensionEndpoints`. |
| **BotNexus.Gateway.Sessions** | Session persistence implementations (InMemory, FileSessionStore) |
| **BotNexus.Gateway.Conversations** | Conversation persistence (InMemory, File, Sqlite) and routing |
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
   (security boundary: in-process/sandbox/container/remote)
              ↓
   Agent executes with LLM provider
              ↓
   ISessionStore
   (Persists conversation history)
```

## Extension Points

The Gateway is **fully extensible** through pluggable interfaces:

### 1. IIsolationStrategy
Controls the security boundary between the agent and the user's environment. Agents cannot be fully trusted (prompt-injection, hostile tool output, mistakes), so the isolation strategy bounds what the agent can reach and what it can leak. Pick a stricter strategy as the trust in the agent decreases.

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

**Built-in strategies (fastest → most isolated):**
- **in-process** — Runs agent directly in Gateway process. No security boundary; shared memory and OS identity. Default, fastest. Suitable for trusted single-user deployments.
- **sandbox** — Separate OS process with IPC and OS-level confinement (reduced privileges, restricted file system, syscall filtering). Planned.
- **container** — Docker container. Host file system, network, and other agents invisible unless granted. Planned.
- **remote** — Agent runs on a remote machine via HTTP/gRPC. Strongest isolation from user-local resources. Planned.

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
    Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default);
}
```

`ChannelStreamTarget` is a typed record (`SessionId`, `ChannelAddress`, optional `BindingId`) that mirrors the addressing fields on `OutboundMessage`. Each adapter consumes whichever field matches its native routing model:

- **SignalR** keys outbound by `target.SessionId` (clients are grouped per session)
- **Telegram** keys by `target.ChannelAddress` (chat id, optionally with a `/topic:NN` suffix)
- **TUI** uses `target.SessionId` purely as a display label
- **InternalChannelAdapter** resolves the parent's originating adapter via `target.SessionId`

#### Channel Capability Flags

Channels declare which interaction features they support. The Gateway uses these flags to decide what events to send:

| Flag | Purpose | Example |
|------|---------|---------|
| `SupportsStreaming` | Real-time token streaming | SignalR: ✅, REST: ❌ |
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

The `GatewayAuthMiddleware` protects the Gateway's HTTP endpoints (REST API plus any endpoints mounted by channel extensions, such as the SignalR hub). It delegates validation to `ApiKeyGatewayAuthHandler`. Requests that upgrade from HTTP (for example, a SignalR negotiation that selects the WebSocket transport) pass through the same middleware — these are classified as `"WS"` in audit logs to distinguish them from plain HTTP requests, but they share one auth pipeline.

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
| `CronNoopRetention` | `7.00:00:00` (7 days) | Prune near-empty cron "noop wake" sessions (≤2 messages) older than this window. User-configurable; set to `null` or `0` to disable. Does not change wake/persist behaviour. |

Configure via the `gateway` section in `config.json`:

```json
{
  "gateway": {
    "sessionCleanup": {
      "checkInterval": "00:05:00",
      "sessionTtl": "1.00:00:00",
      "closedSessionRetention": "7.00:00:00",
      "cronNoopRetention": "7.00:00:00"
    }
  }
}
```

### Session Concurrency

The gateway does not bind a session to any single transport connection. Multiple subscribers (e.g. multiple browser tabs of the same user, the Blazor portal plus a TUI debugger) can observe the same session simultaneously; outbound payloads fan out to every live binding the conversation has, and the channel extension owning each binding is responsible for delivering them. When a channel extension detects that one of its connections is no longer reachable, it raises `StaleChannelConnectionException` so the gateway can demote that binding to `Muted` without sealing the session.

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

### Realtime Streaming

The gateway exposes streaming events (`message_start`, `content_delta`, `thinking_delta`, `tool_start`, `tool_end`, `message_end`, …) as in-process events on `IActivityBroadcaster`. Subscribers — typically channel extensions — re-encode those events for their own transport and deliver them to end-user surfaces. The bundled SignalR channel extension mounts a SignalR hub for the Blazor portal; see `src/extensions/BotNexus.Extensions.Channels.SignalR/` for that hub's wire shape and the Blazor client. To consume streaming events from your own channel, implement `IChannelAdapter` and subscribe to `IActivityBroadcaster` (see `src/extensions/BotNexus.Extensions.Channels.SignalR/SignalRChannelAdapter.cs` for a reference implementation).

## Gateway Lifecycle Management

`botnexus gateway start` launches the gateway as a detached process in its own console window, returning control to your terminal immediately. This allows you to run other CLI commands without blocking on the gateway logs.

### Commands

| Command | Description |
|---------|-------------|
| `botnexus gateway start` | Start the gateway (detached, new window) |
| `botnexus gateway start --attached` | Start in foreground (for debugging) |
| `botnexus gateway stop` | Stop the gateway and clean up |
| `botnexus gateway status` | Check if the gateway is running |
| `botnexus gateway restart` | Stop then start |

### Examples

**Start the gateway on the default port (5005):**
```bash
botnexus gateway start
```

Output:
```
✓ Gateway started (PID 12345)
  URL:  http://localhost:5005
  Logs: C:\Users\{username}\.botnexus\logs\gateway.log
  Stop: botnexus gateway stop
```

**Start on a custom port:**
```bash
botnexus gateway start --port 8080
```

**Start in foreground mode (useful for debugging):**
```bash
botnexus gateway start --attached
```

**Check gateway status:**
```bash
botnexus gateway status
```

Output example:
```
✓ Gateway is running (PID 12345, uptime 2m 14s)
```

**Stop the gateway:**
```bash
botnexus gateway stop
```

**Restart the gateway:**
```bash
botnexus gateway restart
```

### Runtime files

| File | Purpose |
|------|---------|
| `~/.botnexus/gateway.pid` | PID of the running gateway process |
| `~/.botnexus/logs/` | Gateway log files (Serilog output) |

### Platform support

v1 supports **Windows only**. On Linux/macOS, `gateway start` prints a clear error message. Cross-platform support (using `nohup`, systemd, or launchd) is planned for a future release.

### Notes

- The gateway runs in its own console window — closing the window stops the gateway, but the process manager detects this and cleans up the PID file
- Stale PID files (from gateway crashes) are automatically detected and cleaned up on the next `start` or `status` command
- The `--attached` flag is useful for development and debugging; it preserves the original foreground behavior where the gateway blocks the terminal
- All gateway output (logs, errors) continues to be written to `~/.botnexus/logs/` regardless of whether the process runs detached or attached

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

### Test Real-time Streaming

Use the Blazor portal (`http://localhost:5005`) — it connects through the bundled SignalR channel extension. To exercise streaming from a custom channel, register your own `IChannelAdapter` and observe `IActivityBroadcaster` events.

### Explore the API

- **Blazor WebUI:** `http://localhost:5005` — Chat and configuration interface
- **Swagger:** `http://localhost:5005/swagger` — Interactive API docs
- **Health:** `http://localhost:5005/health` — Status check (no auth required)

### Run Tests
```bash
dotnet test tests/gateway/BotNexus.Gateway.Tests/BotNexus.Gateway.Tests.csproj
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
│   └── (channel hubs — e.g. the SignalR hub — live in their own extension projects under src/extensions/)
├── BotNexus.Gateway.Sessions/
│   ├── InMemorySessionStore.cs
│   ├── FileSessionStore.cs
│   └── ...
├── BotNexus.Gateway.Conversations/
│   ├── InMemoryConversationStore.cs
│   ├── FileConversationStore.cs
│   ├── SqliteConversationStore.cs
│   └── DefaultConversationRouter.cs
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

### Session history lost
1. Verify `sessionsDirectory` is writable
2. Check disk space availability
3. Confirm `ISessionStore` is not in-memory for production
4. Check if `SessionCleanupService` TTL is too aggressive — default is 24 hours

### Streaming events don't reach the browser
1. Check the channel extension that owns the connection (e.g. the SignalR hub at `/hubs/gateway` for the Blazor portal) — its logs should show negotiation + transport selection
2. Verify the relevant binding hasn't been demoted to `Muted` after a stale-connection event (see Session Concurrency above)
3. Check Gateway logs for any `StaleChannelConnectionException` from the channel adapter

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
