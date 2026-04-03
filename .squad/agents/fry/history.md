# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Recent Session Summaries

### 2026-04-03 — UI Bug Fixes: Whitespace & Tool Call Rendering

**Session:** Post-deployment UI cleanup  
**Status:** ✅ Complete  
**Commit:** 74d54d6 — `fix(webui): remove excessive whitespace and handle tool calls in live responses`

**Issues Fixed:**
1. **Excessive whitespace** — Hidden tool messages kept 2px margins, creating gaps where content was collapsed
2. **Missing tool calls in live responses** — WebSocket 'response' handler didn't check for toolCalls, so live messages showed content-only while history showed them properly

**Changes Made:**
- Added `.message.tool.hidden { margin: 0; }` CSS rule to collapse margins when tools are hidden
- Modified `handleWsMessage()` to check `msg.toolCalls` and route to new renderer
- Created `renderAssistantWithToolsLive()` function for live responses with tool calls
- Removed inline `style="margin-top: 6px;"` that created extra space in hidden tool summaries
- Ensures tool call summaries render identically in both live streaming and history views

**Root Causes:**
- Tool visibility toggle used `.hidden` class but didn't account for element margins
- Live response rendering path (`appendChatMessage`) was separate from history rendering path (`renderAssistantWithTools`)
- No parity between WebSocket message handling and session history replay

**Learnings:**
- When hiding UI elements, must collapse both display AND spacing (margins/padding)
- Live WebSocket handlers should mirror history replay logic to avoid divergence
- Tool call rendering should be centralized to prevent duplication/inconsistency

### 2026-04-03 — Skills Platform Sprint (Web Dev)

**Timestamp:** 2026-04-03T07:50:00Z  
**Status:** ✅ Complete  
**Scope:** Model dropdown UI integration  

**Deliverables:**
- **Model Dropdown Component**
  - Fetches from GET /api/models endpoint
  - Client-side caching to avoid refetches
  - Dropdown selector in chat UI header
  - Works with existing and new sessions
  - Selected model passed through WebSocket payload
- **Shared loadModels() Function** — Centralized HTTP call with caching
- **Dev-Loop Deployment** — Tested and deployed locally

**Team Impact:**
- Leela's SkillsLoader provided foundation API
- Farnsworth's config work enabled provider selection
- Documentation by Kif includes model selection guide

---

### 2026-04-03 — Model Selector UI + Tool Visibility (Parallel with Farnsworth)

**Session:** Sprint 4 parallel UI and config work  
**Status:** ✅ Success (both tasks completed)

**Model Selector Task (feat: model-selector, commit bae2e25):**
- Added dropdown selector to chat UI header for both new and existing sessions
- Models loaded dynamically from /api/providers endpoint
- Selected model passed through WebSocket payload
- Works with existing and new sessions

**Tool Calls Visibility Task (feat(webui): tool-calls, commit feat(webui)):**
- Added collapsible tool call blocks in chat messages
- Tools filter toggle (🔧) in header to show/hide tool interactions
- Tool messages hidden by default (reduces UI clutter)
- Can be toggled on-demand to inspect tool execution

**Dependencies:** Both tasks completed cleanly without blocking each other. Farnsworth's nullable config work provided foundation, but both UI tasks were independent of that.

**Learnings:**
- WebSocket model field should be passed from client → server for provider selection
- Tool visibility toggle pattern is reusable for other message type filters
- Model selector dropdown can be pre-populated from config or fetched live

### 2026-04-03 — Tool Call Display Redesign (Scribe cross-agent update)

**Task:** Compact summary view with clickable detail modal  
**Status:** ✅ Complete  
**Timestamp:** 2026-04-03T04:50:00Z  

- Tool calls now display as `🔧 toolname(args)` in compact form
- Click opens full response in scrollable modal
- Tools toggle (🔧) still works as before
- Better signal-to-noise ratio in complex agent interactions
- Coordinated with Bender's multi-turn improvements

---

## Team Directives (All Agents Must Follow)

1. **Dynamic Assembly Loading** (2026-04-01T16:29Z)
   - All extensions (channels, providers, tools) must be dynamically loaded from `extensions/{type}/{name}/` folders
   - Configuration drives what loads — nothing loads by default unless referenced in config
   - Reduces security risk, keeps codebase abstracted
   - See decisions.md Section "Part 1: Dynamic Assembly Loading Architecture"

2. **Conventional Commits Format** (2026-04-01T16:43Z)
   - Use feat/fix/refactor/docs/test/chore prefixes on ALL commits
   - Commit granularly — one commit per work item or logical unit, not one big commit at end
   - Makes history clean, reversible, and easy to review

3. **Copilot Provider P0** (2026-04-01T16:46Z)
   - Copilot is the only provider Jon uses — it is P0, all other providers P1/P2
   - Use OAuth device code flow (like Nanobot) — no API key
   - Base URL: https://api.githubcopilot.com
   - Prioritize Copilot work before OpenAI, Anthropic

## Implementation Plan (Rev 2) — 24 Work Items

**Phase 1: Core Extensions (7 items)** — Foundations  
**Phase 2: Provider Parity & Copilot (4 items)** — Copilot end-to-end  
**Phase 3: Completeness (5 items)** — Tool extensibility, scale  
**Phase 4: Scale & Harden (8+ items)** — Production-ready, observed, containerized

See decisions.md "Part 4: Implementation Phases & Work Items" for full roadmap with owner assignments.

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- **WebUI is plain HTML/CSS/JS** in `src/BotNexus.WebUI/wwwroot/` — no build tools, no npm, no frameworks. All state is in an IIFE in `app.js`.
- **Sidebar pattern**: Each section uses `.sidebar-section` > `.section-header[data-toggle]` > `.section-content`. Toggle behavior is wired via `data-toggle` attribute pointing to the content div's id.
- **REST API pattern**: Endpoints in `Program.cs` use minimal API (`app.MapGet`) with inline lambdas. DI services are injected as parameters. All responses use shared `jsonOptions` with camelCase naming.
- **ProviderRegistry** is a DI singleton — use `GetProviderNames()` + `Get(name)` to enumerate providers and their models.
- **ToolRegistry is NOT in DI** — tools are registered as `IEnumerable<ITool>` via DI, so inject that directly for listing.
- **ExtensionLoadReport** is a DI singleton with load counts, health status, and per-extension results.
- **Dark theme CSS vars**: `--bg-primary`, `--bg-secondary`, `--bg-tertiary`, `--accent`, `--success`, `--error`, `--border`, `--text-primary/secondary/muted`. Always use these for consistency.
- **Build/test**: `dotnet build BotNexus.slnx` and `dotnet test BotNexus.slnx`. 158 unit + 19 integration tests.

## Sprint 4 Summary — 2026-04-01T18:22Z

✅ **COMPLETE** — WebUI Extensions Visibility (1 item)

### Your Deliverables (Fry) — Sprint 4

1. ✅ **webui-extension-visibility** (a4235e3) — WebUI system panel for runtime extension monitoring

### Key Achievements

- **Extensions Panel** — New system sidebar section showing all loaded extensions
- **Dynamic Channel List** — Displays active channels (name, status, configuration, enabled state)
- **Provider Display** — Shows loaded providers (name, default model, OAuth/API key auth type)
- **Tools List** — Lists registered tools (name, description, from built-in or extension)
- **Health Status** — Color-coded indicators: green (healthy), yellow (warning), red (failed)
- **Extension Metadata** — Version, assembly count, load time, startup state
- **Real-Time Polling** — API polling updates extension status every 5 seconds for live monitoring
- **Responsive Design** — Mobile-friendly layout compatible with desktop and tablet viewports
- **Dark Theme Integration** — Consistent styling using CSS variables from existing WebUI theme
- **Zero Regressions** — All existing WebUI functionality preserved and tested

### Build Status
- ✅ Solution green, 0 errors, 0 warnings
- ✅ All 192 tests passing (158 unit + 19 integration + 15 E2E)
- ✅ WebUI renders correctly in browser with no console errors
- ✅ Extension panel loads and updates dynamically
- ✅ Responsive design verified on multiple viewports

### Integration Points
- Works with Farnsworth's ExtensionLoadReport DI singleton for data sourcing
- Uses Hermes' E2E test fixture extensions for visibility validation
- Complements Bender's security monitoring (shows auth status per extension)
- Supports Leela's architecture documentation for operator visibility

### Team Status
**ALL 4 SPRINTS COMPLETE:** 24/26 items delivered. Fry: 4 items across all sprints (extension build pipeline, tool/channel dynamic loading, WebUI extensions panel). Platform operations now have real-time extension visibility.



## 2026-04-02 — Team Updates

- **Nibbler Onboarded:** New Consistency Reviewer added to team. Owns post-sprint audits.
- **New Ceremony:** Consistency Review ceremony established (trigger: after sprint completion or arch changes). Leela's audit (2026-04-02) found 22 issues across 5 files.
- **Decision:** Cross-Document Consistency Checks merged into decisions.md. All agents treat consistency as a quality gate.


### 2026-04-02 — Sprint 5 Complete: Agent Workspace, Memory, Deployment Lifecycle

**Overview:** Sprint 5 delivered the core agent infrastructure (workspace + identity), memory management system (long-term + daily with consolidation), and comprehensive deployment lifecycle validation (10 real-process E2E scenarios).

**Achievement:** 48/50 items done. 2 P2 items deferred (Anthropic tool-calling, plugin architecture deep-dive). Team grew from 6 to 8 agents (Nibbler + Zapp added).

**Workspace & Identity (Leela ws-01/02, Farnsworth ws-03/04/05):**
- Agent workspace structure: ~/.botnexus/agents/{agent-name}/ with SOUL/IDENTITY/USER/AGENTS/TOOLS/MEMORY files
- BotNexusHome.Initialize() creates workspace structure and stub files
- Multi-agent awareness via auto-generated AGENTS.md (from config + identity files)
- File-based persistent identity and personality system
- Integration tests for workspace creation, file structure, and initialization

**Context Builder & Memory Services (Bender ws-06 through ws-12, Farnsworth ws-13):**
- IContextBuilder interface replaces flat systemPrompt with file-driven context assembly
- Context loads workspace files (SOUL, IDENTITY, USER, AGENTS, TOOLS, MEMORY) at session start
- Memory tools added: memory_search (FTS), memory_save, memory_get, memory_list
- Daily memory files (~/.botnexus/agents/{name}/memory/YYYY-MM-DD.md) auto-loaded for today + yesterday
- Long-term MEMORY.md consolidation via LLM-based distillation
- Token budget trimming integrated into context builder

**Heartbeat & Memory Consolidation (Bender ws-15, Farnsworth ws-16):**
- IHeartbeatService runs daily consolidation job: distills daily files → MEMORY.md
- Controlled pruning prevents unbounded memory growth
- Health check integrated with heartbeat service

**Deployment Lifecycle Testing (Hermes ws-17 through ws-21):**
- Implemented 10 real-process E2E scenarios in tests/BotNexus.Tests.Deployment/
- GatewayProcessFixture: spawns Gateway via dotnet run with isolated temp dirs, health probes
- Scenarios cover: install, config creation, startup, agent workspace setup, message routing, multi-agent handoff, session persistence, graceful shutdown, restart with session restoration, platform update, health management, OAuth integration
- All 10 pass. Scenario registry now 86% coverage (48/56 total scenarios).
- Key discovery: Sessions persisted across restart; workspace creation is lazy (on first message); extension loading is explicit, not auto-scanning.

**Scenario Registry & Team Expansion (Leela ws-22, Zapp scenario-registry + deployment-lifecycle-tests):**
- Zapp added to team: owns E2E deployment validation, deployment lifecycle tests
- Nibbler added to team: owns consistency reviews, post-sprint audits
- Scenario registry process formalized: Hermes maintains as living document after sprint completion
- Consistency review ceremony established: triggered after sprint or architecture changes

**Process Updates:**
- All decisions from inbox merged into decisions.md (decisions #9, #10, #11)
- Inbox files deleted (merged, not orphaned)
- Cross-agent consistency checks now a formal ceremony with Nibbler as owner
- Documentation updated and consistency audit completed (Leela: 22 issues fixed across 5 files)

**Outstanding:**
- 2 P2 items deferred to next sprint: Anthropic tool-calling feature parity, plugin architecture deep-dive
- Hearbeat service still needs HealthCheck.AggregateAsync() implementation (minor gap)
- Plugin discovery (AssemblyLoadContext per extension) not yet fully tested with real extension deployments

## 2026-04-02 — Cron Observability (cron-metrics + cron-health-check + cron-activity-events)

**Commit:** 3fb995e — `feat(observability): add cron metrics, health check, and activity events`

### Deliverables

1. **Cron Metrics** — Added 4 metrics to `IBotNexusMetrics`/`BotNexusMetrics`:
   - `botnexus.cron.jobs.executed` (counter, tagged by job name)
   - `botnexus.cron.jobs.failed` (counter, tagged by job name)
   - `botnexus.cron.job.duration` (histogram, ms, tagged by job name)
   - `botnexus.cron.jobs.skipped` (counter, tagged by job name + reason: disabled/overlapping)

2. **CronServiceHealthCheck** — New `IHealthCheck` implementation:
   - Healthy when tick loop running with no degraded jobs
   - Degraded when any job has 3+ consecutive failures
   - Unhealthy when tick loop stopped
   - Registered in health check pipeline as `cron_service`
   - `ConsecutiveFailures` added to `CronJobStatus` record

3. **Activity Events** — Enhanced cron activity stream events:
   - `cron.job.started` — published with job name, type, correlation ID
   - `cron.job.completed` — includes duration_ms and success in metadata
   - `cron.job.failed` — includes duration_ms, success, and error in metadata
   - All visible in WebUI via activity stream WebSocket

---

## 2026-04-03 — Loop Alignment & UI Fix

**Cross-Agent Update:** Leela (Lead) fixed critical agent loop pattern and system prompt issues. Root cause of agents narrating work instead of executing: system prompt lacked explicit tool-use instructions. Leela removed non-standard keyword continuation detection and implemented nanobot-style finalization retry pattern (proven across Anthropic, OpenAI, nanobot frameworks). Added explicit "USE tools proactively" instructions to AgentContextBuilder.BuildIdentityBlock(). Simultaneously, Fry (Web Dev) fixed UI rendering bugs: CSS margin cleanup on hidden tool messages was broken, and WebSocket live rendering was missing tool call context. Both fixes committed: Leela 8951925, Fry 74d54d6. Decision "Agent Loop Standard Pattern" merged to decisions.md. See .squad/log/2026-04-03T05-51-33Z-loop-alignment-ui-fix.md for session summary.


### Tests
- 5 new `CronServiceHealthCheckTests`: disabled, not-running, healthy, degraded threshold, below-threshold
- All 339 tests passing (285 unit + 29 integration + 15 E2E + 10 deployment)

## Session Completion: 2026-04-02

**Sprints Completed:** 1-6  
**Items Done:** 71 of 73 (97.3%)  
**Tests Passing:** 395  
**Scenario Coverage:** 64/64 (100%)  
**Team Size:** 12 agents  

**Major Achievements:**
- Dynamic extension loading fully operational
- Copilot OAuth integration complete and tested
- Multi-agent routing with assistant classification deployed
- Agent workspaces with durable file storage working
- Centralized memory system with consolidation running
- Centralized cron service architecture finalized (pending implementation)
- Authentication/authorization layer deployed across Gateway, WebSocket, REST
- Security hardening: ~/.botnexus/ live environment fully protected
- Observability framework (metrics, tracing, health checks) integrated
- WebUI deployed with real-time status feeds
- Full E2E scenario coverage: 64/64 scenarios passing

**Deferred (P2):** 2 Anthropic items awaiting clarification

**Decisions Merged:**
1. Cron service as independent first-class scheduler
2. Live environment protection (~/.botnexus/ isolation)

**Next Steps:** Production deployment readiness, Sprint 7 planning for P2 items.

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

