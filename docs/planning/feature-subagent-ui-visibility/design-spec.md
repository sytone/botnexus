---
id: feature-subagent-ui-visibility
title: Sub-Agent Session Visibility in WebUI
type: feature
priority: medium
status: ready
created: 2026-04-10
updated: 2026-04-15
author: nova
depends_on: []
tags: [webui, subagent, session, ux]
ddd_types: [Session, SessionId, SessionType, SessionStatus, AgentId]
---

# Sub-Agent Session Visibility in WebUI

## Summary

Sub-agent sessions are invisible in the WebUI. The backend persists them and the API returns them, but the sidebar JS groups sessions by `agentId` against registered agents — sub-agents have dynamic agentIds (e.g., `nova--subagent--general--f8c254da...`) that don't match any registered agent, so they silently vanish.

**User expectation:** See sub-agent sessions in the sidebar, click into them for a read-only view of the conversation, and seal them when done to hide clutter.

## Current State (as of 2026-04-15)

### What works
- Sub-agent spawning works (bug-subagent-spawn-path fixed — `::` → `--` in AgentId)
- Sub-agent sessions are persisted to `ISessionStore` via `DefaultAgentSupervisor.GetOrCreateAsync`
- `/api/sessions` returns sub-agent sessions with `sessionType: "agent-subagent"`
- `SessionId.IsSubAgent` returns true for sub-agent session IDs (checks `::subagent::`)
- `SessionStatus.Sealed` already exists in the domain model
- `/api/sessions/{sessionId}/subagents` endpoint exists for listing sub-agents of a session (returns `SubAgentInfo` with name, task, model, archetype, status, timing, turns)
- Sub-agent lifecycle events (spawned/completed/failed/killed) are published via `IActivityBroadcaster` (`SubAgentSpawned`, `SubAgentCompleted`, `SubAgentFailed`, `SubAgentKilled`)
- SignalR subscribe-all model means clients are already subscribed to all session messages
- **Sub-agent panel already built** (`chat.js` lines 1312–1448) — in-session panel shows sub-agents for the active parent session with status icons (🟢/✅/❌/🛑/⏱), name, model, task preview, elapsed time, turns, kill button, and collapsible result summaries. This panel is scoped to the _current_ session — it doesn't provide sidebar-level visibility or cross-session browsing.
- **Sidebar collapse infrastructure** — `storage.js` has `getCollapsedSections()`/`toggleSectionCollapsed()` and `getCollapsedAgents()`/`toggleAgentCollapsed()` for persisted collapse state. `ui.js` `initSectionToggles()` restores section collapse on page load.
- **`sessionsInitialLoad` fixed** — `sidebar.js` now correctly sets the flag to `false` after first render, preventing DOM flash on subsequent `loadSessions()` calls.

### What's missing
- **WebUI sidebar** — `loadSessions()` in `sidebar.js` groups sessions by `agentId` against registered agents. Sub-agent agentIds (e.g., `nova--subagent--general--f8c254da`) don't match any registered agent, so they silently vanish from the sidebar
- **Read-only conversation view** — No concept of a session being view-only in the chat canvas. Input bar is always active. The existing sub-agent panel shows status/results but not the full conversation
- **Seal action** — No UI to seal a session. `SessionStatus.Sealed` exists but no endpoint (`PATCH /seal`) or UI to set it. Suspend/resume endpoints exist as a pattern to follow
- **`Session.ParentSessionId`** — Doesn't exist on the domain model. Parent-child relationship is only tracked in-memory in `DefaultSubAgentManager._parentChildren`. Lost on gateway restart. However, parent can be derived from SessionId format (`{parentSessionId}::subagent::{uniqueId}`)
- **Sidebar nesting** — No concept of sessions nested under other sessions

## Requirements

### Must Have (P0)

1. **Sub-agent sessions visible in sidebar** — appear nested under their parent agent's group, visually distinct from channel sessions
2. **Read-only conversation view** — click a sub-agent session to see its full conversation history in the main canvas. Input bar hidden or disabled
3. **Seal action** — button/action to seal a completed sub-agent session, hiding it from the sidebar
4. **Status indicator** — show running (🟢), completed (✅), failed (❌), killed (🔴), timed out (⏱️)
5. **Real-time streaming** — as a sub-agent works, messages appear live in the canvas (already works via SignalR subscribe-all, just need the UI to display it)

### Should Have (P1)

6. **Sub-agent name** — show the `name` from spawn parameters as the session label (falls back to archetype + short ID)
7. **Duration** — show elapsed time for running sub-agents, total time for completed ones
8. **Collapse/expand** — completed sub-agents collapsed by default, expandable to browse
9. **Badge on parent** — notification badge on parent session when a sub-agent completes

### Nice to Have (P2)

10. **Kill from UI** — button to kill a running sub-agent (endpoint already exists: `DELETE /api/sessions/{sessionId}/subagents/{subAgentId}`)
11. **Seal all** — bulk seal all completed sub-agent sessions
12. **Filter toggle** — show/hide sealed sessions

## Technical Analysis

### Session Identity

Sub-agent sessions have a predictable structure:
- **SessionId**: `{parentSessionId}::subagent::{uniqueId}` — parent relationship is encoded in the ID
- **AgentId**: `{parentAgentId}--subagent--{archetype}--{uniqueId}` — parent agent is encoded in the ID
- **SessionType**: `agent-subagent`

This means we can derive the parent relationship from the session data itself — no schema change needed for P0.

### Parsing parent from SessionId

```javascript
function parseSubAgentSession(session) {
    if (session.sessionType !== 'agent-subagent') return null;
    
    const subagentMarker = '::subagent::';
    const idx = session.sessionId.indexOf(subagentMarker);
    if (idx < 0) return null;
    
    const parentSessionId = session.sessionId.substring(0, idx);
    const subAgentUniqueId = session.sessionId.substring(idx + subagentMarker.length);
    
    // Parse parent agent from agentId: "parent--subagent--archetype--uniqueId"
    const agentParts = session.agentId.split('--subagent--');
    const parentAgentId = agentParts.length > 1 ? agentParts[0] : null;
    
    return { parentSessionId, parentAgentId, subAgentUniqueId };
}
```

### Seal endpoint

Need a new endpoint or extend the existing suspend/resume pattern:

```
PATCH /api/sessions/{sessionId}/seal
```

Sets `session.Status = SessionStatus.Sealed` and `session.UpdatedAt = now`. Similar to the existing `Suspend` endpoint.

### Sidebar rendering changes

In `loadSessions()` (`sidebar.js`):

1. After building `latestByChannel` for each agent, also collect sub-agent sessions where the parent agentId matches
2. Render sub-agent sessions as indented items under the parent agent group
3. Filter out sessions where `status === 'sealed'` (unless show-sealed toggle is on)
4. For sub-agent items: show name/archetype, status icon, duration, and a seal button

### Chat canvas changes

When opening a sub-agent session:
1. Load history via existing `/api/sessions/{sessionId}/history` endpoint
2. Hide or disable the message input bar
3. Show a banner: "Read-only — sub-agent session" with status and seal button
4. If the sub-agent is still running, stream new messages live via SignalR

## Implementation Plan

### Phase 1: Sidebar visibility + read-only view (P0)

**Backend:**
- Add `PATCH /api/sessions/{sessionId}/seal` endpoint to `SessionsController` (follow existing suspend/resume pattern)
- Ensure `/api/sessions` returns sub-agent sessions (it already does — verify no filtering)

**Frontend (`sidebar.js`):**
- In `loadSessions()`: separate sub-agent sessions (`sessionType === 'agent-subagent'`) from channel sessions
- Group sub-agent sessions under their parent agent using parsed `agentId` (`agentParts[0]` from `split('--subagent--')`)
- Render as indented items with status icons (reuse `SUBAGENT_STATUS_MAP` icons from existing `chat.js` panel, or define sidebar-specific set)
- Filter out `sealed` sessions by default
- On click: call `openSubAgentSession(sessionId)` instead of `openAgentTimeline`
- Leverage existing `toggleAgentCollapsed()`/`getCollapsedAgents()` from `storage.js` for persisting sub-agent section collapse state

**Frontend (`chat.js`):**
- New `openSubAgentSession(sessionId)` function:
  - Loads session history via existing `/api/sessions/{sessionId}/history` endpoint
  - Renders messages in the canvas (reuse existing message rendering)
  - Hides input bar, shows read-only banner with seal button
  - Subscribes to live updates if session is active (via existing SignalR subscription)
- **Note:** The existing sub-agent panel (`renderSubAgentPanel()`, `killSubAgent()`, etc.) stays as-is — it shows sub-agents within the active parent session. The new `openSubAgentSession()` provides the full conversation view when a sub-agent is clicked in the sidebar.

**Frontend (`styles.css`):**
- Indented sub-agent items in sidebar
- Status icons and seal button styling
- Read-only banner styling

### Phase 2: Polish (P1)

- Sub-agent name from metadata (existing panel already shows `sa.name || sa.subAgentId` — reuse for sidebar)
- Duration display (existing panel already computes elapsed time — extract as shared helper)
- Collapse/expand for completed sub-agents (use `getCollapsedAgents()` from storage.js)
- Parent session badge on sub-agent completion

### Phase 3: Actions (P2)

- Kill button in read-only banner for running sub-agents (existing `killSubAgent()` in chat.js can be reused)
- Bulk seal
- Sealed session filter toggle

## Edge Cases

1. **Sub-agent spawned but gateway restarted before completion** — session persists but `DefaultSubAgentManager` in-memory state is lost. Sub-agent session shows as "active" with no further updates. Seal button still works to clean up
2. **Multiple sub-agents from same parent** — all shown as siblings under the parent agent
3. **Deeply nested** (sub-agent spawns sub-agent) — parse chain works recursively. Cap UI depth at 2 levels; deeper ones show flat
4. **Rapid completion** — sub-agent finishes before user opens it. History is fully persisted, browsable
5. **Cron-spawned sub-agents** — parent session is a cron session. Group under the cron's agent, not a separate group
6. **Session from before fix** — old sub-agent sessions with `::` in agentId may exist. Parse gracefully, show as orphaned

## Testing Plan

1. Spawn a sub-agent → verify it appears in sidebar under parent agent
2. Click into running sub-agent → verify messages stream live, input bar hidden
3. Wait for completion → verify status updates to ✅
4. Click seal → verify session disappears from sidebar
5. Spawn multiple sub-agents → verify all appear as siblings
6. Refresh page → verify sub-agent sessions still visible (persisted)
7. Open on two browsers → verify both see sub-agent sessions

## Files to Change

| File | Change |
|------|--------|
| `SessionsController.cs` | Add `Seal` endpoint (follow suspend/resume pattern) |
| `sidebar.js` | Sub-agent grouping, rendering, seal action (leverage existing collapse helpers) |
| `chat.js` | `openSubAgentSession()` with RO mode (existing sub-agent panel stays) |
| `styles.css` | Sub-agent sidebar items, RO banner |
| `api.js` | Seal API call helper |
| `storage.js` | Sub-agent collapse state helpers if needed (section collapse already built) |
