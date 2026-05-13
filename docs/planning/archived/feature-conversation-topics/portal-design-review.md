# Portal Conversation-First UI — Design Review

**Author:** Leela (Lead/Architect)  
**Date:** 2026-04-28  
**Issue:** #37  
**Branch:** `feat/37-portal-conversation-ui`

## Summary

The portal should switch from **session-first** to **conversation-first** without introducing a parallel state model.

**Decision:** keep `AgentSessionManager` and `AgentSessionState` as the primary client-side state types, but make them **conversation-aware**. Do **not** create a separate top-level `ConversationManager` + `ConversationState` tree for v1.

Why:
- `ChatPanel.razor` already binds to one per-agent state object and should stay that way.
- SignalR streaming is still session-scoped; the existing manager already routes by `sessionId`.
- Conversations are loaded via REST, but live streaming still lands in the active session inside the active conversation.
- A second parallel state model would force Fry to reconcile session events into conversation state twice.

So the contract is:
- **Sidebar unit:** conversation
- **Live stream routing unit:** session
- **Chat panel binding unit:** `AgentSessionState`
- **History payload:** conversation history entries with boundary markers between sessions

---

## 1. Service changes

## 1.1 `AgentSessionState` grows conversation-awareness

Keep one `AgentSessionState` per agent. Add conversation collections + active selection to it.

### New types

```csharp
public sealed class ConversationListItemState
{
    public required string ConversationId { get; init; }
    public string Title { get; set; } = "New conversation";
    public bool IsDefault { get; set; }
    public string Status { get; set; } = "Active";
    public string? ActiveSessionId { get; set; }
    public int UnreadCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // UI-only state
    public bool HistoryLoaded { get; set; }
    public bool IsLoadingHistory { get; set; }
}

public sealed record ConversationHistoryItem(
    string Kind,
    string SessionId,
    DateTimeOffset Timestamp)
{
    public string? Role { get; init; }
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public string? Reason { get; init; }

    public bool IsBoundary => Kind == "boundary";
}
```

### Add these properties to `AgentSessionState`

```csharp
public Dictionary<string, ConversationListItemState> Conversations { get; } = new();
public string? ActiveConversationId { get; set; }
public bool ConversationsLoaded { get; set; }
public bool IsLoadingConversations { get; set; }
public string? ActiveConversationTitle =>
    ActiveConversationId is not null && Conversations.TryGetValue(ActiveConversationId, out var c)
        ? c.Title
        : null;
```

### Existing properties that stay

These remain and keep their current meaning:
- `SessionId`
- `SessionType`
- `Messages`
- `IsStreaming`
- `CurrentStreamBuffer`
- `ThinkingBuffer`
- `ActiveToolCalls`
- `UnreadCount`
- `SubAgents`

### Meaning changes

- `SessionId` becomes **the currently active live session for the selected conversation**, not “the one session for this agent”.
- `Messages` becomes **the rendered timeline for the selected conversation**, including session-boundary markers.
- `UnreadCount` stays agent-level for the dropdown badge.
- Conversation unread state lives on `ConversationListItemState.UnreadCount`.

## 1.2 `ChatMessage` must support boundary entries

Do not invent a second list just for dividers. Let the message timeline render both normal messages and session boundaries.

Add to `ChatMessage`:

```csharp
public string Kind { get; init; } = "message"; // "message" | "boundary"
public string? BoundaryLabel { get; init; }
public string? BoundarySessionId { get; init; }
```

Helper:

```csharp
public bool IsBoundary => Kind == "boundary";
```

This keeps `ChatPanel` simple: one ordered list, one renderer.

## 1.3 `AgentSessionManager` new responsibilities

### New public methods

```csharp
public Task LoadConversationsAsync(string agentId);
public Task SelectConversationAsync(string agentId, string conversationId);
public Task<string?> CreateConversationAsync(string agentId, string? title = null, bool select = true);
public Task RefreshConversationsAsync(string agentId);
public void MarkConversationRead(string agentId, string conversationId);
```

### Exact behavior

#### `LoadConversationsAsync(agentId)`
- `GET /api/conversations?agentId={agentId}`
- Populate `state.Conversations`
- If `state.ActiveConversationId` is null:
  - select default conversation if present
  - otherwise select most recently updated conversation
- Set `ConversationsLoaded = true`
- Do **not** load history yet unless this is the active/visible agent

#### `SelectConversationAsync(agentId, conversationId)`
- Set `state.ActiveConversationId = conversationId`
- Copy selected conversation’s `ActiveSessionId` into `state.SessionId`
- Clear conversation unread count
- Call `LoadConversationHistoryAsync(agentId, conversationId)` if not loaded
- Fire `OnStateChanged`

#### `CreateConversationAsync(agentId, title, select)`
- `POST /api/conversations`
- Request body:
  ```json
  { "agentId": "...", "title": "..." }
  ```
- Add returned conversation into `state.Conversations`
- If `select == true`, call `SelectConversationAsync(...)`
- Return created `conversationId`

#### `RefreshConversationsAsync(agentId)`
- Re-fetch list from REST
- Preserve `UnreadCount`, `HistoryLoaded`, `IsLoadingHistory` for any existing matching conversation ids
- Preserve `ActiveConversationId` if still present

#### `LoadConversationHistoryAsync(agentId, conversationId)`
Private helper. Calls:
- `GET /api/conversations/{conversationId}/history?limit=200`

Maps response entries into `state.Messages`:
- `kind == "message"` → normal `ChatMessage`
- `kind == "boundary"` → `ChatMessage` with `Kind = "boundary"` and label text

Also:
- set selected conversation `HistoryLoaded = true`
- set `state.SessionId = selectedConversation.ActiveSessionId` after load

## 1.4 Unread rules

### Conversation unread
When a streamed assistant message ends:
- resolve `state` by `sessionId` as today
- find the conversation whose `ActiveSessionId == evt.SessionId`
- if that conversation is **not** `state.ActiveConversationId`, increment `ConversationListItemState.UnreadCount`

### Agent unread
Keep existing behavior:
- if `state.AgentId != ActiveAgentId`, increment `state.UnreadCount`

### Mark read
When a conversation becomes active:
- set conversation unread to 0
- if that agent is the active agent, recompute `state.UnreadCount` as sum of conversation unread counts

## 1.5 No new top-level `ConversationState`

**Explicit decision:** `ChatPanel.razor` continues to take `AgentSessionState`.

Reason:
- the panel already needs streaming buffers, tool calls, connection status, and sub-agent info
- all of that already lives on `AgentSessionState`
- adding a second wrapper would only re-expose the same data through another object

---

## 2. DTOs

## 2.1 File location

Create a new file:

`src/extensions/BotNexus.Extensions.Channels.SignalR.BlazorClient/Services/ConversationContracts.cs`

Do **not** add these to `HubContracts.cs`.

Reason: these are REST DTOs, not hub DTOs.

## 2.2 Client DTO contract

```csharp
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

public sealed record ConversationSummaryDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("activeSessionId")] string? ActiveSessionId,
    [property: JsonPropertyName("bindingCount")] int BindingCount,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed record CreateConversationRequestDto(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string? Title);

public sealed record ConversationResponseDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("activeSessionId")] string? ActiveSessionId,
    [property: JsonPropertyName("bindings")] IReadOnlyList<ConversationBindingDto> Bindings,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed record ConversationBindingDto(
    [property: JsonPropertyName("bindingId")] string BindingId,
    [property: JsonPropertyName("channelType")] string ChannelType,
    [property: JsonPropertyName("channelAddress")] string ChannelAddress,
    [property: JsonPropertyName("threadId")] string? ThreadId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("threadingMode")] string ThreadingMode,
    [property: JsonPropertyName("displayPrefix")] string? DisplayPrefix,
    [property: JsonPropertyName("boundAt")] DateTimeOffset BoundAt);

public sealed record ConversationHistoryResponseDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("entries")] IReadOnlyList<ConversationHistoryEntryDto> Entries);

public sealed class ConversationHistoryEntryDto
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
```

## 2.3 DTO notes

Portal only needs:
- conversation list summaries
- create response
- full history entries

It does **not** need binding mutation DTOs for issue #37 implementation.

---

## 3. Sidebar contract

## 3.1 Structure decision

The existing sidebar uses:
- agent dropdown
- per-agent session list under selected agent

For v1, keep the **agent dropdown**. Replace the **session list** under the selected agent with a **conversation list**.

Do not redesign agent selection in this issue.

## 3.2 Razor pseudocode contract

```razor
@if (Manager.ActiveAgentId is not null &&
     Manager.Sessions.TryGetValue(Manager.ActiveAgentId, out var activeState))
{
    <div class="agent-conversation-list">
        <div class="agent-conversation-list-header">
            <span class="agent-conversation-list-title">Conversations</span>
            <button class="conversation-new-btn"
                    @onclick="() => CreateConversationAndClose(activeState.AgentId)"
                    title="Start a new conversation">
                <span class="conversation-new-btn-icon">+</span>
                <span class="conversation-new-btn-label">New</span>
            </button>
        </div>

        @if (activeState.IsLoadingConversations)
        {
            <div class="conversation-list-loading">Loading conversations…</div>
        }
        else if (activeState.Conversations.Count == 0)
        {
            <div class="conversation-list-empty">No conversations yet.</div>
        }
        else
        {
            @foreach (var conversation in activeState.Conversations.Values
                .OrderByDescending(c => c.IsDefault)
                .ThenByDescending(c => c.UpdatedAt))
            {
                <button class="conversation-list-item @(conversation.ConversationId == activeState.ActiveConversationId ? "active" : "")"
                        @onclick="() => SelectConversationAndClose(activeState.AgentId, conversation.ConversationId)">
                    <span class="conversation-list-item-main">
                        <span class="conversation-list-item-title">@conversation.Title</span>

                        @if (conversation.IsDefault)
                        {
                            <span class="conversation-default-badge">Default</span>
                        }
                    </span>

                    <span class="conversation-list-item-meta">
                        @if (conversation.UnreadCount > 0)
                        {
                            <span class="conversation-unread-dot"
                                  aria-label="Unread messages"></span>
                        }

                        <span class="conversation-updated-at">
                            @FormatConversationTimestamp(conversation.UpdatedAt)
                        </span>
                    </span>
                </button>
            }
        }
    </div>
}
```

## 3.3 Required CSS hooks

- `.agent-conversation-list`
- `.agent-conversation-list-header`
- `.agent-conversation-list-title`
- `.conversation-new-btn`
- `.conversation-list-loading`
- `.conversation-list-empty`
- `.conversation-list-item`
- `.conversation-list-item.active`
- `.conversation-list-item-main`
- `.conversation-list-item-title`
- `.conversation-default-badge`
- `.conversation-list-item-meta`
- `.conversation-unread-dot`
- `.conversation-updated-at`

## 3.4 UI rules

- **Active highlight:** entire row gets `.active`
- **Default badge:** text badge to right of title
- **Unread dot:** small dot, not a numeric pill, for v1
- **New button:** always visible in header
- **Ordering:** default first, then most recently updated

---

## 4. Chat panel contract

## 4.1 Binding decision

`ChatPanel.razor` continues to accept:

```csharp
[Parameter, EditorRequired]
public AgentSessionState State { get; set; } = default!;
```

Do **not** replace this with `ConversationState`.

## 4.2 Header changes

Current header shows agent name. Update it to show agent + conversation.

### Header contract

```razor
<header class="chat-header">
    <div class="chat-header-left">
        <div class="chat-title-stack">
            <h3>@State.DisplayName</h3>
            @if (State.ActiveConversationId is not null)
            {
                <div class="conversation-title-row">
                    <span class="conversation-title">@State.ActiveConversationTitle</span>
                    @if (State.Conversations.TryGetValue(State.ActiveConversationId, out var activeConversation) && activeConversation.IsDefault)
                    {
                        <span class="conversation-default-badge">Default</span>
                    }
                </div>
            }
            <span class="agent-id-label">@State.AgentId</span>
        </div>

        @if (State.IsStreaming)
        {
            <span class="streaming-badge">Streaming…</span>
        }
    </div>

    ...existing actions...
</header>
```

## 4.3 Rendering history

`State.Messages` becomes the single ordered timeline for the selected conversation.

### New rendering branch

Inside the `foreach (var msg in State.Messages)` loop, add this branch first:

```razor
@if (msg.IsBoundary)
{
    <div class="session-boundary" role="separator" aria-label="Session boundary">
        <span class="session-boundary-line"></span>
        <span class="session-boundary-label">@msg.BoundaryLabel</span>
        <span class="session-boundary-line"></span>
    </div>
}
else if (msg.Role == "System")
{
    ...
}
```

## 4.4 Sending messages

No behavioral change in panel event handlers. They still call:
- `Manager.SendMessageAsync(State.AgentId, text)`
- `Manager.SteerAsync(...)`
- `Manager.FollowUpAsync(...)`

But `AgentSessionManager.SendMessageAsync` must send against the selected conversation context:
- if selected conversation has `ActiveSessionId`, continue into it if hub API allows
- otherwise first send creates a new session and the manager updates the selected conversation’s `ActiveSessionId` after refresh

If conversation/session linking is not returned by SignalR send result, Fry should refresh conversations after first send for that agent.

## 4.5 Empty-state text

When no conversation is selected:

```razor
<div class="chat-empty-state">
    Select a conversation or start a new one.
</div>
```

Do not auto-create a new conversation on panel render.

---

## 5. Session boundary divider contract

## 5.1 Exact markup

```razor
<div class="session-boundary" role="separator" aria-label="Session boundary">
    <span class="session-boundary-line"></span>
    <span class="session-boundary-label">
        Session · @FormatBoundaryTimestamp(msg.Timestamp) · @msg.BoundarySessionId
    </span>
    <span class="session-boundary-line"></span>
</div>
```

## 5.2 Exact label format

```text
Session · Apr 27 14:32 · s_abc123
```

Formatting rule:
- `MMM d HH:mm` in local time
- use raw session id as returned by API

## 5.3 Mapping rule

When mapping `ConversationHistoryEntryDto` with `kind == "boundary"`:

```csharp
var label = $"Session · {entry.Timestamp.ToLocalTime():MMM d HH:mm} · {entry.SessionId}";
```

Then create:

```csharp
new ChatMessage("System", string.Empty, entry.Timestamp)
{
    Kind = "boundary",
    BoundaryLabel = label,
    BoundarySessionId = entry.SessionId
}
```

## 5.4 CSS class contract

Required selectors:
- `.session-boundary`
- `.session-boundary-line`
- `.session-boundary-label`

Behavior:
- horizontal rule effect using flex lines on both sides
- centered label
- muted text style
- enough vertical margin to visually separate sessions

---

## 6. Wave breakdown

## Wave 1 — Fry (Blazor/C#)

### Scope
1. Add `ConversationContracts.cs`
2. Extend `AgentSessionState` with conversation list + active conversation fields
3. Extend `ChatMessage` with boundary support
4. Add `AgentSessionManager` methods:
   - `LoadConversationsAsync`
   - `SelectConversationAsync`
   - `CreateConversationAsync`
   - `RefreshConversationsAsync`
5. Replace sidebar session list with conversation list markup
6. Update `ChatPanel.razor` header to show active conversation title
7. Update `ChatPanel.razor` message loop to render session boundaries
8. Wire unread counts per conversation
9. Load conversations when agent becomes active

### Out of scope for Fry
- final visual polish
- spacing/color tuning
- animation/micro-interaction choices

## Wave 2 — Amy (CSS/visual)

### Scope
1. Style conversation list rows
2. Style active highlight
3. Style default badge
4. Style unread dot
5. Style "+ New" button
6. Style session boundary divider
7. Refine header conversation title row
8. Responsive/mobile layout cleanup in sidebar

### Constraints for Amy
- Amy should not rename Fry’s CSS hooks
- Amy should not change data flow or markup shape unless a11y requires a minimal adjustment

---

## 7. Risks and guidance

## Risk 1 — first send may not immediately bind conversation → session
If the send pipeline does not return conversation linkage, Fry should refresh `/api/conversations?agentId=` after first message send.

## Risk 2 — conversation unread resolution from session id
Unread mapping assumes the active live session belongs to one conversation. That matches current gateway contract and is acceptable for v1.

## Risk 3 — active conversation removed or archived
If refresh no longer contains the selected conversation:
- fall back to default conversation
- otherwise first available conversation
- otherwise clear `ActiveConversationId` and empty the panel

---

## 8. Explicit decisions for implementation

1. **Use `AgentSessionState` as the panel state.** No separate `ConversationState` wrapper.
2. **Create `ConversationContracts.cs`.** Do not mix REST DTOs into `HubContracts.cs`.
3. **Replace the sidebar session list with a conversation list under the selected agent.**
4. **Render boundary markers as `ChatMessage` entries in the same timeline list.**
5. **Keep SignalR session routing as-is.** REST provides list/history; SignalR continues to stream by session.

---

## 9. Jon input needed

Only one thing needs Jon’s confirmation before implementation starts:

### Should first-message conversation linking be explicit in the live send flow?
Current API surface clearly supports conversation history/listing, but the Blazor client contract is easiest if the first send into a selected conversation reliably updates that conversation’s `ActiveSessionId` immediately.

If the current gateway already guarantees that via existing routing, no change needed.
If not, Fry can still ship v1 by refreshing the conversation list after first send.

That is not a blocker, just the only place where server behavior needs a quick confirmation.

---

## Final recommendation

Proceed with Fry + Amy split exactly as above.

This keeps the change bounded:
- no duplicated client state model
- no panel rewrite
- no hub contract churn
- conversation-first UX in the sidebar and timeline
- session detail preserved where it matters: inside conversation history
