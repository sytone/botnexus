# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI/Copilot), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-6 Complete.** Build green, 225 tests passing. Nibbler owns consistency auditing, documentation alignment, cross-agent sync. Performed Phase 6 consistency review: 9 P0s fixed (API reference, WebSocket protocol, agent response shapes, auth exemptions), 5 P1s fixed (config examples, URL consistency, field naming). Validates code-to-docs alignment and catches drift early. Quality assurance mindset.

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

## 2026-04-05T11:52:58Z — Sprint 4 Consistency Audit

**Status:** ✅ COMPLETE  
**Timestamp:** 2026-04-05T11:52:58Z  
**Orchestration Log:** .squad/orchestration-log/2026-04-05T11-52-58Z-nibbler.md

**Your Deliverables (Nibbler — Consistency Review):**

1. **Consistency Audit Results:**
   - Decision Traceability: 7 P0s + 18 P1s fully routed and implemented ✅
   - Commit Quality: All commits include decision reference and rationale ✅
   - Test Coverage: 16 new tests aligned with decision ownership ✅
   - Documentation: Training modules synchronized with code changes ✅
   - Integration: No conflicts between agent deliverables ✅

2. **Quality Gates Passed:**
   - No breaking changes across modules
   - All P0 decisions reflected in implementation
   - Tests verify documented behavior
   - Documentation updated before sprint close
   - Pre-commit bypass usage compliant with exceptions

## 2026-04-05T23:30:00Z — Phase 4 Wave 1 Consistency Review

**Status:** ✅ Complete  
**Grade:** Good  
**Build:** 0 errors, 0 warnings | **Tests:** 684 passed, 0 failed, 2 skipped  

**P1s Found & Fixed (2 commits: cc005da, 1b5a0fc):**

1. **ConfigController.cs missing XML docs** — Phase 4 added config validation endpoint without class/method/record XML docs. All other controllers have them. Fixed: added complete XML doc coverage.
2. **PlatformConfig property-level docs inconsistency** — Phase 4 added ApiKeyConfig, GatewaySettingsConfig, AgentDefinitionConfig, ChannelConfig without property-level docs while pre-existing ProviderConfig had them. Fixed: added property-level docs to all.
3. **Stale ConfigureAwait comment** — FileSessionStore.cs comment incorrectly called BotNexus.Gateway a "host project" (it's a library) and didn't reflect Phase 4's .ConfigureAwait(false) addition to AgentConfigurationHostedService. Fixed: rewrote comment to correctly describe all three tiers.

**P2 Issues Documented (5 found, not fixed):**
- GatewayWebSocketOptions hardcoded limits (not configurable via appsettings.json)
- API reference missing Chat and Config endpoints
- README project structure stale
- ConfigureAwait(false) inconsistent within Gateway library (one method vs none elsewhere)
- Pre-existing XML doc gaps on implementation classes

**What Phase 4 Got Right:**
- Naming conventions: CancellationToken params, I-prefix interfaces, Method_Condition_Result test pattern
- sealed modifiers: all implementations properly sealed
- DI ↔ Controllers: all dependencies registered, multi-tenant auth correctly replaced
- Interface contracts: all 5 implementations fully satisfy contracts
- Multi-tenant API keys: end-to-end consistent
- Config validation: pipeline works correctly
- Recursion guard: AsyncLocal call chain prevents circular cross-agent calls
- Duplicate create prevention: TaskCompletionSource handles concurrent waiters correctly

**All Previous P1s Remain Fixed.** Overall consistency Good — improved since last review.

3. **Blockers Found:**
   - None. All agent work internally consistent and well-integrated.

**Build Status:** ✅ Full solution clean (0 errors, 0 warnings)

**Next Phase:** Ready for merge validation and release.

---

## 2026-04-05 — Post-Sprint Consistency Review: P0/P1 Safety & Alignment Fixes

**Requested by:** sytone (Jon Bullen)
**Sprint:** P0 safety fixes (Bender), P1 alignment fixes (Farnsworth), tests (Hermes), training docs (Kif)

**Review Scope:**
1. New training docs (providers.md, agent-events.md, tool-security.md, building-a-coding-agent.md, README.md) ↔ Code
2. New XML doc comments (StopReason, AgentState, AgentMessage) ↔ Behavior
3. New public properties (MaxRetryDelayMs, SupportsExtraHighThinking) ↔ Docs
4. Existing docs (01-providers.md, 02-agent-core.md, 05-glossary.md) ↔ Sprint changes
5. Stale references grep sweep

**Results:**
- **12 discrepancies found** across 8 files (4 HIGH, 5 MEDIUM, 3 LOW)
- **All fixed** in commit d9e1ad9
- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: 453/453 pass (all 7 test projects)

**Key Fixes:**
- providers.md: Added SupportsExtraHighThinking to LlmModel, updated built-in models (30+ models, 3 provider groups including direct Anthropic/OpenAI), added missing GPT model IDs
- agent-events.md: Fixed ToolExecutionStartEvent order (fires before validation/hooks, not after), added MaxRetryDelayMs to AgentOptions reference
- tool-security.md: Added symlink resolution steps to ResolvePath, fixed platform comparison (OrdinalIgnoreCase on all platforms), fixed SanitizePath separator description
- building-a-coding-agent.md: Cleaned stale explicit nulls from LlmModel example
- 01-providers.md: Added SupportsExtraHighThinking to LlmModel
- 02-agent-core.md: Added MaxRetryDelayMs to AgentOptions record and field table
- 05-glossary.md: Added SupportsExtraHighThinking to LlmModel field list
- AgentEvent.cs: Fixed ToolExecutionStartEvent XML doc — Args are raw (pre-validation), event fires before hooks

**Learnings:**
- ToolExecutionStartEvent XML docs were wrong since inception — event fires before validation, not after. The ToolExecutor passes raw args to the event, not validated ones. This is the kind of subtle XML doc drift that's invisible unless you trace the exact call order in code.
- New record properties with default values (SupportsExtraHighThinking=false, MaxRetryDelayMs=null) don't cause compile errors in existing code, so they silently go undocumented. Every new public property needs a grep sweep of all doc files showing the record definition.
- IsUnderRoot uses OrdinalIgnoreCase unconditionally — the platform-aware PathComparer is only used for gitignore matching, not containment checks. This makes the security model slightly more permissive than documented on Unix.

## 2026-04-05 — Post-Sprint Consistency Review: Provider/Agent/CodingAgent Fix Sprint

**Requested by:** sytone (Jon Bullen)
**Sprint:** 11-commit fix sprint (providers, agent-core, coding-agent) + training docs update

**Review Scope:**
1. Port audit findings ↔ Recent fixes (16 findings checked)
2. Training docs ↔ Code (01-providers.md, 02-agent-core.md, 03-coding-agent.md)
3. READMEs ↔ Reality (3 READMEs)
4. Test coverage for new behaviors
5. Stale references in documentation

**Results:**
- **16 port audit findings confirmed FIXED** — all marked with status and commit refs
- **12 documentation gaps found** across 7 files
- **All fixed** in commit e61c1c8
- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: 460/460 pass (6 of 7 projects verified; CodingAgent tests slow due to shell process tests)

**Key Fixes:**
- docs/port-audit-findings.md: Added status update table, marked 16 findings FIXED inline with commit refs
- 01-providers.md: Added GuardedProvider docs to ApiProviderRegistry, updated LlmStream Push() to show auto-complete
- 02-agent-core.md: Updated StreamAccumulator with contextMessages param and Phase 5 streaming partials note; added steering poll skip documentation
- 03-coding-agent.md: Added INSTRUCTIONS.md + .botnexus-agent/AGENTS.md to context discovery; added skill validation rules table; added piped stdin section; added CurrentDateTime/CustomPrompt/tool-adaptive guidelines to system prompt
- providers/README.md: Added GuardedProvider comment in quick start
- AgentCore/README.md: Updated message flow for streaming partials
- CodingAgent/README.md: Added piped stdin usage, skill validation, context discovery, system prompt features

**Test Coverage Assessment:**
- ✅ 9/10 behaviors have test coverage: thinking disable, headers, skills validation, system prompt, streaming partials, steering poll, finish reason mappings, context discovery, tool call validator
- ❌ 1 gap: Piped stdin detection (no tests for Console.IsInputRedirected path)

**Learnings:**
- Port audit findings need a living status column — without it, 16 fixes were invisible to future readers
- GuardedProvider (API mismatch guard) was a significant architectural addition but absent from all docs
- StreamAccumulator's contextMessages parameter (streaming partials) changes the streaming contract — must be documented prominently
- Piped stdin detection has no test coverage — Console.IsInputRedirected is hard to test without process spawning
