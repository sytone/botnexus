# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI/Copilot), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-7A Complete. Full Design Review Complete. Phase 12 Extension-Commands Design Review Complete (Grade: B+).** Build green (0 errors), 276 tests passing (up from 264), Full Review grade A-. Core systems operational:
- Agent registry, supervisor, cross-agent calling with recursion guard + depth limits + timeout
- WebSocket (with reconnect replay + sequence IDs), TUI (with steering), Telegram channel adapters
- File and in-memory session stores (configurable via platform config)
- Session suspend/resume, paginated history, bounded message queuing with backpressure
- OAuth + API key auth
- Provider abstraction: OpenAI, Anthropic, Copilot
- WebUI dashboard with thinking/tool display, reconnection, activity feed
- DIP fix: GatewayWebSocketHandler now uses IGatewayWebSocketChannelAdapter interface
- OpenAPI spec export
- Comprehensive integration tests (39 new tests in Sprint 7A)

**Carried Findings (Sprint 7B):**
- `Path.HasExtension` auth bypass in `GatewayAuthMiddleware`
- StreamAsync background task leak in providers
- SessionHistoryResponse should move to Abstractions.Models
- Monitor GatewaySession SRP — extract replay buffer if it grows further

**Phase 7 Focus:** Resilience (reconnection, pagination, queueing), channel consolidation, test hardening, observability.

---

## Learnings — Provider/Model Configuration Proposal (2026-04-08)

1. **BuiltInModels registers 29 models across 3 providers unconditionally** — `RegisterAll()` populates github-copilot (20), anthropic (4), and openai (5) models with no filtering. The `ModelRegistry` has no concept of active/inactive and no allowlist mechanism. Both `ProvidersController` and `ModelsController` return everything unfiltered.
2. **ProviderConfig already exists but is minimal** — `PlatformConfig.Providers` is a `Dictionary<string, ProviderConfig>` with only `ApiKey`, `BaseUrl`, and `DefaultModel`. No `Enabled` flag, no `Models` allowlist. Adding these fields is backward-compatible since both default safely (`Enabled = true`, `Models = null` = all).
3. **Decorator/wrapper beats mutation for model filtering** — Don't modify `ModelRegistry` contents to filter. Introduce `IModelFilter` that wraps the full registry and applies config-based filtering. The full registry stays available for admin/diagnostics. Controllers switch from `ModelRegistry` to `IModelFilter`.
4. **AgentDescriptor has no model restriction concept** — Only `ModelId` (single default) and `ApiProvider` exist. Adding `AllowedModelIds` as an `IReadOnlyList<string>` with empty = unrestricted semantics maintains backward compat and follows the same pattern as `ToolIds` and `SubAgentIds`.
5. **3-layer filtering composes cleanly** — Layer 1 (platform config: enabled + allowlist) → Layer 2 (API endpoints: return filtered) → Layer 3 (per-agent: intersection with agent's AllowedModelIds). Each layer is independent and testable.
6. **GatewayAuthManager already has provider config lookup** — The auth resolution chain (auth.json → env vars → PlatformConfig) and `TryGetProviderConfig()` helper mean the `Enabled` flag can integrate naturally into the auth flow. A disabled provider could short-circuit auth resolution.

## 2026-04-10T16:30Z — Sub-Agent Spawning Feature Design Review (Lead)

**Status:** ✅ Complete  
**Session:** Design Review ceremony → Approved with 6 modifications

**Your Role:** Lead/Architect. Design review facilitation, spec assessment, architectural decision finalization.

**Deliverables:**
- Spec assessment complete: identified 7 gaps (existing infrastructure not acknowledged, god tool pattern, result delivery mechanism, parallel database table, agentId semantics confusion, incomplete spec, workingDir security)
- 6 architectural decisions finalized: extend existing infrastructure, separate tools, completion via FollowUpAsync, session metadata tracking, no recursive spawning Phase 1, workingDir security
- Risk register: 10 mitigations (resource exhaustion, recursion, orphaned sessions, context leak, tool scoping, delivery race, cost surprise, store growth, timeout race, test determinism)
- Work breakdown: 4-wave delivery model with parallelization notes, ~45-55 new tests estimated
- 3 open items for Jon: tool pattern confirmation, database schema confirmation, scope alignment

**Impact:**
- Unified design direction across 6 agents (Leela, Farnsworth, Hermes, Bender, Kif, Fry)
- 4 concurrent waves enabled with clear parallelization boundaries
- 51 SubAgent tests all passing
- Feature delivered: ISubAgentManager, DefaultSubAgentManager, 3 tools, REST endpoints, WebSocket events, WebUI panel, 470-line feature doc

---

## 2026-04-09 — Unified Config + Agent Directory Proposal (Lead)

**Status:** ✅ Proposal written
**Requested by:** Jon Bullen
**Scope:** Unified config architecture — eliminate 3-source config fragmentation

**Context:**
Config was fragmented: (1) config.json inline agents via PlatformConfigAgentSource (no hot-reload), (2) separate .json files via FileAgentConfigurationSource (hot-reload via FSW), (3) workspace files flat in agent root. Jon requires single config source + structured agent directories.

**Analysis Findings:**
1. PlatformConfigAgentSource.Watch() returns null — inline agents don't hot-reload
2. AgentDefinitionConfig is a subset of FileAgentConfigurationSource's schema — missing displayName, description, allowedModels, subAgents, maxConcurrentSessions, metadata, isolationOptions
3. ProviderConfig lacks Enabled flag and Models allowlist
4. BotNexusHome scaffolds workspace files flat in agent root (no workspace/ subdirectory)
5. PlatformConfigLoader.ConfigChanged event exists but PlatformConfigAgentSource doesn't subscribe
6. FileAgentConfigurationWriter creates individual .json files — becomes obsolete with unified config

**Proposal Covers:**
- Unified config.json v2 schema with enriched ProviderConfig + AgentDefinitionConfig
- Agent directory restructure: workspace/ + data/sessions/ subdirectories
- Migration plan: legacy workspace auto-migration, agentsDirectory deprecation path
- Hot-reload: PlatformConfigAgentSource.Watch() via ConfigChanged subscription
- 5-phase implementation plan (A-E) with agent assignments
- Removal list: FileAgentConfigurationWriter, FileAgentConfigurationSource (deferred)

**Decision written to:** `.squad/decisions/inbox/leela-unified-config.md`

## Learnings — Unified Config Architecture (2026-04-09)

1. **PlatformConfigAgentSource.Watch() is the critical gap** — It returns null while FileAgentConfigurationSource has full FSW hot-reload. The fix is simple: subscribe to the existing PlatformConfigLoader.ConfigChanged static event and re-invoke LoadAsync.
2. **AgentDefinitionConfig is underspecified** — Missing 7 fields that FileAgentConfigurationSource already supports (displayName, description, subAgents, maxConcurrentSessions, metadata, isolationOptions, allowedModels). This asymmetry means agents created via API (which write to file) have richer config than inline agents.
3. **Workspace file migration needs atomic move** — Moving SOUL.md from agent root to workspace/ subdirectory must check for legacy layout and move, not copy. File.Move is atomic on same volume.
4. **systemPromptFile resolution base changes** — Currently relative to configDirectory (where config.json lives). Must change to agent home (~/.botnexus/agents/{id}/) so "workspace/SOUL.md" resolves correctly.
5. **PlatformConfigAgentWriter is the hardest piece** — Atomic read-modify-write of config.json with concurrent edit safety. Needs advisory lock + temp-file-rename pattern.

## 2026-04-09 — Memory System Feature Spec Review (Lead)

**Status:** ✅ Review + Plan delivered
**Requested by:** Jon Bullen
**Scope:** Architectural review of Nova's memory system feature spec + phased implementation plan

**Context:**
Nova produced a 310-line feature spec for agent persistent memory (FTS5 search, auto-indexing, embedding pipeline, compaction). Reviewed against BotNexus architecture: tool system, session lifecycle, config schema, DI patterns, agent data directories.

**Key Findings:**
1. **Strong alignment overall** — SQLite storage, per-agent isolation, config-driven approach all match existing patterns
2. **Tool registration gap** — Memory tools need agent context (ID, config) but IAgentToolFactory.CreateTools(string) only takes workspace path. Solution: create memory tools directly in InProcessIsolationStrategy.CreateAsync()
3. **Session lifecycle events missing** — ISessionStore has no event bus. Recommended new ISessionLifecycleEvents interface for memory indexing (and future features)
4. **Spec schema bug** — FTS5 content_rowid needs INTEGER but spec uses TEXT ULID as PK. Need explicit INTEGER rowid
5. **Chunking strategy undefined** — Recommended per-exchange (user+assistant pair) as indexing granularity
6. **Deduplication gap** — Need idempotency key (session_id + turn_index) to prevent duplicate memories from incremental saves

**Deliverables:**
- Architectural review (alignment, SOLID, over-engineering risks, gaps)
- Integration analysis (tool system, session lifecycle, config mapping, DB path)
- 4-wave implementation plan with agent assignments (Farnsworth/Bender/Hermes)
- 5 key design decisions with rationale
- Risk assessment (performance, concurrency, growth, injection, migration)

**Decision written to:** `.squad/decisions/inbox/leela-memory-system-review.md`

## 2026-04-10 — Cron Infrastructure Architecture Proposal (Lead)

**Status:** ✅ Proposal delivered
**Requested by:** Jon Bullen
**Scope:** Full architecture proposal for BotNexus cron system, based on OpenClaw reference implementation

**Context:**
Jon requested a cron scheduling subsystem for BotNexus. Analyzed OpenClaw's mature TypeScript cron implementation (~25 files across src/cron/) — CronService orchestrator, JSON file store, Croner scheduler, agent tool, isolated sessions, delivery system. Designed C#/.NET equivalent adapted to BotNexus patterns.

**Key Design Decisions:**
1. **SQLite over JSON files** — Matches existing SqliteMemoryStore pattern; enables efficient querying, pagination, and atomic writes
2. **Polymorphic ICronAction via DI** — Interface + dependency injection for extensible action types (vs OpenClaw's discriminated unions)
3. **Cronos library** — Lightweight MIT-licensed cron expression evaluator with timezone support (not Quartz — too heavy)
4. **Cron sessions as first-class GatewaySessions** — ChannelType="cron", zero changes to existing session infrastructure, immediate WebUI visibility
5. **Dual-path job creation** — Config file seeding + dynamic creation via API/agent tool

**Architecture Highlights:**
- `CronScheduler` as BackgroundService (timer loop, concurrency semaphore, startup catch-up)
- `ICronStore` / `SqliteCronStore` (cron_jobs + cron_runs tables, WAL mode)
- `AgentPromptAction` creates isolated cron sessions, dispatches via IAgentCommunicator
- `WebhookAction` for HTTP POST integrations
- `CronTool : IAgentTool` with ownership-based permission model
- REST API: full CRUD + run-now + status + run history
- Session resumption via existing JoinSession pipeline (no new infrastructure)

**Implementation Plan:** 4 waves
- Wave 1 (Farnsworth): Core — project, models, store, scheduler
- Wave 2 (Bender): Actions + session integration
- Wave 3 (Hermes): REST API + agent tool
- Wave 4 (Amy/Hermes): WebUI + polish
- ~60 new tests across all waves

**Decision written to:** `.squad/decisions/inbox/leela-cron-infrastructure.md`

## 2026-04-11 — Sub-Agent Spawning Design Review (Lead)

**Status:** ✅ Design Review Complete — Approved with modifications
**Requested by:** Jon Bullen
**Spec Author:** Nova (via Jon)
**Scope:** Architecture review of sub-agent spawning feature spec + interface definitions + work breakdown

**Context:**
Nova produced a design spec for background sub-agent spawning — agents delegating tasks to isolated LLM sessions with model/tool overrides. Reviewed against existing codebase: `IAgentCommunicator.CallSubAgentAsync()` already provides synchronous sub-agent calls with recursion guards, `AgentDescriptor.SubAgentIds` exists, `IAgentHandle.FollowUpAsync()` provides message injection.

**Key Findings:**
1. **Existing infrastructure not acknowledged** — Spec proposes new system but `DefaultAgentCommunicator` already has sync sub-agent calls, scoped session IDs, recursion guards, and depth limits. New feature extends this to async/background execution.
2. **God tool antipattern** — Spec proposes single `subagents` tool with 5 actions. Recommended splitting into `spawn_subagent`, `list_subagents`, `manage_subagent` per codebase convention.
3. **No new DB table needed** — `GatewaySession.Metadata` + in-memory `ConcurrentDictionary` avoids two-source-of-truth vs proposed `subagent_sessions` table.
4. **Completion delivery via `FollowUpAsync`** — Reuses existing `PendingMessageQueue` infrastructure.
5. **Security gap** — `workingDir` parameter removed from spawn; sub-agents use parent workspace.
6. **Spec truncated** — Open questions section incomplete.

**Deliverables:**
- Spec assessment with 7 gaps identified
- 7 interface/class definitions with project placement
- 10-item risk register with mitigations
- 4-wave work breakdown (16 tasks) assigned to Farnsworth/Bender/Fry/Hermes/Kif
- 6 architectural decisions with rationale
- ~45-55 new tests estimated

**Key Interfaces Defined:**
- `ISubAgentManager` → `BotNexus.Gateway.Abstractions`
- `SubAgentSpawnRequest`, `SubAgentInfo`, `SubAgentStatus` → `BotNexus.Gateway.Abstractions`
- `DefaultSubAgentManager` → `BotNexus.Gateway`
- `SubAgentSpawnTool`, `SubAgentListTool`, `SubAgentManageTool` → `BotNexus.Gateway`
- `SubAgentOptions` → `BotNexus.Gateway`

**Key Architecture Decisions:**
- D1: Extend existing `IAgentCommunicator` sub-agent infra, don't replace
- D2: Separate tools, not god tool
- D3: Completion via `FollowUpAsync`
- D4: Session metadata, not new table
- D5: Sub-agents can't spawn sub-agents (Phase 1)
- D6: No `workingDir` override (security)

**Decision written to:** `.squad/decisions/inbox/leela-subagent-design-review.md`

## 2026-04-11 — Session Switching Bug Design Review (Lead)

**Status:** ✅ Design Review Complete — Approved for Implementation
**Requested by:** Jon Bullen
**Spec Author:** Nova
**Scope:** Root cause diagnosis + fix specification for session switching bug in WebUI

**Context:**
WebUI canvas fails to switch when clicking a different agent's session while another agent is actively working. Previous agent's conversation and loading state bleeds through.

**Root Cause Diagnosis:**
All 3 spec patterns confirmed against actual `app.js` source:
1. **Pattern A (Primary):** SignalR handlers (`MessageStart`, `ContentDelta`, `ToolStart`, etc.) have no client-side sessionId guard. Events render unconditionally. Server uses group routing but race window exists between `LeaveSession` and server processing.
2. **Pattern C (Secondary):** `isStreaming`, `activeToolCalls`, `activeMessageId`, `thinkingBuffer`, processing status bar are all global state never reset by `openAgentTimeline()`.
3. **Pattern B (Symptom):** DOM is cleared but re-populated by stale events during async gap.
4. **Bonus:** `openAgentTimeline` calls `joinSession(agentId, null)` creating orphan sessions on every sidebar click.
5. **Bonus:** `checkAgentRunningStatus()` exists but is never called on switch-back.

**Fix Design:**
- Wave 1 (Fry): State reset in `openAgentTimeline`, session guard on all SignalR handlers, fix orphan session creation, call `checkAgentRunningStatus` on switch
- Wave 2 (Fry): Verify/add `sessionId` to `AgentStreamEvent` backend model
- Wave 3 (Fry): Per-session state Map with LRU eviction (robustness)
- Wave 4 (Hermes): 6 test scenarios covering switch, switch-back, rapid switch, stale events

**Key Decisions:**
- D1: Client-side guard is primary defense (server groups are belt-and-suspenders)
- D2: Wave 1 ships without per-session state map (reset + guard sufficient for bug fix)
- D3: No cancellation of background agent work on switch
- D4: Fix orphan session creation
- D5: All fixes in vanilla JS (no framework introduction)

**Decision written to:** `.squad/decisions/inbox/leela-session-switch-review.md`

## Learnings — WebUI State Architecture (2026-04-11)

1. **WebUI uses global scalar state for streaming** — `isStreaming`, `activeMessageId`, `activeToolCalls`, `thinkingBuffer` are all module-level variables in app.js IIFE. Any multi-session feature must account for this.
2. **SignalR handlers are session-unaware** — None of the `connection.on()` handlers check evt.sessionId before rendering. Group-based routing provides server-side isolation but client has no guard.
3. **`openAgentTimeline` is the session switch entry point** — Called from sidebar click handler (line 1670). It calls `joinSession` twice: once with null (creates orphan), once with latest session ID.
4. **`joinSessionVersion` protects join results, not incoming events** — The version counter prevents stale join results from updating state, but doesn't filter incoming streaming events.
5. **`checkAgentRunningStatus()` is the restore mechanism** — Exists at line 705, queries `/agents/{id}/sessions/{id}/status` REST endpoint and sets `isStreaming` + processing indicators if agent is Running/Idle.

## 2026-06-24 — Playwright E2E Test Coverage Audit (Lead)

**Status:** ✅ Test plan delivered
**Requested by:** Jon Bullen
**Scope:** Audit all WebUI user interactions and produce comprehensive Playwright E2E test plan

**Context:**
WebUI has ~3,600 lines of JavaScript (app.js) with 30+ distinct interaction areas. Only 5 tests existed (session switching). Jon requested full interaction coverage.

**Audit Approach:**
Read all 3 WebUI source files end-to-end (index.html, app.js, styles.css), existing test files (SessionSwitchingE2ETests.cs, WebUiE2ETestHost.cs, PlaywrightFactAttribute.cs), cataloged every user interaction in the UI.

**Findings:**
- 32 interaction areas identified across: connection lifecycle, sidebar navigation, chat sending, streaming display, thinking display, tool call display, steer/follow-up, abort, command palette, copy interactions, sub-agent panel, agent config, agent form modal, model selector, modal dialogs, scroll behavior, error handling, history pagination, mobile sidebar, cron view, activity feed
- Current coverage: 5/97 scenarios (5.2%)

**Deliverables:**
- 97 test scenarios across 19 test classes
- Priority breakdown: P0 (29), P1 (50), P2 (18)
- Implementation order recommendation (P0 classes first)
- Infrastructure enhancement notes for RecordingAgentSupervisor and WebUiE2ETestHost
- Written to `.squad/decisions/inbox/leela-playwright-coverage-plan.md`

## 2026-04-11 — Multi-Session Connection Model Architecture Proposal (Lead)

**Status:** ✅ Proposal written — awaiting Jon's review
**Requested by:** Jon Bullen
**Scope:** Redesign WebUI SignalR connection model to eliminate join/leave race conditions by design

**Context:**
Jon identified a fundamental design flaw: session switching requires 2 server round-trips (LeaveSession → JoinSession), creating a race-condition-rich gap where events are lost and state is undefined. The codebase has accumulated compensating mechanisms: `joinSessionVersion` counter, `timelineSwitchVersion` counter, `isEventForCurrentSession()` drop guard, `sessionSwitchInProgress` flag, 8-second safety timer, and 8+ bail-out points in `openAgentTimeline`.

**Analysis (read code to confirm):**
1. 5 bug classes documented: stale event delivery, rapid-switch races, safety-net timer, state pollution, reconnection gaps
2. Evidence from `SessionSwitchingE2ETests.cs` — tests validate compensating behavior, not correct architecture
3. Root cause: session switching is a server-side operation when it should be a client-side concern

**Proposed Architecture:**
- Gateway pre-warms sessions on startup via `ISessionWarmupService` (IHostedService)
- Client subscribes to ALL sessions on connect via new `SubscribeAll()` hub method
- Events routed to per-session `SessionStore` instances (replaces global mutable state)
- Switching is purely DOM re-render — 0 server calls, 0 race conditions
- Single SignalR connection subscribed to multiple groups (not multiple connections)

**5 Key Design Decisions:**
- D1: Single connection multi-group (native SignalR pattern, simpler reconnection)
- D2: Pull history + push new events (hybrid — avoids bandwidth spike on connect)
- D3: Active + recent sessions pre-warmed (configurable 24h window, max 10/agent)
- D4: LRU memory management with MAX_MEMORY_SESSIONS = 20
- D5: Backward compatible — `JoinSession`/`LeaveSession` stay deprecated, removed in v2

**Interface Changes Specified:**
- New: `ISessionWarmupService`, `SessionWarmupOptions`, `SessionSummary`, `SessionInfo` DTOs
- New hub methods: `SubscribeAll()`, `Subscribe(sessionId)`
- New client-side: `SessionStoreManager`, `SessionStore` classes
- Modified: `GatewayHub.OnConnectedAsync` (capabilities field), `GatewayOptions` (SessionWarmup section)

**Migration Path:** 3 phases
- Phase 1 (server foundation, ~7.5d): Warmup service, SubscribeAll, DTOs — zero client impact
- Phase 2 (client rewrite, ~12.5d): SessionStoreManager, remove join/leave, rewrite E2E tests
- Phase 3 (polish, ~7.5d): Tabbed UI, badges, smart pre-loading, remove deprecated methods

**Risk Register:** 8 risks tracked (memory pressure, bandwidth, group explosion, event ordering, new session creation race, store size, E2E rewrite scope, replay buffer interaction)

**Deliverable:** `docs/planning/feature-multi-session-connection/architecture-proposal.md` (748 lines)

## 2026-07-17 — Agent Delay/Wait Tool Design Spec (Lead)

**Status:** ✅ Spec written
**Requested by:** Jon Bullen
**Scope:** Design spec for an in-session delay/wait tool that lets any agent pause for a specified duration then resume

**Context:**
Jon wants agents to be able to call `delay(seconds)` mid-session — e.g., "wait 5 minutes then do X", "check the build in 30 seconds". Not cron. Not a new session. Just an async pause inside `ExecuteAsync` using `Task.Delay` with the existing `CancellationToken`.

**Key Design Decisions:**
- D1: Standard `IAgentTool` — no changes to agent loop, no new infrastructure
- D2: `Task.Delay` with cancellation token — zero thread cost, cancellable by steering/abort
- D3: Graceful cancellation — returns informational text result (not exception) so model can react
- D4: Clamped, not rejected — values outside range are clamped to avoid model retry loops
- D5: `onUpdate` callback surfaces "waiting" state to UI
- D6: Global config only for v1 (`BotNexus:Tools:Delay:MaxSeconds`, default 1800)

**Tool Shape:**
- Name: `delay`, params: `seconds` (required int, 1–1800), `reason` (optional string)
- Returns: text result with elapsed/requested duration
- On cancel: text result with elapsed-of-requested and cancel reason

**Work Breakdown:** ~4 days across 7 tasks (impl, registration, config, unit tests, integration tests, UI, docs)

**Open Questions:** 3 (WebSocket progress events, per-agent max override, steering delivery semantics — all have recommended answers)

**Deliverable:** `docs/planning/feature-agent-delay-tool/design-spec.md`

---

## File Watcher Tool — Spec + Implementation (2026-07-19)

Jon requested `watch_file` — an `IAgentTool` that watches a file and completes when modified (with timeout). Reactive counterpart to `delay`.

**Design Decisions:**
- D1: `FileSystemWatcher` + `TaskCompletionSource` — OS-native events, no polling
- D2: Debounce (500ms) — coalesces rapid saves from editors/formatters
- D3: Linked cancellation — combines steering/abort token with timeout CTS
- D4: Graceful results (not exceptions) on timeout/cancel — same pattern as DelayTool
- D5: Event types: modified, created, deleted, any
- D6: Path validation — must resolve under workspace (no system directories)

**Tool Shape:**
- Name: `watch_file`, params: `path` (required string), `timeout` (optional int, default 300), `event` (optional string, default "modified")
- Returns: text result with event type, path, and elapsed seconds
- On timeout/cancel: informational text result

**Files Created:**
- `docs/planning/feature-file-watcher-tool/design-spec.md`
- `src/gateway/BotNexus.Gateway/Configuration/FileWatcherToolOptions.cs`
- `src/gateway/BotNexus.Gateway/Tools/FileWatcherTool.cs`

**Files Modified:**
- `GatewayOptions.cs` — added `FileWatcherTool` property
- `GatewayServiceCollectionExtensions.cs` — registered options + config binding
- `InProcessIsolationStrategy.cs` — registered tool for all agents

**Validation:** Build green (0 errors), all changes follow DelayTool pattern.
---

### Design Spec — Infinite Scrollback History (2025-07-24)

**Requested by:** Jon Bullen
**Commit:** `0476bee`
**Status:** DRAFT — awaiting review

Jon reported that clicking "Load earlier messages" wipes current messages from the DOM. Wrote design spec for proper infinite scrollback:

- **Root cause:** `loadEarlierMessages` does `innerHTML = ''` (app.js:2203) then re-renders — destroys scroll position and event listeners
- **New API:** `GET /api/channels/{channelType}/agents/{agentId}/history` with cursor-based cross-session pagination
- **Cursor format:** `{sessionId}:{messageIndex}` — opaque to client, spans sessions automatically
- **Client:** IntersectionObserver on sentinel element, prepend-without-jump pattern, single in-flight guard
- **Session boundaries:** API returns `sessionBoundaries` array; client renders dividers at indicated positions
- **Work breakdown:** Farnsworth (API), Bender (observer/fetch), Fry (dividers/UX), Hermes (tests)

**File created:** `docs/planning/feature-infinite-scrollback/design-spec.md`

---
### Glob Pattern Support for File Access Permissions (2026-04-11)
**Requested by:** Jon Bullen
**Commits:** `5dc7fb6` (spec), `b8da733` (implementation)
**Status:** COMPLETE — build green, 18/18 PathValidator tests passing

Updated the per-agent file permission model to support glob patterns in AllowedReadPaths, AllowedWritePaths, and DeniedPaths:

- **Spec update:** Added Glob Pattern Support section to `docs/planning/feature-tool-permission-model/design-spec.md` — pattern syntax table, matching rules, example config. Updated resolution rules (Rule 6), security considerations, and open questions to reflect glob support.
- **Implementation:** `DefaultPathValidator.cs` — added `PathMatchesPattern`, `IsGlobPattern`, `ResolveGlobPath` methods. Glob patterns (containing `*` or `?`) use `FileSystemName.MatchesSimpleExpression` from `System.IO.Enumeration` (no extra NuGet). Non-glob paths retain existing directory-prefix matching. Forward-slash normalization avoids backslash escape conflicts.
- **Tests:** 5 new tests — `GlobStar_MatchesAllUnderDirectory`, `GlobDoubleStar_MatchesRecursive`, `GlobInDeny_BlocksPattern`, `GlobAndLiteral_BothWork`, `GlobNoMatch_ReturnsFalse`.

**Files changed:**
- `docs/planning/feature-tool-permission-model/design-spec.md`
- `src/gateway/BotNexus.Gateway/Security/DefaultPathValidator.cs`
- `tests/BotNexus.Gateway.Tests/Security/PathValidatorTests.cs`

## 2026-07-08 — DDD Refactoring Design Review (Lead)

**Status:** ✅ Design Review Complete — Approved with 8 modifications (Grade: B+)
**Requested by:** Jon Bullen
**Spec:** `docs/planning/ddd-refactoring/design-spec.md`

**Context:**
Comprehensive DDD refactoring spec proposing 9 phases to align codebase with domain model. Introduced BotNexus.Domain project, value objects, smart enums, session model improvements, cron decoupling, sub-agent identity fix, abstractions split, dedup.

**Key Findings:**
1. **Spec is well-researched** — All claims verified: NormalizeChannelKey x5, Role magic strings x13, IsolationStrategy strings x40+ files, sub-agent identity theft confirmed
2. **Phase 1.1 too large** — 20 types in one shot is a big-bang; decomposed into 1.1a (primitives), 1.1b (session), 1.1c (agent)
3. **Missing serialization strategy** — Value objects need JsonConverter + SQLite column mapping. Decided: readonly record struct + implicit string conversion
4. **Missing test migration** — ~2,000 tests use string patterns. Decided: implicit conversion for backward compat
5. **Abstractions split blast radius** — 13 projects reference Gateway.Abstractions. Decided: TypeForwardedTo attributes for incremental migration
6. **Phases 4/5 are speculative** — World (no consumer) and Agent-to-Agent (feature, not refactor) deferred
7. **GatewaySession decomposition underestimated** — Lock + replay buffer extraction is highest-risk; requires dedicated sub-spec

**8 Architectural Decisions:**
- D1: Value objects use `readonly record struct` + implicit string conversion + JsonConverter
- D2: Tests migrate incrementally via implicit conversion
- D3: Defer Phase 4 (World) — YAGNI
- D4: Defer Phase 5 (Agent-to-Agent) — needs own feature spec
- D5: Defer Phase 2.3 (Soul Session) — underspecified
- D6: Move Phases 9.4/9.5/9.6 to Wave 1 — zero Domain dependency, parallelize
- D7: Phase 7.2 requires dedicated sub-spec — highest-risk item
- D8: Phase 7.1 uses TypeForwardedTo for incremental migration

**Wave Plan:** 6 waves across 2 delivery cycles
- Waves 1-4 (this cycle, ~3-4 weeks): Domain foundation, session model, identity fixes, existence queries
- Waves 5-6 (next cycle, ~2-3 weeks): Abstractions split, GatewaySession decomposition, SystemPromptBuilder
- Future: World, Agent-to-Agent, Soul Session, Cross-World

**Agent Assignments:**
- Farnsworth: Core domain types, value objects, session model, store improvements, abstractions split
- Bender: Sub-agent archetypes, cron decoupling, provider consolidation, SystemPromptBuilder
- Hermes: Tests throughout (per-wave), ~120-150 new tests estimated
- Kif: DDD patterns guide, session model docs, prompt architecture docs

**Decision written to:** `.squad/decisions/inbox/leela-ddd-design-review.md`

## Learnings — DDD Refactoring Patterns (2026-07-08)

1. **Value object pattern for C#/.NET**: Use `readonly record struct` with implicit string conversion for backward compat. Smart enums use `sealed class` with `ConcurrentDictionary` registry for extensibility. Both need `JsonConverter<T>`.
2. **Gateway.Abstractions has 13 downstream dependents** — Any split requires TypeForwardedTo attributes for incremental migration. Cold-turkey removal breaks the build across the entire solution.
3. **GatewaySession mixes domain + infra** — Has `Lock _historyLock` with 5 locked methods, replay buffer, streaming state. Decomposition is the highest-risk refactor item. Requires snapshot tests before touching.
4. **NormalizeChannelKey exists in 5 locations** (3 stores + ChannelHistoryController + PlaywrightFixture tests). A ChannelKey value object eliminates all of them.
5. **Sub-agent identity theft** — DefaultSubAgentManager.cs line 64: `childAgentId = request.ParentAgentId`. Makes parent and child indistinguishable in logs, audit, and session queries.
6. **Only 1 record struct exists in codebase** (ConfigPathResolver). Value objects will introduce a new pattern — needs team documentation.


## 2026-04-15 — Extension-Contributed Commands Design Review (Wave 1)

**Status:** ✅ Complete  
**Grade:** B+

**Context:** Design review ceremony for Extension-Contributed Commands feature — backend-driven command registry enabling extensions to contribute slash commands (e.g., /skills) to WebUI palette without modifying core code.

**Key Decisions Finalized:**
1. Split CommandDefinition → CommandDescriptor (serializable data) + ICommandContributor.ExecuteAsync() (handler)
2. Integrate ICommandContributor into AssemblyLoadContextExtensionLoader.DiscoverableServiceContracts for auto-discovery
3. CommandRegistry in BotNexus.Gateway.Commands aggregates DI instances
4. Built-in commands implement BuiltInCommandContributor : ICommandContributor (dogfoods extension model)
5. Add ResolveTool(string) to IAgentHandleInspector for session-aware commands like /skills add
6. Scope: Phases 1-3 + Skills; defer TUI (Phase 4), Hub (Phase 5)

**8 Gaps Identified, 3 Must-Fix:**
- CommandDefinition non-serializable ✅
- Extension loader integration ✅
- Command-result formatting ✅

**Wave Structure:**
- Wave 1 (Farnsworth/Hermes/Kif): Contracts, CommandRegistry, DI setup, 10 unit tests, API docs
- Wave 2: WebUI command palette integration
- Wave 3: Skills /skills command implementation
- Wave 4: TUI integration

**Deliverables:**
- Approved design-review.md + decision inbox entry
- Orchestration logs for all agents
- Session log completed

