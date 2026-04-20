# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## cli-28 — Gateway /api/status, /api/doctor, /api/shutdown endpoints

**Commit:** 1d1bc34 — `feat(api): add status, doctor, and shutdown Gateway endpoints`

### Deliverables

1. **GET /api/status** — Comprehensive status endpoint combining:
   - Gateway uptime, version, startedAt timestamp
   - Health check summary (status, healthy/degraded/unhealthy counts)
   - Loaded extensions count (providers, channels, tools)
   - Configured agents count (default + named)
   - Registered cron jobs count (enabled, running state)
   - Active sessions count
   - Memory consolidation state (configured, enabled, last run, success)

## 2026-04-10T16:30Z — Sub-Agent Spawning Feature: Wave 4 WebUI (Web Dev)

**Status:** ✅ Complete  
**Commit:** 91f11c3

**Your Role:** Web Dev. WebUI interactive panel.

**Deliverables:**
- Sub-agent panel in session view (collapsible)
  - Real-time active sub-agent list
  - Status indicators (Running, Completed, Failed, Killed, TimedOut)
  - Kill button with ownership validation (disabled if not parent)
  - Result display on completion (last assistant message summary)
  - Timestamps: started, completed
  - Turn counter (current/max)
- WebSocket integration
  - Subscribe to `subagent_spawned`, `subagent_completed`, `subagent_failed` events
  - Real-time list updates without page reload
  - Error state display on kill failures
- UX polish
  - Collapsible panel state preserved in session
  - Loading indicators during spawn/kill operations
  - Graceful fallback if events unavailable
  - Result summary truncation with expand/collapse

---

## 2026-04-05T23:30:00Z — Phase 4 Wave 1 Delivery

**Status:** ✅ Complete  
**Commit:** 5202779  

**WebUI Enhancements (Fry):**
- Thinking blocks display during streaming
- Tool timers show elapsed time for execution phases
- Steer mode UX: "🧭 Steer" button + placeholder update when streaming
- Reconnection banners: user-visible alerts for WebSocket reconnects
- Proper state reset: clean message state on stream end/error/abort

**Design Review (Leela):** A- Grade. Multi-tenant auth solid, runtime hardening textbook-correct. 3 P1s flagged: config endpoint filesystem probing, missing auth on config validation, skipped recursion tests. 4 P2s documented.

**Consistency Review (Nibbler):** Good grade. 2 P1s fixed (ConfigController XML docs, PlatformConfig property docs). Stale comment updated. 5 P2s documented.

2. **GET /api/doctor** — Diagnostic checkup endpoint:
   - Runs all checkups via `CheckupRunner.RunAndFixAsync` (read-only, no auto-fix)
   - Returns summary (passed/warnings/failed counts) and per-checkup results
   - Each result includes: name, category, status, message, advice, canAutoFix
   - Supports `?category=` query param for filtering (e.g., `?category=security`)

3. **POST /api/shutdown** — Graceful shutdown endpoint:
   - Accepts optional `{ "reason": "..." }` JSON body
   - Logs shutdown reason via ILogger
   - Returns 202 Accepted immediately
   - Calls `IHostApplicationLifetime.StopApplication()` after 500ms delay
   - Protected by API key auth

### Infrastructure Changes
- Added `BotNexus.Diagnostics` project reference to Gateway
- Registered `AddBotNexusDiagnostics()` in DI via `BotNexusServiceExtensions`
- Fixed pre-existing `volatile TimeSpan` build error in CronService

### Build Status
- ✅ Solution builds: 0 errors
- ✅ All 322 unit tests passing
- ⚠️ Integration tests have pre-existing failures (CronJobFactory/AgentRouter ambiguous constructor issues from other agents' changes)



### 2026-04-02 — Sprint 7 Complete: CLI Tool, Doctor Diagnostics, Config Hot Reload

**Cross-Agent Update:** Sprint 7 was a major infrastructure sprint combining three interconnected capabilities: the otnexus CLI tool, pluggable doctor diagnostics system, and config hot reload. The CLI tool added 16 commands via System.CommandLine framework for managing BotNexus. The doctor system provides 13 diagnostic checkups across 6 categories (config, security, connectivity, extensions, providers, permissions, resources) with optional auto-fix capability and two fix modes (interactive --fix, force --fix --force). Config hot reload lets the Gateway watch ~/.botnexus/config.json and automatically reload without restart using IOptionsMonitor + FileSystemWatcher. Also deployed three Gateway REST endpoints (/api/status, /api/doctor, /api/shutdown) and fixed a P0 first-run bug where extensions failed to load. Test coverage grew to 443 tests (322 unit + 98 integration + 23 E2E). Kif (Documentation Engineer) joined the team. See .squad/log/2026-04-02T00-34-sprint7-complete.md and .squad/decisions.md Sprint 7 section for full details.

---

## Sprint: 2026-04-03T07:31:24Z

**What:** Comprehensive platform sprint — configuration alignment, provider model exposure, test coverage, documentation.

**Team Output:**
- 6 agents coordinated on common objective
- 1 critical runtime bug fixed (model resolution)
- 45 new tests passing (516 total)
- 950+ lines of documentation
- 5 configuration mismatches resolved
- Full provider model API exposure

**Cross-Agent Dependencies Resolved:**
- Farnsworth's model provider APIs enable Fry's UI dropdown
- Bender's bug fix validates Farnsworth's model interface
- Nibbler's config cleanup enables Hermes' test scenarios
- Kif's docs explain all changes for future maintainers

**Decisions:** API consumer flagging directive (see .squad/decisions.md)

---


---

## Learnings

### Phase 2 WebUI Enrichment (2026-04-04)

**Timestamp:** 2026-04-04  
**Status:** ✅ Complete  
**Commit:** 593731c — feat(webui): Phase 2 enrichment  

**Features Delivered:**
1. **Thinking Display** — Collapsible thinking block for `thinking_delta` events with toggle
2. **Tool Call Enhancement** — Status badges (Running/Done/Error), tool modal, tool count in footer
3. **Sessions Sidebar** — Delete with confirm dialog, loading states, active highlighting
4. **Agent Management** — Add agent form modal, provider/model selection, status dots
5. **Error States** — Styled error messages, reconnecting status, exponential backoff
6. **Activity Monitor** — Collapsible sidebar feed, subscribe toggle, color-coded events

**Patterns Established:**
- `showStreamingIndicator()` / `removeStreamingIndicator()` replaces old `showThinkingIndicator` for processing state
- Thinking block uses `collapsed` class toggle for show/hide, separate from tool visibility
- Tool calls use `data-call-id` attribute for status updates during streaming
- Confirm dialog pattern: `showConfirm(message, title, callback)` — reusable for any destructive action
- Agent form loads providers then models filtered by provider selection
- Activity feed uses `insertBefore(el, firstChild)` for newest-first ordering with max item trim

**Decisions:** All 6 features shipped as single commit (interleaved across 3 tightly-coupled files)

### P0-3 Event Listener Leak Fix (2026-07-24)
**Timestamp:** 2026-07-24
**Status:** Complete
**Commit:** c80a259 — fix(webui): replace per-element listeners with event delegation [P0]

**Fix:** Replaced three per-element addEventListener calls (thinking toggle, tool call click, tool history click) with a single delegated click handler on #chat-messages using event.target.closest(). History tool calls now store data in activeToolCalls map via generated callId instead of closures.

**Pattern Established:** Event delegation on stable parent containers for all dynamically-created interactive elements. Data needed by handlers stored as data-* attributes or in shared lookup maps rather than closure variables.

### WebUI Error/Loading/Reconnect Enhancements (2026-04-05)
**Timestamp:** 2026-04-05
**Status:** Complete
**Commit:** pending — feat(webui): add error states, loading indicators, and reconnection support

**Features Delivered:** Connection banner states (connecting/reconnecting/failure/success), 30s response timeout warning, agent error bubbles (`.message-error`), typing indicator + streaming pulse (`.message-streaming`), send button spinner state (`.btn-sending`), and steer flow via `{ type: "steer" }` with queued badge (`.steer-indicator`).

**Pattern Established:** Keep `currentSessionId` stable across reconnects and immediately rehydrate chat from `GET /api/sessions/{sessionId}` after successful reconnect so UI state self-heals after dropped sockets.

### Thinking/Tool Display Enhancement + Steering UX (2026-07-24)
**Timestamp:** 2026-07-24
**Status:** ✅ Complete
**Commit:** 5202779 — feat(webui): enhance thinking/tool display and steering UX

**Features Delivered:**
1. **Thinking stats** — Live character count in thinking toggle during streaming, final count on completion (e.g., "Thought process (2.3k chars)")
2. **Tool elapsed time** — Live counter on running tool calls, displayed on completion badges (e.g., "✓ Done 3s")
3. **Steer mode UX** — Send button changes to "🧭 Steer" with orange styling when agent is streaming; input placeholder updates to guide user
4. **Reconnection counter** — Banner shows attempt progress (e.g., "attempt 3/10")
5. **State cleanup** — Tool timers and send button state properly reset on abort/error/finalize

**Gateway verification:** Health endpoint (`/health`), WebUI root (`/`), agents/sessions APIs all confirmed working with live Gateway on localhost:5000.

**Patterns Established:**
- `formatCharCount()` utility for human-readable character counts (raw under 1k, "Xk" above)
- `toolStartTimes` map + `setInterval` for live elapsed tracking, cleared on tool_end or abort
- `updateSendButtonState()` manages send button label/style based on `isStreaming` state
- CSS `.btn-steer` class for orange steering mode visual feedback

## Cross-Agent Update (2026-04-06 — Scribe orchestration)

**From:** Leela (Lead)  
**Impact:** Observability proposal (Phase 13) establishes framework for future instrumentation. Fry's WebUI work (Phase 12 Wave 1) already uses correct logging patterns (M.E.L abstractions). No changes needed for Wave 1. Will integrate OTel spans in later waves if tracing becomes requirement for WebUI debugging.

**Action:** None for current sprint. Monitor observability roadmap for Wave 3+ impact on WebSocket/channel layers.

### Sub-Agent Status Panel (Wave 4.3) (2026-04-11)
**Timestamp:** 2026-04-11
**Status:** ✅ Complete
**Commit:** 91f11c3 — feat(webui): add sub-agent status panel with real-time updates

**Features Delivered:**
1. **Collapsible panel** — Between processing-status bar and chat messages; auto-hides when no sub-agents exist
2. **Status indicators** — 🟢 Running, ✅ Completed, ❌ Failed, ⏱ Timed Out, 🛑 Killed with colored left borders
3. **Kill button** — DELETE /api/sessions/{sessionId}/subagents/{subAgentId} with disabled state during request
4. **Result display** — Expandable result summary on completed/failed sub-agents
5. **Real-time WebSocket** — SubAgentSpawned/Completed/Failed/Killed events update panel without polling
6. **REST fetch** — GET /api/sessions/{sessionId}/subagents on session join and manual refresh

**Patterns Established:**
- `activeSubAgents` Map for client-side sub-agent state tracking, cleared on session change/reset
- `SUBAGENT_STATUS_MAP` lookup for consistent emoji/CSS mapping across render and events
- Panel follows existing `section-header` + `collapsed` class toggle pattern from sidebar
- Kill button uses same `fetch(DELETE)` pattern as session delete
- Activity feed integration: sub-agent events tracked via existing `trackActivity()` calls

## Learnings

### Bottom "Agent is thinking" indicator removal

**Commit:** 1326332 — `fix(webui): remove redundant 'Agent is thinking' bottom indicator`

The `showStreamingIndicator()` function appended an "Agent is thinking..." div to `elChatMessages`, causing layout jumps as it was added/removed from the DOM. The top-of-chat `#processing-status` bar already conveys the same information. Made `showStreamingIndicator()` a no-op and removed the orphaned CSS (`.message.thinking`, `.typing-indicator`, `.thinking-dots`, `@keyframes thinkingPulse`). Left `removeStreamingIndicator()` intact as a harmless cleanup call.

### Session Switching Fix — Wave 1 (2026-07-24)
**Timestamp:** 2026-07-24
**Status:** ✅ Complete
**Commit:** c30d4dc — fix(webui): fix session switching during active agent work

**Bug:** Switching agents while one was actively streaming caused stale events to render in the wrong canvas, processing bar persisted across sessions, and orphan sessions were created on every sidebar click.

**Fixes Delivered:**
1. **State reset (W1.1)** — Reset all streaming globals (`isStreaming`, `activeMessageId`, `activeToolCalls`, `activeToolCount`, `thinkingBuffer`, `toolCallDepth`) plus clear timers, hide processing bar/abort button at top of `openAgentTimeline()` before any async work.
2. **Orphan session fix (W1.2)** — Replaced `joinSession(agentId, null)` (which created a throwaway server session) with explicit `LeaveSession` + null out `currentSessionId`.
3. **Session guard (W1.3)** — Added `isEventForCurrentSession(evt)` guard to all 11 SignalR handlers (MessageStart, ContentDelta, ThinkingDelta, ToolStart, ToolEnd, MessageEnd, Error, SubAgentSpawned/Completed/Failed/Killed). Raw string events pass through gracefully.
4. **Status restore (W1.4)** — Call `checkAgentRunningStatus()` after joining the latest session so switching back to a working agent restores the processing UI.

**Pattern Established:** All SignalR event handlers must check `isEventForCurrentSession(evt)` as first line to prevent cross-session rendering during the race window between LeaveSession and server processing.

### Steer button misalignment during streaming

**Commit:** 055d836 — `fix(webui): align steer button with input field during streaming`

`.send-group` used `align-items: flex-end` which caused the Steer button and the ▾ send-mode dropdown to have mismatched heights (different font sizes: 0.85rem vs 0.7rem). Changed to `align-items: stretch` so both buttons fill to the same height, forming a cohesive button group. Also removed an unconditional `.send-group .btn-primary` rule that always zeroed right border-radius even in normal send mode — the existing `:has(.btn-send-mode:not(.hidden))` selector correctly handles this conditionally.

### Per-Session State + Backend Payload — Waves 2-3 (2026-07-24)
**Timestamp:** 2026-07-24
**Status:** ✅ Complete
**Commit:** 8fadbbd — feat(webui): add per-session state management for streaming

**Wave 2 — Backend Verification:**
- `AgentStreamEvent` did NOT include `sessionId`. Added `SessionId` property to the record.
- `SignalRChannelAdapter.SendStreamEventAsync` now enriches events with `sessionId = conversationId` via `with` expression before sending to the SignalR group. This makes the client-side `isEventForCurrentSession(evt)` guard fully effective.

**Wave 3 — Per-Session State Map:**
1. **(W3.1)** Introduced `sessionState` Map + `getSessionState(sessionId)` helper. Returns existing state or creates defaults. LRU ordering: accessing existing entries moves them to end.
2. **(W3.2)** Migrated 7 globals (`isStreaming`, `activeMessageId`, `activeToolCalls`, `activeToolCount`, `thinkingBuffer`, `toolCallDepth`, `toolStartTimes`) from flat variables to per-session state. Added `isCurrentSessionStreaming()` convenience function. Old globals kept as deprecated stubs. Key design: `openAgentTimeline` clears UI elements but does NOT clear outgoing session's per-session state (it may still be streaming server-side).
3. **(W3.3)** LRU eviction caps `sessionState` at 20 entries. `cleanupSessionState(sessionId)` for explicit removal.

### Send-During-Switch Race Condition Fix (2026-07-24)
**Timestamp:** 2026-07-24
**Status:** ✅ Complete
**Commit:** 84b0350 — fix(webui): prevent message send during session switch race window

**Bug:** After Wave 1 fix, a race window remained: `openAgentTimeline()` nulls `currentSessionId` early, then does async work (fetch sessions, render history, join new session). If user types and hits Enter during that gap, `sendMessage()` sees `currentSessionId === null` and calls `joinSession(agentId, null)`, creating an orphan session. Or with unlucky timing, sends to the wrong session.

**Fix (Option B from Nova's research):**
1. **`sessionSwitchInProgress` flag** — New module-scoped boolean, default `false`.
2. **`openAgentTimeline()` wrapped in try/finally** — Sets flag `true` at entry, `false` in `finally` after `joinSession` completes. Early returns (no sessions, no matching channels) also clear the flag via `finally`.
3. **`sendMessage()` guarded** — Returns immediately if `sessionSwitchInProgress` is true, preventing any message dispatch during the async gap.
4. **Input disabled during switch** — `updateSendButtonState()` checks the flag: disables both `elBtnSend` and `elChatInput` while switching. Re-enables in `finally` via `updateSendButtonState()`. User sees "Loading timeline..." so disabled input is natural UX.

**Pattern Established:** Any new function that sends messages to the server should check `sessionSwitchInProgress` before dispatching.

---

### Fix Session Switching Bugs — Channel Type Normalization
**Date:** 2025-07-18
**Requested by:** Jon Bullen
**Commit:** `fix(webui): fix session switching bugs caught by Playwright E2E tests`

**Problem:** 4 of 5 Playwright E2E session-switching tests were failing. Switching A→B→A created a **new** session for A instead of reusing the original. Root cause: channel type mismatch — sessions created via SignalR had `channelType=null` (defaulting to `'signalr'`), but sidebar entries and `openAgentTimeline()` used `'Web Chat'`/`'web chat'`. The filter `(s.channelType || 'signalr') === channelType` never matched, so existing sessions were invisible during switch-back.

**Fix (2 files):**
1. **`app.js` — Added `normalizeChannelKey()` function** that maps `null`, `'signalr'`, `'web-chat'` → `'web chat'`. Applied in:
   - `loadSessions()` — sidebar entries now consistently use `'web chat'` for `data-channel-type` and the `latestByChannel` grouping key
   - `openAgentTimeline()` — session filter and active-class toggle both normalize before comparison
2. **`GatewayHub.cs` — Set `ChannelType` on session creation** — `JoinSession` now sets `session.ChannelType = "signalr"` when null and persists via `SaveAsync`, ensuring data consistency.

**Result:** All 5 Playwright E2E tests pass (BasicSwitchAndSend, SwitchBackAndSend, RapidSwitchAndSend, SendDuringLoading, InboundEventIsolation). All 1,550+ unit tests pass. Build clean.

### Fix Stuck UI After Session Switch — Flag and Input Recovery
**Date:** 2025-07-18
**Requested by:** Jon Bullen
**Commit:** `fix(webui): fix stuck UI after session switch — flag and input recovery`

**Problem:** Sending a message to an agent and then switching agents left the UI stuck — send button disabled, "1 message queued" badge persisted, and no interaction was possible. Full page refresh didn't reliably fix it.

**Root causes (3 bugs):**
1. **`isRestRequestInFlight` stuck:** `sendMessage()` sets this flag via `setSendingState(true)`. When the user switches agents, the `MessageStart` event from the old session is dropped by `isEventForCurrentSession()`, so `setSendingState(false)` is never called. `updateSendButtonState()` sees the stuck flag and keeps the send button disabled.
2. **`messageQueueCount` not cleared:** `openAgentTimeline()` never called `resetQueue()`, so the "1 message queued" display persisted across agent switches.
3. **No concurrency guard:** Rapid sidebar clicks fired overlapping async `openAgentTimeline()` calls. A stale call's `finally` block could reset `sessionSwitchInProgress = false` while the newer call was still loading, creating a window where the UI appeared ready but state was inconsistent.

**Fix (1 file — `app.js`):**
1. **Reset flight/queue state at switch start:** Added `if (isRestRequestInFlight) setSendingState(false)` and `resetQueue()` at the top of `openAgentTimeline()`'s try block.
2. **Version counter for concurrency:** Added `timelineSwitchVersion` counter (same pattern as existing `joinSessionVersion`). Each call captures its version; after every `await`, checks if a newer switch superseded it and bails early.
3. **Version-conditional finally:** Only the latest switch clears `sessionSwitchInProgress`. Stale calls' finally blocks are no-ops.

**Pattern Established:** Async functions with global flag side effects must use version counters when re-entrant calls are possible. Reset all outgoing-session global state (flight flags, queue counts) at the START of a switch, not at the end — because end-of-stream events for the old session may never arrive.

## cli-latest — Floating 'New messages' button when scrolled up

**Commit:** 4ea3bfb — `feat(webui): add floating 'New messages' button when scrolled up`

### Deliverables

1. **HTML:** Added `<button id="btn-new-messages">` inside chat-view, after the existing scroll-to-bottom button.
2. **CSS:** Styled as a centered floating pill — `rgba(88,166,255,0.9)` background, `border-radius: 20px`, `box-shadow`, hover scale effect. Mobile-responsive rule added.
3. **JS — State tracking:** Added `newMessageCount` variable and `elNewMessages` DOM reference. Three helpers: `incrementNewMessageCount()`, `resetNewMessageCount()`, and extended `updateScrollButton()` to reset on natural scroll-to-bottom.
4. **JS — Increment on message finalize:** `finalizeMessage()` calls `incrementNewMessageCount()` before `scrollToBottom()`. When user is scrolled up, button shows "↓ N new messages".
5. **JS — Button click:** Calls `scrollToBottom(true)` + `resetNewMessageCount()` to dismiss.
6. **JS — Session switch reset:** `openAgentTimeline()` calls `resetNewMessageCount()` during cleanup.

**Files changed:** `index.html`, `styles.css`, `app.js` (3 files, +54 lines)
**Validation:** `node --check app.js` passed.

## Learnings — BotNexus.Probe Web UI

### Architecture Decisions
- **Pure vanilla HTML/CSS/JS** — no build tooling, no frameworks. Static files served from wwwroot/ via ASP.NET.
- **Dark theme** with CSS custom properties for the full color palette (--bg, --surface, --card, --primary, etc.) at :root level.
- **ProbeApi static class** in probe.js wraps all fetch() calls with consistent error handling. SSE via EventSource for live stream.
- **IIFE pattern** for all page-specific JS to avoid global scope pollution. Only functions called from inline HTML handlers are exposed via window.
- **DOM helper functions** ($, ___BEGIN___COMMAND_DONE_MARKER___$LASTEXITCODE, el) in probe.js eliminate repetitive createElement boilerplate across all pages.

### Key File Paths
- `tools/BotNexus.Probe/src/BotNexus.Probe/wwwroot/css/probe.css` — Complete dark theme stylesheet (~22KB)
- `tools/BotNexus.Probe/src/BotNexus.Probe/wwwroot/js/probe.js` — ProbeApi client + shared utilities (~8KB)
- `tools/BotNexus.Probe/src/BotNexus.Probe/wwwroot/index.html` — Dashboard with status cards + quick correlate
- `tools/BotNexus.Probe/src/BotNexus.Probe/wwwroot/logs.html` — Log browser with sticky filter bar
- `tools/BotNexus.Probe/src/BotNexus.Probe/wwwroot/sessions.html` — Split-panel session viewer
- `tools/BotNexus.Probe/src/BotNexus.Probe/wwwroot/traces.html` — OTEL trace waterfall visualization
- `tools/BotNexus.Probe/src/BotNexus.Probe/wwwroot/live.html` — SSE-powered live activity stream
- `tools/BotNexus.Probe/src/BotNexus.Probe/wwwroot/correlate.html` — Cross-source correlation pivot page
- Page-specific JS in `wwwroot/js/`: dashboard.js, logs.js, sessions.js, traces.js, live.js, correlate.js

### Patterns
- Log levels color-coded: DEBUG=gray, INFO=cyan, WARN=yellow, ERROR=red, FATAL=magenta
- All timestamps displayed in local timezone with UTC tooltip
- Correlation is the central concept — every ID is clickable and links to the correlate page
- Trace waterfall calculates bar positions from nanosecond timestamps relative to trace start
- Handles both flat span arrays and OTLP ResourceSpans format for trace data

### WebUI Top Banner, Layout Restructure, and Agent Dropdown (2026-04-12)

**Timestamp:** 2026-04-12  
**Status:** ✅ Complete  
**Commit:** fc4ab90 — feat(webui): add top banner, two-column layout, and agent session dropdown

**Features Delivered:**
1. **App Banner** — Full-width header above sidebar+main with BotNexus logo and connection status indicator
2. **Announcements Bar** — Dismissible announcement items below banner; hidden when empty; uses localStorage for dismissed items
3. **Layout Restructure** — Body changed to flex-column; banner at top, #app container below takes remaining height
4. **Agent Dropdown** — Replaced tree-style collapsible agent groups with <select> dropdown for agent selection
5. **All Sessions View** — Shows ALL non-expired sessions for selected agent (not just latest-per-channel)
6. **Session Persistence** — Selected agent ID persisted in sessionStorage across soft refreshes

**Files Changed:**
- `index.html` — Added banner + announcements bar above #app; simplified sessions section with dropdown
- `styles.css` — Added banner, announcements, and dropdown styles; restructured body layout to flex-column
- `ui.js` — Added agentSelectorDropdown to dom cache
- `sidebar.js` — Rewrote loadSessions() to populate dropdown + render all sessions per selected agent; added announcements module

**Patterns Established:**
- `getSelectedAgentId()` / `setSelectedAgentId()` for sessionStorage persistence
- `renderSessionsForAgent(agentId)` renders flat list of all active sessions (filters out expired/sealed)
- `renderAnnouncements(announcements)` manages announcements bar with dismissal via localStorage
- Connection status moved from sidebar to app banner (single source of truth at top)
- Sidebar header now only shows world identity (emoji + name)

**Design Notes:**
- Sub-agent sessions included in the all-sessions list with 🧩 tag indicator
- Dropdown initialized once with `data-initialized` flag to avoid duplicate event handlers
- Fingerprint-based rendering still prevents unnecessary list re-renders
- Announcements expected from `/world` API response as optional array of `{ id, text, type? }` objects


## 2026-04-15 — Blazor UI Layout Restructure

**Status:** ✅ Complete  
**Commit:** 48694d9e  

**Features Delivered:**

1. **MainLayout.razor restructure:**
   - Full-width banner header (🤖 BotNexus logo + title)
   - Dismissible announcements bar below banner (UI shell ready; API wire-up pending)
   - Two-column body: fixed 240px sidebar + flex main canvas
   - Sidebar owns: connection status, nav links, agent dropdown, session list, restart button
   - Agent dropdown + session list persist across page navigation
   - Subscribes to Manager.OnStateChanged for re-render on state updates

2. **Home.razor simplification:**
   - Removed agent list/sidebar controls (now in MainLayout)
   - Only renders chat panels (one per agent, show/hide with active/hidden classes)
   - InitializeAsync still called here (checks Manager.Hub.IsConnected to avoid double-connect)
   - Empty state message updated to "Select an agent from the sidebar to start chatting"

3. **CSS updates (app.css):**
   - Added .app-shell, .app-banner, .banner-header, .banner-logo, .banner-title
   - Added .announcement-bar, .announcement-item, .announcement-content, .announcement-dismiss
   - Added .app-body, .main-sidebar, .sidebar-connection, .main-canvas
   - Added .agent-dropdown-container, .agent-dropdown-label, .agent-dropdown-select
   - Added .agent-session-list, .agent-session-item (active state: order-left: 2px solid var(--accent))
   - Removed obsolete .app-layout, .sidebar, .sidebar-header, .sidebar-content
   - Updated .chat-panel-wrapper flex order (hidden/active via display)
   - Updated .empty-state font-size to 0.95rem

4. **AGENTS.md created:**
   - Non-obvious layout decisions documented (why agent list is in MainLayout, not Home)
   - OnStateChanged event pattern for all components that need re-render
   - "Expired" session filter definition (Killed/Failed sub-agents hidden)
   - Where InitializeAsync is called and why (Home.razor, not MainLayout)
   - CSS gotchas (agent dropdown @onchange uses ChangeEventArgs, not @bind with async)

**Pattern Established:**
- MainLayout as structural shell — owns global UI elements (banner, announcements, sidebar)
- Page components (Home.razor, Configuration.razor) render in MainLayout's @Body slot
- Agent selection state persists across all pages via MainLayout sidebar
- Sub-agents filtered to Running/Completed only (Killed/Failed are "expired")

**Gotchas Discovered:**
- Agent dropdown <select> uses @onchange with ChangeEventArgs (not @bind with async handler)
- Empty option value is "", not 
ull
- Session ID truncation needs Math.Min(8, sub.SubAgentId.Length) to avoid index errors
- Restart button catches empty — connection drop is expected behavior
- GatewayHubConnection.IsConnected is a property (line 69), not a method

