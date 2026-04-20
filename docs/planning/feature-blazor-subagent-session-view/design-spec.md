---
id: feature-blazor-subagent-session-view
title: "Feature: Read-Only Sub-Agent Session Viewing in Blazor UI"
type: feature
priority: medium
status: done
created: 2026-07-27
author: nova
depends_on: [feature-subagent-ui-visibility]
tags: [blazor, subagent, session, ux, transparency]
ddd_types: [Session, SessionId, SessionType, SessionStatus, AgentId]
---

# Read-Only Sub-Agent Session Viewing in Blazor UI

## Summary

Sub-agent sessions appear in the Blazor UI's session list but are not clickable. Users who spawned work via their primary agent have no way to inspect what the sub-agent actually did, how it reasoned, or what tools it called. This limits transparency and debuggability.

**Predecessor:** [feature-subagent-ui-visibility](../archived/feature-subagent-ui-visibility/design-spec.md) delivered sidebar visibility for sub-agent sessions. This spec completes the story by making those sessions browsable in a read-only conversation view.

## Problem

- Sub-agent sessions are visible in the Blazor sidebar (delivered by predecessor) but clicking them does nothing
- Users cannot view the full conversation, tool calls, or reasoning of sub-agent sessions
- The only way to understand what a sub-agent did is to ask the parent agent to summarize — lossy and indirect
- Active sub-agent sessions cannot be observed in real-time while they work

## Requirements

### Must Have (P0)

1. **Clickable sub-agent sessions** — sub-agent sessions in the sidebar open when clicked
2. **Read-only conversation view** — full message history displayed: user/assistant messages, tool calls, tool responses, streaming content
3. **Input disabled** — compose box hidden or disabled; user cannot send messages to a sub-agent session
4. **Read-only indicator** — visual banner or badge making it clear this is a read-only sub-agent session (e.g., "Read-only — sub-agent session" banner at top of canvas)
5. **Works for all states** — both active (in-progress) and completed/expired/sealed sub-agent sessions are viewable
6. **Real-time streaming** — if the sub-agent is still running, new messages stream in live (same SignalR streaming as normal chat, just no input)

### Should Have (P1)

7. **Parent-child visual grouping** — sub-agent sessions grouped under or indented beneath their parent session in the sidebar, making the relationship clear
8. **Status indicator in view** — running/completed/failed/killed/timed-out status visible in the read-only banner
9. **Auto-collapse completed** — completed sub-agent sessions collapsed by default in sidebar to reduce clutter
10. **Tool call rendering** — tool calls and responses rendered with the same fidelity as in normal chat (collapsible tool blocks, syntax highlighting)

### Nice to Have (P2)

11. **Muted/distinct styling** — sub-agent session view uses subtly different styling (muted palette, border accent) to reinforce read-only nature
12. **Breadcrumb navigation** — "← Back to parent session" link in the read-only banner for quick navigation
13. **Hide after time** — auto-hide sealed sub-agent sessions from sidebar after configurable duration
14. **Deep-link support** — URL route for sub-agent sessions so they can be shared/bookmarked

## Technical Analysis

### Blazor Migration Context

The predecessor spec targeted the vanilla JS WebUI (`sidebar.js`, `chat.js`). The current UI is a **Blazor WASM SPA** — implementation will use Razor components, not raw JS. Key differences:

- Session list is a Blazor component, not `sidebar.js`
- Chat canvas is a Blazor component with SignalR integration
- State management uses Blazor services/cascading parameters, not `storage.js`

### Session Identity (unchanged from predecessor)

Sub-agent sessions have predictable structure:
- **SessionId**: `{parentSessionId}::subagent::{uniqueId}`
- **AgentId**: `{parentAgentId}--subagent--{archetype}--{uniqueId}`
- **SessionType**: `agent-subagent`

Parent relationship is derivable from the session ID — no schema changes needed.

### Key Components

| Component | Change |
|-----------|--------|
| **Session list component** | Make sub-agent session items clickable; route to read-only view |
| **Chat component** | Add read-only mode: hide input, show banner, disable send |
| **Session state service** | Track whether current session is read-only (derived from `sessionType`) |
| **SignalR subscription** | Already subscribes to all sessions — no change needed for streaming |

### Read-Only Mode

The chat component needs a `IsReadOnly` flag derived from the session:

```csharp
bool IsReadOnly => CurrentSession?.SessionType == "agent-subagent";
```

When `IsReadOnly`:
- Hide or disable the compose/input area
- Show a read-only banner at the top of the message area
- Continue rendering incoming messages via SignalR (streaming still works)
- All message rendering (text, tool calls, tool responses) works identically to normal view

### Sidebar Grouping (P1)

Sub-agent sessions grouped under their parent using the parsed `SessionId`:

```csharp
string? GetParentSessionId(Session session)
{
    if (session.SessionType != "agent-subagent") return null;
    var marker = "::subagent::";
    var idx = session.SessionId.IndexOf(marker);
    return idx >= 0 ? session.SessionId[..idx] : null;
}
```

Render as indented child items under the parent session, with status icon and collapse toggle.

## Implementation Plan

### Phase 1: Clickable + Read-Only View (P0)

1. **Session list component** — wire click handler on sub-agent session items to navigate to chat view
2. **Chat component** — add `IsReadOnly` mode:
   - Conditionally hide input/compose area
   - Add `<ReadOnlyBanner>` component (status, session name, "read-only" label)
3. **History loading** — use existing `/api/sessions/{sessionId}/history` endpoint (already works for sub-agent sessions)
4. **Streaming** — verify SignalR message subscription works when viewing a sub-agent session (expect it does — subscribe-all model)

### Phase 2: Visual Polish (P1)

5. **Sidebar nesting** — group sub-agent sessions under parent, indented with status icons
6. **Auto-collapse** — completed sub-agents collapsed by default
7. **Tool call fidelity** — ensure tool call/response blocks render correctly in read-only view (likely already works)

### Phase 3: Nice-to-Have (P2)

8. Muted styling for read-only sessions
9. Breadcrumb "← Back to parent" navigation
10. Deep-link URL routing

## Edge Cases

1. **Sub-agent still running** — view shows live streaming; banner shows "Running" status with spinner
2. **Sub-agent completed before user opens** — full history loads from API, status shows completed
3. **Gateway restart while sub-agent active** — session persists but may show stale "active" status; read-only view still works for persisted history
4. **Nested sub-agents** (sub-agent spawns sub-agent) — parse chain works recursively; cap sidebar nesting at 2 levels
5. **No messages yet** — sub-agent just spawned; show empty canvas with "Waiting for sub-agent to start..." placeholder
6. **Sealed sessions** — still viewable if user navigates to them; sidebar may filter them based on existing seal/filter logic

## Testing Plan

1. Spawn a sub-agent → verify session is clickable in sidebar
2. Click running sub-agent → verify messages stream live, input hidden, banner shown
3. Click completed sub-agent → verify full history loads, correct status displayed
4. Verify compose box is not rendered / not functional in read-only view
5. Verify tool calls and tool responses render correctly
6. Spawn multiple sub-agents → verify all are clickable and viewable independently
7. Refresh page → verify sub-agent session view still works after reload
8. Verify read-only banner displays correct status (running/completed/failed/killed)

## Documentation

User guide for observing sub-agent sessions published at: `docs/webui/sub-agent-sessions.md`

Covers:
- How to spawn a sub-agent and locate it in the sidebar
- How to click into read-only session view
- Real-time streaming behavior for active sub-agents
- Session states and status indicators
- Tool call rendering and interaction
- Navigation between parent and sub-agent sessions
- Limitations and troubleshooting

## Related

- [feature-subagent-ui-visibility](../archived/feature-subagent-ui-visibility/design-spec.md) — predecessor: made sub-agent sessions visible in sidebar
- [bug-subagent-realtime-updates](../archived/bug-subagent-realtime-updates/design-spec.md) — fixed SignalR bridge for sub-agent real-time updates
- [feature-session-visibility](../archived/feature-session-visibility/design-spec.md) — session visibility rules
