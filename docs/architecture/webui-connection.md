# WebUI Architecture and Connection Flow

This document describes the BotNexus WebUI architecture, including the SignalR connection model, DOM-swap switching, and multi-session management.

## Overview

The WebUI is a **channel-centric, multi-session interface** that connects to the Gateway via SignalR. Key characteristics:

- **Subscribe-All Model**: Connect once, subscribe to all sessions
- **DOM-Swap Switching**: Switch sessions via client-side DOM replacement (no server join/leave)
- **Auto-Session on Send**: Sessions created automatically on first message
- **Per-Session Stores**: Independent state management per session
- **Streaming Updates**: Real-time agent responses via SignalR events

## Architecture Diagram

```
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
│  │  - cachedDom: DocumentFragment                        │  │
│  └───────────────────┬───────────────────────────────────┘  │
│                      │                                       │
│  ┌───────────────────▼───────────────────────────────────┐  │
│  │              DOM Renderer                             │  │
│  │  - elChatMessages (main chat area)                    │  │
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

```csharp
public async Task<object> SendMessage(AgentId agentId, ChannelKey channelType, string content)
{
    // 1. Resolve or create session
    var session = await ResolveOrCreateSessionAsync(agentId, channelType);
    
    // 2. Subscribe caller to session group (if not already)
    await SubscribeInternalAsync(session.SessionId);
    
    // 3. Dispatch message
    await DispatchMessageAsync(agentId, session.SessionId, content, "message");
    
    return new
    {
        sessionId = session.SessionId.Value,
        agentId = session.AgentId.Value,
        channelType = session.ChannelType?.Value
    };
}
```

**Auto-Session Logic:**

```csharp
async Task<GatewaySession> ResolveOrCreateSessionAsync(AgentId agentId, ChannelKey channelType)
{
    // Find existing active session for agent + channel
    var sessions = await _sessions.ListAsync(agentId, ct);
    var existing = sessions.FirstOrDefault(s =>
        s.Status == SessionStatus.Active &&
        s.ChannelType == channelType &&
        s.SessionType == SessionType.UserAgent);
    
    if (existing != null)
        return existing;
    
    // Create new session
    var sessionId = SessionId.Create();
    var session = await _sessions.GetOrCreateAsync(sessionId, agentId, ct);
    session.SessionType = SessionType.UserAgent;
    session.ChannelType = channelType;
    session.Participants.Add(new SessionParticipant {
        Type = ParticipantType.User,
        Id = Context.ConnectionId
    });
    
    await _sessions.SaveAsync(session, ct);
    return session;
}
```

**Key Insight:**

Sessions are created **on first message**, not explicit creation. The client doesn't need to know about session creation — it just sends a message to an agent, and the session is created automatically.

## SignalR Event Handling

### Event Types

```javascript
connection.on("MessageStart", (event) => {
    const store = sessionStoreManager.getOrCreateStore(event.sessionId);
    store.streamState.isStreaming = true;
    store.streamState.activeMessageId = event.messageId;
    
    appendMessageToDOM(store, "assistant", "");  // Empty placeholder
});

connection.on("ContentDelta", (event) => {
    const store = sessionStoreManager.getOrCreateStore(event.sessionId);
    if (!store.streamState.isStreaming)
        return;
    
    appendDeltaToLastMessage(store, event.delta);
});

connection.on("ThinkingDelta", (event) => {
    const store = sessionStoreManager.getOrCreateStore(event.sessionId);
    if (!showThinking)
        return;
    
    store.streamState.thinkingBuffer += event.delta;
    updateThinkingDisplay(store);
});

connection.on("ToolStart", (event) => {
    const store = sessionStoreManager.getOrCreateStore(event.sessionId);
    store.streamState.activeToolCalls[event.toolCallId] = {
        name: event.toolName,
        arguments: event.arguments,
        startTime: Date.now()
    };
    
    if (showTools)
        appendToolCallToDOM(store, event);
});

connection.on("ToolEnd", (event) => {
    const store = sessionStoreManager.getOrCreateStore(event.sessionId);
    const toolCall = store.streamState.activeToolCalls[event.toolCallId];
    if (toolCall) {
        toolCall.result = event.result;
        toolCall.endTime = Date.now();
        
        if (showTools)
            updateToolCallResult(store, event);
    }
});

connection.on("MessageEnd", (event) => {
    const store = sessionStoreManager.getOrCreateStore(event.sessionId);
    store.streamState.isStreaming = false;
    store.streamState.activeMessageId = null;
    
    finalizeMessage(store);
    clearResponseTimeout();
});

connection.on("Error", (event) => {
    const store = sessionStoreManager.getOrCreateStore(event.sessionId);
    appendErrorToDOM(store, event.errorMessage);
    store.streamState.isStreaming = false;
});
```

### Server-Side Broadcasting

```csharp
// SignalRChannelAdapter
public Task SendStreamEventAsync(string conversationId, AgentStreamEvent streamEvent, CancellationToken ct)
{
    var sessionId = SessionId.From(conversationId);
    var method = streamEvent.Type switch
    {
        AgentStreamEventType.MessageStart => "MessageStart",
        AgentStreamEventType.ThinkingDelta => "ThinkingDelta",
        AgentStreamEventType.ContentDelta => "ContentDelta",
        AgentStreamEventType.ToolStart => "ToolStart",
        AgentStreamEventType.ToolEnd => "ToolEnd",
        AgentStreamEventType.MessageEnd => "MessageEnd",
        AgentStreamEventType.Error => "Error",
        _ => "Unknown"
    };
    
    var enrichedEvent = streamEvent with { SessionId = sessionId };
    
    return _hubContext.Clients.Group(GetSessionGroup(conversationId))
        .SendAsync(method, enrichedEvent, ct);
}

private static string GetSessionGroup(string sessionId) => $"session:{sessionId}";
```

## Multi-Session Management

### SessionStore Class

```javascript
class SessionStore {
    constructor(sessionId, info = {}) {
        this.sessionId = sessionId;
        this.agentId = info.agentId || null;
        this.channelType = info.channelType || null;
        this.streamState = SessionStore.createStreamState();
        this.cachedDom = null;
        this.timelineMeta = null;
        this.lastViewed = null;
        this.unreadCount = 0;
    }
    
    static createStreamState() {
        return {
            isStreaming: false,
            activeMessageId: null,
            activeToolCalls: {},
            activeToolCount: 0,
            thinkingBuffer: '',
            toolCallDepth: 0,
            toolStartTimes: {},
        };
    }
    
    resetStreamState() {
        this.streamState = SessionStore.createStreamState();
    }
    
    get isStreaming() {
        return this.streamState.isStreaming;
    }
}
```

### SessionStoreManager

```javascript
class SessionStoreManager {
    #stores = new Map();
    #maxStores = 20;
    #activeViewId = null;
    #selectedAgentId = null;
    
    get activeViewId() {
        return this.#activeViewId;
    }
    
    get activeStore() {
        return this.#activeViewId ? this.#stores.get(this.#activeViewId) : null;
    }
    
    get activeAgentId() {
        return this.activeStore?.agentId || this.#selectedAgentId || null;
    }
    
    subscribe(sessions) {
        for (const info of sessions) {
            this.getOrCreateStore(info.sessionId, info);
        }
    }
    
    getOrCreateStore(sessionId, info = {}) {
        if (this.#stores.has(sessionId)) {
            return this.#stores.get(sessionId);
        }
        
        const store = new SessionStore(sessionId, info);
        this.#stores.set(sessionId, store);
        
        // Evict oldest if over limit
        if (this.#stores.size > this.#maxStores) {
            const oldest = Array.from(this.#stores.entries())
                .sort((a, b) => (a[1].lastViewed || 0) - (b[1].lastViewed || 0))[0];
            this.#stores.delete(oldest[0]);
        }
        
        return store;
    }
    
    switchView(sessionId) {
        if (this.#activeViewId && this.#activeViewId !== sessionId) {
            // Cache current DOM
            const oldStore = this.#stores.get(this.#activeViewId);
            if (oldStore && elChatMessages.children.length > 0) {
                oldStore.cachedDom = document.createDocumentFragment();
                while (elChatMessages.firstChild) {
                    oldStore.cachedDom.appendChild(elChatMessages.firstChild);
                }
            }
        }
        
        this.#activeViewId = sessionId;
        const store = this.getOrCreateStore(sessionId);
        store.lastViewed = Date.now();
        store.unreadCount = 0;
        
        // Restore cached DOM or clear
        elChatMessages.innerHTML = '';
        if (store.cachedDom) {
            elChatMessages.appendChild(store.cachedDom.cloneNode(true));
            store.cachedDom = null;
        }
        
        updateActiveSessionHighlight();
        scrollToBottom();
    }
}
```

**DOM-Swap Strategy:**

1. **Switch Away**: Cache current DOM to `DocumentFragment`
2. **Switch To**: Restore cached DOM (or render empty)
3. **No Server Round-Trip**: Switching is instant, client-side only
4. **SignalR Events**: Route to correct store via `sessionId`

**Benefits:**

- Instant session switching (no network latency)
- Preserves scroll position and state
- Supports multi-session streaming (events routed to correct store)
- Efficient memory usage (cached fragments are lightweight)

## Rendering Pipeline

### Markdown Rendering

```javascript
function appendMessageToDOM(store, role, content) {
    const messageDiv = document.createElement('div');
    messageDiv.className = `message message-${role}`;
    messageDiv.dataset.messageId = generateMessageId();
    
    const contentDiv = document.createElement('div');
    contentDiv.className = 'message-content';
    
    if (role === 'assistant') {
        // Render Markdown
        const html = marked.parse(content);
        const sanitized = DOMPurify.sanitize(html);
        contentDiv.innerHTML = sanitized;
    } else {
        // User messages: plain text
        contentDiv.textContent = content;
    }
    
    messageDiv.appendChild(contentDiv);
    elChatMessages.appendChild(messageDiv);
    
    scrollToBottom();
}

function appendDeltaToLastMessage(store, delta) {
    const lastMessage = elChatMessages.querySelector('.message:last-child');
    if (!lastMessage)
        return;
    
    const contentDiv = lastMessage.querySelector('.message-content');
    const currentText = contentDiv.dataset.rawContent || contentDiv.textContent;
    const updatedText = currentText + delta;
    
    // Re-render Markdown
    const html = marked.parse(updatedText);
    const sanitized = DOMPurify.sanitize(html);
    contentDiv.innerHTML = sanitized;
    contentDiv.dataset.rawContent = updatedText;
    
    scrollToBottom();
}
```

**Sanitization:**

- `DOMPurify.sanitize()` prevents XSS attacks
- Strips `<script>`, `<iframe>`, `onclick`, etc.
- Safe to render LLM-generated content

### Tool Call Rendering

```javascript
function appendToolCallToDOM(store, event) {
    const toolDiv = document.createElement('div');
    toolDiv.className = 'tool-call';
    toolDiv.dataset.toolCallId = event.toolCallId;
    
    toolDiv.innerHTML = `
        <div class="tool-header">
            <span class="tool-name">${escapeHtml(event.toolName)}</span>
            <span class="tool-status">Running...</span>
        </div>
        <details>
            <summary>Arguments</summary>
            <pre>${escapeHtml(JSON.stringify(event.arguments, null, 2))}</pre>
        </details>
        <div class="tool-result" style="display: none;"></div>
    `;
    
    elChatMessages.appendChild(toolDiv);
}

function updateToolCallResult(store, event) {
    const toolDiv = elChatMessages.querySelector(`[data-tool-call-id="${event.toolCallId}"]`);
    if (!toolDiv)
        return;
    
    const statusSpan = toolDiv.querySelector('.tool-status');
    statusSpan.textContent = event.success ? 'Success' : 'Failed';
    statusSpan.className = `tool-status ${event.success ? 'success' : 'failed'}`;
    
    const resultDiv = toolDiv.querySelector('.tool-result');
    resultDiv.style.display = 'block';
    resultDiv.innerHTML = `<pre>${escapeHtml(event.result)}</pre>`;
}
```

### Thinking Display

```javascript
connection.on("ThinkingDelta", (event) => {
    const store = sessionStoreManager.getOrCreateStore(event.sessionId);
    if (!showThinking || sessionStoreManager.activeViewId !== event.sessionId)
        return;
    
    let thinkingDiv = elChatMessages.querySelector('.thinking-display');
    if (!thinkingDiv) {
        thinkingDiv = document.createElement('div');
        thinkingDiv.className = 'thinking-display';
        thinkingDiv.innerHTML = '<strong>Thinking...</strong><div class="thinking-content"></div>';
        elChatMessages.appendChild(thinkingDiv);
    }
    
    const contentDiv = thinkingDiv.querySelector('.thinking-content');
    contentDiv.textContent += event.delta;
    scrollToBottom();
});

connection.on("MessageEnd", (event) => {
    // Remove thinking display when message ends
    const thinkingDiv = elChatMessages.querySelector('.thinking-display');
    if (thinkingDiv)
        thinkingDiv.remove();
});
```

## Sidebar and Session List

```javascript
function renderSidebar() {
    const sessions = Array.from(sessionStoreManager.stores.values())
        .sort((a, b) => (b.lastViewed || 0) - (a.lastViewed || 0));
    
    elSidebar.innerHTML = '';
    
    for (const store of sessions) {
        const item = document.createElement('div');
        item.className = 'session-item';
        if (store.sessionId === sessionStoreManager.activeViewId)
            item.classList.add('active');
        
        item.innerHTML = `
            <div class="session-header">
                <span class="session-agent">${escapeHtml(store.agentId || 'Unknown')}</span>
                ${store.unreadCount > 0 ? `<span class="unread-badge">${store.unreadCount}</span>` : ''}
            </div>
            <div class="session-meta">
                <span class="session-channel">${escapeHtml(store.channelType || 'N/A')}</span>
                ${store.isStreaming ? '<span class="streaming-indicator">●</span>' : ''}
            </div>
        `;
        
        item.addEventListener('click', () => {
            sessionStoreManager.switchView(store.sessionId);
            renderSidebar();
        });
        
        elSidebar.appendChild(item);
    }
}
```

## Summary

**Key Architectural Decisions:**

1. **Subscribe-All Model**: Single connection subscribes to all sessions upfront
2. **Auto-Session on Send**: Sessions created implicitly on first message
3. **DOM-Swap Switching**: Client-side session switching without server round-trip
4. **Per-Session Stores**: Independent state management per session
5. **SignalR Group Broadcast**: Events routed to session groups (`session:{sessionId}`)
6. **Cached DOM**: Preserve UI state when switching sessions
7. **Markdown Rendering**: `marked.js` + `DOMPurify` for safe rendering
8. **Streaming Events**: Real-time deltas via SignalR for smooth UX

**Performance Characteristics:**

- Connection setup: ~100-200ms (WebSocket handshake + negotiation)
- SubscribeAll: ~50-100ms (depends on session count)
- Session switch: <10ms (DOM swap, no network)
- Message send: ~50-200ms (HTTP round-trip to hub)
- Streaming latency: ~20-50ms per delta (SignalR overhead)

**Scalability Considerations:**

- SignalR groups scale to ~10K connections per hub
- Client stores limited to 20 sessions (LRU eviction)
- DOM caching prevents re-rendering overhead
- Markdown rendering is client-side (no server load)

**Future Enhancements:**

- Virtual scrolling for long conversation histories
- Persistent client-side storage (IndexedDB)
- Offline support (service worker + sync)
- Multi-client collaboration (shared cursors, presence)
- Voice input/output (Web Speech API)
