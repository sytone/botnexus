# Gateway Service Architecture

**By:** Leela (Lead/Architect)  
**Date:** 2026-04-06  
**Status:** Approved — ready for implementation  
**Requested by:** Jon Bullen (Copilot)

---

## Summary

This ADR defines the Gateway Service — BotNexus's central orchestration layer. The Gateway manages agent lifecycles, routes messages between channels and agents, persists sessions, and exposes REST + WebSocket APIs for management and real-time interaction.

---

## Project Structure

### New Projects (5)

| Project | Location | Purpose |
|---------|----------|---------|
| **BotNexus.Gateway.Abstractions** | `src/gateway/BotNexus.Gateway.Abstractions/` | Pure interfaces and models. Zero dependencies. All extension points live here. |
| **BotNexus.Gateway** | `src/gateway/BotNexus.Gateway/` | Runtime engine — agent management, routing, isolation, orchestration. |
| **BotNexus.Gateway.Api** | `src/gateway/BotNexus.Gateway.Api/` | ASP.NET Core surface — REST controllers, WebSocket middleware. |
| **BotNexus.Gateway.Sessions** | `src/gateway/BotNexus.Gateway.Sessions/` | Session store implementations (in-memory, file-backed JSONL). |
| **BotNexus.Channels.Core** | `src/channels/BotNexus.Channels.Core/` | Channel adapter base classes for external protocols. |

### Dependency Graph

```
BotNexus.Gateway.Abstractions          ← Zero dependencies (net10.0 only)
    ↑
    ├── BotNexus.Gateway               ← + AgentCore, Providers.Core
    │       ↑
    │       └── BotNexus.Gateway.Api   ← + Microsoft.AspNetCore.App
    │
    ├── BotNexus.Gateway.Sessions      ← (Abstractions only)
    │
    └── BotNexus.Channels.Core         ← (Abstractions only)
```

**Critical constraint:** The Gateway project references AgentCore (which transitively brings Providers.Core). It does **not** reference any specific provider package (Anthropic, OpenAI, Copilot). Provider registration is handled at the composition root, not in the Gateway.

---

## Key Interfaces (Extension Points)

All interfaces live in `BotNexus.Gateway.Abstractions`. Each is an explicit extension point designed for dependency injection.

### Agent Management

| Interface | Namespace | Purpose |
|-----------|-----------|---------|
| `IAgentRegistry` | `.Agents` | Static registry of agent descriptors (CRUD). The "phone book." |
| `IAgentSupervisor` | `.Agents` | Lifecycle management of running agent instances (create, track, stop). |
| `IAgentHandle` | `.Agents` | Handle to a running agent — `PromptAsync()`, `StreamAsync()`, `AbortAsync()`. Implementations are isolation-strategy-specific. |
| `IAgentCommunicator` | `.Agents` | Sub-agent and cross-agent communication. Enables multi-agent workflows. |

### Infrastructure

| Interface | Namespace | Purpose |
|-----------|-----------|---------|
| `IIsolationStrategy` | `.Isolation` | Pluggable execution environment. Named strategies: `in-process`, `sandbox`, `container`, `remote`. |
| `IChannelAdapter` | `.Channels` | External protocol integration (Telegram, Discord, TUI). Started/stopped by Gateway. |
| `IChannelDispatcher` | `.Channels` | Callback for channel adapters to push inbound messages into the routing pipeline. |
| `ISessionStore` | `.Sessions` | Persistence for gateway sessions. Implementations: in-memory, file-backed. |
| `IMessageRouter` | `.Routing` | Routes inbound messages to target agent(s). |
| `IActivityBroadcaster` | `.Activity` | Fan-out event broadcasting for real-time monitoring. |
| `IGatewayAuthHandler` | `.Security` | Pluggable authentication (API key, JWT). |

### Design Decisions on Interfaces

1. **`IAgentHandle` wraps `AgentCore.Agent`** — The in-process handle subscribes to the existing `AgentEvent` system and translates events to `AgentStreamEvent`. This means the Gateway event model maps 1:1 to AgentCore events without coupling.

2. **`IChannelAdapter` uses push-based dispatch** — Adapters call `IChannelDispatcher.DispatchAsync()` instead of publishing to a message bus. This removes the need for a shared bus and simplifies the lifecycle (the Gateway itself implements `IChannelDispatcher`).

3. **`IIsolationStrategy` is a factory** — It creates `IAgentHandle` instances. The handle abstracts the isolation boundary, so the rest of the Gateway is completely unaware of how agents execute.

4. **Session model is Gateway-level** — `GatewaySession` is distinct from the AgentCore message timeline. The Gateway persists high-level conversation history; the AgentCore manages detailed execution state in memory.

---

## API Surface

### REST Endpoints

```
POST   /api/agents                           Register a new agent
GET    /api/agents                           List all registered agents
GET    /api/agents/{id}                      Get agent details
DELETE /api/agents/{id}                      Unregister an agent

GET    /api/agents/{id}/sessions/{sid}/status Get agent instance status
GET    /api/agents/instances                  List all active instances
POST   /api/agents/{id}/sessions/{sid}/stop   Stop a specific instance

GET    /api/sessions                          List sessions (filter by agentId)
GET    /api/sessions/{id}                     Get session details + history
DELETE /api/sessions/{id}                     Delete a session

POST   /api/chat                             Non-streaming chat (request/response)
```

### WebSocket Protocol

**Endpoint:** `ws://host/ws?agent={agentId}&session={sessionId}`

**Client → Server:**
```json
{ "type": "message", "content": "Hello" }
{ "type": "abort" }
{ "type": "ping" }
```

**Server → Client:**
```json
{ "type": "connected", "connectionId": "...", "sessionId": "..." }
{ "type": "message_start", "messageId": "..." }
{ "type": "content_delta", "delta": "...", "messageId": "..." }
{ "type": "tool_start", "toolCallId": "...", "toolName": "...", "messageId": "..." }
{ "type": "tool_end", "toolCallId": "...", "toolResult": "...", "messageId": "..." }
{ "type": "message_end", "messageId": "...", "usage": { "inputTokens": N, "outputTokens": N } }
{ "type": "error", "message": "...", "code": "..." }
{ "type": "pong" }
```

**Key improvements over the archive WebSocket protocol:**
- Message IDs for event correlation
- Structured tool execution events (start/end with call ID)
- Explicit usage reporting per message
- Error codes for programmatic handling
- Session ID negotiation at connection time

---

## Integration with Existing Code

### AgentCore Integration

The `InProcessIsolationStrategy` creates a `BotNexus.AgentCore.Agent` instance from an `AgentDescriptor`:

1. Resolves the `LlmModel` from `ModelRegistry` using `(ApiProvider, ModelId)`
2. Constructs `AgentOptions` with the descriptor's system prompt, tools, and model
3. Wraps the `Agent` in an `InProcessAgentHandle`
4. The handle's `StreamAsync()` subscribes to `AgentEvent`s and translates them to `AgentStreamEvent`s

**The Gateway never calls provider APIs directly.** It creates agents via `AgentCore.Agent`, which internally uses `LlmClient` → `IApiProvider` → specific provider.

### Session Bridging

The archive's `SessionManager` used file-backed JSONL with `.meta.json` sidecars. The new `FileSessionStore` preserves this pattern but:
- Implements `ISessionStore` (the new contract)
- Uses the Gateway's `GatewaySession` model instead of the old `Session` class
- Adds proper DI lifecycle (injected logger, configured store path)
- Thread-safe via `SemaphoreSlim` with in-memory cache

### Provider Independence

The Gateway's dependency chain is:
```
Gateway → AgentCore → Providers.Core
```

**No** dependency on Providers.Anthropic, Providers.OpenAI, etc. Specific providers are registered at the application's composition root via `ApiProviderRegistry.Register()`.

---

## Sub-Agent and Cross-Agent Communication

### Sub-Agent Calls (Same Gateway)

A parent agent calls a child agent through `IAgentCommunicator.CallSubAgentAsync()`:
1. Parent's tool invocation triggers the call
2. Communicator creates a scoped session: `{parentSessionId}::sub::{childAgentId}`
3. Supervisor gets/creates the child agent instance
4. Child processes the message and returns the response
5. Parent receives the response as a tool result

### Cross-Agent Calls (Remote Gateway)

Defined in the interface but **not implemented** in Phase 1. The contract is:
```csharp
Task<AgentResponse> CallCrossAgentAsync(
    string sourceAgentId, string targetEndpoint,
    string targetAgentId, string message, CancellationToken ct);
```

When implemented, this will call the remote Gateway's `/api/chat` endpoint.

---

## Security Model

### Authentication
- `IGatewayAuthHandler` — pluggable per scheme (API key, JWT)
- `GatewayAuthContext` — carries headers, query params, path, method
- `GatewayAuthResult` — success with identity or failure with reason

### Authorization
- `GatewayCallerIdentity.AllowedAgents` — per-caller agent access control
- `GatewayCallerIdentity.IsAdmin` — admin flag for management operations

### Tenant Isolation
- Sessions are scoped by session ID (channel:conversation:agent composite)
- Agent instances are keyed by `{agentId}::{sessionId}`
- No cross-session data leakage by design

### Phase 1 Scope
- API key auth handler (implementation stub ready)
- Per-agent caller ACL enforcement in controllers
- JWT auth is Phase 2

---

## What Gets Implemented Now vs. Stubbed

### Implemented (Phase 1)

| Component | Status |
|-----------|--------|
| All interfaces + models in Abstractions | ✅ Complete |
| `DefaultAgentRegistry` | ✅ Complete |
| `DefaultAgentSupervisor` | ✅ Complete |
| `DefaultMessageRouter` | ✅ Complete |
| `InMemoryActivityBroadcaster` | ✅ Complete |
| `InProcessIsolationStrategy` + `InProcessAgentHandle` | ✅ Complete |
| `GatewayHost` (BackgroundService) | ✅ Complete |
| `AgentsController`, `SessionsController`, `ChatController` | ✅ Complete |
| `GatewayWebSocketHandler` | ✅ Complete |
| `InMemorySessionStore` | ✅ Complete |
| `FileSessionStore` (JSONL) | ✅ Complete |
| `ChannelAdapterBase` + `ChannelManager` | ✅ Complete |
| DI registration extensions | ✅ Complete |

### Stubbed for Phase 2

| Component | Notes |
|-----------|-------|
| Sandbox isolation strategy | Interface defined, no implementation |
| Container isolation strategy | Interface defined, no implementation |
| Remote isolation strategy | Interface defined, no implementation |
| Cross-agent communication | Interface method throws `NotImplementedException` |
| JWT authentication handler | Interface defined, no implementation |
| Telegram channel adapter | Base class ready, no protocol implementation |
| Discord channel adapter | Base class ready, no protocol implementation |
| OpenAPI spec generation | Controllers have XML docs; Swashbuckle not yet added |

---

## Implementation Assignments

| Team Member | Scope | Target Files |
|-------------|-------|-------------|
| **Farnsworth** | Review and refine interfaces, add missing XML docs, ensure contracts are airtight | `src/gateway/BotNexus.Gateway.Abstractions/**` |
| **Bender** | Wire up DI, implement full `GatewayHost` message flow, integration testing | `src/gateway/BotNexus.Gateway/**`, `src/gateway/BotNexus.Gateway.Sessions/**` |
| **Fry** | WebUI channel — implement the static WebUI + WebSocket client against the new protocol | `src/gateway/BotNexus.Gateway.Api/WebSocket/**` |

---

## Files Created

```
src/gateway/BotNexus.Gateway.Abstractions/
├── BotNexus.Gateway.Abstractions.csproj
├── Activity/IActivityBroadcaster.cs
├── Agents/IAgentCommunicator.cs
├── Agents/IAgentHandle.cs
├── Agents/IAgentRegistry.cs
├── Agents/IAgentSupervisor.cs
├── Channels/IChannelAdapter.cs
├── Isolation/IIsolationStrategy.cs
├── Models/AgentDescriptor.cs
├── Models/AgentExecution.cs
├── Models/AgentInstance.cs
├── Models/GatewayActivity.cs
├── Models/GatewaySession.cs
├── Models/Messages.cs
├── Routing/IMessageRouter.cs
├── Security/IGatewayAuthHandler.cs
└── Sessions/ISessionStore.cs

src/gateway/BotNexus.Gateway/
├── BotNexus.Gateway.csproj
├── GatewayHost.cs
├── Activity/InMemoryActivityBroadcaster.cs
├── Agents/DefaultAgentRegistry.cs
├── Agents/DefaultAgentSupervisor.cs
├── Extensions/GatewayServiceCollectionExtensions.cs
├── Isolation/InProcessIsolationStrategy.cs
└── Routing/DefaultMessageRouter.cs

src/gateway/BotNexus.Gateway.Api/
├── BotNexus.Gateway.Api.csproj
├── Controllers/AgentsController.cs
├── Controllers/ChatController.cs
├── Controllers/SessionsController.cs
├── Extensions/GatewayApiServiceCollectionExtensions.cs
└── WebSocket/GatewayWebSocketHandler.cs

src/gateway/BotNexus.Gateway.Sessions/
├── BotNexus.Gateway.Sessions.csproj
├── FileSessionStore.cs
└── InMemorySessionStore.cs

src/channels/BotNexus.Channels.Core/
├── BotNexus.Channels.Core.csproj
├── ChannelAdapterBase.cs
└── ChannelManager.cs
```
