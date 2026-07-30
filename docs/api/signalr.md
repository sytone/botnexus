# SignalR Hub API Reference

Reference for the BotNexus **SignalR gateway hub** — the real-time transport used by the
WebUI (desktop and mobile portals) and by satellite clients.

> **Relationship to [`signalr-hub-contract.md`](../signalr-hub-contract.md).** That page is
> the narrative protocol guide: connection lifecycle, query parameters, event semantics, and
> client behaviour rules. **This page is the API-surface reference**: the exact hub method
> signatures, their required authorization scope, and the wire shape of every payload record.
> Read the contract page for *how to use* the hub; read this page for *what exists*.

Source of truth for everything below:

- `src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs` (client to server methods)
- `src/extensions/BotNexus.Extensions.Channels.SignalR/IGatewayHubClient.cs` (server to client events)
- `src/extensions/BotNexus.Extensions.Channels.SignalR/HubContracts.cs` (payload records)
- `src/extensions/BotNexus.Extensions.Channels.SignalR/MediaContentPartDto.cs`
- `src/extensions/BotNexus.Extensions.Channels.SignalR/HubScopeGuard.cs` (per-method scopes)
- `src/extensions/BotNexus.Extensions.Channels.SignalR/SignalRAuthPolicy.cs`
- `src/extensions/BotNexus.Extensions.Channels.SignalR/SignalREndpointContributor.cs` (hub route)

---

## Endpoint

The hub is mapped by `SignalREndpointContributor.MapEndpoints`:

```
/hub/gateway
```

On a default gateway that is `http://localhost:5005/hub/gateway`. Standard SignalR
negotiation applies (WebSockets, Server-Sent Events, or Long Polling).

Two optional connection query parameters (`client`, `clientVersion`) are documented in
[the hub contract](../signalr-hub-contract.md#connection-query-parameters).

---

## Authorization

Two independent gates apply.

### 1. Hub-level authentication policy

`GatewayHub` carries `[Authorize(Policy = SignalRAuthPolicy.PolicyName)]` — the policy name
is `"SignalRHubAuth"`. `SignalRAuthRequirementHandler` evaluates it at runtime:

| Condition | Result |
|-----------|--------|
| One or more authentication schemes are registered (e.g. JWT Bearer) | An authenticated user is required. |
| No authentication schemes are registered | The requirement always succeeds (backward compatibility). |

### 2. Per-method scope guard

`HubScopeGuard` enforces least privilege per method. Scopes are read from the OAuth-style
`scope` claim (space-delimited) and/or one or more `scp` claims. The recognised vocabulary is:

| Scope value | Grants |
|-------------|--------|
| `gateway:read` | Passive read/inspect access. |
| `gateway:control` | Write/control access. **Implies** `gateway:read`. |

**Backward compatibility:** when the caller presents *no* recognised scope claim at all, the
guard permits every method. Enforcement only engages once a connection actually carries scope
claims, so existing full-trust clients keep working while a deliberately read-only-scoped
connection is restricted.

---

## Client to server methods

Every method below is a `public` member of `GatewayHub`. The **Scope** column is the scope
asserted by the guard call at the top of the method body; methods with no guard call are
marked `-` (no scope assertion in code).

### Discovery & subscription

| Method | Returns | Scope |
|--------|---------|-------|
| `SubscribeAll()` | `SubscribeAllResult` | - |
| `GetAgents()` | `AgentDescriptor[]` | - |
| `GetAgentStatus(agentId, sessionId)` | `AgentInstance?` | `gateway:read` |
| `Ping()` | `long` | - |

- **`SubscribeAll()`** joins the connection to the conversation-keyed group of every session
  it can currently see, so it keeps receiving events for sessions created later within those
  conversations (compaction creates a new session but keeps the conversation). Each session
  contributes both the real `conversation:{conversationId}` group and a back-compat
  `conversation:{sessionId}` synonym. Returns the sessions available at subscribe time for UI
  initialisation.
- **`GetAgentStatus(agentId, sessionId)`** returns the live `AgentInstance` from the supervisor,
  or `null` when no instance is running. This is a synchronous read; it is the hub counterpart
  of `GET /api/agents/{agentId}/sessions/{sessionId}/status`.
- **`Ping()`** returns `DateTimeOffset.UtcNow.UtcTicks`. It is a no-op round trip used to prove
  the transport is genuinely alive rather than a zombie socket.

### Messaging

| Method | Returns | Scope |
|--------|---------|-------|
| `SendMessage(agentId, channelType, content, conversationId?)` | `SendMessageResult` | `gateway:control` |
| `SendMessageWithMedia(agentId, channelType, content, contentParts, conversationId?)` | `SendMessageResult` | `gateway:control` |

`SendMessageWithMedia` takes `IReadOnlyList<MediaContentPartDto> contentParts`. It throws
`ArgumentException` when `content` is blank *and* `contentParts` is empty — a message must
carry text or at least one attachment.

### Steering a running agent

| Method | Returns | Scope |
|--------|---------|-------|
| `Steer(agentId, sessionId, content, conversationId)` | `SendMessageResult` | `gateway:control` |
| `InterruptAndSteer(agentId, sessionId, message)` | `bool` | `gateway:control` |
| `FollowUp(agentId, sessionId, content)` | `Task` | `gateway:control` |
| `Abort(agentId, sessionId)` | `Task` | `gateway:control` |

### Session control

| Method | Returns | Scope |
|--------|---------|-------|
| `ResetSession(agentId, sessionId)` | `Task` | `gateway:control` |
| `CompactSession(agentId, sessionId)` | `CompactSessionResult` | `gateway:control` |
| `RespondToAskUser(conversationId, requestId, freeFormText, selectedValues, cancelled)` | `Task` | `gateway:control` |

**`RespondToAskUser` failure modes.** It completes a pending `ask_user` request without
entering the normal dispatch queue, and throws `HubException` when:

| Condition | Message |
|-----------|---------|
| ask_user handling not wired up | `ask_user response handling is not available.` |
| `conversationId` blank | `Conversation ID is required.` |
| `requestId` blank | `Request ID is required.` |
| Conversation not found | `Conversation '<id>' not found.` |
| Conversation has no `signalr` channel binding | `Caller does not have access to this conversation.` |
| No matching pending request | `No matching ask_user request is pending for this conversation.` |

`selectedValues` entries are trimmed and blank entries dropped; an empty result becomes `null`.

> `OnConnectedAsync` and `OnDisconnectedAsync` are SignalR lifecycle overrides, not
> client-callable methods.

---

## Server to client events

The typed client contract is `IGatewayHubClient`; each member maps to a client-side handler
name. Semantics for each event are documented in
[the hub contract](../signalr-hub-contract.md). The full signature list:

| Event | Parameter type |
|-------|----------------|
| `Connected` | `ConnectedPayload` |
| `SessionReset` | `SessionResetPayload` |
| `AgentsChanged` | `AgentsChangedPayload` |
| `ConversationChanged` | `ConversationChangedPayload` |
| `SteeringFeedback` | `SteeringFeedbackPayload` |
| `RunStarted` | `AgentStreamEvent` |
| `RunEnded` | `AgentStreamEvent` |
| `TurnEnd` | `AgentStreamEvent` |
| `TurnInterrupted` | `AgentStreamEvent` |
| `MessageStart` | `AgentStreamEvent` |
| `ThinkingDelta` | `AgentStreamEvent` |
| `ContentDelta` | `object` (see [`ContentDeltaPayload`](#contentdeltapayload)) |
| `ToolStart` | `AgentStreamEvent` |
| `ToolEnd` | `AgentStreamEvent` |
| `MessageEnd` | `AgentStreamEvent` |
| `Error` | `AgentStreamEvent` |
| `UserInputRequired` | `AgentStreamEvent` |
| `SubAgentSpawned` | `SubAgentEventPayload` |
| `SubAgentCompleted` | `SubAgentEventPayload` |
| `SubAgentFailed` | `SubAgentEventPayload` |
| `SubAgentKilled` | `SubAgentEventPayload` |
| `CanvasUpdated` | `(string agentId, string conversationId, string html)` |
| `CanvasStateChanged` | `(string conversationId, string key, object? value)` |
| `TodoUpdated` | `(string agentId, string conversationId, string? todoJson)` |

---

## Payload reference

All records live in `HubContracts.cs` and carry explicit `[JsonPropertyName]` attributes, so
the JSON field names below are exact.

### Method return types

#### SendMessageResult

Returned by `SendMessage` and `SendMessageWithMedia`.

| Field | Type | Notes |
|-------|------|-------|
| `sessionId` | string | Session the message was routed to. |
| `agentId` | string | Target agent. |
| `channelType` | string \| null | Resolved channel key. |

#### SubscribeAllResult

| Field | Type | Notes |
|-------|------|-------|
| `sessions` | `SessionSummary[]` | Sessions visible at subscribe time. |

#### CompactSessionResult

| Field | Type | Notes |
|-------|------|-------|
| `succeeded` | bool | Whether compaction ran to completion. |
| `summarized` | int | History entries folded into the summary. |
| `preserved` | int | History entries retained verbatim. |
| `tokensBefore` | int | Estimated tokens before compaction. |
| `tokensAfter` | int | Estimated tokens after compaction. |
| `failureReason` | string \| null | Set only when `succeeded` is false. |

### Event payloads

#### ConnectedPayload

| Field | Type | Notes |
|-------|------|-------|
| `connectionId` | string | SignalR connection id. |
| `agents` | `AgentSummary[]` | Agents visible to this connection. |
| `serverVersion` | string | Gateway version string. |
| `capabilities` | `HubCapabilities` | Advertised hub capabilities. |

**AgentSummary**: `agentId` (string), `displayName` (string), `emoji` (string \| null),
`description` (string \| null).

**HubCapabilities**: `multiSession` (bool).

#### SessionResetPayload

| Field | Type |
|-------|------|
| `agentId` | string |
| `sessionId` | string |
| `conversationId` | string \| null |

#### ContentDeltaPayload

| Field | Type | Notes |
|-------|------|-------|
| `sessionId` | string | Session producing the content. |
| `contentDelta` | string \| null | The chunk of assistant content. |
| `conversationId` | string \| null | Owning conversation. |
| `role` | string \| null | `null` for ordinary streamed content (the client then renders the assistant bubble). Only set when an agent post must render under a specific role, e.g. an on-behalf-of-user kickoff stamped `user`. Trailing-optional, so existing wire messages deserialize unchanged. |

#### SubAgentEventPayload

Used by `SubAgentSpawned`, `SubAgentCompleted`, `SubAgentFailed`, and `SubAgentKilled`.

| Field | Type |
|-------|------|
| `sessionId` | string |
| `subAgentId` | string |
| `name` | string \| null |
| `task` | string |
| `model` | string \| null |
| `archetype` | string |
| `status` | string |
| `startedAt` | timestamp |
| `completedAt` | timestamp \| null |
| `turnsUsed` | int |
| `resultSummary` | string \| null |
| `timedOut` | bool |
| `childSessionId` | string \| null |
| `conversationId` | string \| null |

#### AgentsChangedPayload

| Field | Type | Notes |
|-------|------|-------|
| `changeType` | string | `added`, `updated`, or `removed` (emitted by `AgentsController`). |
| `agentId` | string \| null | The affected agent, when known. |

#### ConversationChangedPayload

| Field | Type |
|-------|------|
| `changeType` | string |
| `agentId` | string |
| `conversationId` | string |
| `updatedAt` | timestamp \| null |

#### SteeringFeedbackPayload

| Field | Type | Notes |
|-------|------|-------|
| `agentId` | string | |
| `sessionId` | string | |
| `kind` | `SteeringFeedbackKind` | `Injected` or `Queued`. |
| `conversationId` | string \| null | |

#### MediaContentPartDto

Argument type for `SendMessageWithMedia`. Binary data is base64-encoded for JSON transport.
These properties are **not** `[JsonPropertyName]`-annotated, so they use the serializer's
default camel-casing.

| Field | Type | Notes |
|-------|------|-------|
| `mimeType` | string | **Required.** e.g. `audio/wav`, `image/png`. |
| `base64Data` | string \| null | Base64-encoded binary payload. |
| `text` | string \| null | Text content, for text parts. |
| `fileName` | string \| null | Optional original filename. |

---

## See also

- [SignalR hub contract](../signalr-hub-contract.md) — protocol narrative and client rules
- [SignalR mobile keepalive](../signalr-mobile-keepalive.md)
- [REST API overview](README.md)
