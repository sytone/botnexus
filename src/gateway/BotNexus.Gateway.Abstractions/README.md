# BotNexus.Gateway.Abstractions

> All gateway contracts — interfaces, models, and enums — with zero implementations.

## Overview

This package defines the complete contract surface for the BotNexus Gateway. Every interface, model record, and enum that crosses a module boundary lives here. By depending only on this package, extension authors can build channel adapters, session stores, isolation strategies, and routing logic without coupling to any concrete implementation.

This is a leaf dependency — it references no other BotNexus packages.

## Key Types

### Interfaces

| Type | Namespace | Description |
|------|-----------|-------------|
| `IActivityBroadcaster` | Activity | Publishes and subscribes to real-time gateway activity events via fan-out channels. |
| `IAgentCommunicator` | Agents | Enables sub-agent (local) and cross-agent (remote, Phase 2) calls between agents. |
| `IAgentConfigurationSource` | Agents | Loads agent descriptors from external configuration and watches for changes. |
| `IAgentHandle` | Agents | Interaction surface for a running agent instance — prompt, stream, steer, abort, and follow-up. |
| `IAgentRegistry` | Agents | Thread-safe registry ("phone book") of static agent descriptors. |
| `IAgentSupervisor` | Agents | Manages the lifecycle of running agent instances — create, track, and stop. |
| `IChannelAdapter` | Channels | Pluggable adapter for external communication channels (Telegram, Discord, TUI, etc.). |
| `IChannelDispatcher` | Channels | Callback interface for channel adapters to dispatch inbound messages into routing. |
| `IChannelManager` | Channels | Read-only registry for looking up registered channel adapters. |
| `IIsolationStrategy` | Isolation | Security boundary between agent and user — bounds what the agent can reach and leak (in-process, sandbox, container, remote). |
| `IMessageRouter` | Routing | Routes inbound messages to the appropriate agent(s) based on targeting, session, and defaults. |
| `IGatewayAuthHandler` | Security | Authenticates API and WebSocket requests; returns a caller identity. |
| `ISessionStore` | Sessions | Persistence interface for gateway sessions (get, create, save, delete, list). |

### Models (Records and Classes)

| Type | Kind | Description |
|------|------|-------------|
| `AgentDescriptor` | Record | Static agent configuration — ID, model, provider, tools, isolation strategy, metadata. |
| `AgentExecutionContext` | Record | Context passed to isolation strategies when creating an agent handle (session ID, history, parameters). |
| `AgentResponse` | Record | Complete response from an agent after processing a prompt (content, usage, tool calls). |
| `AgentResponseUsage` | Record | Token usage information (input/output token counts). |
| `AgentToolCallInfo` | Record | Summary of a tool call made during agent execution (ID, name, error flag). |
| `AgentStreamEvent` | Record | A single streaming event from an agent (content delta, thinking delta, tool events, errors). |
| `AgentInstance` | Class | Runtime state of a live agent instance bound to a session (status, timestamps, isolation info). |
| `GatewayActivity` | Record | A real-time activity event for monitoring and UI updates. |
| `GatewaySession` | Class | Gateway-level session tracking conversation history and metadata. Thread-safe history access. |
| `SessionEntry` | Record | A single entry in a session's conversation history (role, content, timestamp, tool info). |
| `InboundMessage` | Record | A message received from a channel adapter, ready for routing to an agent. |
| `OutboundMessage` | Record | A message to send back through a channel adapter. |
| `GatewayAuthContext` | Record | Request context provided to authentication handlers (headers, query params, path, method). |
| `GatewayAuthResult` | Record | Result of an authentication attempt — success with identity or failure with reason. |
| `GatewayCallerIdentity` | Record | Authenticated caller identity with permissions, allowed agents, and admin flag. |

### Enums

| Enum | Description |
|------|-------------|
| `AgentStreamEventType` | Streaming event types: `MessageStart`, `ContentDelta`, `ThinkingDelta`, `ToolStart`, `ToolEnd`, `MessageEnd`, `Error`. |
| `AgentInstanceStatus` | Agent instance lifecycle states: `Starting`, `Idle`, `Running`, `Stopping`, `Stopped`, `Faulted`. |
| `GatewayActivityType` | Activity event categories: `MessageReceived`, `ResponseSent`, `StreamDelta`, `AgentProcessing`, `AgentCompleted`, `ToolExecutionStarted`, `ToolExecutionCompleted`, `AgentStarted`, `AgentStopped`, `SessionCreated`, `Error`, `System`. |

## Usage

Reference this package to write code against gateway contracts without pulling in implementations:

```csharp
// Implement a custom session store
public class RedisSessionStore : ISessionStore
{
    public Task<GatewaySession?> GetAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
    public Task<GatewaySession> GetOrCreateAsync(string sessionId, string agentId, CancellationToken ct = default) { /* ... */ }
    public Task SaveAsync(GatewaySession session, CancellationToken ct = default) { /* ... */ }
    public Task DeleteAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
    public Task<IReadOnlyList<GatewaySession>> ListAsync(string? agentId = null, CancellationToken ct = default) { /* ... */ }
}
```

```csharp
// Implement a custom authentication handler
public class JwtAuthHandler : IGatewayAuthHandler
{
    public string Scheme => "Bearer";

    public Task<GatewayAuthResult> AuthenticateAsync(GatewayAuthContext context, CancellationToken ct = default)
    {
        // Validate JWT from Authorization header
        var token = context.Headers.GetValueOrDefault("Authorization")?.Replace("Bearer ", "");
        // ... validate and return GatewayAuthResult.Success(identity) or GatewayAuthResult.Failure(reason)
    }
}
```

## Configuration

This package contains no configurable options — it defines only contracts. Configuration lives in the implementing packages.

## Dependencies

None. This is the leaf package in the BotNexus dependency graph.

- **Target framework:** `net10.0`
- **NuGet packages:** None
- **Project references:** None

## Extension Points

Every interface in this package is an extension point. To extend the gateway:

| To do this... | Implement... |
|---------------|-------------|
| Add a new communication channel | `IChannelAdapter` (or derive from `ChannelAdapterBase` in Channels.Core) |
| Custom session persistence | `ISessionStore` |
| Custom authentication | `IGatewayAuthHandler` |
| Custom message routing | `IMessageRouter` |
| Custom agent isolation | `IIsolationStrategy` |
| External agent configuration | `IAgentConfigurationSource` |
| Activity monitoring | Subscribe via `IActivityBroadcaster.SubscribeAsync()` |

All implementations must be **thread-safe** — the gateway calls them concurrently.
