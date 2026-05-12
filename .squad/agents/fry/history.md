# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform built in C#/.NET
- **Stack:** C# (.NET latest), Blazor Server, SignalR
- **Created:** 2026-04-01

## Core Context

**Fry's Specialization:** Web Dev for BotNexus WebUI (Blazor + vanilla JS). Owns chat panel, message rendering, streaming UI, WebSocket integration, layout structure.

**Key Delivered Features:**
- Phase 4 Wave 1: Thinking blocks, tool timers, steer mode UX, reconnection banners
- Sub-Agent Feature Waves 1-4: WebUI panel with real-time updates + kill button + ownership validation
- Session Switching: Fixed race condition (async re-entry, flight flags, version counters)
- Floating 'New Messages' Button: Increment counter when scrolled up, dismiss on click
- Blazor Layout Restructure: Agent list/sidebar from Home.razor to MainLayout.razor, dropdown + session list
- Auto-Scroll Bug Fix Wave 1: Reordered OnAfterRenderAsync (markdown-first, scroll-last), hardened chat.js

**Key Patterns Established:**
1. **Version counters for re-entrant async** — capture version; after every await, bail if superseded
2. **Reset state at switch START, not end** — flags reset before awaiting new operations
3. **OnAfterRenderAsync reorder for Blazor** — separate DOM-mutating work from scroll logic
4. **Component-level CSS state** — data attributes or classes for active state via component lifecycle

**Test Philosophy:** Manual browser testing for scroll/layout UX; bUnit for component lifecycle; Playwright for E2E.

## Learnings

### 2026-05-11 — Cron Virtual Session Cleanup Must Route Through Conversation Archive
Virtual cron conversation projections (`cron-session:{sessionId}`) should be cleaned up via `DELETE /api/conversations/{conversationId}` (ArchiveConversationAsync), NOT `DELETE /api/sessions/{sessionId}`. The backend handles `cron-session:` prefixed IDs idempotently — returns 204 even when no backing session exists. This preserves session records/history while hiding the conversation from the sidebar. The prior approach of calling session delete was incorrect as it permanently destroyed session data. Stale orphans (no ActiveSessionId) also route through conversation archive since the backend handles them gracefully.

### 2026-05-11 — PortalLoadService Must Not Abort on Stale Cron History 404
When loading initial history during portal startup, virtual cron-session projections must use `GetSessionHistoryAsync` (session endpoint), not `GetHistoryAsync` (conversation endpoint). If a stale cron projection returns 404, it must be removed and the service must retry with the next conversation — never allow a single 404 to abort `InitializeAsync` for all agents/conversations. The fix uses a `while` loop with fallback and catches `HttpRequestException` with 404 status specifically.

### 2026-07-29 — JS Interop Must Guard Against Non-DOM ElementReference
Blazor's `ElementReference` for conditionally-rendered elements (e.g., `@if (!IsReadOnly)`) serialises as a truthy non-element object when the element is absent. JS helpers receiving ElementReferences must check `typeof element.addEventListener === 'function'` (not just `!element`) before using DOM APIs. The Blazor side should also skip the JS call entirely when the element is known to be absent, and reset any binding flags when the element may have been destroyed and recreated (e.g., read-only → interactive transitions).
