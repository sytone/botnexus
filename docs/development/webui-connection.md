# WebUI Architecture and Connection Flow

This document describes the BotNexus WebUI architecture, including the SignalR connection model, per-channel containers, and multi-session management.

## Overview

The WebUI is a **channel-centric, multi-session interface** that connects to the Gateway via SignalR. Key characteristics:

- **Subscribe-All Model**: Connect once, subscribe to all sessions
- **Per-Channel Containers**: Each agent+channel gets its own permanent DOM container — no DOM swapping
- **Auto-Session on Send**: Sessions created automatically on first message
- **Per-Session Stores**: Independent state management per session
- **Streaming Updates**: Real-time agent responses via SignalR events

## Architecture Diagram

```text
┌─────────────────────────────────────────────────────────────┐
│                         Browser                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │               SignalR Client                          │  │
│  │  connection.on("MessageStart", ...)                   │  │
│  │  connection.on("ContentDelta", ...)                   │  │
│  │  connection.on("MessageEnd", ...)                     │  │
│  └───────────────────┬───────────────────────────────────┘  │
│                      │                                       │
│  ┌───────────────────▼───────────────────────────────────┐  │
│  │         SessionStoreManager                           │  │
│  │  - stores: Map<sessionId, SessionStore>              │  │
│  │  - activeViewId: string | null                       │  │
│  │  - switchView(sessionId): void                       │  │
│  └───────────────────┬───────────────────────────────────┘  │
│                      │                                       │
│  ┌───────────────────▼───────────────────────────────────┐  │
│  │         SessionStore (per session)                    │  │
│  │  - sessionId: string                                  │  │
│  │  - agentId: string                                    │  │
│  │  - channelType: string                                │  │
│  │  - streamState: { isStreaming, ... }                 │  │
│  │  - containerId: string (permanent DOM container)      │  │
│  └───────────────────┬───────────────────────────────────┘  │
│                      │                                       │
│  ┌───────────────────▼───────────────────────────────────┐  │
│  │              DOM Renderer                             │  │
│  │  - per-channel containers (permanent, show/hide)      │  │
│  │  - elSidebar (session list)                           │  │
│  │  - marked.js (Markdown rendering)                     │  │
│  │  - DOMPurify (XSS sanitization)                       │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                        ▲
                        │ SignalR over WebSocket
                        ▼
┌─────────────────────────────────────────────────────────────┐
│                     Gateway (Server)                        │
│  ┌──────────────────────────────────────────────────────┐   │
│  │              GatewayHub                              │   │
│  │  - SubscribeAll()                                    │   │
│  │  - SendMessage(agentId, channelType, content)       │   │
│  │  - Steer(agentId, sessionId, content)               │   │
│  │  - FollowUp(agentId, sessionId, content)            │   │
│  └───────────────────┬──────────────────────────────────┘   │
│                      │                                       │
│  ┌───────────────────▼──────────────────────────────────┐   │
│  │       SignalRChannelAdapter                          │   │
│  │  - SendStreamEventAsync(conversationId, event)       │   │
│  │  - Broadcasts to group: session:{sessionId}          │   │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

## Connection Flow

### 1. Initial Connection

```javascript
// app.js
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/gateway")
    .withAutomaticReconnect()
    .build();

await connection.start();
connectionId = connection.connectionId;
```

**Connection Lifecycle:**

1. WebSocket handshake to `/gateway` endpoint
2. SignalR negotiation (protocol selection)
3. Connection established → `connectionId` assigned
4. Automatic reconnect on disconnect

### 2. SubscribeAll

```javascript
const result = await connection.invoke("SubscribeAll");
// result.sessions = [ { sessionId, agentId, channelType, ... }, ... ]

sessionStoreManager.subscribe(result.sessions);
renderSidebar();
```

**Server Side:**

```csharp
public async Task<object> SubscribeAll()
{
    var sessions = await _warmup.GetAvailableSessionsAsync(Context.ConnectionAborted);
    
    foreach (var session in sessions)
    {
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GetSessionGroup(session.SessionId),
            Context.ConnectionAborted);
    }
    
    return new { sessions };
}
```

**Result:**

- Client is added to all session groups: `session:{sessionId}`
- Client receives list of all available sessions
- `SessionStoreManager` creates a `SessionStore` for each session
- Sidebar is rendered with session list

### 3. Sending a Message

```javascript
async function sendMessage(content) {
    const agentId = sessionStoreManager.activeAgentId;
    const channelType = currentChannelType || "signalr";
    
    const result = await connection.invoke("SendMessage", agentId, channelType, content);
    // result = { sessionId, agentId, channelType }
    
    // Switch to the session (auto-created if new)
    sessionStoreManager.switchView(result.sessionId);
}
```

**Server Side:**

Resolves or creates a session for the agent+channel pair, subscribes the caller to the session group, and dispatches the message.

```csharp
public async Task<object> SendMessage(AgentId agentId, ChannelKey channelType, string content)
```

See [GatewayHub.cs](../../src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs)

**Auto-Session Logic:**

Finds an existing active UserAgent session for the agent+channel pair, or creates a new one with auto-generated SessionId and UserAgent type.

See [GatewayHub.cs](../../src/extensions/BotNexus.Extensions.Channels.SignalR/GatewayHub.cs)

**Key Insight:**

Sessions are created **on first message**, not explicit creation. The client doesn't need to know about session creation — it just sends a message to an agent, and the session is created automatically.

## SignalR Event Handling

### Event Types

Each handler resolves the `SessionStore` via `sessionStoreManager.getOrCreateStore(event.sessionId)` and updates state accordingly:

| Event | Handler Behavior |
|-------|-----------------|
| `MessageStart` | Sets `isStreaming = true`, records `activeMessageId`, appends empty assistant message placeholder |
| `ContentDelta` | Appends delta text to the last message (ignored if not streaming) |
| `ThinkingDelta` | Accumulates delta in `thinkingBuffer`, updates thinking display (if enabled) |
| `ToolStart` | Tracks tool call in `activeToolCalls` with name, arguments, and start time; renders tool UI |
| `ToolEnd` | Records result and end time on the tracked tool call; updates status in DOM |
| `MessageEnd` | Clears streaming state, finalizes message rendering, removes thinking display |
| `Error` | Appends error message to DOM, clears streaming state |

See [events.js](../../src/BotNexus.WebUI/wwwroot/js/events.js)

### Server-Side Broadcasting

Maps `AgentStreamEventType` to SignalR method name, enriches the event with `SessionId`, and broadcasts to the session group via `IHubContext`.

See [SignalRChannelAdapter.cs](../../src/extensions/BotNexus.Extensions.Channels.SignalR/SignalRChannelAdapter.cs)

## Multi-Session Management

### SessionStore Class

Per-session client state: tracks `sessionId`, `agentId`, `channelType`, streaming state (`isStreaming`, `activeToolCalls`, `thinkingBuffer`), permanent DOM container reference, and unread count.

See [session-store.js](../../src/BotNexus.WebUI/wwwroot/js/session-store.js)

### SessionStoreManager

Key behaviors:

- Manages `Map<sessionId, SessionStore>` with LRU eviction (max 20 stores)
- `getOrCreateStore(sessionId)` — creates a new `SessionStore` on first access, evicts the oldest entry when over the limit
- `switchView(sessionId)` — hides the current container, shows the target (no DOM rebuild), resets unread count

See [session-store.js](../../src/BotNexus.WebUI/wwwroot/js/session-store.js)

**Per-Channel Container Strategy:**

1. **Each session** gets a permanent DOM container created on first event
2. **Switch Away**: Hide the current container (`display: none`)
3. **Switch To**: Show the target container — no content rebuild needed
4. **No Server Round-Trip**: Switching is instant, client-side only
5. **SignalR Events**: Route to the correct container by `sessionId`

**Benefits:**

- Instant session switching (no network latency, no DOM rebuild)
- No content loss — containers persist even when not visible
- Supports multi-session streaming (events always append to the correct container)
- Simpler than DOM fragment caching — no clone/restore logic

## Rendering Pipeline

### Markdown Rendering

Renders messages using `marked.parse()` for Markdown with `DOMPurify.sanitize()` for XSS protection. Delta updates re-render the full accumulated text to maintain valid Markdown.

See [chat.js](../../src/BotNexus.WebUI/wwwroot/js/chat.js)

**Sanitization:**

- `DOMPurify.sanitize()` prevents XSS attacks
- Strips `<script>`, `<iframe>`, `onclick`, etc.
- Safe to render LLM-generated content

### Tool Call Rendering

Tool calls rendered as collapsible elements with name, status indicator, arguments (in `<details>`), and result. Status updates from Running → Success/Failed on ToolEnd.

See [chat.js](../../src/BotNexus.WebUI/wwwroot/js/chat.js)

### Thinking Display

Thinking deltas accumulated in a temporary `<div>` with the thinking buffer. Removed on MessageEnd.

See [events.js](../../src/BotNexus.WebUI/wwwroot/js/events.js)

## Sidebar and Session List

Renders session list sorted by last-viewed time. Each entry shows agent name, channel type, unread badge, and streaming indicator. Clicking switches the active view.

See [sidebar.js](../../src/BotNexus.WebUI/wwwroot/js/sidebar.js)

## Summary

**Key Architectural Decisions:**

1. **Subscribe-All Model**: Single connection subscribes to all sessions upfront
2. **Auto-Session on Send**: Sessions created implicitly on first message
3. **Per-Channel Containers**: Each agent+channel gets a permanent DOM container — show/hide, not swap
4. **Per-Session Stores**: Independent state management per session
5. **SignalR Group Broadcast**: Events routed to session groups (`session:{sessionId}`)
6. **Persistent Containers**: No content loss when switching — containers persist even when hidden
7. **Markdown Rendering**: `marked.js` + `DOMPurify` for safe rendering
8. **Streaming Events**: Real-time deltas via SignalR for smooth UX

**Performance Characteristics:**

- Connection setup: ~100-200ms (WebSocket handshake + negotiation)
- SubscribeAll: ~50-100ms (depends on session count)
- Session switch: <10ms (show/hide container, no network)
- Message send: ~50-200ms (HTTP round-trip to hub)
- Streaming latency: ~20-50ms per delta (SignalR overhead)

**Scalability Considerations:**

- SignalR groups scale to ~10K connections per hub
- Client stores limited to 20 sessions (LRU eviction)
- Per-channel containers avoid re-rendering overhead on switch
- Markdown rendering is client-side (no server load)

**Future Enhancements:**

- Virtual scrolling for long conversation histories
- Persistent client-side storage (IndexedDB)
- Offline support (service worker + sync)
- Multi-client collaboration (shared cursors, presence)
- Voice input/output (Web Speech API)
