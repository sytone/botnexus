# Design Spec — Infinite Scrollback History

| Field            | Value                                           |
|------------------|-------------------------------------------------|
| **Author**       | Leela (Lead/Architect)                          |
| **Requested by** | Jon Bullen                                      |
| **Status**       | DRAFT — awaiting review (updated for DDD)       |
| **Created**      | 2025-07-24                                      |
| **Updated**      | 2026-04-12                                      |
| **Scope**        | Gateway API + WebUI chat history loading        |
| **DDD Types**    | Session, SessionId, AgentId, ChannelKey, SessionStatus |

---

## 1. Overview

### What's Broken

The current "Load earlier messages" button in `app.js` (`loadEarlierMessages`, line 2172) **wipes the entire chat DOM** (`elChatMessages.innerHTML = ''` at line 2203) and re-renders everything from scratch. This destroys the user's scroll position, removes event listeners, and makes the chat appear to lose messages. The "Load older sessions" button (`loadOlderSessions`, line 2130) appends older sessions inline but uses the same destructive pattern.

Additionally, the current API is **offset-based** (`GET /sessions/{sessionId}/history?offset=N&limit=M`), which is single-session scoped. The client must manually orchestrate cross-session loading by fetching session lists and iterating — a responsibility that belongs server-side.

### Target Experience

Smooth, infinite scrollback across sessions. The user scrolls up; older messages appear automatically. Session boundaries are shown as divider lines. The scroll position never jumps. The client doesn't need to know when one session ends and another begins — the API handles cross-session pagination transparently.

---

## 2. UX Spec

### Initial Load

1. Client calls the history endpoint with no cursor → returns newest messages (last 50)
2. Messages render in chronological order, viewport scrolled to bottom
3. If the session has more history, a sentinel element sits above the first message

### Scroll-Up Loading

1. An `IntersectionObserver` watches a sentinel `<div>` at the top of the chat container
2. When the sentinel enters the viewport → fire fetch with the current `nextCursor`
3. A spinner/skeleton appears at the top while loading
4. Fetched messages are **prepended** to the DOM
5. `scrollTop` is adjusted by the height of the inserted content — no visible jump

### Session Boundaries

When the API response includes `sessionBoundaries`, the client inserts a divider element at the indicated position:

```
─────── Session started Jan 15, 2025 at 2:30 PM ───────
```

After exhausting the first session's history, the next batch seamlessly includes messages from the previous session for the same channel, preceded by a boundary marker.

### End State

When `hasMore: false` is returned, the sentinel is replaced with:

```
─────── Beginning of conversation history ───────
```

### Loading Indicator

A subtle spinner or "Loading older messages..." text appears at the top of the chat while a fetch is in-flight. Only one fetch runs at a time (see §4).

---

## 3. API Spec

### Endpoint

```
GET /api/channels/{channelKey}/agents/{agentId}/history
```

This is a **new** endpoint on a new `ChannelHistoryController`. It replaces the per-session `GET /api/sessions/{sessionId}/history` for scrollback purposes (that endpoint remains for direct session access).

**Path Parameters:**

- `channelKey`: `ChannelKey` value object from BotNexus.Domain.Primitives (normalized, case-insensitive). The channel for this scrollback thread (e.g., "telegram-group-123", "slack-channel-acme").
- `agentId`: `AgentId` value object from BotNexus.Domain.Primitives. The agent whose sessions to scroll through.

### Query Parameters

| Parameter   | Type   | Required | Default | Description                                          |
|-------------|--------|----------|---------|------------------------------------------------------|
| `cursor`    | string | No       | —       | Opaque pagination cursor. Omit for newest messages.  |
| `limit`     | int    | No       | 50      | Number of messages to return (max 200).              |

`direction` is always "older" (scrollback) — not a parameter, just the default behavior.

### Cursor Format

Opaque to the client. Internal structure:

```
{sessionId}:{messageIndex}
```

Example: `sess_abc123:42` means "start before message index 42 in session `sess_abc123`." When `messageIndex` reaches 0, the API automatically locates the previous session for the same `agentId + channelKey` pair (using `ChannelKey` value object equality, which is normalized and case-insensitive) and continues from its last message.

### Response

```json
{
  "messages": [
    {
      "id": "msg_001",
      "sessionId": "sess_abc123",
      "role": "user",
      "content": "Hello",
      "timestamp": "2025-07-24T10:30:00Z",
      "metadata": {}
    }
  ],
  "nextCursor": "sess_abc123:0",
  "hasMore": true,
  "sessionBoundaries": [
    {
      "insertBeforeIndex": 12,
      "sessionId": "sess_prev456",
      "startedAt": "2025-07-23T14:00:00Z"
    }
  ]
}
```

- **`messages`** — Ordered oldest-first within the batch (so the client can prepend the array in order)
- **`nextCursor`** — Pass this as `cursor` on the next request. `null` when no more history
- **`hasMore`** — `false` when the entire channel history is exhausted
- **`sessionBoundaries`** — Positions where the client should insert session dividers. `insertBeforeIndex` is relative to the `messages` array in this response

### Cross-Session Logic (Server-Side)

1. Resolve all `Session` records (BotNexus.Domain.Sessions.Session) for `(AgentId, ChannelKey)` ordered by `CreatedAt DESC` (newest first)
2. Parse cursor to find starting session + message index
3. Read messages backwards from that point
4. If the current session is exhausted and `limit` not yet met, move to the previous session and continue filling
5. Record any session transitions as `sessionBoundaries`
6. Sessions with 0 messages are skipped automatically

**Session Status Filtering:**

- Include `Session` records with `Status == SessionStatus.Active` or `SessionStatus.Sealed` (SessionStatus smart enum from BotNexus.Domain.Primitives)
- Skip `Status == SessionStatus.Suspended` (resumable, but not history) and `Status == SessionStatus.Expired` (retention-pruned)

### ISessionStore Changes

Add one method to `ISessionStore` (BotNexus.Gateway.Contracts):

```csharp
/// <summary>
/// Lists sessions for a specific agent filtered by channel key,
/// ordered by CreatedAt descending (newest first).
/// Uses ChannelKey value object equality (normalized, case-insensitive).
/// </summary>
Task<IReadOnlyList<Session>> ListByChannelAsync(
    AgentId agentId,
    ChannelKey channelKey,
    CancellationToken cancellationToken = default);
```

The existing `GetHistorySnapshot(offset, limit)` on `Session` (BotNexus.Domain.Sessions.Session) is sufficient for reading message pages within a session.

---

## 4. Client Implementation

### Intersection Observer

```javascript
const sentinel = document.createElement('div');
sentinel.className = 'history-sentinel';
chatContainer.prepend(sentinel);

const observer = new IntersectionObserver(entries => {
    if (entries[0].isIntersecting && !isFetching && nextCursor !== null) {
        fetchOlderMessages();
    }
}, { root: chatContainer, rootMargin: '200px 0px 0px 0px' });

observer.observe(sentinel);
```

The `rootMargin` triggers the fetch 200px before the sentinel is actually visible — preloading for smooth UX.

### Prepend Without Scroll Jump

```javascript
async function fetchOlderMessages() {
    isFetching = true;
    showTopSpinner();

    const data = await fetchJson(
        `/api/channels/${channelKey}/agents/${agentId}/history?cursor=${nextCursor}&limit=50`
    );

    const scrollHeightBefore = chatContainer.scrollHeight;

    // Insert session dividers and messages
    prependBatch(data.messages, data.sessionBoundaries);

    // Restore scroll position
    chatContainer.scrollTop += chatContainer.scrollHeight - scrollHeightBefore;

    nextCursor = data.nextCursor;
    hasMore = data.hasMore;
    hideTopSpinner();
    isFetching = false;

    if (!hasMore) {
        observer.disconnect();
        showEndOfHistory();
    }
}
```

### Fetch Discipline

- **Single in-flight request:** The `isFetching` flag prevents concurrent fetches
- **Debounce:** The IntersectionObserver naturally debounces (fires once when sentinel enters viewport)
- **Rapid scroll:** If the user scrolls past the sentinel very fast, only one fetch fires. The sentinel repositions after prepend, so it triggers again when needed

### SessionStore Integration

Fetched pages are cached in the existing `SessionStore` (from the multi-session model) keyed by cursor. If the user scrolls down and back up, cached pages are served without re-fetching.

---

## 5. Edge Cases

| Scenario | Behavior |
|----------|----------|
| **New messages while scrolled up** | Don't auto-scroll. Show a floating "↓ New messages" button anchored to bottom of chat. Clicking it scrolls to bottom and dismisses. |
| **Session with 0 messages** | API skips it and loads from the previous session. The client never sees it. |
| **Deleted/archived sessions** | API treats them as end-of-history. `hasMore: false`. Sessions with `Status == Expired` are excluded from history walks. |
| **Rapid scrolling** | Single in-flight guard prevents duplicate fetches. Observer re-fires after each prepend completes. |
| **Channel switch mid-fetch** | The fetch callback checks if the active view still matches `(channelKey, agentId)`. If not, the response is discarded. |
| **Reconnect after disconnect** | On SignalR reconnect, the client does NOT re-fetch history. Existing DOM is preserved. Only live events resume. |
| **Very large sessions (10k+ messages)** | The cursor-based model handles this naturally — no offset drift, no N+1 queries. |

---

## 6. Work Breakdown

| Task | Owner | Description |
|------|-------|-------------|
| `ListByChannelAsync` on `ISessionStore` | Farnsworth | Add method + implement in `FileSessionStore` and `InMemorySessionStore` |
| `ChannelHistoryController` | Farnsworth | New controller with cursor parsing, cross-session walk, response assembly |
| Integration tests for cross-session pagination | Hermes | Cursor continuity, boundary markers, empty-session skipping, end-of-history |
| Remove `loadEarlierMessages` / `loadOlderSessions` | Bender | Delete the broken click-based loading code |
| IntersectionObserver + `fetchOlderMessages` | Bender | Sentinel, prepend logic, scroll preservation |
| Session divider rendering at boundary points | Fry | Insert dividers using `sessionBoundaries` from API response |
| "New messages" floating button | Fry | Detect new messages while scrolled up, show/dismiss button |
| Cache layer in SessionStore | Bender | Cache fetched pages by cursor key, invalidate on channel switch |
| E2E tests for scrollback UX | Hermes | Scroll triggers fetch, no scroll jump, session dividers render, end-of-history state |

### Sequencing

1. **Farnsworth** delivers API + `ListByChannelAsync` first (client work depends on it)
2. **Bender** builds observer + fetch loop against the new endpoint
3. **Fry** adds dividers + new-message indicator in parallel with Bender
4. **Hermes** writes integration + E2E tests throughout

---

## 7. Migration

The existing `GET /sessions/{sessionId}/history` endpoint is **not removed** — it's still used for direct session inspection (admin, debugging). The WebUI chat loading code switches entirely to the new channel-scoped endpoint. The old `loadEarlierMessages` and `loadOlderSessions` functions are deleted.
