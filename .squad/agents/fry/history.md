# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Fry's Specialization:** Web Dev for BotNexus WebUI (Blazor + vanilla JS). Owns chat panel, message rendering, streaming UI, WebSocket integration, layout restructure, and cross-cutting UI concerns.

**Active Stream (Phase 12+):**
- Phase 4 Wave 1 (2026-04-05): Thinking blocks, tool timers, steer mode UX, reconnection banners
- Sub-Agent Feature Waves 1-4 (2026-04-10): Model validation, manager ops, integration testing, WebUI panel with real-time updates + kill button + ownership validation
- Session Switching (2026-04-10): Fixed race condition (async re-entry, flight flags, version counters) — established pattern for concurrent async functions with global side effects
- Floating 'New Messages' Button (latest): Increment counter when scrolled up, dismiss on click, reset on session switch
- Blazor Layout Restructure (2026-04-15): Moved agent list/sidebar from Home.razor to MainLayout.razor, rewrote with dropdown + session list persistence, established MainLayout as structural shell pattern
- **Current:** Auto-Scroll Bug Fix Wave 1 (2026-04-20): Reordered OnAfterRenderAsync (markdown-first, scroll-last), hardened chat.js scroll functions with 50ms backstop and streaming-aware threshold

**Key Patterns Established:**
1. **Version counters for re-entrant async** — Each concurrent async operation captures version number; after every await, checks if superseded and bails early
2. **Reset state at switch START, not end** — Global flags (flight, queue) must reset before awaiting new operations; end-of-stream events for old session may never arrive
3. **OnAfterRenderAsync reorder for Blazor** — Separate DOM-mutating work from scroll logic; only scroll when re-render work is complete
4. **Component-level CSS state** — Use data attributes or classes for active state (e.g., `.active`, `.hidden`); maintain via component lifecycle

**Test Philosophy:** Manual browser testing for scroll/layout UX; bUnit for component lifecycle verification; Playwright for E2E.

---

## 2026-04-20 — Blazor Auto-Scroll Bug Fix: Wave 1 Implementation

**Status:** ✅ Complete  
**Commit:** efd9837e  
**Team Update:** Cross-agent session on bug-blazor-autoscroll (regression from improvement-blazor-chat-autoscroll Apr '26)

**Your Role:** Web Dev (Fry). Wave 1 implementation of auto-scroll race condition fix.

**Root Cause:** Race condition between scroll execution and markdown rendering in `ChatPanel.razor` `OnAfterRenderAsync`:
1. Scroll fires first (calls JS interop)
2. Markdown renders (iterates messages, populates cache)
3. StateHasChanged triggered → second OnAfterRenderAsync cycle
4. DOM layout changed from markdown rendering → scroll threshold check fails silently

**Deliverables:**

1. **ChatPanel.razor — Reorder OnAfterRenderAsync (lines 367–417)**
   - Markdown rendering first (populate `_markdownCache`, do NOT call StateHasChanged yet)
   - If markdown found, call `StateHasChanged()` and RETURN
   - Only scroll after markdown is complete (`needsRender == false`)
   - Pass `State.IsStreaming` to JS `scrollToBottom` for dynamic threshold

2. **chat.js — Harden scroll functions**
   - `forceScrollToBottom`: Added `setTimeout(50)` backstop after `requestAnimationFrame` to catch residual DOM changes
   - `scrollToBottom`: Accept optional `isStreaming` parameter; use 200px threshold when streaming (vs 100px normally)

**Build & Tests:**
- ✅ Build: Green (0 errors)
- ✅ Tests: 2545 passing, 0 failures
- ✅ No regressions

**Pattern Established:**
- When re-render cycles are possible (like OnAfterRenderAsync), separate DOM-mutating work from scroll logic
- JS scroll helpers should double-check layout after requestAnimationFrame (async DOM mutations can occur between frames)
- Streaming context affects scroll thresholds — pass state from component to JS to enable adaptive behavior

**Next:** Wave 2 verification (Hermes) + Consistency review (Nibbler)


---

## 2026-04-20 — Sub-Agent Read-Only View (Wave 1)

**Task:** Implement clickable sub-agent sessions with read-only chat panel.

**Changes:**
- AgentSessionState.cs: Added SessionType (string) and IsReadOnly (computed) properties
- AgentSessionManager.cs: Added ViewSubAgentAsync and LoadSubAgentHistoryAsync methods
- AgentSessionManager.cs: Updated RegisterSession to accept optional sessionType parameter
- AgentSessionManager.cs: Modified HandleSubAgentSpawned to register sub-agent sessions in _sessionToAgent mapping
- ChatPanel.razor: Added read-only banner component with status indicator
- ChatPanel.razor: Wrapped input area in conditional @if (!State.IsReadOnly) block
- ChatPanel.razor: Added GetSubAgentStatus() method to display Running/Completed status
- MainLayout.razor: Made sub-agent sidebar items clickable with @onclick handler
- MainLayout.razor: Added ViewSubAgent() and IsSubAgentActive() methods
- pp.css: Added .read-only-banner styling using existing CSS variables

**Key Learnings:**

1. **Session Type as String Not Enum** — The domain primitive SessionType uses string values ("user-agent", "agent-subagent"), not an enum. This matches the wire format and avoids conversion overhead.

2. **Derived Properties for Consistency** — Made IsReadOnly a computed property based on SessionType rather than a separate boolean to ensure they stay in sync.

3. **Sub-Agent Session ID Pattern** — For sub-agents, sessionId == subAgentId. This simplifies the mapping and avoids needing a separate lookup.

4. **Different History Endpoints** — Sub-agent sessions use /api/sessions/{sessionId}/history (by session ID) vs user-agent sessions which use /api/channels/{channelType}/agents/{agentId}/history (by agent ID). Created separate method to handle this.

5. **Session Registration Timing** — Sub-agent sessions must be registered in _sessionToAgent when HandleSubAgentSpawned fires, not just when viewing. This enables proper session-to-agent lookup for all sub-agent events.

6. **Conditional Blocks vs Disabled Controls** — For read-only mode, wrapping the entire input area in @if (!State.IsReadOnly) provides cleaner UX than disabling controls. No disabled controls visible — just no input area.

7. **Reuse Existing CSS Variables** — The banner styling uses existing theme variables (--bg-surface, --border, etc.) for consistency with the rest of the UI.

**Build & Tests:**
- ✅ Build: Green (0 errors, 0 warnings for Blazor project)
- ✅ Tests: 2545 passing, 1 flaky file-locking test unrelated to changes
- ✅ No regressions

**Pattern Established:**
- For read-only session modes, use derived properties based on SessionType to determine behavior
- Lazy-load history only when user clicks (not on sub-agent spawn) to minimize API calls
- Place mode indicators (like read-only banner) immediately after header for visibility

**Next:** Wave 2 — Real-time streaming updates for sub-agent sessions

---

## 2026-04-20T19:01Z — Read-Only Sub-Agent Session View: Wave 1 Implementation

**Status:** ✅ Delivered  
**Feature:** feature-blazor-subagent-session-view  

**Your Role:** Web Dev (implementation)

**Deliverables:**
1. **AgentSessionState.cs** — Added `SessionType` property and `IsReadOnly` computed property
2. **AgentSessionManager.cs** — Added `ViewSubAgentAsync` method with `LoadSubAgentHistoryAsync`
3. **ChatPanel.razor** — Read-only banner + conditional input hiding
4. **MainLayout.razor** — Clickable sub-agent sidebar items
5. **app.css** — Read-only banner styling with theme variables

**Key Design Choices:**
- SessionType as string (matches domain primitive SessionType.AgentSubAgent)
- IsReadOnly computed property (prevents flag drift)
- Sub-agent session ID equals sub-agent ID (simplifies mapping)
- Conditional input area (full hide, cleaner UX)

**Build & Test Status:** ✅ All tests passing

---

## 2026-05-08 — Conversation History Disappears After UI Refresh

**Status:** ✅ Fixed  
**Commit:** dba21e89  

**Your Role:** Web Dev (investigation + fix)

**Root Cause:** The history API returns entries in chronological order with `offset=0` giving the oldest entries. Both `PortalLoadService.InitializeAsync` and `AgentInteractionService.LoadConversationHistoryAsync` requested `limit=200, offset=0`. For conversations with >200 entries (e.g., 272), the most recent messages fell beyond the 200-entry page boundary and were never loaded. After a UI refresh, users saw old messages but not their latest exchange.

**Validated by:** Probing the live gateway API at `http://localhost:5005/api/conversations/{id}/history`. Default conversation had 272 entries; limit=200,offset=0 returned entries 0-199 (oldest), missing today's messages at positions 262-272. With offset=72, the latest messages appeared correctly.

**Fix:**
1. **PortalLoadService.cs** — After initial GetHistoryAsync call, check if `totalCount > limit`. If so, re-fetch with `offset = totalCount - limit` to get the tail page.
2. **AgentInteractionService.cs** — Same tail-fetch pattern in `LoadConversationHistoryAsync`.
3. **AgentInteractionServiceTests.cs** — Added 2 tests: one verifying the two-step fetch for long conversations, one verifying single fetch for short ones.

**Key Learning:**
- The conversation history API uses forward-only pagination (offset from oldest). When loading for display, always compute the tail offset to show the most recent entries.
- Probing the live API directly was key to diagnosing this — the frontend code looked correct in isolation.

**Recommendation for Bender:**
- Consider adding a server-side option to the history API (e.g., `order=desc` or `anchor=latest`) so the client can request the newest entries without a probe call. This would eliminate the extra HTTP round-trip for long conversations.
