---
id: bug-session-switching-ui
title: "Session Switching Broken During Active Agent Work"
type: bug
priority: high
status: partially-delivered
created: 2026-04-10
updated: 2026-07-18
author: nova
tags: [webui, session, ux, multi-agent, critical-ux]
blocks: [feature-subagent-ui-visibility]
likely_fixed_by: feature-multi-session-connection (subscribe-all model)
verification_needed: true
partial_fix: 28a0329 (receive-side event isolation)
---

## Status Note (2026-04-13)

**Partially fixed.** Commit `28a0329` addresses the **receive-side cross-session bleed** (Pattern A):
- **Server**: `SendAsync` and `SendStreamDeltaAsync` now wrap `ContentDelta` payloads as `{ sessionId, contentDelta }` instead of raw strings
- **Client**: `routeEvent()` no longer falls back to `activeViewId` when `sessionId` is missing — events without a `sessionId` are dropped with a console warning

### Still open: Send-side misrouting
The **confirmed root cause** (2026-04-10) is the client-side race in `openAgentTimeline()` where `currentSessionId` is stale/null during async switch operations. A user typing and pressing Enter during the switch window still routes their message to the wrong session. This is **not addressed** by `28a0329`.

### Remaining work
1. **Send-side fix** — `sendMessage()` must not send to a stale/null `currentSessionId` during switch. Options: disable chat input during switch, queue sends, or set `currentSessionId` synchronously before async operations
2. **Per-session loading state** (Pattern C) — loading indicators are still global
3. **Tests** — no tests were added with `28a0329`. The expanded testing plan (SignalR integration + Playwright E2E) is still required
4. **Acceptance criteria** — all items below remain unchecked

**Action**: Address send-side race condition and add tests per the expanded testing plan.

### New symptom: Cross-agent receive bleed (2026-04-15)

**Observed by Jon:** Switched to Aurum, sent a message, got a response. Switched back to Nova, continued chatting. Aurum's response ("[[reply_to_current]] I can't access it yet...") appeared in Nova's chat canvas.

**Investigation:** Nova's session store (`f2add6ff...`) does NOT contain Aurum's message. Aurum's session (`8031c4d6...`) correctly contains it. **This is purely a WebUI rendering issue** -- the backend sessions are isolated.

**Root cause hypothesis:** `SubscribeAll()` subscribes the client to ALL session groups. When Aurum's stream events arrive, they're rendered into whichever chat container is currently visible (Nova's) instead of being routed to Aurum's container. The `28a0329` fix may not cover the case where the user actively switches agents mid-stream -- the `routeEvent()` function may be checking against the wrong `activeViewId` when the switch happens between Aurum responding and the events arriving.

**This means `28a0329` did NOT fully fix Pattern A.** The receive-side bleed still occurs when switching between agents while one is actively responding.

# Design Spec: Session Switching Broken During Active Agent Work

## Overview

The WebUI conversation canvas does not properly switch when clicking a different agent's session while another agent is actively working. The previous agent's conversation and loading state bleeds through, making session switching non-functional during active work.

## Bug Summary

**Steps to reproduce:**
1. Have Agent A (e.g., Nova) actively running tool calls
2. Click on Agent B's session in the sidebar
3. Observe: Canvas still shows Agent A's conversation with loading timeline
4. Expected: Canvas shows Agent B's conversation

## Requirements

### Must Fix
1. Clicking a session in the sidebar immediately switches the conversation canvas to that session
2. Previous session's streaming/loading state does not bleed into the new view
3. Works regardless of whether the previous session's agent is actively working
4. Loading indicators are per-session, not global

### Should Fix
5. Background agent continues working — switching away does NOT cancel its work
6. Switching back to the working agent shows its current state (including new messages generated while away)
7. Sidebar shows activity indicator on sessions with active work (so user knows agent is still working)

### Nice to Have
8. Smooth transition animation between sessions
9. "Agent is working" badge on sidebar session entries
10. Notification when a background agent completes while viewing a different session

## Root Cause Analysis (Probable)

The issue is almost certainly one of these patterns:

### Pattern A: Single Shared Stream Subscription
```
Problem:
  SignalR messages -> single handler -> renders to canvas
  Session switch doesn't change the handler's target

Fix:
  SignalR messages -> route by sessionId -> only render if sessionId matches active view
```

### Pattern B: Component State Not Reset
```
Problem:
  <ConversationCanvas> holds messages in state
  Switching sessions doesn't clear/replace the state
  New session messages append to old session's messages

Fix:
  Use sessionId as React key: <ConversationCanvas key={activeSessionId} />
  Or explicitly clear state on session change
```

### Pattern C: Global Loading State
```
Problem:
  isLoading = true (global)
  Session switch doesn't reset it
  Loading spinner persists

Fix:
  loadingState = Map<sessionId, boolean>
  UI reads: loadingState[activeSessionId]
```

## Proposed Fix

### Core Principle
**The conversation canvas is a pure function of the selected session ID.** All state (messages, loading, streaming) is keyed by session ID.

### Implementation

#### 1. Session-Scoped Message Rendering
```typescript
// Messages from SignalR should be routed to session-specific stores
function onMessage(sessionId: string, message: Message) {
  sessionMessages[sessionId].push(message);

  // Only trigger re-render if this is the active session
  if (sessionId === activeSessionId) {
    renderCanvas();
  }
}
```

#### 2. Canvas Keyed by Session
```tsx
// React pattern: key change forces full remount
<ConversationCanvas
  key={activeSessionId}
  sessionId={activeSessionId}
  messages={sessionMessages[activeSessionId]}
  isLoading={loadingState[activeSessionId]}
/>
```

#### 3. Session Switch Handler
```typescript
function switchSession(newSessionId: string) {
  // 1. Update active session ID
  activeSessionId = newSessionId;

  // 2. Load conversation history for new session (if not cached)
  if (!sessionMessages[newSessionId]) {
    sessionMessages[newSessionId] = await fetchSessionHistory(newSessionId);
  }

  // 3. Re-render canvas with new session's data
  renderCanvas();

  // NOTE: Do NOT cancel the previous session's agent work
  // It continues in the background
}
```

#### 4. Per-Session Loading State
```typescript
// Loading state is per-session
const loadingState: Record<string, boolean> = {};

function onAgentStarted(sessionId: string) {
  loadingState[sessionId] = true;
  if (sessionId === activeSessionId) renderLoadingIndicator();
}

function onAgentCompleted(sessionId: string) {
  loadingState[sessionId] = false;
  if (sessionId === activeSessionId) hideLoadingIndicator();
  else showSidebarCompletionBadge(sessionId);
}
```

## Testing Plan

1. **Basic switch**: Agent A working, switch to Agent B — verify canvas shows B
2. **Switch back**: Switch to A — verify it shows A's current state including new messages
3. **Multiple switches**: Rapidly switch between 3+ sessions — verify no state bleed
4. **Agent completes while away**: A finishes while viewing B — verify A's sidebar updates
5. **Switch to idle session**: Switch from active agent to an idle session — verify no loading indicator
6. **New messages while away**: A generates 5 messages while viewing B — verify all 5 appear when switching back to A

## Scope

- **Primarily frontend** — the core bug is a WebUI rendering/state management issue
- **Minor backend enrichment** — `AgentStreamEvent.SessionId` property added for client-side routing verification; `SignalRChannelAdapter.SendStreamEventAsync` enriches outbound events with `sessionId`
- Agent work continues unaffected in background

## Updated Root Cause Analysis (2026-04-10)

The original analysis identified three probable patterns (shared stream, component state, global loading). After reading the actual code, the root cause is more specific:

### Confirmed: Client-Side State Race in openAgentTimeline

The server (`GatewayHub.cs`) is stateless — every `SendMessage` call takes explicit `agentId` + `sessionId` params. The server routes correctly based on what it receives. **The bug is the client sending the wrong session ID.**

The race window in `app.js`:
1. `openAgentTimeline()` (line 1778) sets `currentSessionId = null` after leaving the old session
2. It then does multiple async operations (fetch session list, render history)
3. `joinSession()` is called at the end, and `currentSessionId` is only set after the async hub response
4. If the user types and sends during this window, `sendMessage()` (line 1499) reads `currentSessionId` as either `null` (creating a brand new session) or stale (routing to the old session)

### Send-Side Misrouting (New Finding)

Jon confirmed on 2026-04-10: switching from Nova -> assistant session, the UI visually updated, but the sent message ("hey...") was delivered to Nova's session. This is the race window described above — `currentSessionId` hadn't been updated yet when Enter was pressed.

## Expanded Testing Plan

### Layer 1: Extend SignalR Integration Tests (C#)

Extend `tests/BotNexus.Gateway.Tests/Integration/SignalRIntegrationTests.cs` using existing patterns (`RecordingDispatcher`, `WebApplicationFactory`, `CreateStartedConnection`).

These validate the server contract. Some already exist — add edge cases:

| # | Scenario | Status |
|---|----------|--------|
| 1 | Switch session: leave A, join B, send to B | EXISTS (`Hub_SwitchSession_JoinNewAfterLeavingOld`) |
| 2 | Rapid switch: concurrent join A + join B, send to B | EXISTS (`Hub_RapidSessionSwitch_LatestWins`) |
| 3 | Multiple agents: join agent-a/s1, join agent-b/s2, send to each | NEW |
| 4 | Send with stale session ID after leave | NEW — simulates the bug: leave A, send to A before joining B |
| 5 | Two clients in different sessions, events isolated | EXISTS (`Hub_ChannelAdapter_SendsToCorrectGroup`) |

### Layer 2: Playwright E2E Tests (New — Critical)

Create `tests/BotNexus.WebUI.Tests/` with Playwright tests that drive the actual browser UI. This is the **only** layer that can catch the JS state bug.

**Setup:**
- Use `WebApplicationFactory<Program>` to host the gateway (same as integration tests)
- Playwright connects to the test server URL
- Use `RecordingDispatcher` to capture dispatched messages for assertion
- Expose a test-only REST endpoint (or use the existing `/sessions` API) to verify which session received the message

**Test Scenarios:**

| # | Scenario | Steps | Assert |
|---|----------|-------|--------|
| 1 | Basic switch + send | Create 2 agents. Click agent A, send msg. Click agent B, send msg. | RecordingDispatcher shows msg 1 -> A's session, msg 2 -> B's session |
| 2 | Switch back + send | Click A, click B, click A, send msg | Message dispatched to A's session |
| 3 | Rapid switch + send | Click A, immediately click B (< 200ms), wait for load, send msg | Message dispatched to B's session |
| 4 | Send during loading | Click A, then click B, type and send before "Loading timeline..." completes | Message either queued or dispatched to B (never to A) |
| 5 | Inbound event isolation | Agent A responds while viewing B | Response appears in A's session history, not in B's canvas |
| 6 | Two browser tabs | Open two tabs, each viewing a different agent, send from each | Each message goes to the correct agent's session |
| 7 | Refresh persistence | Switch to B, refresh page | B's session is still active after reload |

**Key assertion pattern:**
```
// Pseudocode for Playwright test
const dispatcher = getRecordingDispatcher();
await page.click('[data-agent-id="agent-b"]');
await page.waitForSelector('.chat-input:not([disabled])');
await page.fill('.chat-input', 'test message');
await page.click('.btn-send');
// Verify the message went to the right session
expect(dispatcher.lastMessage.sessionId).toBe(agentBSessionId);
expect(dispatcher.lastMessage.targetAgentId).toBe('agent-b');
```

### Layer 3: JS Unit Tests (Optional, Post-Fix)

If `app.js` is refactored to extract session state management into a testable module, add Vitest/Jest tests for the state machine logic. Not required for the initial fix.

## Acceptance Criteria (Updated)

- [ ] Bug is reproduced in a Playwright E2E test (message goes to wrong session)
- [ ] Fix is implemented — `sendMessage()` cannot send to a stale/null session during switch
- [ ] All Playwright test scenarios pass
- [ ] Existing `SignalRIntegrationTests` continue to pass
- [ ] New integration tests for edge cases added and passing
- [ ] Chat input is disabled or sends are queued during session switch (no silent misrouting)
- [ ] Test suite runs in CI or as a pre-release gate
