---
updated_at: 2026-04-12T00:13:00Z
focus_area: Infinite Scrollback Wave 1–2 Delivered
active_issues: [Wave 3 UI blocked on Fry]
status: scrollback_wave2_complete
---

# What We're Focused On

**Infinite scrollback Wave 1 & 2 delivered (2026-04-12 00:13Z).** API for cross-session history + IntersectionObserver client pattern. 4-agent team: Leela design review + wave plan, Farnsworth (ListByChannelAsync + ChannelHistoryController), Bender (IntersectionObserver + fetchOlderMessages, removed broken loadEarlierMessages/loadOlderSessions), Hermes (19 tests: 10 API + 3 store + 6 pagination). Commits: 21fb7bb, fcc3785, 5ab9951.

**Previous:** Multi-session connection model fully delivered (2026-04-11 19:30Z). Fundamental architectural pivot: sessions now pre-warmed at gateway startup, WebUI holds all sessions simultaneously (separate connection per channel/session), switching is pure UI with zero server calls. Eliminated entire class of race conditions. 6 commits, 9 tests, 83/83 E2E passing.

## Current Status

✅ **Sprint:** Phase 12 complete (26+ commits)
✅ **Build:** 0 errors, 0 warnings
✅ **Tests:** 1,015 passing (155 Core + 81 Anthropic + 60 OpenAI + 40 OpenAICompat + 26 Copilot + 71 AgentCore + 146 CodingAgent + 436 Gateway)
✅ **P0s:** 0 open
✅ **Design Reviews:** A- (W1), A- (W2), A (W3)
✅ **Gateway Tests:** 337 → 436 (+99)

## Phase 12 Deliverables

### Wave 1 — Security + Foundation
- Fixed P0 auth bypass (Path.HasExtension → route allowlist)
- Fixed P0 AssemblyPath information disclosure
- Added GET /api/channels and GET /api/extensions endpoints
- Moved SessionHistoryResponse to Abstractions
- WebSocket channel README
- +23 gateway tests

### Wave 2 — Middleware + WebUI Enhancement
- Rate limiting middleware (per-client, configurable)
- Correlation ID middleware
- Session metadata GET/PATCH API
- Config versioning with migration hooks
- WebUI channels panel + extensions panel
- Auth middleware DIP fix (constructor injection)
- SupportsThinkingDisplay naming alignment
- API reference update (all endpoints documented)
- +24 gateway tests

### Wave 3 — Persistence + Documentation
- SQLite session store (Microsoft.Data.Sqlite)
- Agent health check endpoint
- Agent lifecycle events (registered/unregistered/config-changed)
- Session metadata caller authorization
- Rate limiter stale-entry eviction
- WebSocket protocol specification (724 lines)
- Configuration reference guide (676 lines)
- Developer guide update
- +23 gateway tests

## Remaining Backlog (P1/P2)
1. DefaultAgentRegistry.PublishActivity sync-over-async in lock
2. WebUI module splitting (app.js 73KB → ES modules)
3. WebUI model selector
4. Telegram steering support
5. Config diff CLI command
6. E2E integration suite (full gateway lifecycle test)
7. StreamAsync task leak (providers — user review needed)
