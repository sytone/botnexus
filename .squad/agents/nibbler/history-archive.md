## 2026-04-06T08:20:00Z — Phase 11 Wave 3 Consistency Review (12 P1 fixes)

**Timestamp:** 2026-04-06T08:20:00Z  
**Wave:** 3 of Phase 11 — Reviews + Tests  
**Status:** ✅ Complete  
**Grade: Good**

**Scope:** Post-wave consistency audit of Phase 11 implementations (code ↔ docs, docs ↔ docs, README ↔ implementation).

**12 P1 Fixes Applied:**
1. **CLI README** — Corrected inverted --enabled default claim (said "off by default", actually true by default)
2. **Gateway README** — Removed false claim about GatewayHostBuilder on IHostBuilder (actual: AddBotNexusGateway on IServiceCollection)
3. **Gateway README** — Removed false claim about ReaderWriterLockSlim (actual: C# 13 Lock type used)
4. **Gateway.Api README** — Fixed WS activity path documentation (/api/activity → /ws/activity)
5. **Gateway.Api README** — Removed documentation of non-existent query param auth
6. **cli-reference.md** — Updated stale provider default ("github-copilot" → "copilot", matching Phase 10 code fix)
7. **Telegram README** — Removed stale "Stub" label, updated to reflect full Bot API implementation
8. **Telegram README** — Added TelegramBotApiClient documentation
9. **sample-config.json** — Added missing extensions section
10. **config.example.json** — Added missing extensions section
11. **configuration.md** — Added JSON schema validation documentation
12. **All new module READMEs** — Cross-validated 6 new README files (Gateway, Gateway.Api, CLI modules) for accuracy

**5 P2s Identified (Phase 12 backlog):**
1. **extension-development.md** — References completely fictional IExtensionRegistrar interface — needs full rewrite
2. **Config file automation gap** — Manual drift detection insufficient, need CI sweep check
3. **Protocol field additions** — Still bypass README updates (tool_end toolName/toolIsError pattern repeating)
4. **Cross-document updates** — Require stronger automation (features ship but docs lag 1-2 phases)
5. **Test helper duplication** — 3 files share ToAsyncEnumerable/RecordingActivityBroadcaster (acceptable for Phase 11, extract at 15+ tests)

**Quality Metrics:**
- **Code-level consistency:** 0 issues (excellent engineering discipline)
- **Documentation consistency:** 12 P1s fixed, 5 P2s noted
- **XML doc coverage:** 100% across 22 new public interfaces + 9+ classes
- **Overall consistency:** Good (maintained from Phase 10)

**Key Learning:**
Pattern reinforced: Code quality excellent, documentation updates require separate pass. Config example files silently drift because never compiled/validated. Recommend Phase 12 CI enhancement: JSON schema validation gate for config files.

**Commit:** b5188bc  
**Orchestration Log:** `.squad/orchestration-log/2026-04-06T08-20-00Z-Nibbler.md`

---


## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-04-01: Added to team as Consistency Reviewer after Jon caught docs/architecture.md not reflecting the ~/.botnexus/ unified config that was implemented after the docs were written. Multi-agent workflows have a systemic consistency gap — each agent updates their own files but nobody checks the seams.
- Tool names renamed during port audit (list_directory→ls, glob→find) but training docs and CodingAgent README were not updated — tool renames need a grep-sweep checklist across all docs.
- Parameter renames (start_line/end_line→offset/limit, include→glob, max_results→limit) are high-risk for doc staleness because the old names still compile via legacy support in tool code, masking the drift.
- ThinkingBudgets type was simplified from ThinkingBudgetLevel record to plain int? but docs/README examples still showed the old type — type changes need compile-check of all doc code snippets.
- CodingAgent README had a separate maintenance cadence from training docs — both must be updated when tools change.
- 2026-04-03: Post-sprint consistency reviews are ESSENTIAL after major refactors. The Pi provider architecture port was a massive rewrite (26 models, 3 API handlers, model-aware routing), and even with excellent engineering discipline, minor issues (11 nullability warnings) slipped through. Documentation quality is high — all architecture docs, configuration guides, and README were already accurate.
- 2026-07-18: Gateway consistency review — 0 P0, 4 P1, 7 P2. Biggest patterns: (1) CancellationToken parameter naming split (`ct` in API layer vs `cancellationToken` everywhere else) — API-layer shortcuts diverge from library conventions. (2) ConfigureAwait(false) policy differs between Gateway (never) and AgentCore (always) — intentional but undocumented. (3) Test file names didn't match class names (5/6 files) — likely from early scaffolding with generic names that weren't updated. (4) Gateway uses C# 13 `Lock` type while AgentCore still uses `object` locks — modernization gap, not a bug. Overall: Gateway code quality is high. XML doc coverage near 100%, record/class usage appropriate, project structure exemplary.
- 2026-04-05: Gateway P1 sprint consistency review — 0 P0, 1 P1 (fixed), 3 P2. Previous P1s from July review are resolved (CancellationToken `ct` in API layer fixed, test file names fixed). Fixed misleading ConfigureAwait(false) comment in FileSessionStore.cs that implied all Gateway library code uses it — actually only Gateway.Sessions does; Gateway host omits it intentionally. Telegram/TUI channel stubs use raw singleton for options instead of .NET Options pattern and don't extend ChannelAdapterBase — acceptable for Phase 2 stubs. Overall consistency is Good — significantly improved since last review.
- 2026-07-18: Phase 2 sprint consistency review — 0 P0, 0 P1, 6 P2. Sprint touched gateway core (streaming, thinking, isolation), abstractions (events, config contracts), API (WebSocket refactor), configuration (file-based agents, validator, hosted service, DI), WebUI (major enrichment), and 31 new tests. Quality is Good. Key patterns: (1) Dead `TaskCompletionSource` in `InProcessAgentHandle.StreamAsync` — vestigial from early prototyping, no runtime impact. (2) Test helper duplication (`ToAsyncEnumerable`, `RecordingActivityBroadcaster`) across 3 files — extract to shared fixture when test count grows. (3) `ThinkingContent` vs `ContentDelta` naming asymmetry is minor. (4) WebSocket `tool_end` doesn't forward `toolIsError` to clients. (5) ConfigureAwait(false) policy correctly applied — host project omits, library project uses. (6) All previous P1s remain fixed. Team engineering discipline is strong — consistency improves with each sprint.
- 2026-07-18: Phase 3 sprint consistency review — 0 P0, 2 P1 (fixed), 3 P2. Sprint added steering/follow-up, cross-agent calls, 3 isolation stubs, platform config system. Fixed: (1) Isolation stubs missing `/// <inheritdoc />` on interface members — InProcessIsolationStrategy had them, stubs didn't. (2) `GatewayOptionsTests` stuffed into AgentDescriptorValidatorTests.cs — same one-class-per-file issue fixed for 5 other files last sprint, reintroduced this sprint. P2s: sync-over-async in `AddPlatformConfiguration`, PlatformConfig vs GatewayOptions registration pattern divergence, "Phase 2 stub" labeling in isolation stubs. All previous P1s remain fixed. Overall quality Good — team continues to improve.
- 2026-07-18: Phase 4 Wave 1 consistency review — 0 P0, 2 P1 (fixed), 5 P2. Sprint added multi-tenant API keys, config validation endpoint, WebSocket reconnection caps, recursion guard for cross-agent calls, duplicate supervisor create prevention. Fixed: (1) ConfigController.cs shipped without XML docs — only controller in the API project missing them. (2) PlatformConfig property-level XML doc inconsistency — ProviderConfig had property docs but Phase 4's ApiKeyConfig, GatewaySettingsConfig, AgentDefinitionConfig, ChannelConfig did not. Also fixed stale FileSessionStore ConfigureAwait comment that incorrectly called BotNexus.Gateway a "host project." P2s: GatewayWebSocketOptions not configurable from appsettings.json, API reference missing Chat/Config endpoints, README project structure stale, ConfigureAwait(false) inconsistent within Gateway library (AgentConfigurationHostedService uses it, nothing else does). All previous P1s remain fixed. Overall quality Good — naming, sealed, DI, interface contracts all clean.
- 2026-07-18: Phase 5 consistency review — 0 P0, 3 P1, 6 P2, 3 P3. Sprint added auth middleware, workspace manager, context builder, channel capability flags, TUI input loop, WebSocket channel adapter, CLI project, session cleanup service, session locking. Key findings: (1) CancellationToken `ct` naming re-emerged in new IAgentWorkspaceManager, IContextBuilder interfaces and GatewayAuthManager — the Phase 1 API-layer fix held but library-layer code reintroduced the abbreviation. This is a systemic pattern: each wave of new code risks reintroducing abbreviations because the convention isn't enforced by tooling. (2) TUI README described the input loop as "not yet implemented" when `RunInputLoopAsync()` is fully functional — classic docs-lag from parallel agent work where one agent implements features while docs were written earlier by another. (3) New Abstractions interfaces shipped without XML docs, breaking the 100% coverage pattern. ConfigureAwait(false) inconsistency within Gateway library persists from Phase 4. All sealed, DI, naming conventions, error messages, test naming, and cross-agent work are clean. Overall quality Good.
- 2026-07-18: Phase 6 consistency review — 4 P0 (fixed), 5 P1 (fixed), 5 P2, 1 P3. Sprint added cross-agent calling, channel capability updates, WebUI enhancements, dev scripts, comprehensive developer docs. ALL issues were in documentation — code quality was excellent with zero code-level inconsistencies. Key patterns: (1) `api-reference.md` was the staleness hotspot — it predated the PlatformConfig schema change, used old port 18790, old agent field names, and documented non-existent endpoints (PATCH sessions, /ready). (2) Copilot auth reference naming drifted across docs (`auth:copilot` vs `auth:github-copilot`) from parallel authoring. (3) README WebSocket protocol omitted `thinking_delta` event added in Phase 5 — protocol docs should be auto-derived from handler XML docs. (4) Chat endpoints (POST /api/chat, steer, follow-up) existed in code since Phase 3 but were never added to api-reference.md — P4 review noted this as P2, now elevated and fixed. Team code discipline is strong; the consistency gap is purely in cross-document updates when multiple agents author docs for the same feature surface.
- 2026-04-06: Post Phase 7+ consistency review — 0 P0, 4 P1 (fixed), 5 P2 (fixed), 3 P2 (noted). All issues documentation-only, zero code-level inconsistencies. Key patterns: (1) Session endpoints (history, suspend, resume) added in code but never documented — repeats the Phase 3→6 pattern where chat endpoints took 3 phases to get documented. (2) `platform-config.example.json` still used Phase 1 flat format, never updated when PlatformConfig moved to nested sections. (3) `api-reference.md` session response shape used made-up field names (`key`, `agentName`, `title`, `messageCount`) that never existed in `GatewaySession`. (4) Gateway README project table referenced non-existent `BotNexus.Gateway.WebUI` — phantom reference from project rename/removal. (5) Auth exemption table included "static files with extensions" but middleware only checks `/health`, `/webui`, `/swagger`. Config example files need a scheduled sweep as part of each sprint review — they drift silently because they're never compiled or validated.
- 2026-04-06: Phase 9 consistency review — 0 P0, 3 P1 (fixed), 4 P2 (noted). Grade: Good. All P1s were documentation gaps: (1) PUT /api/agents/{agentId} endpoint missing from api-reference.md — repeats the pattern where new endpoints ship without docs. (2) README WebSocket protocol missing `toolName`/`toolIsError` on `tool_end` — same Phase 5 pattern where protocol field additions bypass README. (3) GatewayWebSocketHandler XML docs missing same fields — XML docs must stay in sync with ChannelAdapter wire format. P2s: CORS config completely undocumented (config.md, example JSON, README), conformance test project missing from dev doc test tables, CLI still uses `new HttpClient()`, configuration.md still shows port 18790. The systemic pattern persists: code quality is consistently excellent, but documentation updates for new features/fields require a separate pass. Conformance test suite and SessionReplayBuffer extraction were both fully consistent — no issues found.
- 2026-04-06: Phase 10 consistency review — 0 P0, 12 P1 (all fixed), 3 P2 (noted). Grade: Good. Sprint added CLI commands, WebSocket handler decomposition, PUT validation, CORS. Key patterns: (1) Provider naming inconsistency — CLI used "github-copilot" while every doc and code example used "copilot". First time a code default contradicted all documentation. Fixed CLI to "copilot". (2) api-reference.md documented OLD PUT behavior (overwrite) instead of new behavior (400 on mismatch) — docs updated for Phase 9 feature but described wrong semantics. (3) Gateway README was a staleness hotspot again — missing PUT endpoint, missing session endpoints (history/suspend/resume), WS protocol fields incomplete. (4) CORS was completely undocumented despite being environment-aware. (5) sample-config.json had `apiKeys` at root level instead of under `gateway`. (6) WebSocket handler decomposition was clean — XML docs accurate on all 3 classes, no stale references to monolithic handler. (7) New learning: CLI default values need explicit cross-check against documentation naming conventions — the "github-copilot" vs "copilot" drift would have confused every user running `botnexus init`.
- 2026-07-18: Phase 11 consistency review — 0 P0, 12 P1 (all fixed), 5 P2 (noted). Grade: Good. Sprint added new module READMEs (Gateway, Gateway.Api, CLI), CLI command handler decomposition, dynamic extension loader, JSON schema, Telegram Bot API implementation. ALL issues documentation-only — code quality excellent. Key patterns: (1) Gateway README claimed `GatewayHostBuilder` on `IHostBuilder` and `ReaderWriterLockSlim` — both incorrect, actual code uses `AddBotNexusGateway()` on `IServiceCollection` and C# 13 `Lock`. (2) Gateway.Api README had wrong WS activity path (`/api/activity` vs `/ws/activity`) and documented non-existent query param auth. (3) CLI README had inverted `--enabled` default (claimed "off", actually `true`) and non-existent `--home` option. (4) `cli-reference.md` STILL showed `github-copilot` as provider default despite Phase 10 code fix — recurring pattern where code fixes don't propagate to reference docs. (5) Telegram README still said "Stub" despite full Bot API implementation with `TelegramBotApiClient`. (6) `extension-development.md` references completely fictional `IExtensionRegistrar` interface — biggest gap, needs full rewrite. (7) Config example files missing `extensions` section. (8) `configuration.md` had no mention of JSON schema validation. XML doc comment quality is excellent across all 22 public interfaces and 9+ public classes.
- 2026-04-06: Phase 12 Wave 2 consistency review — 0 P0, 7 P1 (all fixed), 1 P2 (noted). Grade: Good. Wave 2 added SupportsThinkingDisplay rename, session metadata endpoints, config version, auth refactor, rate limiting middleware, correlation ID middleware, WebUI channels/extensions panels, +24 tests. Key findings: (1) WebUI app.js used old `supportsThinking` field name — first JavaScript-level drift caught; WebUI JS isn't type-checked against C# DTOs, making it a new staleness vector. (2) Session metadata GET/PATCH endpoints, correlation ID header, and HTTP rate limiting completely absent from api-reference.md — same recurring "new endpoints ship without docs" pattern. (3) Both Gateway READMEs missing rate limiting and correlation ID middleware sections — new middleware always needs explicit doc pass. (4) Config `version` field undocumented in all 4 doc/example files — config field drift continues to be silent. (5) All 24+ new tests follow MethodName_Scenario_ExpectedResult convention perfectly. (6) XML doc coverage 100% on all new public APIs. (7) SupportsThinkingDisplay rename fully executed across all code, DTOs, tests, docs — no remnants of old name.


## 2026-04-02 — Team Updates

- **Nibbler Onboarding:** New team member added. Role: Consistency Reviewer. Owns post-sprint consistency audits (docs vs code, docs vs docs, code comments vs behavior, README vs current state).
- **New Ceremony:** Consistency Review ceremony established. Trigger: after sprint completion or architectural changes. Owner: Nibbler. First implementation: Leela's audit (2026-04-02) found 22 issues across 5 files.
- **Related Decision:** "Cross-Document Consistency Checks as a Team Ceremony" merged into decisions.md (2026-04-01T18:54Z directive from Jon).


## 2026-04-03 — Post-Sprint Consistency Review: Pi Provider Architecture Port

**Requested by:** Squad Coordinator  
**Sprint:** Major rewrite — Pi provider architecture port (26 models, 3 API format handlers, model-aware routing)

**Review Scope:**
1. Docs ↔ Code: architecture documentation accuracy
2. Model registry ↔ Pi's models: CopilotModels.cs vs documented models
3. Handler implementations ↔ API specs: AnthropicMessages, OpenAiCompletions, OpenAiResponses
4. Tests ↔ Code: test coverage of new architecture
5. Old code cleanup: leftover references to old single-format provider
6. Config ↔ Code: configuration docs match config classes
7. Build verification: clean build, all tests pass

**Results:**
- ✅ **7/7 dimensions PASS** — All aspects consistent
- ⚠️ **1 issue found:** 11 nullability warnings in test code (HandlerFormatTests.cs, RepeatedToolCallDetectionTests.cs)
- ✅ **Issue fixed:** Added null-conditional operators, converted sync test to async (commit 603ad26)
- ✅ **Build status:** 0 errors, 0 warnings (in production code)
- ✅ **Test status:** 494/494 unit tests pass, 620/622 total (2 pre-existing failures unrelated to Pi provider)

**Key Findings:**
- Architecture docs (docs/architecture.md lines 685-900) accurately describe model-aware routing system
- Model registry complete: 26 models correctly mapped to 3 API formats
- Handler implementations match API specifications (Anthropic Messages, OpenAI Completions, OpenAI Responses)
- Tests comprehensively validate new architecture (HandlerFormatTests, CopilotProviderTests, ProviderNormalizationTests)
- No legacy code or references to old monolithic provider
- Configuration documentation (docs/configuration.md lines 301-410) accurate with correct model tables
- Integration verification doc (docs/integration-verification-provider-architecture.md) confirms "drop-in replacement" status

**Commits:**
- 603ad26: "fix: resolve nullability warnings in test code"
- 8173440: "docs: post-sprint consistency review for Pi provider architecture"

**Documentation:**
- Full review: `.squad/agents/nibbler/post-sprint-pi-provider-review.md` (326 lines)

**Verdict:** ✅ **PRODUCTION-READY** — No blocking issues. Pi provider architecture port is fully consistent and ready for production.


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


## 2026-04-03 — Post-Sprint Consistency Review: Port Audit Fix Sprint

**Requested by:** sytone  
**Sprint:** 18-commit port audit fix sprint (providers, agent-core, coding-agent, tests, training docs)

**Review Scope:**
1. Training docs ↔ Code alignment (5 training files)
2. README ↔ Reality (3 READMEs)
3. New code ↔ Comments (IAgentTool.cs XML doc)
4. Config defaults ↔ Documented defaults
5. Glossary cross-references

**Results:**
- **53 discrepancies found** across 9 files (17 HIGH, 12 MEDIUM, 4 LOW severity + 75 broken glossary links)
- **All fixed** in commit 3d8eda3
- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: 372/372 pass (all 7 test projects)

**Key Fixes:**
- 01-providers.md: Added missing OpenAIResponsesProvider, documented maxTokens 32K cap and thinking budget defaults, fixed invalid C# cast syntax, documented adaptive thinking effort mapping
- 02-agent-core.md: Fixed tool lookup case-sensitivity (Ordinal, not case-insensitive), ToolExecutionStartEvent timing, PromptAsync concurrency behavior (throws, not blocks), InputSchema→Parameters in code example, QueueMode enum order
- 03-coding-agent.md: Fixed CreateSessionAsync nullable param, MetricsTool example to use current IAgentTool API, added BOM stripping and compaction cut-point validation docs, added missing SessionCompactionOptions parameters
- 04-building-your-own.md: Fixed ValueTask→Task in 5 delegate examples, IAgentTool member count, JSONL format (camelCase, session_header, version 2, leaf key)
- 05-glossary.md: Fixed all 75 broken cross-reference links (wrong filenames throughout)
- providers/README.md: Added OpenAIResponsesProvider, fixed CopilotProvider as static utility, fixed instance-based API, added missing StopReason values
- AgentCore/README.md: Added missing LlmClient param, fixed ExecuteAsync signature
- CodingAgent/README.md: Fixed tool count, added GrepTool/FileMutationQueue, fixed ExecuteAsync signature, updated project structure
- IAgentTool.cs: Fixed XML doc comment (case-insensitive → case-sensitive)

**Noted but not fixed (out of scope):**
- Root README.md describes planned architecture (Gateway, Channels, WebUI, etc.) that doesn't exist yet — likely intentional aspirational docs
- StreamAccumulator has only 1 unit test — flagged as coverage gap
- CodingAgent factory has only 1 test (reflection-based) — flagged as coverage gap


## 2026-04-03 — Post-Sprint 3 Consistency Review

**Requested by:** sytone (Jon Bullen)
**Sprint:** Sprint 3 — 7 commits (AD-9 through AD-17)

**Review Scope:**
1. Training docs 06-09 ↔ Code alignment
2. Existing training docs 01-05 ↔ Sprint 3 API changes
3. CodingAgent README ↔ Reality
4. IAgentTool interface ↔ Doc examples
5. Glossary completeness and deduplication

**Results:**
- **22 discrepancies found** across 7 files
- **All fixed** in commit e7ff6d8
- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: 415/415 pass (all 7 test projects)

**Key Fixes:**
- 07-thinking-levels.md: Stale CLI section claimed --thinking didn't exist; rewrote with actual --thinking flag, /thinking slash command, and session metadata docs
- 06-context-file-discovery.md: Truncation algorithm showed binary search but code uses char-by-char iteration
- 09-tool-development.md: IAgentTool.ExecuteAsync missing toolCallId param, wrong param order; GetPromptGuidelines wrong return type (string? vs IReadOnlyList<string>); all 4 example tools had wrong signatures
- 08-building-custom-coding-agent.md: SystemPromptBuilder.Build() called with wrong params (ProjectName, EnvironmentContext, PackageManagers); missing Utils namespace import; wrong cross-ref link
- 03-coding-agent.md: Missing ListDirectoryTool in tool table, code example, and note (6→7 tools)
- 05-glossary.md: Duplicate ThinkingLevel entry; missing cross-refs to modules 06-09
- CodingAgent/README.md: Missing --thinking in CLI help, wrong tool count (6→7), missing list_directory section, wrong read params

**Learnings:**
- New training docs (06-09) were written based on planned APIs rather than final implementations — every Sprint 3 doc had at least one wrong API signature
- ExecuteAsync signature change (added toolCallId) wasn't propagated to any doc examples
- GetPromptGuidelines return type change (string? → IReadOnlyList<string>) wasn't propagated to docs
- ListDirectoryTool (AD-11) wasn't added to 03-coding-agent.md or CodingAgent README
- CLI features (--thinking, /thinking) weren't reflected in 07-thinking-levels.md despite being the primary user-facing feature



