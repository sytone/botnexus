---
id: feature-session-visibility
title: "Session Visibility Rules for Multi-Session UI"
type: feature
priority: high
status: draft
created: 2026-04-11
updated: 2026-04-11
author: jon
tags: [signalr, sessions, ui, gateway, multi-session, ddd]
depends_on: [feature-multi-session-connection, ddd-refactoring]
---

# Design Spec: Session Visibility Rules for Multi-Session UI

**Type**: Feature
**Priority**: High (prerequisite for multi-session UX)
**Status**: Draft
**Author**: Jon Bullen
**Depends on**:
- [Multi-Session Connection Model](../feature-multi-session-connection/architecture-proposal.md)
- [DDD Refactoring](../ddd-refactoring/design-spec.md) (Phase 1 — domain types, Phase 7.2 — Session/GatewaySession split)

## Problem

The multi-session architecture proposal defines *how* the client subscribes to multiple session groups simultaneously, but does not specify *which* sessions should be visible to a user. Without visibility rules:

- Users see every session ever created — including stale, expired, and superseded ones
- Channel conversations (Telegram, Slack) that were sealed and continued produce duplicate entries in the sidebar
- The session list grows unbounded with no pruning logic

This spec defines the server-side filtering logic that determines which sessions appear in the UI sidebar and are subscribed via `SubscribeAll`.

## Domain Types (Post-DDD Refactoring)

This spec builds on the domain types delivered by the [DDD refactoring](../ddd-refactoring/design-spec.md). Key types referenced throughout:

| Type | Location | Role |
|---|---|---|
| `Session` | `BotNexus.Domain.Gateway.Models` | Pure domain record — status, participants, history |
| `GatewaySession` | `BotNexus.Domain.Gateway.Models` | Infrastructure wrapper — thread-safe runtime, replay buffer |
| `SessionStatus` | `BotNexus.Domain.Primitives` | Smart enum: `Active`, `Suspended`, `Sealed` |
| `SessionType` | `BotNexus.Domain.Primitives` | Smart enum: `UserAgent`, `AgentSelf`, `AgentSubAgent`, `AgentAgent`, `Soul`, `Cron` |
| `ChannelKey` | `BotNexus.Domain.Primitives` | Value object — normalized on construction, case-insensitive equality |
| `AgentId` | `BotNexus.Domain.Primitives` | Value object wrapping string |
| `SessionId` | `BotNexus.Domain.Primitives` | Value object with typed factories (`Create`, `ForSubAgent`, `ForSoul`, etc.) |
| `ISessionStore` | `BotNexus.Gateway.Contracts` | Persistence interface — already has `ListAsync`, `ListByChannelAsync`, `GetExistenceAsync` |
| `ExistenceQuery` | `BotNexus.Gateway.Contracts` | Query DTO for filtering by date range, session type, limit |

## Design

### Session Visibility by Status

| `SessionStatus` | Visible? | Rationale |
|---|---|---|
| `Active` | Always | Agent instance is running or idle — user can interact |
| `Suspended` | Always | Resumable — user may reactivate |
| `Sealed` | Conditionally | History value, but superseded channel sessions are hidden from sidebar (still accessible via scrollback) |

**Status Value Object:** Uses `SessionStatus` smart enum with typed values: `SessionStatus.Active`, `SessionStatus.Suspended`, `SessionStatus.Sealed`.

### Session Visibility by Type

Not all `SessionType` values are user-facing. Internal session types are filtered from the sidebar:

| `SessionType` | Visible in sidebar? | Rationale |
|---|---|---|
| `UserAgent` | Yes | Primary user interaction sessions |
| `AgentSelf` | No | Agent's internal self-reflection — not user-relevant |
| `AgentSubAgent` | No | Worker sessions spawned by agents — transient, internal |
| `AgentAgent` | No | Agent-to-agent peer conversations — no user participant |
| `Soul` | No | Daily soul sessions (heartbeat, reflection) — internal lifecycle |
| `Cron` | No | Scheduled trigger sessions — internal |

Only `SessionType.UserAgent` sessions appear in the sidebar. All other types are internal and excluded from visibility regardless of status.

### Sealed Session Visibility (Channel Continuation)

A sealed `UserAgent` session is shown in the sidebar **unless** all of the following are true:

1. The session has a `ChannelKey` (`session.ChannelType != null`)
2. A **newer** session exists for the same `AgentId + ChannelKey` combination
3. The newer session has `SessionStatus.Active` or `SessionStatus.Suspended`

When these conditions are met, only the latest session for that channel thread is shown. The sealed predecessor is hidden from the sidebar but remains in the session store — accessible via REST and via scrollback.

**Rationale**: Channel conversations are logically continuous — a Telegram thread that was sealed and resumed is still "one conversation" from the user's perspective. Showing both the sealed and active sessions adds clutter without value.

**Scrollback access**: Sealed channel sessions are hidden from the *sidebar*, not from history. When a user scrolls up in the active session, the [Infinite Scrollback](../feature-infinite-scrollback/design-spec.md) feature transparently pages through older sealed sessions for the same `AgentId + ChannelKey` using the `GET /api/channels/{channelType}/agents/{agentId}/history` endpoint. This endpoint uses cursor-based pagination that automatically walks across session boundaries, powered by `ISessionStore.ListByChannelAsync()`. Session boundaries appear as divider lines (e.g., "Session started Jan 15 at 2:30 PM") so the user can see where one session ended and the next began. This means the sidebar shows *one* entry per channel thread, but the full conversation history — spanning all sealed predecessors — is available by scrolling.

**Non-channel sessions** (e.g., ad-hoc WebUI sessions with `ChannelType == null`) are always shown when sealed, since they represent distinct interactions with no continuation relationship.

### Visibility Query

A new method on `ISessionStore` encapsulates the filtering logic. It builds on the existing `ListAsync` and uses the domain value types:

```csharp
/// <summary>
/// Returns sessions visible to the given user, applying visibility rules:
/// - Only SessionType.UserAgent sessions are included
/// - Active and Suspended sessions are always included
/// - Sealed sessions are included unless superseded by a newer channel continuation
/// </summary>
Task<IReadOnlyList<SessionSummary>> GetVisibleSessionsAsync(
    string? userId = null,
    CancellationToken cancellationToken = default);
```

#### SessionSummary DTO

Lightweight projection — no history payload, suitable for sidebar rendering:

```csharp
public sealed record SessionSummary(
    SessionId SessionId,
    AgentId AgentId,
    ChannelKey? ChannelType,
    SessionStatus Status,
    SessionType SessionType,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

#### Implementation Logic (Pseudocode)

```
GetVisibleSessions(userId):
    all = sessionStore.ListAsync()

    // Step 1: Filter to user-facing session types only
    userSessions = all.Where(s => s.SessionType == SessionType.UserAgent)

    active    = userSessions.Where(s => s.Status == SessionStatus.Active)
    suspended = userSessions.Where(s => s.Status == SessionStatus.Suspended)
    sealed    = userSessions.Where(s => s.Status == SessionStatus.Sealed)

    // Step 2: Determine which channel keys have an active/suspended replacement
    activeChannelKeys = active.Concat(suspended)
                              .Where(s => s.ChannelType != null)
                              .Select(s => (s.AgentId, s.ChannelType.Value))
                              .ToHashSet()

    // Step 3: A sealed channel session is hidden if a replacement exists
    visibleSealed = sealed.Where(s =>
        s.ChannelType is null                                       // non-channel: always show
        || !activeChannelKeys.Contains((s.AgentId, s.ChannelType!.Value)))  // no active replacement

    return active + suspended + visibleSealed
           |> OrderByDescending(s => s.UpdatedAt)
```

Note: `ChannelKey` equality is built-in (normalized at construction), so the `HashSet` comparison works correctly without custom comparers.

### Integration with SubscribeAll

The `GatewayHub.SubscribeAll()` method (defined in the [multi-session architecture proposal](../feature-multi-session-connection/architecture-proposal.md)) calls `GetVisibleSessionsAsync` to determine which session groups to join:

```csharp
public async Task<SubscriptionResult> SubscribeAll()
{
    var sessions = await _sessionStore.GetVisibleSessionsAsync(
        Context.UserIdentifier, Context.ConnectionAborted);

    foreach (var session in sessions)
    {
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            $"session:{session.SessionId}",
            Context.ConnectionAborted);
    }

    return new SubscriptionResult(
        sessions.Select(s => new SessionInfo(
            s.SessionId.Value, s.AgentId.Value, (string?)s.ChannelKey,
            s.Status.Value, s.SessionType.Value, s.MessageCount,
            s.CreatedAt, s.UpdatedAt)).ToList());
}
```

**Simplified Connection Model:** The client calls `SubscribeAll()` **once** on connect. The server adds the connection to all visible session groups. All messages from all visible sessions are sent to the client; the client routes events to per-session stores based on `sessionId`. **No explicit session join/leave from the client.** Switching views is a local DOM operation — the connection remains in all groups.

### Client-Side Routing

The client receives the filtered session list and renders the sidebar. All event routing uses `sessionId` — no client-side filtering of visibility is needed.

```javascript
connection.on("SessionList", (sessions) => {
    for (const session of sessions) {
        storeManager.getOrCreate(session.sessionId, session.agentId);
    }
    renderSessionSidebar(sessions);  // only visible sessions are in this list
});
```

When an event arrives for a session that isn't the active view, the store accumulates it and shows an unread badge:

```javascript
connection.on("ContentDelta", (sessionId, content) => {
    const store = storeManager.get(sessionId);
    if (!store) return;  // session was pruned from visibility — ignore

    store.appendContent(content);
    if (sessionId === storeManager.activeViewId) {
        renderDelta(content);
    } else {
        store.unreadCount++;
        renderUnreadBadge(sessionId, store.unreadCount);
    }
});
```

### Dynamic Visibility Updates

When a session transitions state (e.g., sealed → a new active session is created for the same channel), the server broadcasts a `SessionVisibilityChanged` event so the client can update the sidebar without reconnecting:

```csharp
// Server-side: broadcast when visibility changes
// Triggered by session lifecycle events (seal, create, resume)
await _hubContext.Clients.All.SendAsync("SessionVisibilityChanged", new
{
    Added = newlyVisibleSessions,     // SessionSummary[] — sessions that should now appear
    Removed = hiddenSessionIds        // SessionId[] — sessions that should be removed from sidebar
});
```

**When this fires:**
- A `UserAgent` session is sealed AND a new `UserAgent` session is created for the same `AgentId + ChannelKey` → the sealed session moves to `Removed`, the new one to `Added`
- A new ad-hoc `UserAgent` session is created (no channel) → appears in `Added`
- A session's status changes in a way that affects visibility (e.g., `Suspended → Active`) → no visibility change, but status update event fires separately

```javascript
// Client-side: handle visibility changes
connection.on("SessionVisibilityChanged", ({ added, removed }) => {
    for (const sessionId of removed) {
        storeManager.remove(sessionId);
        removeSidebarEntry(sessionId);
    }
    for (const session of added) {
        storeManager.getOrCreate(session.sessionId, session.agentId);
        addSidebarEntry(session);
        connection.invoke("Subscribe", session.sessionId);
    }
});
```

## Edge Cases

### 1. Multiple sealed sessions for the same channel, no active replacement

All sealed sessions remain visible in the sidebar. The user sees the most recent sealed session as the entry point. Scrolling up in any of them pages through older sessions via the [Infinite Scrollback](../feature-infinite-scrollback/design-spec.md) endpoint (`GET /api/channels/{channelType}/agents/{agentId}/history`), which uses `ISessionStore.ListByChannelAsync()` to walk across session boundaries.

### 2. User explicitly navigates to a hidden sealed session (via URL or search)

The REST endpoint `GET /api/sessions/{sessionId}` still returns the session. The UI can render it in a read-only view without adding it to the sidebar subscription.

### 3. Session sealed while user is viewing it

The session remains visible in the sidebar (sealed sessions are shown unless superseded). The UI updates the status indicator to show it's read-only. If a new session is created for the same channel, a `SessionVisibilityChanged` event triggers the swap — the sealed session is removed from the sidebar, and the new active session is added. The user can still scroll up in the new session to see the sealed session's history.

### 4. Reconnection

On reconnect, the client calls `SubscribeAll` again. The server re-evaluates visibility rules and returns the current set. The client reconciles its local stores with the new list (add new, remove gone).

### 5. Soul and Cron sessions

`SessionType.Soul` and `SessionType.Cron` sessions are never visible in the sidebar — they are internal lifecycle sessions. They remain queryable via `ISessionStore.GetExistenceAsync()` with a `SessionType` filter for diagnostics or agent-facing tools, but they do not participate in session visibility.

### 6. Sub-agent sessions

`SessionType.AgentSubAgent` sessions are spawned by agents as disposable workers. They are not shown in the sidebar. Their results flow back to the parent session, which is the user-visible session. The parent session shows `SubAgentSpawned` / `SubAgentCompleted` events inline.

## Scope

### In Scope

- `GetVisibleSessionsAsync` method on `ISessionStore` — uses `SessionType`, `SessionStatus`, and `ChannelKey` value types
- `SessionSummary` DTO for lightweight sidebar projections
- `SessionType`-based filtering (only `UserAgent` sessions visible)
- Sealed-session channel-continuation pruning logic using `ChannelKey` equality
- `SessionVisibilityChanged` hub event
- Client-side sidebar rendering from filtered session list
- Integration with `SubscribeAll` from the multi-session proposal
- Integration with [Infinite Scrollback](../feature-infinite-scrollback/design-spec.md) for accessing sealed channel history via `ListByChannelAsync`

### Out of Scope

- Session archiving / retention policy (existing session lifecycle handles this)
- Per-user session permissions (future feature — currently all sessions are visible to all connected users)
- Search/filter UI for finding hidden sessions
- Session grouping by agent in the sidebar (UX concern, not visibility)
- Soul/Cron session diagnostics UI (internal sessions are queryable via `GetExistenceAsync` but not surfaced in the standard sidebar)

## Testing

### Unit Tests

| Test | Validates |
|---|---|
| `GetVisibleSessions_ExcludesNonUserAgentSessionTypes` | Soul, Cron, AgentSelf, AgentSubAgent, AgentAgent sessions are never returned |
| `GetVisibleSessions_IncludesAllActiveSessions` | Active `UserAgent` sessions always appear |
| `GetVisibleSessions_IncludesSuspendedSessions` | Suspended `UserAgent` sessions always appear |
| `GetVisibleSessions_IncludesSealedNonChannelSessions` | Sealed sessions with `ChannelType == null` are always shown |
| `GetVisibleSessions_HidesSealedWhenChannelHasActiveReplacement` | Sealed channel session hidden when same `AgentId + ChannelKey` has an Active session |
| `GetVisibleSessions_ShowsSealedWhenNoActiveReplacement` | Sealed channel session shown when no newer session exists |
| `GetVisibleSessions_MultipleSealed_NoActive_AllVisible` | Multiple sealed sessions for same channel are all shown when no active exists |
| `GetVisibleSessions_OrderedByUpdatedAtDescending` | Results sorted most-recent first |
| `GetVisibleSessions_ChannelKeyNormalization` | `ChannelKey.From("SignalR")` and `ChannelKey.From("signalr")` are treated as the same channel |
| `GetVisibleSessions_ReturnsSummaryWithoutHistory` | Returned `SessionSummary` includes `MessageCount` but no `History` payload |

### Integration Tests

| Test | Validates |
|---|---|
| `SubscribeAll_JoinsOnlyVisibleSessionGroups` | Client receives only `UserAgent` sessions and is added to correct groups |
| `SessionVisibilityChanged_FiredOnSeal` | When a session is sealed and a new one created for the same channel, the event fires with correct added/removed lists |
| `Reconnect_ReEvaluatesVisibility` | After reconnect, the client gets the updated visible session set |
| `ScrollbackLoadsSealedHistory` | Scrolling up in an active channel session loads messages from sealed predecessor sessions via `ListByChannelAsync` |
