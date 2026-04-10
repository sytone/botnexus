# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI/Copilot), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-6 Complete.** Build green, 225 tests passing. Nibbler owns consistency auditing, documentation alignment, cross-agent sync. Performed Phase 6 consistency review: 9 P0s fixed (API reference, WebSocket protocol, agent response shapes, auth exemptions), 5 P1s fixed (config examples, URL consistency, field naming). Validates code-to-docs alignment and catches drift early. Quality assurance mindset.

---

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

## 2026-04-11 — Post-Sprint Consistency Review: Sub-Agent Spawning Feature

**Requested by:** Jon Bullen (auto-triggered ceremony)
**Sprint:** Sub-agent spawning feature (4 waves, 10 commits across Farnsworth, Bender, Fry, Kif)

**Review Scope:**
1. Feature doc (sub-agent-spawning.md) ↔ Code (tools, manager, options, controller, WebUI)
2. Interface definitions ↔ Implementation
3. Tool parameter schemas ↔ Documentation
4. API endpoints ↔ Documentation
5. WebSocket event names ↔ Code ↔ WebUI listeners
6. Config defaults ↔ Documented defaults
7. Stale reference sweep (god-tool, workingDir, subagent_sessions)
8. Design spec open questions ↔ Implementation

**Results:**
- **9 discrepancies found** in docs/features/sub-agent-spawning.md
- **All fixed** in commit ff120a5
- ✅ Build: 0 errors, 0 warnings
- ✅ Tests: 1,792/1,792 pass (16 test projects)

**Key Fixes (P0 — incorrect information):**
- Session ID format: docs said `::sub::{childAgentId}::{uniqueId}` but code uses `::subagent::{uniqueId}`
- Timeout param: docs said `timeout` but code uses `timeoutSeconds`
- Phantom class: docs referenced `SubAgentCompletionHook` which doesn't exist; completion is handled inline by `DefaultSubAgentManager.RunSubAgentAsync()` → `OnCompletedAsync()`
- Missing param: `apiProvider` parameter undocumented
- Wrong response shapes: `manage_subagent` kill returns `{ killed: bool }`, not `{ status: "Killed" }`; status returns `subAgentId/status/resultSummary/startedAt/completedAt`, not the fields docs listed
- Response key casing: `list_subagents` returns `"subAgents"` (camelCase), docs had `"subagents"` (lowercase)

**Key Fixes (P1 — stale/incomplete):**
- Status updated from "Draft / In Progress" to "Complete"
- Removed 6 `<!-- DRAFT: verify against implementation -->` comments
- Example IDs changed from `sa_abc123` prefix to realistic GUID hex format

**Code Quality Notes (no fix needed):**
- Interface ↔ Implementation: `DefaultSubAgentManager` fully implements all 5 `ISubAgentManager` methods ✅
- Config defaults: `SubAgentOptions` defaults (5, 30, 600, 1, "") match both `SubAgentSpawnRequest` defaults and docs ✅
- DI wiring: `ISubAgentManager` registered as singleton in `GatewayServiceCollectionExtensions` ✅
- Tool schemas: JSON schemas in spawn/list/manage tools match implementation params ✅
- API endpoints: `GET/DELETE /sessions/{id}/subagents` match docs ✅
- Design spec `workingDir` param and `subagents` god-tool intentionally not implemented (documented in feature doc Security section)

**Learnings:**
- Multi-agent sprints produce "ghost references" — classes mentioned in docs that were designed but never implemented (SubAgentCompletionHook). Always grep for class names referenced in docs.
- Response shapes are the #1 drift source. Tool return values should be documented by reading the actual Serialize() call, not by guessing from the model.
- Parameter naming inconsistency between design spec (`timeout`) and implementation (`timeoutSeconds`) shows why design specs should be treated as historical, not prescriptive.
- The `apiProvider` parameter was in code but invisible in docs — new parameters added during implementation need a doc sweep.
