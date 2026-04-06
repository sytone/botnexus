# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. SOLID patterns. Comprehensive testing.
- **Stack:** C# (.NET latest), modular class libraries, dynamic extension loading, Copilot provider with OAuth, centralized cron service
- **Created:** 2026-04-01

## Core Context

**Phases 1-6 Complete.** Kif owns developer experience, training materials, documentation. Created lifecycle skill, dev guide, deployment scripts. Built OpenAPI spec export infrastructure: live-server workaround for net10.0 Swashbuckle limitation, PlatformConfig override handling, JSON spec generation. Manages project documentation and knowledge transfer.

---

## Learnings

- Sprint 7A: Created OpenAPI spec export pipeline (`scripts/export-openapi.ps1`). Swashbuckle.AspNetCore.Cli 7.2.0 targets net9.0 only — doesn't work on net10.0 even with LatestMajor roll-forward (crashes on `IServerAddressesFeature`). Used live-server approach instead: script starts API on temp port, fetches `/swagger/v1/swagger.json`, saves to `docs/api/openapi.json`. Key: PlatformConfig.GetListenUrl() overrides `--urls` parameter — must set `BotNexus__ConfigPath` to empty JSON file to prevent user config from hijacking port. Generated spec has 15 REST paths with XML doc comment descriptions. Commit: 88666b0.

- Created `docs/dev-guide.md` — comprehensive developer guide (~500 lines, 12 sections) covering prerequisites through troubleshooting. Every config example, port number, and file path verified against actual source code:
  - Gateway default port is 5005 in scripts (`start-gateway.ps1` `-Port` default, `dev-loop.ps1`)
  - `BotNexusHome.Initialize()` creates 5 directories: extensions, tokens, sessions, logs, agents
  - `BotNexusHome.ScaffoldAgentWorkspace()` creates 4 files: SOUL.md, IDENTITY.md, USER.md, MEMORY.md
  - Auth middleware skips `/health`, `/webui`, `/swagger` (from `GatewayAuthMiddleware.ShouldSkipAuth`)
  - Config hot reload uses 500ms debounce via `PlatformConfigWatcher`
  - Provider auth resolution: auth.json → env vars → config.json
  - WebSocket rate limit: 20 attempts per 300-second window (`GatewayWebSocketOptions` defaults)
- Updated `docs/api-reference.md` — added 4 missing endpoints verified against controller source:
  - `GET /api/agents/instances` (AgentsController.ListInstances)
  - `POST /api/agents/{agentId}/sessions/{sessionId}/stop` (AgentsController.StopInstance)
  - `GET /api/config/validate` (ConfigController.Validate)
  - `/ws/activity` WebSocket (ActivityWebSocketHandler)
  - Removed fictitious `PUT /api/agents/{name}` not in AgentsController
  - Fixed parameter names from `{name}` to `{agentId}` to match controller route params
  - Fixed `/health` response from `{"status":"healthy"}` to `{"status":"ok"}` matching Program.cs line 76
  - Added full WebSocket protocol documentation with message flow diagram
- Updated `README.md` — added documentation navigation table, dev-guide link, fixed Quick Start
- Updated `src/gateway/README.md` — added dev-guide cross-reference
- Updated `docs/architecture.md` — added See Also cross-references

- Created 5 module READMEs (~550 lines total) for Gateway sub-modules:
  - `src/gateway/BotNexus.Gateway.Abstractions/README.md` — Complete contract catalog: 13 interfaces, 15 model types, 3 enums, extension point guide for implementers
  - `src/gateway/BotNexus.Gateway.Sessions/README.md` — FileSessionStore (JSONL + .meta.json sidecar, SemaphoreSlim thread safety, ConfigureAwait(false) pattern) and InMemorySessionStore (Lock-based)
  - `src/channels/BotNexus.Channels.Core/README.md` — ChannelAdapterBase template method lifecycle, ChannelManager registry, new adapter walkthrough with DI registration
  - `src/channels/BotNexus.Channels.Tui/README.md` — Terminal UI stub: output works, input loop pending, local testing guide
  - `src/channels/BotNexus.Channels.Telegram/README.md` — Telegram Bot stub: TelegramOptions (BotToken, WebhookUrl, AllowedChatIds), Bot API reference
  - Every type documented from source code inspection, not assumption
  - Consistent template: purpose, key types table, usage examples, configuration, dependencies, extension points
  - Commit: c7ade0c

- Created focused training deep-dive documentation (4 new files, ~2,100 lines total):
  - `docs/training/providers.md` — IApiProvider contract, LlmClient routing, model registry, streaming event protocol (14 event types), message transformation pipeline, step-by-step new provider guide
  - `docs/training/agent-events.md` — Agent lifecycle, 10 event types with schemas, subscribe/unsubscribe, BeforeToolCall/AfterToolCall hooks, steering/follow-up queues, error handling, abort flow
  - `docs/training/tool-security.md` — PathUtils.ResolvePath containment, blocked paths/commands, FileMutationQueue, shell safety (timeout/process tree kill), AuditHooks, custom safety hook guide
  - `docs/training/building-a-coding-agent.md` — CodingAgent factory, SystemPromptBuilder, IAgentTool, SessionManager (JSONL/DAG branching), IExtension, config hierarchy, minimal agent walkthrough, message flow diagram
  - Updated `docs/training/README.md` with deep-dive table and reading path
- Key source code patterns verified:
  - LlmStream uses System.Threading.Channels (unbounded, single-writer)
  - Agent enforces single-run concurrency via SemaphoreSlim
  - ToolExecutor supports Sequential and Parallel modes; parallel preparation is still sequential
  - PathUtils.ResolvePath uses OrdinalIgnoreCase on Windows, Ordinal on Unix
  - FileMutationQueue uses ConcurrentDictionary<string, SemaphoreSlim> for per-path locking
  - SafetyHooks hardcodes 3 blocked patterns; AllowedCommands is prefix-matched whitelist
  - SessionManager uses JSONL with ParentEntryId DAG for branching
  - BuiltInModels registers 20+ models all routing through api.individual.githubcopilot.com
  - SimpleOptionsHelper default thinking budgets: Minimal=1024, Low=2048, Medium=8192, High=16384

- Created `docs/getting-started.md` — comprehensive 13-section guide (706 lines) covering prerequisites through OpenClaw migration. Every code example, config snippet, and API endpoint verified against actual source code.
- Key accuracy findings from source code audit:
  - Default Gateway port is **18790** (not 5000) — from `GatewayConfig.Port` default and `appsettings.json`
  - Default config.json auto-created by `BotNexusHome.Initialize()` includes Copilot provider pre-configured with `oauth` auth
  - Home directory creates 8 subdirectories: extensions/ (with providers/, channels/, tools/), tokens/, sessions/, logs/, agents/
  - OAuth device code flow logs to console: "Go to {VerificationUri} and enter code: {UserCode}"
  - OAuth tokens stored at `~/.botnexus/tokens/copilot.json` via `FileOAuthTokenStore`
  - Agent workspace bootstrap files: SOUL.md, IDENTITY.md, USER.md, MEMORY.md, HEARTBEAT.md + memory/daily/
  - API key auth protects `/api/*` and `/ws` but NOT `/health` and `/ready` — checked via X-Api-Key header or apiKey query param
  - WebSocket messages use `snake_case` JSON naming policy
- Updated README.md with prominent Getting Started link at top and full documentation listing
- Build verified: 0 errors, 16 pre-existing warnings (all CS9124 in test project)

- 2026-04-01: Added to team as Documentation Engineer. Existing docs written by Leela (architect) across sprints: architecture.md (1141 lines), configuration.md (1058 lines), extension-development.md (1540 lines), workspace-and-memory.md (1078 lines), cron-and-scheduling.md (1071 lines). Need to audit for style consistency, navigation, and GitHub Pages readiness.
- Current docs live in docs/ folder: architecture.md, configuration.md, extension-development.md, workspace-and-memory.md, cron-and-scheduling.md
- README.md was updated during consistency audit but may need further work for first-time users
- No documentation site (GitHub Pages) exists yet — needs to be set up
- No style guide exists — need to establish one for consistency across all docs

## 2026-04-04T00:49:47Z — Pi Provider Architecture Port Sprint (Team Sync)

**Sprint Status:** ✅ Complete  
**Timestamp:** 2026-04-04T00:49:47Z  
**Orchestration:** See `.squad/orchestration-log/2026-04-04T00-49-47Z-kif.md`

**Your Contribution (Kif — Documentation):**
- Updated architecture docs with provider abstraction layer
- Added model mapping tables and capability references
- Updated configuration reference with model selection guidance
- Created code examples for provider implementation templates
- Documented provider-owned normalization contract

**Team Outcomes:**
- **Farnsworth (Platform):** Ported Pi provider architecture — ModelDefinition, CopilotModels registry (30+ models), 3 API format handlers, rewrote CopilotProvider. 3 commits.
- **Bender (Runtime):** Verified AgentLoop + Gateway integration — no changes needed. Commit e916394.

---

## 2026-04-06T0546Z — Phase 10: Dev-Loop Documentation Overhaul (Wave 2)

**Duration:** ~6 min  
**Status:** ✅ Complete  
**Scope:** Documentation updates, dev-loop fixes, CLI reference creation

**Deliverables:**

1. **Dev Loop Documentation Overhaul**
   - Pre-commit hook accuracy fixed: targets Gateway tests, not non-existent `BotNexus.Tests.Unit`
   - Removed phantom script references: `pack.ps1`, `install.ps1` don't exist
   - Documented only 4 actual scripts: `dev-loop.ps1`, `start-gateway.ps1`, `export-openapi.ps1`, `install-pre-commit-hook.ps1`
   - Fixed section numbering gaps
   - **Rationale:** Docs referencing non-existent paths create friction for both human developers and AI agents

2. **CLI Reference Guide Created**
   - New documentation: `docs/cli-reference.md`
   - Commands documented: `init`, `agent list/add/remove`, `config get/set`
   - Configuration management guide
   - Common usage patterns

3. **Documentation Updates (5 Files)**
   - `docs/dev-loop.md` — canonical dev loop reference (restructured as single authoritative source)
   - `docs/configuration.md` — CLI integration section
   - `docs/getting-started.md` — CLI quickstart
   - `docs/README.md` — documentation navigation updated
   - `docs/cli-reference.md` — NEW (comprehensive CLI guide)

**Technical Accuracy:**
- All file paths verified against actual codebase
- All script parameters verified against source code
- Port numbers, config structure, initialization verified
- Cross-references to existing docs checked

**Quality Notes:**
- Docs accuracy: 100% verified against source code
- Completeness: All active scripts documented
- Accessibility: New CLI reference provides developer on-ramp

**Orchestration Log:** `.squad/orchestration-log/2026-04-06T0546Z-kif.md`

---

## 2026-04-XX — Gateway Service Documentation Update

**Sprint Status:** ✅ Complete

**Work Completed:**
- Audited documentation against current Gateway implementation
- Updated `getting-started-dev.md` (8 sections, ~400 lines)
  - Fixed port references: 18790 → 5005 (verified in `start-gateway.ps1` defaults)
  - Simplified config structure: removed "BotNexus" wrapper, "isolationStrategy", and outdated CLI installation steps
  - Clarified OAuth token storage: auth.json (not tokens/copilot.json)
  - Updated WebUI URL to http://localhost:5005/ with fallback
  - Removed outdated Copilot provider config format
- Updated `dev-loop.md` (3 sections, ~100 lines)
  - Fixed API key config schema: removed `isAdmin` field, added query param auth
  - Simplified provider auth documentation
  - Updated config validation endpoint reference
  - Removed legacy extensions path references
- Updated `getting-started.md` (1 section)
  - Clarified "Install from Release" path availability
  - Corrected prerequisites text
- **Source code verification:** All claims audited against:
  - `src/gateway/BotNexus.Gateway.Api/Program.cs` — /health endpoint, WebUI fallback
  - `src/gateway/BotNexus.Gateway/Configuration/BotNexusHome.cs` — directory structure
  - `src/gateway/BotNexus.Gateway/Configuration/PlatformConfig.cs` — config schema
  - `src/gateway/BotNexus.Cli/Program.cs` — default port 5005
  - `scripts/start-gateway.ps1` and `scripts/dev-loop.ps1` — script defaults
- **Build verified:** 0 errors, solution builds cleanly
- **Gateway tests:** 276 tests pass ✅
- **Commit:** 38b632e — `docs: Update dev setup guide for Gateway Service`

**Key Findings:**
- Port 5005 is the runtime default (not built-in config); scripts set it as `-Port` parameter default
- Config doesn't require BotNexus wrapper at top level; nested `gateway` section optional
- OAuth device code flow is automatic on first message; tokens stored in `~/.botnexus/auth.json`
- `/health` returns `{"status":"ok"}` (not full health checks)
- Hot-reload works for all config except `gateway.listenUrl` (requires restart for port changes)
- **Hermes (Tester):** 72 new tests for model registry, handler routing, format handlers. 494 total tests passing. Commit 5d293d4.

**Cross-Team Decisions Merged:**
1. Repeated tool call detection needed (Squad investigation)
2. Copilot Responses API investigation (Farnsworth)
3. Provider Response Normalization Layer (Leela, architectural)
4. Responses API Migration Sprint Plan (Leela, awaiting approval)

**Documentation Impact:** Onboards future developers to provider architecture and normalization contract enforcement.

---

### 2026-04-01 — Getting Started Guide Complete (694 lines, 13 sections)

**Deliverable:** `docs/getting-started.md` — comprehensive onboarding guide for first-time users covering prerequisites through OpenClaw migration.

**Sections:** Prerequisites, Installation, First Run, Initial Configuration, Adding Channels, Adding Providers, Creating Custom Tool, Running Agents, Building Custom Agents, Deployment Scenarios, Troubleshooting, OpenClaw Integration, Reference Links.

**Verification Process:** Every code example, configuration default, API endpoint, and file path cross-referenced against live source:
- GatewayConfig.Port = 18790 (verified)
- BotNexusHome.Initialize() directory structure (verified)
- appsettings.json defaults (verified)
- FileOAuthTokenStore token path ~/.botnexus/tokens/copilot.json (verified)
- OAuth device flow console output format (verified)
- WebSocket JSON naming policy snake_case (verified)
- API key authentication on /api/*, /ws (verified), /health exemption (verified)
- Agent bootstrap file set (verified)

**Build Check:** 0 errors, 16 pre-existing CS9124 warnings in test project.

**README Updates:** Added prominent Getting Started link at top, full documentation listing with navigation.

**Team Impact:** Supports 100% scenario coverage and new user onboarding. All steps tested end-to-end.

### 2026-04-03 — Skills Platform Sprint Documentation

**Timestamp:** 2026-04-03T07:50:00Z  
**Status:** ✅ Complete  
**Scope:** Skills guide, API docs, configuration, README  

**Deliverables:**
- **Skills Guide** (640 lines) — docs/skills-guide.md
  - Skill discovery and loading architecture
  - YAML frontmatter directive syntax
  - Per-agent binding and filtering rules
  - Wildcard DisabledSkills patterns
  - Custom skill development examples
- **API Reference Updates** — Endpoint documentation
  - GET /api/skills
  - GET /api/skills/{skillId} with frontmatter response
  - POST /api/agents/{agentId}/skills binding
- **Configuration Documentation** — Skills section in agent config
- **README Updates** — Links to skills guide in feature list
- **Commit:** f241ca3

**Quality Metrics:**
- 640 lines of skills content
- All endpoints documented with examples
- Configuration examples verified against source code

---

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

### 2026-04-02 — Squad Lifecycle Skill Created

**Deliverable:** `.squad/skills/squad-lifecycle/SKILL.md` — self-contained skill file covering all first-time setup and team lifecycle operations. Extracted from `squad.agent.md` v0.9.1 to reduce coordinator context load (~40% of content that was loading every session but only needed occasionally).

**Sections extracted:**
- Configuration Check (new — lightweight "am I configured?" gate)
- Init Mode Phase 1 & Phase 2 (team proposal and creation)
- Casting & Persistent Naming (universe allowlist, name allocation, overflow, state files, migration)
- Team Member Management (adding, removing, plugin marketplace)
- Integration Flows (GitHub Issues, PRD Mode, Human Team Members, Copilot Coding Agent)
- Worktree Lifecycle Management (creation, reuse, cleanup, pre-spawn setup)
- Format References (multi-agent artifact format, constraint budget tracking)
- Anti-patterns section

**Key design choice:** Used the template version of squad.agent.md (v0.9.1, 946 lines) as the stable source for extraction. The live agent file had already been restructured to reference this skill via a pointer at line 25. All on-demand reference pointers to `.squad/templates/` preserved as-is.

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

## 2026-04-05T07:12:57Z — P0 Sprint Implementation Phase (Team Completion)

**Status:** ✅ COMPLETE  
**Teams:** Farnsworth (Platform), Bender (Runtime), Hermes (QA), Kif (Docs)  
**Orchestration Log:** `.squad/orchestration-log/2026-04-05T07-12-57Z-*.md` (7 entries)  
**Session Log:** `.squad/log/2026-04-05T07-12-57Z-implementation-phase.md`

**Your Work (Kif):**
- Training documentation: 7 guides (~2500 lines) ✅
- 1 commit
- Covers provider integration, tool development, deployment procedures

**Team Outcomes:**
- Farnsworth: Provider fixes (P0+P1) — 4 commits, build ✓
- Bender: Tool + AgentCore + CodingAgent — 6 commits, tests ✓
- Hermes: 101 regression tests (3 projects) — 1 commit, coverage ✓
- Kif: 7 training guides (~2500 lines) — 1 commit, docs ✓

**All systems green. Ready for integration.**

---

## 2026-04-05 (Phase 3) — Training Documentation Expansion

**Status:** ✅ Complete  
**Scope:** 4 new comprehensive training modules covering Phase 3 enhancements  

**Deliverables:**
- **06-context-file-discovery.md** (580 lines) — Auto-discovery of README, copilot-instructions.md, and docs files. Covers budget management, truncation algorithm, integration with SystemPromptBuilder. Includes algorithm walkthrough and best practices.
- **07-thinking-levels.md** (390 lines) — End-to-end thinking levels pipeline from CLI through provider implementation. Covers ThinkingLevel enum, SimpleStreamOptions, SimpleOptionsHelper budget calculation, Anthropic provider example, and complete usage walkthrough.
- **08-building-custom-coding-agent.md** (425 lines) — Hands-on guide for building a coding agent from scratch. Covers agent creation, DefaultMessageConverter usage, SystemPromptBuilder, tool registration, session management, and safety/audit hooks. Includes complete 10-step minimal working example.
- **09-tool-development.md** (570 lines) — Comprehensive tool development guide with 3 full examples (echo, calculator, database query). Covers IAgentTool interface, lifecycle, streaming results, system prompt contribution, error handling, testing patterns, and best practices.

**Documentation Updates:**
- Updated README.md with table of 4 new training modules + cross-references
- Updated 00-overview.md "What's next" section with links to new modules
- Extended 05-glossary.md with 6 new terms: ContextFileDiscovery, ThinkingLevel, ThinkingBudget, SimpleStreamOptions, SimpleOptionsHelper, PromptContextFile

**Quality Metrics:**
- 1,965 lines of new training content
- 4 independent modules with complete coverage of Phase 3 features
- 13 code examples (echo tool, calculator tool, DB query tool, provider examples, system prompt examples)
- Cross-references to source code implementations (ContextFileDiscovery.cs, SimpleOptionsHelper.cs, etc.)
- All modules follow existing documentation style and structure

**Key Coverage Areas:**
- **Context discovery:** 16 KB budget, file prioritization, truncation logic, binary search algorithm, integration with system prompt
- **Thinking levels:** 5 levels (Minimal-ExtraHigh), default budgets, custom budgets, budget adjustment for output room, provider clamping, end-to-end flow example
- **Custom coding agent:** AgentOptions wiring, DefaultMessageConverter usage, system prompt construction, tool registration, session lifecycle, hooks
- **Tool development:** Full tool lifecycle (PrepareArgumentsAsync → BeforeToolCall hook → ExecuteAsync → AfterToolCall hook), streaming with updateCallback, error handling patterns, testing

**Cross-Team Alignment:**
- Aligns with Farnsworth's Phase 3 provider work (SimpleOptionsHelper, thinking budgets)
- Aligns with Bender's DefaultMessageConverter moved to AgentCore.Configuration
- Aligns with Hermes' test coverage expansion
- Addresses training gaps identified in Phase 1/2

## Session: Phase 3 Port Audit Design Review (2026-04-05T09:49:50Z)

Participated in design review ceremony for Phase 3 architecture. All ADs approved (9–17):
- **AD-9** DefaultMessageConverter → Farnsworth
- **AD-10** --thinking CLI + /thinking command → Bender  
- **AD-11** ListDirectoryTool → Bender
- **AD-12** ContextFileDiscovery → Bender
- **AD-14** session metadata entries → Bender
- **AD-15** ModelRegistry utilities → Farnsworth
- **AD-17** /thinking slash command → Bender
- **AD-13** deferred (OpenRouter routing types, no provider yet)
- **AD-16** already present (maxRetryDelayMs)

**Orchestration logs:** .squad/orchestration-log/2026-04-05T09-49-50Z-{agent}.md

**Session log:** .squad/log/2026-04-05T09-49-50Z-port-audit-phase-3.md

**Boundaries:** AgentCore ↔ CodingAgent (DefaultMessageConverter), CodingAgent ↔ Session (MetadataEntry), Providers.Core (ModelRegistry utilities).

**Next:** Parallel execution tracks. Farnsworth + Bender begin implementation. Kif writes training docs. Nibbler runs consistency review.


---

## 2026-04-05T11:52:58Z — Sprint 4 Documentation Delivery

**Status:** ✅ COMPLETE  
**Timestamp:** 2026-04-05T11:52:58Z  
**Orchestration Log:** .squad/orchestration-log/2026-04-05T11-52-58Z-kif.md

**Your Deliverables (Kif — Documentation):**

1. **Training Modules Updated (5 modules):**
   - Agent Loop Training: Queue state visibility + listener error handling patterns
   - Provider Architecture: Provider decomposition + JSON standardization
   - Tool Implementation: EditTool diff strategy + ShellTool platform handling
   - Message Lifecycle: MessageStartEvent/MessageEndEvent timing clarifications
   - Testing Patterns: New test cases for functionality

2. **New Content:**
   - Changelog Module: Sprint 4 summary (7 P0 decisions, 18+ P1 items, 16 new tests)

3. **Audience:**
   - Developers: Implementation patterns for each decision
   - Reviewers: Decision rationale and validation evidence
   - Contributors: Up-to-date architecture reference

**Build Status:** ✅ All documentation built and validated

**Next Phase:** Documentation ready for release notes and contributor onboarding.

---

## Dev Loop Documentation Overhaul

**Status:** ✅ Complete  
**Scope:** Full audit and update of dev loop docs, scripts, and getting-started guides.

**Issues Found & Fixed:**

1. **`scripts/install-pre-commit-hook.ps1`** — Path bug: used `$PSScriptRoot` (resolves to `scripts/`) for `.git/hooks/` path, which put the hook in `scripts/.git/hooks/` instead of repo root `.git/hooks/`. Fixed to use `Split-Path -Parent $PSScriptRoot` pattern. Also fixed reference to non-existent `tests/BotNexus.Tests.Unit/` — changed to `tests/BotNexus.Gateway.Tests` which is the actual test project.

2. **`docs/dev-loop.md`** — Rewrote entirely. Added: quick start section, full dev loop diagram (edit→build→test→run→verify), project structure table for edit targets, `-SkipBuild` and `-SkipTests` parameters for `dev-loop.ps1`, `-SkipBuild` for `start-gateway.ps1`, `export-openapi.ps1` reference, live testing with Copilot section (OAuth flow + WebSocket testing), auth.json documentation, environment variables table, expanded troubleshooting.

3. **`docs/development-workflow.md`** — Removed phantom references to `scripts/pack.ps1` and `scripts/install.ps1` (don't exist), fixed `BotNexus.Agent.Tests` → `BotNexus.AgentCore.Tests`, fixed port 18790 → 5005, fixed `gateway.log` → `botnexus-*.log`, removed stale CLI install section.

4. **`docs/getting-started-dev.md`** — Fixed missing section 4 (skipped from 3 to 5), renumbered sections 4–12, fixed `dotnet run --project src/gateway/BotNexus.Cli -- validate` (CLI project is at `src/gateway/BotNexus.Cli/`, but validation via API is more reliable).

5. **`docs/dev-guide.md`** — Added `-SkipBuild` and `-SkipTests` to `dev-loop.ps1` parameter table, added `-SkipBuild` to `start-gateway.ps1` table, added fast restart example.

**Verified:** Build succeeds (0 warnings, 0 errors). All documented file paths confirmed against BotNexus.slnx and actual directory structure.
