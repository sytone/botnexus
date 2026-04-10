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
