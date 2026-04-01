# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

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

### Tests
- 5 new `CronServiceHealthCheckTests`: disabled, not-running, healthy, degraded threshold, below-threshold
- All 339 tests passing (285 unit + 29 integration + 15 E2E + 10 deployment)
