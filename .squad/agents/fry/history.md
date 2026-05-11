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

### 2026-05-11 — Cron Virtual Session Cleanup Must Route Through Session Deletion
Virtual cron conversation projections (`cron-session:{sessionId}`) are UI-only constructs backed by session data, not real conversation records. Cleanup must call `DELETE /api/sessions/{sessionId}` — not `DELETE /api/conversations/{virtualId}` — or the backend returns 404. Stale orphans with no `ActiveSessionId` should be cleaned up locally without any API call. The `file.exe` URL prefix in browser dev-tools console logs was a display artifact, not a real REST base URL issue.
