---
id: bug-session-switching-ui
title: "Session Switching — UI Message Routing Research"
type: research
created: 2026-04-10
updated: 2026-04-10
author: nova
---
# Research: Session Switching UI — Message Misrouting

## Architecture Summary

### Server-Side (GatewayHub)
The SignalR hub (`GatewayHub.cs`) is **stateless per-connection** — it doesn't track which session a client is "in". Every hub method (`SendMessage`, `Steer`, `FollowUp`) takes explicit `agentId` + `sessionId` parameters. The server trusts whatever the client sends.

```
Client -> Hub.SendMessage(agentId, sessionId, content)
                              |
                     ChannelDispatcher routes by sessionId
```

**Implication**: The server cannot have a session routing bug. If the wrong `sessionId` arrives, the client sent it wrong.

### Client-Side (app.js — 3,581 lines, vanilla JS)
The UI manages session state via module-scoped variables:

```javascript
let currentSessionId = null;   // line 13
let currentAgentId = null;     // line 14
```

Messages are sent using these variables:
```javascript
// sendMessage() at line 1499
await hubInvoke('SendMessage', currentAgentId, currentSessionId, text);
```

### Session Switching Flow
When a user clicks a session in the sidebar:

1. `loadSessions()` renders sidebar items with click handler -> `openAgentTimeline(agentId, channelType)`
2. `openAgentTimeline()` (line 1778):
   - Clears UI state (response timeout, tool timers, abort button)
   - Leaves old session: `await hubInvoke('LeaveSession', currentSessionId)`
   - Sets `currentSessionId = null`
   - Fetches all sessions for the agent
   - Renders history for recent sessions
   - Calls `await joinSession(agentId, latestSession.sessionId)` at the **end**
3. `joinSession()` (line 695):
   - Increments `joinSessionVersion` for supersession detection
   - Sets `currentAgentId = agentId` immediately
   - Leaves previous session group: `await hubInvoke('LeaveSession', currentSessionId)`
   - Invokes `JoinSession` on hub
   - On success: sets `currentSessionId = result.sessionId`

## Bug Analysis

### Root Cause: Race Window in openAgentTimeline

The critical issue is the **async gap** between when the UI visually updates and when `currentSessionId` is set:

```
User clicks session B
  +-- openAgentTimeline('agent-b', 'Web Chat')
  |   +-- LeaveSession(currentSessionId)  // leaves A
  |   +-- currentSessionId = null          // <-- DANGER ZONE STARTS
  |   +-- UI clears and shows "Loading timeline..."
  |   +-- Fetches session list (async network call)
  |   +-- Renders history (async, multiple fetches)
  |   +-- joinSession(agentId, latestSessionId)
  |       +-- currentAgentId = agentId     // set immediately
  |       +-- hubInvoke('JoinSession', ...)  // async
  |       +-- currentSessionId = result.sessionId  // <-- SET ONLY HERE
  +-- User can type and hit Enter at ANY point during this window
```

If the user sends a message while `currentSessionId` is still `null` or stale:
- `sendMessage()` checks `if (!currentSessionId)` -> calls `joinSession(currentAgentId, null)` which **creates a new session** instead of using the intended one
- Or if the timing hits just right, `currentSessionId` could still hold the **old** value from session A

### Secondary Issue: openAgentTimeline Sets currentSessionId = null Too Early

At line ~1791:
```javascript
if (currentSessionId) {
    try { await hubInvoke('LeaveSession', currentSessionId); } catch(e) {}
    currentSessionId = null;  // <-- nulled before join completes
}
```

Then at line ~1868:
```javascript
await joinSession(agentId, latestSession.sessionId);
// currentSessionId is only set INSIDE joinSession after hub response
```

The window between `null` and the join response completing is when the bug manifests.

### Why the Version Guard Doesn't Help

`joinSession` has a `joinSessionVersion` guard to supersede stale joins — this protects against **rapid switching** at the join level. But it doesn't protect against `sendMessage()` reading a stale/null `currentSessionId` during the async gap.

## Affected Code Paths

| Function | Line | Issue |
|----------|------|-------|
| `sendMessage()` | 1499 | Reads `currentSessionId` which may be null/stale during switch |
| `openAgentTimeline()` | 1778 | Sets `currentSessionId = null` before join completes |
| `joinSession()` | 695 | Sets `currentSessionId` only after async hub response |
| `isEventForCurrentSession()` | 481 | Guards inbound events — correct, but means events for the new session are dropped during the gap |

## Server-Side Tests Already Passing

The existing `SignalRIntegrationTests.cs` covers server-side routing correctly:
- `Hub_SwitchSession_JoinNewAfterLeavingOld` — verifies leave/join/send goes to correct session
- `Hub_RapidSessionSwitch_LatestWins` — verifies concurrent joins resolve correctly
- `Hub_ChannelAdapter_SendsToCorrectGroup` — verifies events go to correct SignalR group only

**The server is not the problem.** The client sends the wrong session ID.

## Testing Strategy

### What Needs Testing

The bug is in the **client-side state management** — specifically the JS code in `app.js`. There are three viable approaches:

### Approach 1: Playwright E2E (Full Stack)

Drives the real WebUI in a real browser against a running gateway.

**Pros:**
- Tests the actual user experience end-to-end
- Catches CSS/DOM/JS state bugs
- Uses `WebApplicationFactory<Program>` pattern already established in `SignalRIntegrationTests.cs` — just add Playwright on top

**Cons:**
- Requires Node.js + Playwright installed
- Slower than pure C# tests
- Need to figure out how to host the WebUI (it's an embedded resource in `BotNexus.WebUI.csproj`)

**How to verify routing:**
- Use the `RecordingDispatcher` pattern from `SignalRIntegrationTests.cs` to capture dispatched messages
- Playwright switches sessions in the UI, types, sends
- Assert via REST API or the recording dispatcher that the message arrived at the correct session ID

### Approach 2: SignalR Client Integration Test (Simulate UI Logic)

Reproduce the exact JS switching logic in a C# test using `HubConnection` — mimicking what `app.js` does.

**Pros:**
- Extends existing `SignalRIntegrationTests.cs` — same patterns, same infrastructure
- Fast, no browser needed
- Can precisely control timing (delays between leave/join/send)

**Cons:**
- Tests the **protocol** not the **UI state** — if the bug is purely in how `app.js` manages `currentSessionId`, a C# client that does the right thing won't reproduce it
- Can't catch DOM-level bugs

**How to verify:**
- Create tests that simulate the exact sequence: connect -> join A -> leave A -> join B -> send to B
- Add timing variants: send immediately after join (before response), send during leave, rapid switch
- These tests should **pass** (server routes correctly) — they prove the protocol works
- The **failing** test needs to simulate the JS bug: send to A's session ID while intending B

### Approach 3: Hybrid (Recommended)

**Layer 1 — Expand SignalR Integration Tests (C#):**
Add tests to `SignalRIntegrationTests.cs` that cover the exact protocol sequences the UI should execute. These validate the server contract and serve as a specification for correct client behavior.

**Layer 2 — Playwright E2E Tests:**
Add a `tests/BotNexus.WebUI.Tests/` project with Playwright tests that drive the actual UI. These are the only tests that can catch the actual bug (JS state management).

**Layer 3 — JS Unit Tests (Optional):**
If `app.js` is refactored to separate state management from DOM, unit test the state logic directly with a test runner like Vitest.

## Recommended Test Scenarios

### SignalR Integration Tests (C# — extend existing)

These already partially exist. Add:

| # | Scenario | Test |
|---|----------|------|
| 1 | Send during active join | Start join, send message before join completes -> message should use the session from the completed join |
| 2 | Leave + Join + immediate Send | Leave A, join B, send immediately -> message arrives at B |
| 3 | Multiple agents, interleaved | Join agent-a/session-1, join agent-b/session-2, send to each -> correct routing |
| 4 | Concurrent clients, different sessions | Client 1 in session A, client 2 in session B -> events only go to correct client |

### Playwright E2E Tests (New)

| # | Scenario | Validation |
|---|----------|------------|
| 1 | Click session A, switch to B, send message | Message dispatched with session B's ID |
| 2 | Switch A -> B -> A, send message | Message dispatched with session A's ID |
| 3 | Rapid click A then B (< 200ms), send | Message dispatched with session B's ID |
| 4 | Send while "Loading timeline..." is shown | Message waits for join or goes to correct session |
| 5 | Agent response arrives while viewing other session | Response appears in correct session pane, not current view |
| 6 | Two tabs open, different sessions | Each tab sends to its own session |
| 7 | Switch session, refresh page | Correct session is active after reload |

## Proposed Fix Direction

The core fix is to make `sendMessage()` aware of the switching state:

**Option A: Block sends during switch**
```javascript
let sessionSwitchInProgress = false;

async function openAgentTimeline(agentId, channelType) {
    sessionSwitchInProgress = true;
    try {
        // ... existing logic ...
        await joinSession(agentId, latestSession.sessionId);
    } finally {
        sessionSwitchInProgress = false;
    }
}

async function sendMessage() {
    if (sessionSwitchInProgress) {
        // Wait for switch to complete, or queue the message
        await waitForSessionReady();
    }
    // ... existing send logic using currentSessionId ...
}
```

**Option B: Disable input during switch**
Disable the chat input and send button while `openAgentTimeline` is running. Re-enable after `joinSession` completes. Simple, no race conditions.

**Option C: Queue sends with target session**
Capture the intended session at switch time, queue any sends during the gap, replay them after join completes.

**Recommendation:** Option B is simplest and safest. The user sees "Loading timeline..." anyway — disabling input during that window is natural UX. Option A is more robust if the loading is fast enough that users don't notice the gap.

## File References

| File | Path | Relevance |
|------|------|-----------|
| WebUI JS | `src/BotNexus.WebUI/wwwroot/app.js` | All client-side session state + switching |
| SignalR Hub | `src/gateway/BotNexus.Gateway.Api/Hubs/GatewayHub.cs` | Server-side hub (stateless, routes by params) |
| Channel Adapter | `src/gateway/BotNexus.Gateway.Api/Hubs/SignalRChannelAdapter.cs` | Sends events to session groups |
| Hub Unit Tests | `tests/BotNexus.Gateway.Tests/SignalRHubTests.cs` | Existing hub unit tests |
| Integration Tests | `tests/BotNexus.Gateway.Tests/Integration/SignalRIntegrationTests.cs` | Existing SignalR integration tests |
| WebUI Project | `src/BotNexus.WebUI/BotNexus.WebUI.csproj` | Static files served as embedded resources |
