---
id: feature-subagent-ui-visibility-review
title: "Design Review: Sub-Agent Session Visibility in WebUI"
type: design-review
status: complete
created: 2026-04-16
author: leela
spec: feature-subagent-ui-visibility
reviewers: [leela, farnsworth, bender, fry]
decision: approved-with-conditions
---

# Design Review — Sub-Agent Session Visibility in WebUI

**Ceremony**: Design Review  
**Spec**: `feature-subagent-ui-visibility`  
**Facilitator**: Leela (Lead Architect)  
**Scope**: P0 only — Phase 1 (sidebar visibility + read-only view + seal)

---

## 1. Design Review Findings

### 1.1 Spec Validation Against Codebase

| Spec Claim | Verified | Notes |
|---|---|---|
| Sub-agent sessions returned by `/api/sessions` | ✅ | `sessionType: "agent-subagent"` present in responses |
| Sessions vanish because sidebar iterates registered agents | ✅ | `sidebar.js:111` — `for (const agent of agents)` only renders groups for registered agents; sub-agent agentIds have no matching registration |
| `SessionStatus.Sealed` exists | ✅ | `SessionStatus.cs:18` — enum value present |
| Suspend/Resume endpoints exist as pattern | ✅ | `SessionsController.cs:266-303` — `PATCH /suspend` and `PATCH /resume` |
| Seal endpoint does NOT exist yet | ✅ | Confirmed absent from `SessionsController.cs` |
| `SessionId.IsSubAgent` checks `::subagent::` | ✅ | `SessionId.cs:101` |
| AgentId format `{parent}--subagent--{archetype}--{uniqueId}` | ✅ | `DefaultSubAgentManager.cs:66` |
| Sub-agent panel in `chat.js:1312-1448` | ✅ | `SUBAGENT_STATUS_MAP`, `renderSubAgentPanel()`, `killSubAgent()`, `fetchSubAgents()` all present |
| Sidebar collapse persistence via `storage.js` | ✅ | `getCollapsedAgents()`/`toggleAgentCollapsed()` and section equivalents confirmed |
| `sessionsInitialLoad` bug fixed | ✅ | Flag set to `false` at lines 79, 102, 180 |
| SignalR subscribe-all for live streaming | ✅ | `IActivityBroadcaster` with `SubAgentSpawned/Completed/Failed/Killed` events |
| `ParentSessionId` not on domain model | ✅ | Only tracked in-memory in `DefaultSubAgentManager._parentChildren` |

**Conclusion**: Spec is accurate. No factual errors found.

---

### 1.2 Risks & Gaps

#### Risk 1: Sealed Sessions Still Returned by API (Medium)

The `/api/sessions` endpoint currently returns **all** sessions. Once we add
the Seal endpoint, sealed sub-agent sessions will still be returned unless we
add query filtering. The spec says "filter out `sealed` sessions by default"
but places filtering only in JS.

**Recommendation**: For P0, client-side filtering is acceptable. Add a
`?status=active,suspended` query parameter to `/api/sessions` as P1 tech
debt — avoids transmitting sealed sessions over the wire as they accumulate.

#### Risk 2: AgentId Parsing Fragility (Low)

The `split('--subagent--')` approach to extract the parent agentId is correct
for the current format but could break if an agent's own ID contains
`--subagent--`. This is unlikely but worth a defensive check.

**Recommendation**: Parse with `indexOf` (as spec's `parseSubAgentSession`
does) rather than `split`. The spec's reference implementation is correct.

#### Risk 3: Channel Grouping Mismatch (Medium)

The sidebar currently groups sessions by agent → channel (one item per
channel type). Sub-agent sessions don't have meaningful channel types — they
are direct agent-to-agent conversations. Rendering them through the existing
`latestByChannel` path would show them as "Web Chat" items, which is
misleading.

**Recommendation**: Sub-agent sessions MUST NOT go through `latestByChannel`.
Render them as a flat list under the parent agent group, after the channel
items, with distinct styling.

#### Risk 4: Status Enum Serialisation (Low)

`SessionStatus` is a C# enum serialised as a string. The frontend
`SUBAGENT_STATUS_MAP` in `chat.js` uses PascalCase keys (`Running`,
`Completed`, etc.). The seal endpoint will set `SessionStatus.Sealed`, which
must be handled in JS as `"Sealed"`. Ensure the status map is extended.

**Recommendation**: Add `Sealed: { icon: '🔒', label: 'Sealed', css: 'sealed' }`
to the shared status map.

#### Risk 5: Race Condition on Seal While Running (Low)

If a user seals a session while a sub-agent is still running, messages may
still stream in via SignalR after the status is set to Sealed.

**Recommendation**: The Seal endpoint should reject requests where
`session.Status == Active` (only allow sealing Completed/Failed/Killed
sessions). This matches the UX intent — you seal to hide clutter, not to stop
execution.

#### Risk 6: Old Pre-Fix Sessions with `::` in AgentId (Low)

Spec mentions old sessions may exist with `::` in agentId (before the
`::` → `--` fix). These won't parse correctly with the `--subagent--`
splitter.

**Recommendation**: `parseSubAgentSession` should check `sessionType ===
'agent-subagent'` first (as the spec does), and fall back to showing these as
orphaned items in an "Other Sessions" group at the bottom of the sidebar.

---

### 1.3 Interface Contracts

#### Contract 1: Seal Endpoint

```
PATCH /api/sessions/{sessionId}/seal
```

**Request**: Empty body (follows suspend/resume pattern)

**Preconditions**:
- Session must exist → `404 NotFound` if missing
- Session must be a sub-agent (`SessionId.IsSubAgent`) → `400 BadRequest` if not
- Session status must be `Completed`, `Failed`, or `Killed` → `409 Conflict` if `Active`
- Already sealed → `204 NoContent` (idempotent)

**Success Response**: `200 OK`
```json
{
  "sessionId": "nova-abc123::subagent::f8c254da",
  "status": "Sealed",
  "updatedAt": "2026-04-16T10:30:00Z"
}
```

**Side Effects**:
- Sets `session.Status = SessionStatus.Sealed`
- Sets `session.UpdatedAt = DateTimeOffset.UtcNow`
- Publishes `GatewayActivityType.SessionStatusChanged` (reuse existing pattern
  or add new `SubAgentSealed` type — recommend reusing existing)

**Frontend invocation** (`api.js`):
```javascript
export async function sealSession(sessionId) {
    const res = await fetch(`${API_BASE}/sessions/${encodeURIComponent(sessionId)}/seal`, {
        method: 'PATCH'
    });
    return res.ok;
}
```

#### Contract 2: Sub-Agent Session Data in `/api/sessions`

No endpoint changes. Existing response already includes sub-agent sessions.
Frontend parses sub-agent identity client-side:

```javascript
// Input: session object from /api/sessions
// Output: { parentSessionId, parentAgentId, subAgentUniqueId } or null
function parseSubAgentSession(session) {
    if (session.sessionType !== 'agent-subagent') return null;

    const marker = '::subagent::';
    const idx = session.sessionId.indexOf(marker);
    if (idx < 0) return null;

    const parentSessionId = session.sessionId.substring(0, idx);

    const agentMarker = '--subagent--';
    const agentIdx = session.agentId.indexOf(agentMarker);
    const parentAgentId = agentIdx > 0 ? session.agentId.substring(0, agentIdx) : null;

    const subAgentUniqueId = session.sessionId.substring(idx + marker.length);
    return { parentSessionId, parentAgentId, subAgentUniqueId };
}
```

#### Contract 3: Read-Only Conversation View

Uses existing endpoint — no backend changes:

```
GET /api/sessions/{sessionId}/history
```

Frontend contract for `openSubAgentSession()`:

```javascript
// Opens a sub-agent session in read-only mode on the chat canvas.
// - Loads full conversation history
// - Hides input bar (sets canvas to read-only mode)
// - Shows read-only banner with status + seal button
// - If session.status is Active/Running, subscribes to SignalR live stream
async function openSubAgentSession(sessionId) { ... }
```

#### Contract 4: Sidebar Sub-Agent Rendering

Sub-agent items rendered **after** channel items within a parent agent group:

```html
<div class="agent-group">
  <div class="agent-group-header">Nova</div>
  <div class="agent-group-channels">
    <!-- existing channel items -->
    <div class="list-item">💬 Web Chat</div>
  </div>
  <div class="agent-group-subagents">
    <!-- NEW: sub-agent items -->
    <div class="subagent-item running">
      <span class="subagent-status">🟢</span>
      <span class="subagent-label">general-f8c254da</span>
    </div>
    <div class="subagent-item completed">
      <span class="subagent-status">✅</span>
      <span class="subagent-label">explore-a1b2c3d4</span>
      <button class="seal-btn" title="Seal">🔒</button>
    </div>
  </div>
</div>
```

Sub-agent items are:
- Visually indented (CSS `padding-left`) relative to channel items
- Smaller font, muted color to establish visual hierarchy
- Seal button only shown on completed/failed/killed (not active, not sealed)
- Click triggers `openSubAgentSession(sessionId)` (not `openAgentTimeline`)

#### Contract 5: Status Map Extension

Shared status map (extract from `chat.js` into a shared module or duplicate
in `sidebar.js` for P0):

```javascript
const SUBAGENT_STATUS_MAP = {
    Running:   { icon: '🟢', label: 'Running',   css: 'running' },
    Completed: { icon: '✅', label: 'Completed', css: 'completed' },
    Failed:    { icon: '❌', label: 'Failed',    css: 'failed' },
    Killed:    { icon: '🛑', label: 'Killed',    css: 'killed' },
    TimedOut:  { icon: '⏱',  label: 'Timed Out', css: 'timedout' },
    Sealed:    { icon: '🔒', label: 'Sealed',    css: 'sealed' }
};
```

---

### 1.4 Spec Adjustments Required

1. **Seal precondition**: Add guard — reject `PATCH /seal` on `Active`
   sessions. Only allow sealing terminal states (Completed/Failed/Killed).
2. **Sub-agent rendering path**: Clarify sub-agent items MUST NOT go through
   `latestByChannel` grouping. They render as a separate list within the
   agent group.
3. **Status map**: Add `Sealed` entry to `SUBAGENT_STATUS_MAP`.
4. **Orphaned sessions**: Add a catch-all "Other Sessions" group for
   sub-agent sessions whose parent agentId doesn't match any registered agent
   (covers pre-fix data and edge cases).

---

## 2. Wave Breakdown

### Wave 1: Backend + Frontend Skeleton (Parallel)

**Goal**: Seal endpoint live, sidebar rendering sub-agent sessions, shared
parsing utility.

| Agent | Deliverables |
|---|---|
| **Bender** (Gateway/API) | `SessionsController.cs` — Add `PATCH /sessions/{sessionId}/seal` endpoint following suspend/resume pattern. Guards: 404 if not found, 409 if Active, 204 if already Sealed. Sets `SessionStatus.Sealed` + `UpdatedAt`. |
| **Fry** (Web/Frontend) | `sidebar.js` — Modify `loadSessions()` to: (1) parse sub-agent sessions via `parseSubAgentSession()`, (2) group under parent agent, (3) render as indented items after channels, (4) filter out Sealed by default, (5) click handler calls placeholder `openSubAgentSession()`. `api.js` — Add `sealSession(sessionId)` helper. Add `parseSubAgentSession()` utility (can live in `api.js` or new `subagent-utils.js`). |
| **Amy** (UI/CSS) | `styles.css` — `.agent-group-subagents` container, `.subagent-item` with indent + smaller font, status icon colors (`.running`, `.completed`, `.failed`, `.killed`, `.timedout`), `.seal-btn` inline button. |

**Dependencies**: None — this is the first wave. Backend and frontend work in
parallel. Fry stubs the seal API call (optimistic); Bender delivers the real
endpoint.

**Files changed**:
- `src/gateway/BotNexus.Gateway.Api/Controllers/SessionsController.cs`
- `src/BotNexus.WebUI/wwwroot/js/sidebar.js`
- `src/BotNexus.WebUI/wwwroot/js/api.js`
- `src/BotNexus.WebUI/wwwroot/css/styles.css`

---

### Wave 2: Read-Only Conversation View + Seal Integration

**Goal**: Clicking a sub-agent in the sidebar opens its full conversation in
read-only mode. Seal button works end-to-end.

| Agent | Deliverables |
|---|---|
| **Fry** (Web/Frontend) | `chat.js` — Implement `openSubAgentSession(sessionId)`: (1) fetch history via `/api/sessions/{sessionId}/history`, (2) render messages reusing existing message renderer, (3) hide input bar + show read-only banner (`"🔒 Read-only — sub-agent session"`), (4) if session status is Active, subscribe to SignalR live updates (already subscribed via subscribe-all — just wire message handler to canvas), (5) banner includes seal button for terminal states, wired to `sealSession()` → refresh sidebar. |
| **Amy** (UI/CSS) | `styles.css` — `.readonly-banner` bar styling (fixed position above canvas, muted background, icon + text + seal button). Input bar `.hidden-readonly` state. |
| **Hermes** (Tester) | Integration tests: (1) Seal endpoint — happy path, idempotent, reject Active, 404 on missing. (2) E2E: spawn sub-agent → appears in sidebar → click → read-only view → seal → disappears. (3) Verify sealed sessions filtered from sidebar on reload. |

**Dependencies**: Wave 1 must be complete (sidebar rendering + seal endpoint
+ API helper).

**Files changed**:
- `src/BotNexus.WebUI/wwwroot/js/chat.js`
- `src/BotNexus.WebUI/wwwroot/css/styles.css`
- `tests/BotNexus.Gateway.Api.Tests/Controllers/SessionsControllerTests.cs`
  (seal endpoint unit tests)
- `tests/BotNexus.WebUI.Tests/` (E2E/integration if test harness exists)

---

### Wave 3: Polish + Edge Cases

**Goal**: Harden edge cases, documentation, final QA pass.

| Agent | Deliverables |
|---|---|
| **Fry** (Web/Frontend) | Handle orphaned sub-agent sessions (pre-fix `::` agentIds) — show in "Other" group or gracefully skip. Handle rapid status transitions (sub-agent completes while user has it open — update banner reactively via SignalR). |
| **Hermes** (Tester) | Edge case tests: (1) gateway restart with active sub-agent, (2) multiple sub-agents from same parent, (3) seal + page refresh persistence, (4) orphaned sessions with old agentId format. |
| **Kif** (Docs) | Update `docs/planning/feature-subagent-ui-visibility/design-spec.md` with review findings. Add user-facing note to WebUI docs about sub-agent visibility. |

**Dependencies**: Wave 2 must be complete.

**Files changed**:
- `src/BotNexus.WebUI/wwwroot/js/sidebar.js` (orphan handling)
- `src/BotNexus.WebUI/wwwroot/js/chat.js` (reactive status update)
- `docs/planning/feature-subagent-ui-visibility/design-spec.md` (amendments)
- Test files (edge case coverage)

---

## 3. Summary

| Wave | Agents | Duration Estimate | Parallelism |
|---|---|---|---|
| **Wave 1**: Backend + Sidebar | Bender, Fry, Amy | 1 sprint | Full parallel — no cross-dependencies |
| **Wave 2**: Read-Only View + Seal UX | Fry, Amy, Hermes | 1 sprint | Fry/Amy parallel; Hermes starts after Fry delivers |
| **Wave 3**: Edge Cases + Docs | Fry, Hermes, Kif | 0.5 sprint | Full parallel |

**Total estimate**: ~2.5 sprints for P0 delivery.

**Critical path**: Wave 1 (Fry's sidebar changes) → Wave 2 (Fry's chat.js
read-only view) → Wave 3 (Hermes edge case validation).

**Decision**: ✅ **Approved with conditions** — spec adjustments in §1.4 must
be incorporated before implementation begins.
