# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI/Copilot), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-04-01: Added to team as Consistency Reviewer after Jon caught docs/architecture.md not reflecting the ~/.botnexus/ unified config that was implemented after the docs were written. Multi-agent workflows have a systemic consistency gap — each agent updates their own files but nobody checks the seams.
- 2026-04-03: Post-sprint consistency reviews are ESSENTIAL after major refactors. The Pi provider architecture port was a massive rewrite (26 models, 3 API handlers, model-aware routing), and even with excellent engineering discipline, minor issues (11 nullability warnings) slipped through. Documentation quality is high — all architecture docs, configuration guides, and README were already accurate.

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

