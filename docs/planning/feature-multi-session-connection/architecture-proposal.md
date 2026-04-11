# Architecture Proposal — Multi-Session Connection Model

| Field        | Value                                      |
|--------------|--------------------------------------------|
| **Author**   | Leela (Lead/Architect)                     |
| **Requested by** | Jon Bullen                            |
| **Status**   | DRAFT — awaiting review                    |
| **Created**  | 2026-04-11                                 |
| **Scope**    | Gateway + WebUI SignalR connection model    |

---

## 1. Problem Statement

### The Current Model

The WebUI maintains a **single SignalR connection** and a **single active session** at any time. When the user switches between agents/sessions in the sidebar, the client:

1. Calls `LeaveSession(currentSessionId)` — removes the connection from the SignalR group
2. Calls `JoinSession(agentId, sessionId)` — adds the connection to a new group, creates/resumes the session server-side

This is a sequential async dance across two server round-trips, with the client in an undefined state between them.

```
User clicks Agent B
  │
  ├─► await LeaveSession("session-A")     ← round trip 1
  │     (connection removed from group A)
  │
  ├─► [WINDOW: no group membership]       ← events for BOTH sessions are lost here
  │
  ├─► await JoinSession("agent-b", null)  ← round trip 2
  │     (connection added to group B)
  │
  └─► Update UI state
```

### Specific Bugs This Architecture Produces

**Bug class 1: Stale event delivery.** Events arrive for a session the client already left. The client has a `isEventForCurrentSession()` guard (app.js:484) that drops them silently. This means **real data is lost** — if Agent A finishes a tool call while the user is viewing Agent B, that result is discarded.

**Bug class 2: Race conditions on rapid switching.** The client uses a `joinSessionVersion` counter (app.js:697) and a separate `timelineSwitchVersion` counter (app.js:1784) to detect when a newer switch supersedes an in-flight one. There are version checks after *every* `await` in the `openAgentTimeline` function (8 separate bail-out points at lines 1823, 1842, 1896, 1902, 1906). This is defensive code compensating for an architectural flaw.

**Bug class 3: The safety-net timer.** `openAgentTimeline` has an 8-second safety timer (app.js:1792) that force-clears the `sessionSwitchInProgress` flag if the function hangs due to slow REST or SignalR timeouts. A timer to fix hangs means the function's execution model is unpredictable.

**Bug class 4: State pollution across sessions.** Global mutable state (`currentSessionId`, `currentAgentId`, `isStreaming`, `activeToolCalls`, etc.) is shared across all sessions. The codebase migrated some of this to per-session state via `getSessionState()` (app.js:57), but the globals remain — marked DEPRECATED but still read. Every handler must check "is this event for *my* session?" before touching the DOM.

**Bug class 5: Reconnection re-join.** On reconnect, the client re-joins the current session (app.js:498, 667). But if events were in flight during the disconnect, they're lost. The replay buffer (`SessionReplayBuffer`) mitigates this, but only for the session that was active when the disconnect happened.

### Evidence from E2E Tests

The `SessionSwitchingE2ETests.cs` reveals how much testing effort goes into validating the current model works:

- `RapidSwitchAndSend_RoutesToLatestSelection` — tests rapid clicking between agents
- `SendDuringLoading_DoesNotMisrouteToPreviousSession` — tests message delivery during the switch gap
- `InboundEvents_AreIsolatedToOriginSession` — tests that events from a left session don't bleed into the current view

These tests are correct and valuable, but they're testing **compensating behavior** for an architecture that should never need it.

### Root Cause

The root cause is that **session switching is a server-side operation**. Changing which session the user is viewing requires two server round-trips and a state transition on the server (group membership). This couples a UI concern (which panel is visible) to a network operation (SignalR group management).

---

## 2. Proposed Architecture

### Core Principle

> **Session switching is a client-side operation. The server always delivers events for all sessions.**

The client subscribes to **all** sessions on connect. Switching is purely a DOM operation — show a different panel. Zero server calls. Zero race conditions. Zero version counters.

### 2.1 Gateway-Side: Session Pre-Warming

When the gateway starts (or when an agent is registered), it pre-loads sessions from the session store so they're ready before any client connects.

```
┌─────────────────────────────────────────────────────┐
│                   Gateway Startup                    │
│                                                      │
│  1. Load all agents from IAgentRegistry              │
│  2. For each agent:                                  │
│     a. ISessionStore.ListAsync(agentId)              │
│     b. Filter to Active + recent Expired sessions    │
│     c. Pre-load into memory cache                    │
│  3. Gateway is "warm" — ready for client connections │
│                                                      │
└─────────────────────────────────────────────────────┘
```

**New service: `ISessionWarmupService`**

```csharp
namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Pre-loads sessions on gateway startup so clients can subscribe immediately.
/// </summary>
public interface ISessionWarmupService : IHostedService
{
    /// <summary>
    /// Returns all sessions that are available for client subscription.
    /// Includes Active sessions and recently Expired sessions within the retention window.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns available sessions filtered by agent.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> GetAvailableSessionsAsync(
        string agentId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Lightweight summary for client subscription — no history payload.
/// </summary>
public sealed record SessionSummary(
    string SessionId,
    string AgentId,
    string? ChannelType,
    SessionStatus Status,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

### 2.2 Client-Side: Multi-Session Subscription

On connect, the client receives a manifest of all available sessions and subscribes to all of them. Events arrive tagged with `sessionId` and are routed to per-session message stores.

```
┌─────────────────────────────────────────────────────────┐
│                    Client Architecture                   │
│                                                          │
│  ┌──────────────┐    ┌──────────────────────────────┐   │
│  │  SignalR Hub  │───►│  Event Router                │   │
│  │  (1 connection│    │  route(event) {              │   │
│  │   all groups) │    │    store = stores[event.sid] │   │
│  └──────────────┘    │    store.append(event)        │   │
│                      │    if (sid == activeView)     │   │
│                      │      renderToDOM(event)       │   │
│                      │  }                            │   │
│                      └──────────────────────────────┘   │
│                                                          │
│  ┌──────────────────────────────────────────────────┐   │
│  │  Session Message Stores (Map<sessionId, Store>)   │   │
│  │                                                    │   │
│  │  store-aaa: [msg1, msg2, msg3, ...]  ← active     │   │
│  │  store-bbb: [msg1, msg2, ...]        ← background │   │
│  │  store-ccc: [msg1, ...]              ← background │   │
│  └──────────────────────────────────────────────────┘   │
│                                                          │
│  ┌──────────────────────────────────────────────────┐   │
│  │  View Controller                                   │   │
│  │                                                    │   │
│  │  switchView(sessionId) {                           │   │
│  │    activeView = sessionId            ← LOCAL ONLY  │   │
│  │    renderFromStore(stores[sessionId])              │   │
│  │  }                                                 │   │
│  └──────────────────────────────────────────────────┘   │
│                                                          │
└─────────────────────────────────────────────────────────┘
```

**No more `LeaveSession` / `JoinSession` on switch.** Switching is a DOM re-render from an in-memory store.

### 2.3 SignalR Group Model

The server adds the client's connection to **all** session groups on connect:

```
┌──────────┐         ┌──────────────┐
│  Client   │────────►│  GatewayHub  │
│ connects  │         │              │
└──────────┘         │  OnConnected: │
                      │   for each   │
                      │   session:   │
                      │     Groups   │
                      │      .Add()  │
                      └──────┬───────┘
                             │
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
        ┌──────────┐  ┌──────────┐  ┌──────────┐
        │session:A │  │session:B │  │session:C │
        │  group   │  │  group   │  │  group   │
        └──────────┘  └──────────┘  └──────────┘
```

**Events already carry `sessionId`** — the `SignalRChannelAdapter` already enriches events with `SessionId` (line 56). The client just needs to route on it instead of dropping non-current events.

### 2.4 Sequence Diagram — New Session Switch

```
  User                Client                 Server
   │                    │                       │
   │ click Agent B      │                       │
   │───────────────────►│                       │
   │                    │                       │
   │                    │ activeView = "B"      │
   │                    │ render(stores["B"])    │
   │                    │                       │
   │  ◄─── UI updated ─┘                       │
   │       (0ms, no server call)                │
   │                    │                       │
   │                    │  ◄── ContentDelta     │
   │                    │      {sid: "A", ...}  │
   │                    │                       │
   │                    │ stores["A"].append()  │
   │                    │ (badge Agent A: "●")  │
   │                    │                       │
```

Compare to current model: **2 round-trips + version checks + safety timer → 0 round-trips**.

---

## 3. Key Design Decisions

### D1: One Connection with Multi-Group vs. Multiple Connections

| Aspect | Single Connection, Multi-Group | Multiple Connections |
|--------|-------------------------------|---------------------|
| **WebSocket count** | 1 | N (one per session) |
| **Browser limits** | Not a concern | 6 per domain (HTTP/1.1), unlimited HTTP/2 |
| **Server memory** | 1 HubCallerContext per client | N HubCallerContexts per client |
| **Event ordering** | Guaranteed within connection | Per-connection only |
| **Complexity** | Low — SignalR groups do this natively | High — connection manager needed |
| **Reconnection** | 1 reconnect restores everything | N reconnects needed |
| **Selective unsubscribe** | Remove from group | Close connection |

**Recommendation: Single connection, multi-group.** This is what SignalR groups are designed for. Multiple connections add complexity with no meaningful benefit for our scale (tens of sessions, not thousands). Events already carry `sessionId` for routing.

### D2: Server-Push All Events vs. Client-Pull History + Push New

| Aspect | Push Everything | Pull History + Push New |
|--------|----------------|------------------------|
| **Initial load** | Server replays all history on connect | Client fetches history via REST, receives new events via SignalR |
| **Bandwidth** | Heavy on connect (all history for all sessions) | Light on connect (summaries only), lazy-load history |
| **Latency to first render** | Slow — must wait for all history | Fast — render empty shells, fill on demand |
| **Simplicity** | Simpler protocol | Slightly more complex but already exists (REST pagination) |

**Recommendation: Pull history + push new events (hybrid).** On connect, the client receives the session manifest (summaries only). History is loaded via the existing REST `/sessions/{id}` endpoint when the user actually views a session. New events flow in real-time via SignalR for all subscribed sessions. This is the existing pattern — we just stop *unsubscribing* from sessions.

### D3: Session Pre-Warming Scope

| Scope | Description | Memory | Startup Time |
|-------|-------------|--------|--------------|
| **All sessions ever** | Load everything from the store | Unbounded | Slow |
| **Active sessions only** | `Status == Active` | Bounded, small | Fast |
| **Active + recent** | Active + Expired within 24h | Bounded, reasonable | Fast |

**Recommendation: Active + recently active (configurable window).** The warmup service loads sessions with `Status == Active` plus sessions that expired within a configurable retention window (default: 24 hours). This covers the common case (resume where you left off) without loading the entire archive.

```csharp
public sealed class SessionWarmupOptions
{
    /// <summary>
    /// How far back to look for expired sessions to pre-warm.
    /// Default: 24 hours.
    /// </summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum number of sessions to pre-warm per agent.
    /// Default: 10.
    /// </summary>
    public int MaxSessionsPerAgent { get; set; } = 10;

    /// <summary>
    /// Whether to pre-warm sessions on gateway startup.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
```

### D4: Client-Side Memory Management

The client will hold message stores for all subscribed sessions. We need bounds.

| Strategy | Description |
|----------|-------------|
| **LRU eviction** | Keep the N most-recently-viewed sessions in memory. Evict the rest. |
| **Lazy loading** | Only load history when viewed. Keep streaming events in a bounded ring buffer. |
| **Tiered storage** | Hot (in DOM) → Warm (in JS memory) → Cold (fetch from server) |

**Recommendation: Tiered storage with LRU.** The existing `sessionState` Map with `SESSION_STATE_MAX = 20` is already an LRU cache. Extend this pattern:

```javascript
// Per-session message store
const sessionStores = new Map(); // sessionId → { messages: [], streamState: {}, lastViewed: Date }
const MAX_MEMORY_SESSIONS = 20;  // configurable

function getOrCreateStore(sessionId) {
    if (sessionStores.has(sessionId)) {
        const store = sessionStores.get(sessionId);
        sessionStores.delete(sessionId); // move to end (LRU)
        sessionStores.set(sessionId, store);
        return store;
    }
    const store = {
        messages: [],           // rendered history
        streamState: {},        // active streaming state (tools, thinking, etc.)
        historyLoaded: false,   // false = needs REST fetch on view
        lastViewed: null,
        unreadCount: 0,
    };
    sessionStores.set(sessionId, store);

    // Evict oldest if over limit
    if (sessionStores.size > MAX_MEMORY_SESSIONS) {
        const oldest = sessionStores.keys().next().value;
        sessionStores.delete(oldest);
    }
    return store;
}
```

When a session is evicted, its history is discarded. Viewing it again triggers a REST fetch. **Streaming state is never evicted** for sessions the server is still pushing events for — the unreadCount accumulates and the sidebar shows a badge.

### D5: Backward Compatibility

| Concern | Strategy |
|---------|----------|
| **Old clients (pre-multi-session)** | Continue to work. `JoinSession`/`LeaveSession` remain on the hub. Old clients call them; new clients don't. |
| **New clients + old gateway** | New clients detect server version from `Connected` event (`serverVersion` field). If the server doesn't support `SubscribeAll`, fall back to join/leave model. |
| **Mixed deployment** | Not a concern — single gateway, single WebUI. Deploy together. |

**Recommendation: Keep `JoinSession`/`LeaveSession` as deprecated methods.** Mark them `[Obsolete]` but don't remove them. New clients simply never call them. Remove in a future major version.

---

## 4. Interface Changes

### 4.1 Modified: `GatewayHub.cs`

```csharp
public sealed class GatewayHub : Hub
{
    // ... existing dependencies + new ones ...
    private readonly ISessionWarmupService _warmup;

    // NEW: Client subscribes to all available sessions on connect
    public async Task<SubscriptionResult> SubscribeAll()
    {
        var sessions = await _warmup.GetAvailableSessionsAsync(Context.ConnectionAborted);

        foreach (var session in sessions)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                GetSessionGroup(session.SessionId),
                Context.ConnectionAborted);
        }

        _logger.LogInformation(
            "Hub SubscribeAll: connection={ConnectionId} sessions={Count}",
            Context.ConnectionId, sessions.Count);

        return new SubscriptionResult(
            sessions.Select(s => new SessionInfo(
                s.SessionId, s.AgentId, s.ChannelType,
                s.Status.ToString(), s.MessageCount,
                s.CreatedAt, s.UpdatedAt)).ToList());
    }

    // NEW: Subscribe to a specific new session (created after initial SubscribeAll)
    public async Task<SessionInfo> Subscribe(string sessionId)
    {
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GetSessionGroup(sessionId),
            Context.ConnectionAborted);

        var session = await _sessions.GetAsync(sessionId, Context.ConnectionAborted);
        if (session is null)
            throw new HubException($"Session '{sessionId}' not found.");

        return new SessionInfo(
            session.SessionId, session.AgentId, session.ChannelType,
            session.Status.ToString(), session.MessageCount,
            session.CreatedAt, session.UpdatedAt);
    }

    // EXISTING: JoinSession stays for backward compat (marked obsolete)
    [Obsolete("Use SubscribeAll + Subscribe. Will be removed in v2.")]
    public async Task<object> JoinSession(string agentId, string? sessionId)
    { /* ... existing implementation unchanged ... */ }

    // EXISTING: LeaveSession stays for backward compat
    [Obsolete("Use SubscribeAll. Will be removed in v2.")]
    public Task LeaveSession(string sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GetSessionGroup(sessionId));

    // MODIFIED: SendMessage unchanged — it already takes agentId + sessionId
    public Task SendMessage(string agentId, string sessionId, string content)
    { /* ... unchanged ... */ }

    // MODIFIED: OnConnectedAsync — include available session count
    public override async Task OnConnectedAsync()
    {
        // ... existing logic ...

        var sessions = await _warmup.GetAvailableSessionsAsync(Context.ConnectionAborted);

        await Clients.Caller.SendAsync("Connected", new
        {
            connectionId = Context.ConnectionId,
            agents = _registry.GetAll().Select(a => new { a.AgentId, a.DisplayName }),
            serverVersion = typeof(GatewayHub).Assembly.GetName().Version?.ToString() ?? "dev",
            // NEW: signal multi-session support
            capabilities = new { multiSession = true },
            availableSessionCount = sessions.Count
        });

        await base.OnConnectedAsync();
    }
}

// New DTOs
public sealed record SubscriptionResult(IReadOnlyList<SessionInfo> Sessions);

public sealed record SessionInfo(
    string SessionId,
    string AgentId,
    string? ChannelType,
    string Status,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

### 4.2 Modified: `SignalRChannelAdapter.cs`

No structural changes needed. The adapter already:
- Sends to groups via `_hubContext.Clients.Group(GetSessionGroup(conversationId))`
- Enriches events with `SessionId` (line 56)

The only change is that **more clients will be in each group** (all clients, not just the one viewing that session). This is fine — SignalR handles this efficiently.

### 4.3 Modified: `GatewayOptions.cs`

```csharp
public sealed class GatewayOptions
{
    // ... existing options ...

    /// <summary>
    /// Options controlling session pre-warming and multi-session subscription behavior.
    /// </summary>
    public SessionWarmupOptions SessionWarmup { get; set; } = new();
}
```

### 4.4 New REST Endpoint: Session History (Already Exists)

The existing `GET /api/sessions/{sessionId}` endpoint already returns session history. No new REST endpoints needed. The client will continue to use this for lazy-loading history when a session tab is first viewed.

### 4.5 Client-Side Data Structures

```javascript
// ─── New: Session Store Manager ──────────────────────────
// Replaces: currentSessionId, joinSession(), LeaveSession(), isEventForCurrentSession()

class SessionStoreManager {
    #stores = new Map();      // sessionId → SessionStore
    #maxStores = 20;
    #activeViewId = null;     // which session is currently rendered in the DOM

    get activeViewId() { return this.#activeViewId; }

    subscribe(sessions) {
        // Called after SubscribeAll response
        for (const info of sessions) {
            this.#getOrCreate(info.sessionId, info);
        }
    }

    switchView(sessionId) {
        // ZERO server calls — this is the key design change
        this.#activeViewId = sessionId;
        const store = this.#stores.get(sessionId);
        if (!store) return;
        store.unreadCount = 0;
        store.lastViewed = new Date();

        if (!store.historyLoaded) {
            // Lazy-load history via REST
            this.#loadHistory(sessionId);
        } else {
            renderFromStore(store);
        }
    }

    routeEvent(sessionId, event) {
        const store = this.#getOrCreate(sessionId);
        store.appendEvent(event);

        if (sessionId === this.#activeViewId) {
            // Render immediately to DOM
            renderEventToDOM(event);
        } else {
            // Background session — increment badge
            store.unreadCount++;
            updateSidebarBadge(sessionId, store.unreadCount);
        }
    }

    createSession(agentId) {
        // Called when user sends first message to a new agent
        // Server creates session via SendMessage → returns sessionId
        // Client then calls hub.Subscribe(sessionId) to join the new group
    }

    async #loadHistory(sessionId) {
        const data = await fetchJson(`/sessions/${encodeURIComponent(sessionId)}`);
        const store = this.#stores.get(sessionId);
        if (store && data?.history) {
            store.messages = data.history;
            store.historyLoaded = true;
            if (sessionId === this.#activeViewId) {
                renderFromStore(store);
            }
        }
    }

    #getOrCreate(sessionId, info = {}) {
        if (this.#stores.has(sessionId)) {
            // LRU: move to end
            const s = this.#stores.get(sessionId);
            this.#stores.delete(sessionId);
            this.#stores.set(sessionId, s);
            return s;
        }

        const store = new SessionStore(sessionId, info);
        this.#stores.set(sessionId, store);

        if (this.#stores.size > this.#maxStores) {
            // Evict oldest that isn't actively streaming
            for (const [id, s] of this.#stores) {
                if (!s.isStreaming && id !== this.#activeViewId) {
                    this.#stores.delete(id);
                    break;
                }
            }
        }
        return store;
    }
}

class SessionStore {
    constructor(sessionId, info = {}) {
        this.sessionId = sessionId;
        this.agentId = info.agentId || null;
        this.channelType = info.channelType || null;
        this.messages = [];
        this.streamState = {
            isStreaming: false,
            activeMessageId: null,
            activeToolCalls: {},
            thinkingBuffer: '',
            toolCallDepth: 0,
            toolStartTimes: {},
        };
        this.historyLoaded = false;
        this.lastViewed = null;
        this.unreadCount = 0;
    }

    get isStreaming() { return this.streamState.isStreaming; }

    appendEvent(event) {
        // Merge event into local state — same logic as current handlers
        // but scoped to THIS store, not globals
    }
}
```

### 4.6 What Gets Removed (Eventually)

| Current Code | Replaced By |
|-------------|-------------|
| `currentSessionId` global | `SessionStoreManager.activeViewId` |
| `joinSession()` function | `SessionStoreManager.switchView()` |
| `LeaveSession` hub call on switch | Nothing — removed entirely |
| `isEventForCurrentSession()` guard | `SessionStoreManager.routeEvent()` — routes, never drops |
| `joinSessionVersion` counter | Not needed — no async server calls on switch |
| `timelineSwitchVersion` counter | Not needed — switch is synchronous |
| `sessionSwitchInProgress` flag | Not needed — switch is instant |
| 8-second safety timer | Not needed — nothing to time out |
| Reconnect re-join logic (app.js:498, 667) | `SubscribeAll()` called on reconnect — subscribes to everything |

---

## 5. Migration Path

### Phase 1: Foundation (Ship First)

**Goal:** Add `SubscribeAll` to the hub and multi-group subscription. Client still uses join/leave for now.

**Changes:**
- [ ] Implement `ISessionWarmupService` + `SessionWarmupOptions`
- [ ] Register as `IHostedService` in DI
- [ ] Add `SubscribeAll()` and `Subscribe()` to `GatewayHub`
- [ ] Add `capabilities` field to `Connected` event payload
- [ ] Add `SessionSummary` and `SessionInfo` DTOs
- [ ] Unit tests for warmup service
- [ ] Integration tests for `SubscribeAll` hub method

**Client is unchanged.** This is purely additive server-side work. No risk to existing behavior.

### Phase 2: Client Multi-Session Model

**Goal:** Client subscribes to all sessions and routes events locally. No more join/leave on switch.

**Changes:**
- [ ] Implement `SessionStoreManager` and `SessionStore` classes in `app.js`
- [ ] On `Connected` event: call `SubscribeAll()` instead of deferred `joinSession()`
- [ ] Replace `isEventForCurrentSession()` drop-guard with `routeEvent()` dispatch
- [ ] Replace `openAgentTimeline` switch logic with `switchView()` (synchronous)
- [ ] Add sidebar unread badges for background sessions with new events
- [ ] Remove `joinSessionVersion`, `timelineSwitchVersion`, `sessionSwitchInProgress`, safety timer
- [ ] Update `sendMessage()` to use `activeViewId` from store manager
- [ ] Handle new session creation: `SendMessage` → server creates session → `Subscribe(newSessionId)`
- [ ] Update reconnect handler: call `SubscribeAll()` on reconnect instead of `joinSession()`
- [ ] Update all E2E tests

**E2E Test Impact:**
- `SessionSwitchingE2ETests` — rewrite to verify instant switching (no `WaitForInvocationCount` delays)
- `ConnectionLifecycleE2ETests` — update reconnect test to verify multi-group re-subscription
- All other E2E tests — update `OpenAgentTimelineAsync` helper to not wait for JoinSession

### Phase 3: Full Vision

**Goal:** Polished UX. Tab-style session management. Cross-session notifications.

**Changes:**
- [ ] Tabbed session UI (switch sessions within the same view)
- [ ] Visual indicators: "Agent A is working..." badge while viewing Agent B
- [ ] Smart pre-loading: when user hovers over a session in sidebar, start fetching history
- [ ] Session auto-subscribe: when a new session is created (e.g., incoming Telegram message), automatically add the client to the group and notify
- [ ] `Unsubscribe(sessionId)` for explicit opt-out of noisy sessions
- [ ] Per-session notification preferences
- [ ] Remove deprecated `JoinSession`/`LeaveSession` hub methods

### Backward Compatibility Strategy

```
Phase 1:  Server supports BOTH models (SubscribeAll + JoinSession)
Phase 2:  Client uses SubscribeAll; JoinSession still works but unused
Phase 3:  JoinSession/LeaveSession removed after deprecation period
```

New clients detect multi-session support via `capabilities.multiSession` in the `Connected` event. Old clients (if any) ignore the field and continue using `JoinSession`.

---

## 6. Risk Register

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|------------|
| R1 | **Memory pressure from holding all sessions in client** | Medium | Low | LRU eviction with `MAX_MEMORY_SESSIONS = 20`. Evicted sessions refetch history on view. Streaming state for active sessions is tiny (a few KB). |
| R2 | **Bandwidth from receiving events for all sessions** | Low | Low | Events are already small (streaming deltas). In practice, only 1-2 sessions are actively generating output at any time. Idle sessions produce zero traffic. |
| R3 | **SignalR group explosion (many clients × many sessions)** | Low | Medium | SignalR groups are cheap (dictionary lookups, no network overhead per group). Even 100 clients × 50 sessions = 5,000 memberships — trivial for in-process SignalR. Only a concern with Redis backplane at extreme scale. |
| R4 | **Event ordering across sessions** | Low | None | Events within a session are ordered (same group, same connection). Cross-session ordering is irrelevant — sessions are independent. No change from current behavior. |
| R5 | **New session creation race** | Medium | Medium | When a user sends a first message to a new agent, the server creates the session and returns the sessionId. The client must `Subscribe(sessionId)` before the server starts pushing events. Mitigation: server buffers initial events until the client confirms subscription, OR the client calls `Subscribe` synchronously before `SendMessage`. |
| R6 | **Session store size on gateway startup** | Low | Low | Configurable `MaxSessionsPerAgent` cap and `RetentionWindow` in `SessionWarmupOptions`. Default caps are conservative (10 per agent, 24h window). |
| R7 | **E2E test rewrite scope** | High | Medium | Phase 2 requires updating ~10 E2E test files. The test infrastructure (`WebUiE2ETestHost`, `PlaywrightFixture`) needs updated helpers that don't depend on join/leave timing. Plan dedicated sprint time for this. |
| R8 | **Interaction with reconnect replay buffer** | Low | Medium | The `SessionReplayBuffer` in `GatewaySession` was designed for single-session replay. With multi-session subscription, reconnect needs to replay events for ALL sessions since disconnect. Each session's buffer is independent, so this should work — but needs explicit testing. |

---

## 7. Work Breakdown

### Phase 1 — Server Foundation

| Task | Owner | Est. | Dependencies |
|------|-------|------|-------------|
| `ISessionWarmupService` interface + `SessionWarmupOptions` config | **Farnsworth** | 1d | None |
| `SessionWarmupService` implementation (IHostedService) | **Farnsworth** | 2d | Interface |
| `SessionSummary` / `SessionInfo` DTOs | **Farnsworth** | 0.5d | None |
| `SubscribeAll()` + `Subscribe()` hub methods | **Farnsworth** | 1d | Warmup service |
| `capabilities` field in `Connected` event | **Farnsworth** | 0.5d | None |
| Unit tests for warmup service | **Hermes** | 1d | Implementation |
| Integration tests for new hub methods | **Hermes** | 1d | Hub methods |
| Update `GatewayOptions` with `SessionWarmup` section | **Farnsworth** | 0.5d | Config model |

**Phase 1 total: ~7.5 days**

### Phase 2 — Client Multi-Session Model

| Task | Owner | Est. | Dependencies |
|------|-------|------|-------------|
| `SessionStoreManager` + `SessionStore` JS classes | **Bender** | 2d | Phase 1 |
| Refactor `initSignalR()` — replace event handlers to use store routing | **Bender** | 2d | Store manager |
| Remove join/leave on switch — implement `switchView()` | **Bender** | 1d | Store manager |
| Sidebar unread badges for background sessions | **Fry** | 1d | Store manager |
| New session creation flow (first message → Subscribe) | **Bender** | 1d | Hub Subscribe method |
| Reconnect handler — call `SubscribeAll()` on reconnect | **Bender** | 0.5d | Store manager |
| Remove deprecated globals, version counters, safety timer | **Bender** | 1d | All client work |
| Update E2E test helpers (`WebUiE2ETestHost`) | **Hermes** | 1d | Client changes |
| Rewrite `SessionSwitchingE2ETests` | **Hermes** | 1d | Updated helpers |
| Update remaining E2E tests (connection, streaming, tools) | **Hermes** | 2d | Updated helpers |

**Phase 2 total: ~12.5 days**

### Phase 3 — Polish

| Task | Owner | Est. | Dependencies |
|------|-------|------|-------------|
| Tabbed session UI | **Fry** | 3d | Phase 2 |
| "Agent working" sidebar indicators | **Fry** | 1d | Store manager |
| Smart history pre-loading (hover-to-fetch) | **Bender** | 1d | Store manager |
| Auto-subscribe for new sessions (server push) | **Farnsworth** | 1d | Phase 2 |
| Remove deprecated `JoinSession`/`LeaveSession` | **Farnsworth** | 0.5d | Phase 3 complete |
| Final E2E pass for new UI features | **Hermes** | 1d | All Phase 3 |

**Phase 3 total: ~7.5 days**

---

## Appendix A: Current vs. Proposed — Side by Side

| Aspect | Current | Proposed |
|--------|---------|----------|
| Connection model | 1 connection, 1 group at a time | 1 connection, N groups simultaneously |
| Session switch | `LeaveSession` → `JoinSession` (2 RTTs) | `switchView()` (0 RTTs) |
| Event routing | Drop events not for `currentSessionId` | Route all events to per-session stores |
| State management | Global mutable variables | Per-session `SessionStore` instances |
| Reconnection | Re-join current session only | `SubscribeAll()` — re-join all sessions |
| Race condition surface | 8+ bail-out points, 2 version counters, safety timer | None — switch is synchronous |
| Background session awareness | None — events dropped | Unread badges, streaming indicators |
| Data loss on switch | Events during switch gap are lost | Zero data loss — all events buffered |

## Appendix B: Decision Log

| Decision | Options Considered | Choice | Rationale |
|----------|-------------------|--------|-----------|
| D1 | Single conn multi-group / Multiple conns | Single connection | Native SignalR pattern; simpler; better reconnection |
| D2 | Push all / Pull + Push | Pull history + push new | Already implemented; avoids bandwidth spike on connect |
| D3 | All sessions / Active only / Active + recent | Active + recent (24h) | Covers resume case without unbounded memory |
| D4 | Unlimited / Fixed LRU / Tiered | LRU with MAX=20 | Extends existing `sessionState` pattern |
| D5 | Breaking change / Backward compat | Backward compatible | `JoinSession` stays deprecated; clean removal in v2 |
