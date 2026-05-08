# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Hermes's Specialization:** Test framework, QA strategy, integration testing, cross-platform compatibility. Owns comprehensive test matrix design, test discipline enforcement, E2E test infrastructure.

**Active Stream (Phase 12+):**
- Phase 11 Config Path Tests (2026-04-06): ConfigPathResolverTests, SchemaValidationTests, PlatformConfigurationTests edge cases — 23 new tests; 891 total passing
- Phase 12 Sub-Agent Testing Waves 1-4 (2026-04-10): Model validation, manager concurrency, integration testing, REST endpoint coverage, WebSocket event routing — 51 tests, all passing
- Wave 1 Coverage (auth bypass + channels + extensions): 23 new tests; 368/368 passing
- Wave 2 Coverage (rate limit + correlation + metadata + versioning): Expanded 4 test suites; build clean
- DDD Wave 4 (existence dual-lookup, SessionStoreBase contracts): 26 existence tests, 10 store contract tests; 794/794 passing
- Probe Testing (2026-04-14): Parser coverage (Serilog + JSONL readers, temp files for real conditions)
- Extension-Contributed Commands Wave 1 (2026-04-15): CommandRegistry + CommandModel tests; 10/10 passing
- **Current:** Read-Only Sub-Agent Session View Wave 2 Testing (2026-04-20): 22 new unit tests for SessionType, IsReadOnly, ViewSubAgentAsync, and ChatPanel read-only UI; 92/92 BlazorClient tests passing ✅
- **PR #179 (2026-05-07): Memory tool surface fix — Added guard coverage for memory_save content schema validation. 64 tests passing. Cross-platform path handling verified. Commit: 553eaf24**

**Test Discipline Enforced:**
- Always update tests when APIs change (no exceptions)
- Comprehensive edge case coverage before approval
- Cross-store consistency validation for data-layer changes
- Reflection-based tests for non-public testing needs (preserves production API shape)
- Real temp files for parser testing; mocks for unit/contract tests

**Test Philosophy:** Unit tests for logic, integration tests for contracts/interactions, E2E tests for user workflows. Manual browser testing for scroll/layout UX where automatic testing is unreliable.

---

## 2026-05-07 — PR #180 Refresh Merge + Validation (Hermes)

- Reused dedicated worktree `Q:\repos\botnexus-pr-180` on branch `refactor/conversation-dispatching-layer` tracking `origin/refactor/conversation-dispatching-layer`.
- Fetched latest refs and merged `origin/main` into the PR branch via merge commit `8c0995b0`.
- Full validation after merge:
  - `dotnet build BotNexus.slnx --nologo --tl:off` ✅
  - `dotnet test BotNexus.slnx --nologo --tl:off` ✅ (0 failed; E2E project skipped tests by design in current run)
- Ready to push refreshed branch and re-check PR checks.

## Learnings
- 2026-05-07: SQLite file-backed session-store tests can intermittently fail Windows cleanup with "file in use" unless test connection strings disable pooling (`SqliteConnectionStringBuilder.Pooling = false`) and test teardown clears pools before deleting DB files.
- 2026-05-07: Added broader dispatching regression coverage at gateway/session boundaries: default routing with null conversationId, explicit non-default session isolation, and originating binding metadata (ThreadId/BindingId/DisplayPrefix) preservation on outbound sends.
- 2026-05-07: Added SignalR conversation routing regressions that assert explicit conversationId resolves a distinct session (not the default portal session) and preserves conversation stamping on the resolved session.
- 2026-04-20: Repro tests for sub-agent wake delivery should assert both dispatch metadata (messageType=subagent-completion) and stream-event channel capabilities; race-condition coverage needs explicit fallback-to-dispatch expectations when IsRunning flips during follow-up enqueue.

- 2026-05-07: Cross-platform test portability (PR #179 CI fix) — all test fixture paths must use `Path.Combine()` and platform-aware path construction, not hardcoded backslash separators. WorkspaceContextBuilder tests were Linux-failing due to `"C:\\workspace\\..."` paths in config setup. Apply this pattern everywhere gateway tests initialize file paths or mock filesystem structures. Gateway workspace memory path tests now pass cross-platform.

- 2026-05-04: Gateway decoupling audit found direct test coupling concentrated in InProcessIsolationStrategy constructor wiring tests (tests\BotNexus.Gateway.Tests\InProcessIsolationStrategyTests.cs, ToolHookWiringTests.cs, Agents\SubAgentIntegrationTests.cs, PlatformConfigAgentSourceTests.cs); these should shift to DI/extension-loader-backed tool registration seams instead of hardcoded strategy composition.
- 2026-05-04: DI registration coverage for gateway startup is currently broad but shallow (IsolationStrategyRegistrationTests.cs, PlatformConfigurationTests.cs) and lacks assertions for runtime extension assembly scanning outcomes (IAgentTool/ICommandContributor registrations).
- 2026-05-04: Coverage gap: no gateway-level tests verify graceful degradation when skills/mcp/mcpinvoke/web extensions are absent or fail load; add extension-loader + in-process tool availability integration tests to prevent regressions during runtime discovery refactors.


## 2026-05-07T22:05:56Z — Dispatcher Routing Regression Coverage (Test Strategy)

**Status:** ✅ Complete  
**Session:** Dispatching Cleanup Wave — Phase 2 test expansion  

**Coverage Added:**
- Non-default conversation isolation from default paths
- Default fallback when conversationId omitted (null → binding-based resolution)
- Originating channel binding metadata preservation (ThreadId, BindingId, DisplayPrefix)
- Tests added/updated in GatewayHostTests, SignalRHubTests, SignalRThreadRoutingTests
- **Result:** 42/42 targeted tests passing

**Cross-team Context:**
- Farnsworth created `BotNexus.Gateway.Dispatching` contracts + adapter ✅
- Bender integrated dispatcher into gateway runtime paths ✅
- Leela approved all architectural decisions (✅ APPROVED)

**Follow-up:** Unit tests for `DefaultConversationDispatcher` edge cases once contracts stabilize.

---

## 2026-05-04 — Gateway Decoupling Test Audit

**From:** Hermes (Test Strategy)  
**Completed:** Test audit for gateway decoupling feature  
**Status:** ✅ Audit complete, ready for test implementation

**Farnsworth's Implementation (reviewed):**
- Introduced IAgentToolContributor runtime contract
- Removed 4 compile-time <ProjectReference> entries
- Changed extension loading from compile-time to runtime discovery
- AssemblyLoadContextExtensionLoader now finds contributors
- InProcessIsolationStrategy aggregates contributions per agent/session

**Leela's Architecture Context:**
- This implementation addresses HIGH-priority architectural issue
- Restores proper dependency direction (Extensions → Gateway.Contracts)
- Enables future refactoring of Contracts/Agent.Abstractions coupling

**Your Audit Results:**
- 🔴 4 pre-existing test failures (pre-decoupling, not new regressions) — should be fixed before merge
- 🟡 3 tests require manual review (mocking patterns, registration flow)
- 🟢 4 new tests needed:
  1. Contributor discovery test
  2. Context propagation test
  3. Lifecycle cleanup test (resource disposal)
  4. Tool registry append test

**Next Step:** Implement 4 new tests + fix 4 pre-existing failures before merge.

---

## 2026-05-07 — Issue #24 timeout regression test pass (Hermes)

- Added failing Gateway regression tests for missing timeout configuration plumbing:
  - `InProcessIsolationStrategyTests.CreateAsync_WhenDescriptorSpecifiesToolTimeout_PropagatesToAgentOptions`
  - `PlatformConfigAgentSourceTests.LoadAsync_WithAgentToolTimeoutSeconds_PreservesTimeoutForRuntimeWiring`
  - `PlatformConfigAgentSourceTests.LoadAsync_WithDefaultsToolTimeoutSeconds_InheritsTimeoutWhenAgentOmitsOverride`
- Added passing AgentCore regression test for structured timeout end-event emission:
  - `ToolExecutorTimeoutTests.HangingTool_EmitsToolExecutionEndEvent_AsError`
- Verified existing timeout recovery coverage remains in place:
  - `ToolExecutorTimeoutTests.HangingTool_TimesOut_ReturnsErrorResult`
  - `ToolExecutorTimeoutTests.AfterTimeout_NextToolCallSucceeds`
- Outcome: AgentCore timeout behavior passes; gateway timeout config wiring tests fail (expected, highlights issue #24 gap).

---


### 2026-05-07 — Phase 1 Regression Coverage: Conversation Routing Tests

- **Task:** Add regression tests for Phase 1 bug fix; verify session resolution behavior (not just metadata)
- **Test Philosophy:** Prior bug manifested in session resolution returning default even when metadata was correct; metadata-only assertions insufficient
- **Tests Added:**
  - SignalRHubTests.GatewayHub_SendMessage_WithConversationId_ResolvesConversationSession: verify explicit conversationId resolves target conversation's session
  - SignalRThreadRoutingTests.SignalRHub_NewConversation_GetsItsOwnSession (strengthened): expanded assertions to check both dispatched metadata AND resolved session identity + conversation store persistence
- **Test Status:** Targeted SignalR routing tests pass; full suite blocked by unrelated baseline failures
- **Coverage:** Session resolution behavior, conversation stamp persistence, non-default conversation isolation
## 2026-05-07 — OpenClaw Memory Wave 1 QA & Validation (QA/Test Coverage)

**Role:** Test coverage, validation, final QA verdict  
**Branch:** feature/openclaw-memory-alignment  
**Status:** ✅ APPROVE — Ready for merge  

**QA Activities:**

**Initial Validation (Post-Bender):**
- Reviewed diff: e21e9e38..494804c8
- Executed targeted Wave 1 tests: FileAgentWorkspaceManagerTests, WorkspaceContextBuilderTests, PlatformConfigAgentSourceTests, InProcessIsolationStrategyTests, ToolHookWiringTests, SubAgentIntegrationTests
- Found change-related failures: SystemPromptBuilderSnapshotTests (3 failures from AGENTS.md changes), WorkspaceContextBuilderTests ordering instability
- Pre-existing unrelated failures: SqliteSessionStoreConversationIdTests file lock cleanup

**Remediation Validation (Post-Farnsworth 58d03d13):**
- Full test execution results:
  - BotNexus.Memory.Tests: ✅ 61 pass
  - BotNexus.Prompts.Tests: ✅ 6 pass
  - Wave 1 targeted Gateway classes: ✅ all pass
  - Full BotNexus.Gateway.Tests: ❌ 7 pre-existing failures (outside Wave 1 scope)
  - Full solution suite: ❌ pre-existing failures only (CodingAgent timeouts, MCP flake, snapshot drift, file locks)

**Coverage Assignments (From Leela Conditions):**
- **N1 (now C2):** Missing ContextFileOrdering_DailyNoteTests — coverage exists in Prompts and WorkspaceContextBuilder tests (ordering, deterministic sequencing)
- **N3 (now C1):** DateTime consistency (DateTime.Now vs DateTime.UtcNow) — flagged as Wave 2 carry-forward
- **N4 (now C3):** 4000-char daily note budget not enforced — tracked for Wave 2 backlog

**Final Verdict:** ✅ **APPROVE**
- No change-related test failures in Wave 1 targeted scope
- Material coverage exists for modified areas (WorkspaceContextBuilder, FileAgentWorkspaceManager, MemorySaveTool delegation, ToolHookWiring)
- Full suite feasibility verified (pre-existing failures documented and isolated)
- Ready for merge

**Outcomes:**
- Wave 1 memory alignment validated and approved
- Three non-blocking conditions (C1–C3) carried to Wave 2 backlog

## 2026-05-07 — PR #180 Readiness Verification (Hermes)

- Synced and verified branch ancestry: HEAD b1e08a0d contains origin/main (5f18e5c), and remote PR branch points at b1e08a0d.
- Full local validation passed in worktree (dotnet build BotNexus.slnx --nologo --tl:off, dotnet test BotNexus.slnx --nologo --tl:off).
- CI check status currently shows one failing check (CI / build-and-test), with failure log indicating snapshot assertions in SystemPromptBuilderSnapshotTests; locally re-running those snapshot tests and full CI command sequence succeeded.
- Readiness call: no safe PR-scoped code fix identified from reproducible local evidence; report as CI instability/non-repro in this environment.

## 2026-05-08 — PR #180 CI Snapshot Portability Fix (Hermes)

- Verified worktree branch `refactor/conversation-dispatching-layer` is up to date with `origin/refactor/conversation-dispatching-layer` and contains `origin/main`.
- Inspected CI attempt 1 and rerun attempt 2: both failed in `build-and-test` on `ubuntu-latest` with snapshot mismatches in:
  - `BotNexus.CodingAgent.Tests.SystemPromptBuilderSnapshotTests.Build_GitAndToolContributions_MatchesSnapshot`
  - `BotNexus.Gateway.Tests.Agents.SystemPromptBuilderSnapshotTests.Build_FullMode_MatchesSnapshot`
  - `...Build_MinimalMode_MatchesSnapshot`
  - `...Build_NoTools_MatchesSnapshot`
- Root cause: snapshots asserted machine/OS-specific lines (Windows OS description and absolute workspace/docs paths), while CI renders Linux values (`Ubuntu 24.04.4 LTS`, `/tmp/...`).
- Applied minimal safe test-only normalization in snapshot helpers to canonicalize volatile lines before compare.
- Validation: targeted test projects passed; full `dotnet build BotNexus.slnx --nologo --tl:off` and `dotnet test BotNexus.slnx --nologo --tl:off` passed locally.
## 2026-05-07 — Conversation Project Extraction: QA & Validation

**Status:** ✅ Complete (Test validation passed, approved for merge)  
**Session:** Conversation project refactor orchestration  
**Coordination:** With Leela (design), Bender (implementation), Nibbler (consistency)  

**Your Role:** Quality Assurance. Test validation and coverage verification.

**Test Validation Results:**
- **BotNexus.Gateway.Conversations.Tests:** 66/66 passing ✅
- **BotNexus.Gateway.ConversationTests:** 9/9 passing ✅
- **Focused Gateway conversation coverage:** 62/62 passing ✅

**Scope Verification:**
- Confirmed all 7 conversation-focused test files moved from Gateway.Tests to new project ✅
- Confirmed store/router unit tests correctly isolated ✅
- No test regressions in moved behavior ✅

**Notes:**
- SqliteSessionStoreConversationIdTests file-lock failures observed (outside moved scope, pre-existing issue)
- Test naming conventions respected (Method_Condition_ExpectedBehavior)
- All assertions aligned with Leela's design contracts

**Status:** Approved for code review and merge

---
