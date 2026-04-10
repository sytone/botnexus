---
updated_at: 2026-04-10T17:35:00Z
focus_area: Session Switching Bug Delivered
active_issues: []
status: session_switching_delivered
---

# What We're Focused On

**Session switching bug delivered (2026-04-10 17:35Z).** 4-wave concurrent pipeline: Wave 1 (Fry: core fix + guards + orphan cleanup), Wave 2 (Fry: backend sessionId), Wave 3 (Fry: per-session state Map), Wave 4 (Hermes: 7 tests). Design review led by Leela: 3 patterns confirmed, 6 decisions. Build green. Commits: 8fadbbd, b549fb5.

**Previous:** Sub-Agent Spawning feature delivered (2026-04-10 16:30Z). 4-wave concurrent delivery: Wave 1 (abstractions + config), Wave 2 (runtime implementation), Wave 3 (tools), Wave 4 (REST + WebUI + docs). 51 SubAgent tests all passing. Commits: f57b157, b614205, ff63957, 4d4b6a7, 25c8876, ad72475, c75a033, 91f11c3, 041d65a.

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
