# WebUI architecture and connection flow

This document describes the BotNexus WebUI architecture, including the SignalR connection model, multi-conversation state management, and Blazor component structure.

> **Note:** The WebUI is a Blazor Server application at `src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/`. Its state and transport services live in the shared `…BlazorClient.Core/Services/` project, which the mobile portal reuses.

## Overview

The WebUI is an **agent-centric, multi-conversation interface** that connects to the Gateway via SignalR. Key characteristics:

- **Subscribe-All Model**: Connect once, subscribe to every existing session group
- **Per-Agent Subscription**: A second verb joins per-agent groups so not-yet-created conversations can still notify
- **Auto-Session on Send**: Sessions created automatically on first message
- **Single State Store**: One `ClientStateStore` holds agents, conversations, messages and stream state
- **Streaming Updates**: Real-time agent responses via SignalR events

## Architecture Diagram

```text
+-------------------------------------------------------------+
|                        Browser                              |
|  +-------------------------------------------------------+  |
|  |            Blazor components (ChatPanel, Home)        |  |
|  |            render from store state                    |  |
|  +---------------------------+---------------------------+  |
|                              |                              |
|  +---------------------------v---------------------------+  |
|  |                   ClientStateStore                    |  |
|  |  - Agents: IReadOnlyDictionary<string, AgentState>    |  |
|  |  - ActiveAgentId / ActiveConversationId (read-only)   |  |
|  |  - SelectView(agentId, conversationId, source)        |  |
|  |  - GetStreamState(conversationId)                     |  |
|  +---------------------------^---------------------------+  |
|                              |                              |
|  +---------------------------+---------------------------+  |
|  |                 GatewayEventHandler                   |  |
|  |  HandleMessageStart / HandleContentDelta / ...        |  |
|  +---------------------------^---------------------------+  |
|                              |                              |
|  +---------------------------+---------------------------+  |
|  |                GatewayHubConnection                   |  |
|  |  SignalR client, reconnect, resume coordination       |  |
|  +-------------------------------------------------------+  |
+-------------------------------------------------------------+
                              ^
                              | SignalR over WebSocket (/hub/gateway)
                              v
+-------------------------------------------------------------+
|                     Gateway (Server)                        |
|  +-------------------------------------------------------+  |
|  |                     GatewayHub                        |  |
|  |  - SubscribeAll() -> SubscribeAllResult               |  |
|  |  - SubscribeAgents(agentIds)                          |  |
|  |  - SendMessage(agentId, channelType, content, convId) |  |
|  |  - Steer / FollowUp / Abort / CompactSession          |  |
|  +---------------------------+---------------------------+  |
|                              |                              |
|  +---------------------------v---------------------------+  |
|  |              SignalRChannelAdapter                    |  |
|  |  - broadcasts to group session:{sessionId}            |  |
|  |  - broadcasts to per-agent groups                     |  |
|  +-------------------------------------------------------+  |
+-------------------------------------------------------------+
```

## Connection Flow

### 1. Initial Connection

The client dials the hub endpoint mapped by `SignalREndpointContributor`:

```csharp
app.MapHub<GatewayHub>("/hub/gateway");
```

`GatewayHubConnection` builds the connection with automatic reconnect and appends the connect-time
query parameters described in the [SignalR hub contract](../signalr-hub-contract.md).

**Connection Lifecycle:**

1. SignalR negotiation against `/hub/gateway` (protocol and transport selection)
2. Connection established, `connectionId` assigned
3. Automatic reconnect on disconnect, with resume coordination via `HubResumeCoordinator`

### 2. SubscribeAll and SubscribeAgents

```csharp
var result = await connection.InvokeAsync<SubscribeAllResult>("SubscribeAll");
// result.Sessions = IReadOnlyList<SessionSummary>
```

`SubscribeAll` joins the connection to the group of every **existing** session and returns their
summaries.

Because those groups are derived from sessions that already exist, they can never cover a
conversation that has not been created yet — and "created" is one of the change types
`ConversationChanged` carries. So the client also calls:

```csharp
await connection.InvokeAsync("SubscribeAgents", agentIds);
```

`SubscribeAgents` joins the per-agent notification groups for the agents the client renders, so it
receives `ConversationChanged` for those agents and no others. The agent is the smallest scope that
can name a not-yet-existing conversation. The call is **idempotent** — SignalR's group join is a
no-op for a connection already in the group, so every reconnect may call it without accumulating
anything. Blank entries in the list are ignored.

### 3. Sending a Message

```csharp
public Task<SendMessageResult> SendMessage(
    AgentId agentId, ChannelKey channelType, string content, string? conversationId = null)
```

`SendMessageResult` carries `sessionId`, `agentId` and `channelType`. The server resolves or creates
a session for the agent and channel, subscribes the caller to the session group, and dispatches the
message.

See [GatewayHub.cs](../../src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs).

**Key Insight:**

Sessions are created **on first message**, not by an explicit create call. The client sends a message
to an agent and the session appears.

## SignalR Event Handling

`GatewayEventHandler` in `…BlazorClient.Core/Services/` owns every inbound event. Each handler
resolves the target conversation and mutates `ClientStateStore`, which raises `OnChanged` so the
Blazor components re-render.

| Event | Handler | Behaviour |
|---|---|---|
| `RunStarted` / `RunEnded` | `HandleRunStarted` / `HandleRunEnded` | Sets and clears `IsRunActive`, the authoritative bracket around the whole agent loop. |
| `MessageStart` | `HandleMessageStart` | Marks the conversation streaming and opens a message buffer. |
| `ContentDelta` | `HandleContentDelta` | Appends the delta to the conversation's stream buffer. |
| `ThinkingDelta` | `HandleThinkingDelta` | Accumulates thinking content for the thinking display. |
| `ToolStart` / `ToolEnd` | `HandleToolStart` / `HandleToolEnd` | Tracks and completes entries in `ActiveToolCalls`. |
| `MessageEnd` | `HandleMessageEnd` | Commits the stream buffer to a `ChatMessage` and clears streaming state. |
| `TurnEnd` / `TurnInterrupted` | `HandleTurnEnd` / `HandleTurnInterrupted` | Ends or unwinds the turn. |
| `UserInputRequired` | `HandleUserInputRequired` | Stores the pending `ask_user` prompt for the conversation. |
| `SessionReset` | `HandleSessionReset` | Clears messages for the reset session. |
| `SubAgentSpawned` / `Completed` / `Failed` / `Killed` | `HandleSubAgent*` | Maintains the `SubAgentInfo` list. |
| `SteeringFeedback` | `HandleSteeringFeedback` | Advances the steering queue entry status. |
| `CanvasUpdated` / `CanvasStateChanged` | `HandleCanvas*` | Updates canvas HTML and canvas state keys. |
| `TodoUpdated` | `HandleTodoUpdated` | Replaces the conversation's todo list. |
| `ConversationChanged` | `HandleConversationChanged` | Creates, renames, archives or removes a conversation in the store. |
| `Error` | `HandleError` | Appends an error message and clears streaming state. |

Connection lifecycle is handled by `HandleReconnecting`, `HandleReconnectedAsync` and
`HandleDisconnected`.

### Server-Side Broadcasting

`SignalRChannelAdapter` maps each `AgentStreamEventType` to its hub method name, enriches the event,
and broadcasts to the session group (`session:{sessionId}`) or the per-agent group via `IHubContext`.

See [SignalRChannelAdapter.cs](../../src/extensions/BotNexus.Extensions.Channels.SignalR/SignalRChannelAdapter.cs).

## Multi-Conversation State

### ClientStateStore

`ClientStateStore` (interface `IClientStateStore`) is the single client-side state container. It is
not a per-session store map — it holds every agent and every conversation:

| Member | Purpose |
|---|---|
| `Agents` | `IReadOnlyDictionary<string, AgentState>` of every known agent. |
| `ActiveAgentId` / `ActiveConversationId` | The current selection. **Read-only properties** — there is no public setter. |
| `SelectView(agentId, conversationId, source)` | The *sole* mutation path for the active view, so an inbound event can never steal the selection from the user. |
| `SeedAgents` / `UpsertAgent` / `RemoveAgent` | Agent roster maintenance. |
| `SeedConversations` / `GetConversation` / `SetActiveConversation` | Conversation roster maintenance. |
| `GetMessages` / `AppendMessage` / `PrependMessages` / `ClearMessages` | Message list per conversation (`PrependMessages` backs history paging). |
| `GetStreamState` / `SetStreaming` / `AppendStreamBuffer` / `CommitStreamBuffer` | Streaming buffers per conversation. |
| `TryResolveAgentBySession` / `TryResolveAgentByConversation` / `TryResolveConversationBySession` | Routing lookups used by the event handlers. |
| `GetPendingAskUser` / `SetPendingAskUser` / `ClearPendingAskUser` | Pending `ask_user` prompt. |
| `GetSteeringQueue` / `AddSteeringEntry` / `UpdateSteeringEntry` | Steering queue per conversation. |
| `OnChanged` / `NotifyChanged` / `NotifyChangedThrottled` | Render notification, throttled on the streaming hot path. |

### ConversationState and ConversationStreamState

`ConversationState` carries `ConversationId`, `Title`, `IsDefault`, `IsPinned`, `Status`,
`ActiveSessionId`, `UnreadCount`, `CreatedAt`/`UpdatedAt`, and the write-once provenance axes
described in [Conversation provenance](../features/conversation-provenance.md). `Source` is
`init`-only by design so no inbound SignalR event can mutate it.

`ConversationStreamState` carries `IsStreaming`, `IsRunActive`, `Buffer` and `ActiveToolCalls`.
`IsRunActive` is the primary driver of turn-active UI: unlike `IsStreaming` and `ActiveToolCalls`,
which drop to a quiescent state in the gaps between message-end and the next tool-start, it stays
asserted across the entire run so steer, follow-up and stop controls do not flicker. The `Buffer` is
backed by a `StringBuilder`, making delta accumulation amortised O(1) per token rather than an O(n)
copy of the growing reply.

**View Switching:**

1. Each conversation keeps its own messages and stream state in the store
2. Switching away preserves the previous conversation's content in memory
3. Switching to a conversation renders via Blazor component diffing
4. No server round-trip — switching is client-side only
5. Inbound events route to the correct conversation by `conversationId`, regardless of which one is shown

## Rendering Pipeline

### Markdown Rendering

Markdown is rendered in the browser by `wwwroot/js/markdown.js`, which uses the vendored
`marked.min.js` and `purify.min.js`.

**Sanitization fails closed.** If either `marked` or `DOMPurify` is unavailable, `renderMarkdown`
returns the original text HTML-escaped by a deliberately hand-written escaper (readable, but inert)
and logs a console warning naming the missing dependency. Unsanitized HTML is never returned — which
is what makes it safe to render LLM-generated content.

### Tool Call Rendering

Tool calls render as collapsible elements showing name, status, arguments and result. Status advances
from running to success or failure when `ToolEnd` arrives.

See [ChatPanel.razor](../../src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Components/ChatPanel.razor).

### Thinking Display

Thinking deltas accumulate during streaming and are cleared on `MessageEnd`.

## Sidebar and Conversation List

The sidebar lists agents and their conversations, showing unread badges and streaming indicators.
Clicking an entry calls `SelectView` with an explicit `SelectionSource`, which is the only way the
active view changes.

See [Home.razor](../../src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Pages/Home.razor).

## Summary

**Key Architectural Decisions:**

1. **Subscribe-All plus Subscribe-Agents**: session groups for existing work, agent groups for conversations that do not exist yet
2. **Auto-Session on Send**: sessions created implicitly on first message
3. **Single Shared Store**: one `ClientStateStore` in `BlazorClient.Core`, reused by desktop and mobile portals
4. **Selection is Write-Guarded**: `SelectView` is the sole mutation path for the active view
5. **Run-Active Bracket**: `RunStarted`/`RunEnded` drive turn-active UI, not per-message streaming flags
6. **Fail-Closed Markdown**: missing `marked`/`DOMPurify` degrades to escaped text, never raw HTML
7. **Streaming Events**: real-time deltas via SignalR for smooth output

**Related:**

- [SignalR hub contract](../signalr-hub-contract.md) — the authoritative event, method and connection-parameter surface
- [Conversation provenance](../features/conversation-provenance.md) — the axes carried on every conversation payload
