# BotNexus.Gateway

> Gateway runtime — agent orchestration, routing, isolation, and lifecycle management.

## Overview

This package implements the core BotNexus Gateway runtime. It orchestrates agent instances, manages message routing, handles session persistence, broadcasts activity events, and enforces isolation boundaries that protect users from agent actions. All functionality is exposed via interfaces in `BotNexus.Gateway.Abstractions`.

This package depends on abstractions, session management, channel adapters, and agent providers, but contains no ASP.NET Core concerns — those are in `BotNexus.Gateway.Api`.

## Key Types

### Services (Orchestration)

| Type | Namespace | Description |
|------|-----------|-------------|
| `AgentSupervisor` | Agents | Manages the lifecycle of agent instances — creation, tracking, status, and termination. Thread-safe with concurrency limits per agent. |
| `AgentRegistry` | Agents | In-memory registry of agent descriptors, loaded from platform configuration and watched for changes. |
| `MessageRouter` | Routing | Routes inbound messages to the appropriate agent instance based on targeting, session ID, and default agent. |
| `ActivityBroadcaster` | Activity | Fan-out broker for real-time gateway events (agent started, session created, tool called, streaming deltas). |
| `GatewayAuthHandler` | Security | Validates API keys and enforces caller identity and agent permissions. |
| `SessionStore` | Sessions | Persistence layer for gateway sessions — uses `ISessionStore` to delegate to file/database backends. |

### Configuration & Extension

| Type | Namespace | Description |
|------|-----------|-------------|
| `AddBotNexusGateway` | Extensions | Extension method on `IServiceCollection` — registers all gateway services and loads platform configuration. |
| `PlatformConfigLoader` | Configuration | Loads `config.json` from `~/.botnexus/` or override location; validates schema; watches for changes. |

### Models

| Type | Namespace | Description |
|------|-----------|-------------|
| `GatewayOptions` | Configuration | Configuration settings for the gateway (concurrency limits, session constraints, resource allocation). |

## Usage

### In ASP.NET Core

```csharp
// In Program.cs
builder.Services.AddBotNexusGateway();
// This registers all gateway services, loads config, sets up activity broadcasting, and starts config watcher
```

### Dependency Injection

Once registered, services are available for injection:

```csharp
public class SomeController(
    IAgentSupervisor supervisor,
    ISessionStore sessions,
    IActivityBroadcaster broadcaster)
{
    // Use injected services
}
```

## Activity Events

The gateway broadcasts `GatewayActivity` events through `IActivityBroadcaster`. Key event types:

- `"agent_started"` — Agent instance created for a session
- `"agent_stopped"` — Agent instance terminated
- `"session_created"` — New conversation session created
- `"message_routed"` — Inbound message routed to an agent
- `"tool_called"` — Tool execution in agent
- `"streaming_started"` — Response streaming began
- `"streaming_ended"` — Response streaming completed
- `"error"` — An error occurred during agent execution

Subscribers can filter by `agentId` via the activity broadcaster's query parameter.

## Configuration Watching

The gateway watches `~/.botnexus/config.json` for changes. Most changes (agent definitions, provider settings, API keys) hot-reload. Changes to `gateway.listenUrl` require a restart.

## Thread Safety

All gateway services are thread-safe:

- `AgentRegistry` — Uses `Lock` for safe concurrent access to agent descriptors
- `AgentSupervisor` — Thread-safe instance tracking with per-agent concurrency limits
- `SessionStore` — Delegates to underlying storage backend (typically uses file locks or database transactions)

## Extension Points

Extensions can:

1. Provide custom **isolation strategies** (implement `IIsolationStrategy`)
2. Add **channel adapters** (implement `IChannelAdapter`)
3. Add **session stores** (implement `ISessionStore`)
4. Customize **routing logic** (implement `IMessageRouter`)

Refer to the architecture documentation for extension integration patterns.

## Further Reading

- [BotNexus.Gateway.Abstractions](../BotNexus.Gateway.Abstractions/README.md) — Contract surface
- [BotNexus.Gateway.Api](../BotNexus.Gateway.Api/README.md) — ASP.NET Core hosting
- [BotNexus.Gateway.Sessions](../BotNexus.Gateway.Sessions/README.md) — Session persistence
- [Architecture Overview](../../docs/architecture.md) — System design
