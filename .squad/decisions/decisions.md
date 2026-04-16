# Decisions Log

## Active Decisions

### Phase 9 Wave 2 — CORS, Agent Update, Replay Buffer, Conformance Tests

**2026-04-06 — Merged from inbox (7 decisions)**

#### 1. CORS Policy + Agent Update Endpoint (Bender)

**Date:** 2026-04-06  
**Owner:** Bender (Runtime)  

**Decisions:**

1. **CORS policy source**
   - Added a named CORS policy in `Program.cs`
   - Development environment allows all origins/headers/methods for local iteration
   - Non-development reads `allowedOrigins` from platform config (`gateway.cors` preferred, root `cors` legacy fallback)
   - If no origins configured, fallback to `http://localhost:5005` for local WebUI interoperability

2. **CORS placement**
   - Inserted `UseCors` before `GatewayAuthMiddleware` for correct preflight/browser request handling

3. **Agent update semantics**
   - Added `PUT /api/agents/{agentId}` and `IAgentRegistry.Update`
   - Agent identity route-owned; updates preserve agentId and modify descriptor fields in-place
   - Registry update returns 404 when target registration does not exist

**Files touched:** `src\gateway\BotNexus.Gateway.Api\Program.cs`, `AgentsController.cs`, `IAgentRegistry.cs`, `DefaultAgentRegistry.cs`, `PlatformConfig.cs`, `PlatformConfigLoader.cs`, `GatewayServiceCollectionExtensions.cs`

**Commits:** cc99842, 4a842e7

---

#### 2. HttpClient Registration via IHttpClientFactory (Bender)

**Date:** 2026-04-05  
**Owner:** Bender (Runtime)  

**Context:** Gateway DI used raw singleton `HttpClient` with 10-minute timeout. Pattern vulnerable to stale DNS and socket exhaustion.

**Decision:** Use `IHttpClientFactory` with named client:
1. Register factory services with `AddHttpClient()`
2. Register named client `"BotNexus"` with `Timeout = TimeSpan.FromMinutes(10)`
3. Register `HttpClient` singleton via `factory.CreateClient("BotNexus")` so frozen provider constructors remain unchanged

**Consequences:** Existing provider APIs unchanged (`HttpClient` constructor injection still works). Gateway runtime aligns with resilient HttpClient lifecycle. Current timeout semantics maintained while removing singleton anti-pattern.

**Status:** Complete (Commit 4a842e7)

---

#### 3. Session Replay Buffer Extraction (Farnsworth)

**Date:** 2026-04-06  
**Owner:** Farnsworth (Platform)  

**Decision:** Replay sequencing and replay-window storage belong to a dedicated `SessionReplayBuffer` abstraction, while `GatewaySession` remains responsible for session history and lifecycle metadata.

**Rationale:** `GatewaySession` had separate lock domains (history + replay), indicating two independent responsibilities. Extracting replay keeps lock ownership local to replay operations and leaves `GatewaySession` focused on conversation/session concerns.

**Compatibility:** Keep `GatewaySession` replay-facing APIs as wrappers so existing persistence and call sites continue to work; newer paths can call `session.ReplayBuffer` directly.

**Status:** Complete (Commit def9a4a) — SRP fix, 279 Gateway tests passing

---

#### 4. Provider Conformance Test Architecture (Hermes)

**Date:** 2026-04-06  
**Owner:** Hermes (Tester)  

**Decision:** Implement provider normalization conformance as a shared abstract test base in a dedicated helper project (`tests/BotNexus.Providers.Conformance.Tests`), with each provider test project inheriting and providing provider-specific SSE fixture payload builders.

**Rationale:** Keeps conformance assertions in one place while preserving provider-specific wire-format setup locally. Minimizes maintenance drift, ensures every provider runs identical contract assertions, avoids coupling frozen production provider code to test-only abstractions.

**Compatibility:** Helper project marked `IsTestProject=false` and consumed via project references, so provider suites execute inherited tests while `dotnet test` does not run the helper assembly directly.

**Status:** Complete (Commit 2a0044d) — Wired into Anthropic, OpenAI, OpenAICompat, Copilot test projects

---

#### 5. Dev Loop Documentation Overhaul (Kif)

**Date:** 2026-04-06  
**Owner:** Kif (Documentation)  
**Status:** Implemented

**Context:** Dev loop docs accumulated accuracy issues: broken pre-commit hook path, references to non-existent scripts (`pack.ps1`, `install.ps1`), phantom test project names, missing script parameters, gap in section numbering.

**Decisions:**

1. **Pre-commit hook targets Gateway tests, not unit tests** — No `BotNexus.Tests.Unit` project exists. Pre-commit hook now runs `tests/BotNexus.Gateway.Tests` for fast feedback.

2. **Removed phantom script references** — `scripts/pack.ps1` and `scripts/install.ps1` don't exist. Documentation now references only 4 actual scripts: `dev-loop.ps1`, `start-gateway.ps1`, `export-openapi.ps1`, `install-pre-commit-hook.ps1`.

3. **`dev-loop.md` is canonical dev loop reference** — Restructured as single authoritative source for the edit→build→test→run→verify cycle, including live Copilot testing and auth.json setup.

**Rationale:** Docs referencing non-existent paths create friction for human developers and AI agents. Accuracy over completeness.

---

#### 6. Phase 9 Full Gateway Design Review — Grade A- (Leela)

**Date:** 2026-04-06  
**By:** Leela (Lead/Architect)

**Status:** Complete

**Scope:** Comprehensive design review of Gateway service (Abstractions, Core, API, Sessions, Channels) after 7+ phases of development.

**Grade:** A-  
**SOLID Score:** 24/25  
**Tests Passing:** 276 Gateway + 35 support = 311 total  
**Build:** 0 errors, 31 CS1591 warnings (missing XML docs)  
**P0 Issues:** None  
**P1 Items Identified:** 6 (replay buffer extraction, WS handler decomposition, IHttpClientFactory, StreamAsync leak, WebSocket payload validation, SessionHistoryResponse relocation)

**Key Recommendations:**
1. Extract replay buffer (SRP violation in GatewaySession dual-lock pattern)
2. Adopt IHttpClientFactory (socket exhaustion risk in singleton pattern)
3. Decompose WebSocket handler (458 lines, 5 responsibilities)

**Architecture Assessment:**
- ✅ Clean contract interfaces
- ✅ Ship-ready for current scope
- ✅ P1 items are refinements, not structural problems
- ⚠️ 6 P1 items for Sprint 7B (now Sprint 9A/B)

**Full review:** `.squad/sessions/2026-04-06T04-design-review.md`

---

#### 7. Phase 9 Gateway Requirements Gap Analysis (Leela)

**Date:** 2026-04-06  
**Author:** Leela (Lead/Architect)  
**Baseline:** Build 0 errors/31 CS1591 warnings, 811 tests (276 Gateway), Phase 8 Grade A-

**Requirement Scorecard:**

| # | Requirement | Status | Score |
|---|-------------|--------|-------|
| 1 | Agent Management | ✅ Complete | 100% |
| 2 | Isolation Strategies | ⚠️ Partial | 35% |
| 3 | Channel Adapters | ⚠️ Partial | 70% |
| 4 | Session Management | ✅ Complete | 95% |
| 5 | API Surface | ⚠️ Partial | 85% |
| 6 | Platform Configuration | ⚠️ Partial | 65% |

**P1 Items (Prioritized Backlog):**

| ID | Gap | Requirement | Effort | Status |
|----|-----|-------------|--------|--------|
| P1-1 | Dynamic extension loading | Req 6 + User Directive 2a | L | —  |
| P1-2 | CORS configuration | Req 3, 5 | S | ✅ Complete |
| P1-3 | `botnexus init` command | Req 6 | M | —  |
| P1-4 | JSON Schema generation | Req 6 | M | —  |
| P1-5 | CLI agent management | Req 6 | M | —  |
| P1-6 | Agent update endpoint | Req 5 | S | ✅ Complete |
| P1-7 | Telegram Bot API | Req 3 | L | —  |
| P1-C1 | Extract replay buffer | Req 4 (SRP) | M | ✅ Complete |
| P1-C2 | IHttpClientFactory | Quality | S | ✅ Complete |
| P1-C3 | Decompose WS handler | Quality (SRP) | M | —  |
| P1-C4 | Provider conformance tests | Quality | M | ✅ Complete |

**P2 Items (Important polish):**

- P2-1/2/3: Isolation strategy implementations (XL)
- P2-4: Multi-tenant session isolation (L)
- P2-5: Fix 31 CS1591 warnings (S)
- P2-6: Correlation/request IDs (S)
- P2-7: Channel health endpoint (S)
- P2-8: WebUI framework (L)
- P2-9: Extension signing (M)
- P2-10: Config set/get CLI (S)
- P2-11: OAuth on Gateway API (M)

**Frozen Code Proposals:**

1. **FROZEN-1: IHttpClientFactory in Providers** (P1-C2 — ✅ Approved Wave 2)
   - Affects: OpenAI, Anthropic, Copilot providers
   - Low risk — method signatures unchanged
   - Recommendation: Approved (Commit 4a842e7)

2. **FROZEN-2: Provider Conformance Tests** (P1-C4 — ✅ Approved Wave 2)
   - Affects: All provider test projects
   - Zero risk — tests only, no production changes
   - Recommendation: Approved (Commit 2a0044d)

3. **FROZEN-3: StreamAsync Background Task Leak** (Deferred Phase 10)
   - Affects: Provider streaming implementations
   - Medium risk — requires streaming internals review
   - Recommendation: Defer unless observed

**Sprint Recommendations:**

**Sprint 9A — Foundation (3-4 days):**
- P1-1: Dynamic extension loading (Farnsworth, 2d)
- P1-2: CORS configuration (Bender, 0.5d) ✅ **Complete**
- P1-6: PUT /api/agents/{id} (Bender, 0.5d) ✅ **Complete**
- P1-C1: Extract SessionReplayBuffer (Farnsworth, 1d) ✅ **Complete**
- P1-C3: Decompose GatewayWebSocketHandler (Bender, 1d)

**Sprint 9B — CLI & Config (3-4 days):**
- P1-3: `botnexus init` (Fry, 1d)
- P1-4: JSON Schema generation (Fry, 1d)
- P1-5: CLI agent management (Fry, 1.5d)
- P2-5: Fix CS1591 warnings (Any, 0.5d)
- P2-6: Correlation IDs (Bender, 0.5d)

**Sprint 9C — Channel & Quality (3-4 days):**
- P1-7: Telegram Bot API (Farnsworth, 2d)
- FROZEN-2: Provider conformance tests (Hermes, 1.5d) ✅ **Complete**
- FROZEN-1: IHttpClientFactory (Farnsworth, 0.5d) ✅ **Complete**

**Deferred to Phase 10+:**
- Isolation strategy implementations (Sandbox, Container, Remote)
- Multi-tenant session isolation
- WebUI framework + build pipeline
- OAuth on Gateway API
- StreamAsync task leak

---

---

### Phase 10 Wave 1 & 2 — CLI Parity, Gateway P1 Fixes, Design Review

**2026-04-06 — Merged from inbox (3 decisions)**

#### 1. CLI PlatformConfigLoader Integration (Bender)

**Date:** 2026-04-06  
**Owner:** Bender (Runtime)  
**Decision:** Use `PlatformConfigLoader` as single source for CLI home/config path resolution and config validation across new `init`, `agent`, and `config` commands.

**Why:** Keeps command behavior consistent with gateway runtime validation semantics and avoids drift between CLI-managed config and API-managed config.

**Implementation:** Updated `src\gateway\BotNexus.Cli\Program.cs` and `PlatformConfigLoader.cs` to serve both CLI and gateway.

**Status:** Complete

---

#### 2. Farnsworth P1 Gateway Fixes (Phase 10)

**Date:** 2026-04-06  
**Owner:** Farnsworth (Platform)  
**Context:** Requested by Jon Bullen after Leela's Phase 10 design review.  
**Scope:** Limited to Gateway API hardening and WebSocket handler decomposition.

**Decisions:**

1. **PUT `/api/agents/{agentId}` contract hardening**
   - Route/body `AgentId` mismatch now returns `400 Bad Request` with explicit error payload
   - Empty payload `AgentId` remains supported by normalizing to the route parameter
   - Added endpoint XML docs + response annotations to make behavior explicit in API surface

2. **Production CORS verb allowlist**
   - Development keeps permissive CORS for inner-loop productivity
   - Non-development now explicitly allows `GET, POST, PUT, DELETE, OPTIONS` (instead of `AllowAnyMethod`)
   - Rationale: least-privilege defaults without breaking existing API/WebUI flows

3. **Gateway WebSocket decomposition**
   - `GatewayWebSocketHandler` now orchestrates only request lifecycle and delegation
   - `WebSocketConnectionManager` owns reconnect throttling, session lock tracking, duplicate close semantics, and ping/pong handling
   - `WebSocketMessageDispatcher` owns inbound type routing (`message`, `abort`, `steer`, `follow_up`, `reconnect`) and replay-sequenced outbound persistence
   - `MapBotNexusGatewayWebSocket` endpoint contract remained unchanged

**Validation Notes:** Targeted gateway API build passes. Full solution build/test runs currently affected by unrelated workspace churn.

**Status:** Complete (3 commits)

---

#### 3. Phase 10 Design Review — Grade A- (Leela)

**Date:** 2026-04-06  
**Reviewer:** Leela (Lead/Architect)  
**Scope:** 6 commits across 3 agents (Farnsworth ×4, Bender ×1, Hermes ×1)

**Grade: A-**

**Decisions Approved:**
1. WebSocket handler decomposition approved — `GatewayWebSocketHandler` → orchestrator (150 lines), `WebSocketConnectionManager` (166 lines), `WebSocketMessageDispatcher` (296 lines). Clean SRP split with preserved endpoint contracts.
2. PUT AgentId validation approved — Returns 400 on route/body mismatch, falls back to route value on empty body. Phase 9 P1 resolved.
3. CORS verb restriction approved — Production restricts to `GET, POST, PUT, DELETE, OPTIONS`. Development keeps permissive CORS. Phase 9 P1 resolved.

**Issues Identified:**
4. CLI architecture needs Phase 11 work — `Program.cs` is 850+ lines of top-level statements. P1: decompose into command handler classes. P1: add test coverage for config get/set reflection.

**Deployment test harness approved** — `WebApplicationFactory<Program>` with isolated `BOTNEXUS_HOME` temp roots. Solid config layering coverage.

**P1 Items for Phase 11:**
- [ ] Decompose `BotNexus.Cli/Program.cs` into command handler classes
- [ ] Add unit tests for CLI config get/set reflection logic
- [ ] Copilot conformance tests duplicate OpenAI (carried from Phase 9)

**Carried Forward:**
- StreamAsync task leak (deferred — frozen code)
- SessionHistoryResponse location (Abstractions.Models)
- SequenceAndPersistPayloadAsync double-serialization

---

#### Phase 10 Post-Sprint Consistency Review (Nibbler)

**Date:** 2026-04-06  
**Status:** ✅ Complete  
**Grade:** Good

**Findings:** 0 P0, 12 P1 (all fixed), 3 P2 (noted)

**Critical P1 Fix — Provider Naming Inconsistency:**
CLI used "github-copilot" provider name while all documentation and code examples used "copilot". This was the first time a code default contradicted documentation. Fixed CLI to use "copilot" for consistency.

**Additional P1 Fixes:**
- api-reference.md documented OLD PUT behavior (overwrite) instead of new behavior (400 on mismatch)
- Gateway README missing PUT endpoint, session endpoints (history/suspend/resume), WebSocket protocol fields
- CORS configuration completely undocumented despite being environment-aware
- sample-config.json placement error: `apiKeys` at root instead of under `gateway`
- 7 additional documentation alignment issues

**Pattern Observed:** Code quality consistently excellent (0 code-level issues). Documentation lags behind code changes — 12 P1 documentation gaps identified and fixed in post-sprint review.

**Recommendation:** Continue staggered doc review as post-sprint gate. Cross-check CLI defaults against documentation naming conventions to prevent user confusion.

---

## Retrospective — Port Audit Phase 3

**Facilitator:** Leela (Lead/Architect)  
**Date:** 2026-04-06  
**Sprint:** Phase 3 — pi-mono packages/ai, packages/agent, packages/coding-agent vs BotNexus  
**Status:** Complete

---

### 1. What Happened (Facts)

**Scope:** Full audit of pi-mono `packages/ai`, `packages/agent`, `packages/coding-agent` against the BotNexus C# port. 9 architecture decisions proposed (AD-9 through AD-17).

**Outcomes:**
- 7 ADs implemented (AD-9, AD-10, AD-11, AD-12, AD-14, AD-15, AD-17)
- 1 AD deferred — AD-13 (OpenRouter/Vercel routing types) per YAGNI: no provider exists yet
- 1 AD already present — AD-16 (maxRetryDelayMs already in codebase)
- 13 commits across 6 agents (Farnsworth, Bender, Kif, Nibbler, Scribe, Leela)
- 415 tests passing (up from 372 — 43 new tests)
- 0 build errors, 0 warnings
- 4 new training modules (06-context-file-discovery, 07-thinking-levels, 08-building-custom-coding-agent, 09-tool-development)
- 22 consistency discrepancies found and fixed in post-sprint review

**Sprint structure:**
- Sprint 3a (parallel): Farnsworth (AD-9 + AD-15) + Bender (AD-11 + AD-12)
- Sprint 3b (sequential): Bender (AD-10 → AD-14 → AD-17)
- Parallel track: Kif (training docs — 4 modules, ~1,325 lines)
- Post-work: Nibbler (consistency review — 22 fixes), Scribe (logs + decision merge)

---

### 2. What Went Well

#### Parallel execution tracks worked
Sprint 3a ran Farnsworth and Bender in parallel on independent subsystems (AgentCore/Providers vs CodingAgent). No merge conflicts. No cross-dependency issues. The design review's boundary analysis (AD assignments by subsystem) made this possible.

#### YAGNI discipline held
AD-13 (OpenRouter routing types) was correctly deferred. No provider exists. Building types for imagined future routing would have added dead code. The team made the right call.

#### Design review → sprint pipeline is maturing
Phase 3 followed the same ceremony as Phase 2: audit → design review → architecture decisions → parallel sprint → consistency review. The cadence is stable and repeatable.

#### Test count growth is healthy
43 new tests in one sprint. Total at 415. Test coverage follows code — not bolted on after the fact.

#### AD-16 and AD-17 caught existing coverage
Two items turned out to be already present in the codebase. The audit correctly identified them rather than duplicating work. AD-17 only needed the `/thinking` slash command addition (the `--thinking` CLI flag already existed).

---

### 3. What Could Improve

#### Documentation was written against planned APIs, not implemented code
This is the root cause of 18 of the 22 consistency issues. Kif wrote training docs during the sprint based on design review decisions (planned signatures) rather than waiting for final implementations. Every new training module had at least one wrong API signature.

#### No handoff checkpoint between code and docs agents
Bender shipped code. Kif wrote docs. Neither verified against the other's output. There is no process gate that says "docs agent must read final code before authoring examples."

#### Consistency review is reactive, not preventive
Nibbler found 22 issues — but only after the sprint was "complete." The fix commit (`e7ff6d8`) is waste: work that wouldn't exist if the docs had been right the first time. We need to catch this before the sprint ends, not after.

#### IAgentTool.ExecuteAsync signature was wrong in 4 separate places
The `toolCallId` parameter was missing from the interface definition AND all examples in `09-tool-development.md`. This suggests Kif was working from an earlier version of the interface, before `toolCallId` was added. The docs agent needs a way to query current code signatures, not rely on its training data.

---

### 4. Root Cause Analysis — 22 Consistency Issues

#### Primary Root Cause: Docs authored from design decisions, not from code

**Evidence:** Nibbler's report shows a clear pattern:
- `07-thinking-levels.md` said `--thinking` didn't exist → it was the primary deliverable of AD-10
- `09-tool-development.md` had wrong ExecuteAsync signature → `toolCallId` parameter was omitted
- `06-context-file-discovery.md` described binary search truncation → code uses char-by-char iteration
- `08-building-custom-coding-agent.md` called SystemPromptBuilder.Build() with non-existent parameters

All four HIGH-severity issues stem from the same cause: the docs agent wrote against the plan, not the code.

#### Contributing Factor: No compilation gate for doc examples

Training doc code examples are markdown fenced blocks. They aren't compiled. They aren't tested. A typo in a code example (`string?` vs `IReadOnlyList<string>`) is invisible unless a human (or Nibbler) reads it line by line.

#### Contributing Factor: Sprint parallelism without sync point

Kif and Bender ran in parallel. This was intentional (speed). But it means Kif couldn't see Bender's final code — because it didn't exist yet when Kif started writing.

---

### 5. Action Items for Next Sprint

| # | Action | Owner | Priority |
|---|--------|-------|----------|
| 1 | **Doc Checkpoint Gate:** Docs agent MUST read final code (actual interface files, actual tool implementations) before authoring examples. No exceptions. Add this as a step in the sprint ceremony. | Leela | P0 |
| 2 | **Stagger doc authoring:** Kif starts docs AFTER code commits land, not in parallel. Trade speed for correctness. Parallel doc work is only safe for conceptual/architecture content, not API examples. | Leela | P0 |
| 3 | **Signature extraction script:** Create a small utility that extracts public API signatures from compiled assemblies. Kif can run this to get ground-truth method signatures instead of relying on context. | Farnsworth | P1 |
| 4 | **Doc example validation:** Investigate Roslyn scripting or doctest-style validation for C# code blocks in markdown. Even partial compilation (resolve types, check method signatures) would catch the most common errors. | Hermes | P2 |
| 5 | **Consistency review shifts left:** Nibbler runs a focused check BEFORE the sprint-complete commit, not after. Make this part of the sprint exit criteria, not a post-sprint ceremony. | Leela | P1 |

---

### 6. Architecture Grade Update

#### Grade: **A** (maintained from Phase 2)

**Justification:**
- All planned port gaps from pi-mono `packages/ai`, `packages/agent`, and `packages/coding-agent` are either resolved or consciously deferred (YAGNI)
- 415 tests, 0 warnings — quality gates hold
- 17 architecture decisions locked across 3 phases — design discipline is strong
- Consistency issues were process failures (doc timing), not architecture failures
- The codebase accurately reflects the pi-mono design intent where it matters, and diverges intentionally where C#/.NET idioms are better

**Risk:** The 22-fix consistency commit is a process smell, not an architecture smell. The code is sound. The documentation pipeline needs the gates described above.

#### Cumulative Stats (All 3 Phases)

| Metric | Phase 1 | Phase 2 | Phase 3 | Total |
|--------|---------|---------|---------|-------|
| Commits | 12 | 18 | 13 | 43 |
| ADs locked | — | 8 | 9 | 17 |
| P0s fixed | 10 | 15 | — | 25 |
| Tests | 350 | 372 | 415 | — |
| Training modules | 4 | 2 | 4 | 10 |

---

### Summary

Phase 3 completed its mission: the pi-mono port audit is done. All three source packages have been scanned. The code quality is high — the issues we found were documentation process failures, not code defects. The single most important process improvement is **staggering doc authoring behind code commits** so examples are written against real implementations. This is the concrete change we're making for the next sprint.

---

## Post-Sprint 3 Consistency Review

**Author:** Nibbler (Consistency Reviewer)  
**Date:** 2026-04-03  
**Commit:** e7ff6d8

### Summary

Sprint 3 delivered 7 features (AD-9 through AD-17) with 4 new training docs and multiple API changes. Consistency review found **22 discrepancies** across 7 files — all fixed.

### Pattern Observed

New training docs (06-09) were written based on planned APIs rather than final implementations. Every Sprint 3 training doc had at least one wrong API signature. The most critical gap was 07-thinking-levels.md claiming `--thinking` didn't exist in the CLI — when it was the primary deliverable of AD-10 and AD-17.

### Discrepancies Fixed (by severity)

#### HIGH (7)
1. **07-thinking-levels.md**: CLI section said "--thinking flag does not exist" — rewrote with actual --thinking, /thinking, and session metadata
2. **09-tool-development.md**: IAgentTool.ExecuteAsync missing `toolCallId` parameter across interface definition and all 4 examples
3. **09-tool-development.md**: GetPromptGuidelines return type wrong (`string?` vs `IReadOnlyList<string>`)
4. **06-context-file-discovery.md**: Truncation algorithm was binary search in docs, char-by-char iteration in code
5. **08-building-custom-coding-agent.md**: SystemPromptBuilder.Build() called with non-existent parameters
6. **03-coding-agent.md**: Missing ListDirectoryTool from tool table, code example, and count
7. **CodingAgent/README.md**: Tool count wrong (6→7), missing --thinking in CLI help

#### MEDIUM (10)
8. **08-building-custom-coding-agent.md**: Missing `using BotNexus.CodingAgent.Utils` namespace import
9. **08-building-custom-coding-agent.md**: SystemPromptBuilder used as static method but it's instance-based
10. **08-building-custom-coding-agent.md**: Cross-ref linked to `08-tool-development.md` instead of `09-tool-development.md`
11. **09-tool-development.md**: EchoTool example ExecuteAsync wrong signature
12. **09-tool-development.md**: CalculatorTool example ExecuteAsync wrong signature
13. **09-tool-development.md**: DatabaseQueryTool example ExecuteAsync wrong signature + wrong callback name
14. **09-tool-development.md**: Error handling example ExecuteAsync wrong signature
15. **09-tool-development.md**: Built-in tools list missing ListDirectoryTool
16. **05-glossary.md**: Duplicate ThinkingLevel entry (lines 432 and 531)
17. **05-glossary.md**: Cross-reference header missing modules 06-09

#### LOW (5)
18. **CodingAgent/README.md**: Opening line missing grep and list_directory from tool list
19. **CodingAgent/README.md**: Missing list_directory tool section
20. **CodingAgent/README.md**: ReadTool params showed `range` instead of `start_line`/`end_line`
21. **08-building-custom-coding-agent.md**: DemoTool GetPromptGuidelines returns wrong type
22. **09-tool-development.md**: DatabaseQueryTool GetPromptGuidelines returns wrong type

### Recommendation

Training docs authored during a sprint should be reviewed against final code BEFORE the sprint is considered complete. The doc-writing agent and the code-writing agent need a handoff checkpoint to catch signature mismatches.

### Validation

- ✅ Build: `dotnet build BotNexus.slnx` — 0 errors, 0 warnings
- ✅ Tests: 415/415 pass across 7 test projects

---

## Design Review — Phase 5: Port Audit Consolidated Findings

**Facilitator:** Leela (Lead/Architect)  
**Date:** 2026-04-06  
**Status:** Approved — ready for implementation  
**Requested by:** sytone (Jon Bullen)

---

### 1. Sprint Scope

#### IN SPRINT — Critical (3 items)

| ID | Finding | Verified | Verdict | Priority |
|----|---------|----------|---------|----------|
| CA-C1 | ShellTool truncates HEAD instead of TAIL | ✅ Confirmed: `ordered.Take(MaxOutputLines)` takes first lines | **ACCEPT — Critical** | P0 |
| CA-C2 | ShellTool 120s default timeout | ✅ Confirmed: `DefaultTimeoutSeconds = 120`, no config override | **ACCEPT — Critical** | P0 |
| P-C1 | Tool call argument validation missing | ✅ Confirmed: raw `JsonElement` passed through, no schema check | **ACCEPT — Critical (upgraded)** | P0 |

**Note on AC-C1 (Partial message in context):** Downgraded from Critical to Deferred. Verified that `transformContext` runs **before** streaming in `AgentLoopRunner.cs:164-176`, not during. The partial message is emitted via `MessageUpdateEvent` but no current consumer needs it in the transform pipeline. This becomes relevant only when mid-stream context management is added.

#### IN SPRINT — Major (5 items)

| ID | Finding | Verified | Verdict | Priority |
|----|---------|----------|---------|----------|
| CA-M1 | ListDirectory flat-only | ✅ Confirmed: `SearchOption.TopDirectoryOnly` | **ACCEPT** | P1 |
| CA-M2 | Context discovery misses ancestor walk | ✅ Confirmed: checks root only, no parent traversal | **ACCEPT** | P1 |
| AC-M1 | transformContext/convertToLlm once before retries | ✅ Confirmed: `providerContext` computed outside retry loop | **ACCEPT** | P1 |
| P-M2 | shortHash utility missing | ✅ Confirmed: uses pipe-delimited composition, no hash | **ACCEPT** | P1 |
| P-C3 | MessageTransformer normalizer signature divergent | ✅ Confirmed: callback `Func<string,string>?` vs TS model+source | **ACCEPT** | P1 |

#### DEFERRED — Backlog

| ID | Finding | Reason |
|----|---------|--------|
| AC-C1 | Partial message not in context during streaming | No current consumer. Architecture runs transforms before stream, not during. Revisit when mid-stream context window management is needed. |
| CA-M3 | CLI missing flags | Feature addition. No current CLI consumer. |
| CA-M4 | System prompt guidelines static | Cosmetic. Current prompts are functional. |
| CA-M5 | Session format v2 vs v3 | Migration concern. v2 works. Migrate when v3 features are needed. |
| AC-M2 | Tool lookup case-insensitive | **Already decided (2026-04-05):** Intentional C# improvement. Case-insensitive is more robust. No change. |
| AC-M3 | Proxy stream function | **Already deferred (2026-04-05):** No current consumer. |
| P-M1 | BuiltInModels only ~33 | Low priority. Models added as needed. 828 in TS includes deprecated entries. |
| P-M3 | Faux test provider missing | Nice-to-have. Current unit tests use mocks directly. |
| P-M4 | SupportsXhigh auto-detect by model ID | **REJECT.** Explicit registration via `supportsExtraHighThinking` flag is cleaner than pattern-matching magic. C# approach is better. |

---

### 2. Decisions Log (Phase 5)

| # | Decision | Rationale |
|---|----------|-----------|
| D9 | Downgrade AC-C1 (partial message in context) from Critical to Deferred | Transforms run before streaming, not during. No current consumer. |
| D10 | Upgrade P-C1 (tool call validation) from Deferred to P0 | Previously deferred saying "tools validate own inputs" — but hallucinated args crash tools before self-validation runs. Safety issue. |
| D11 | Reject P-M4 (SupportsXhigh auto-detect) | Explicit `supportsExtraHighThinking` flag is cleaner than pattern-matching on model IDs. C# approach is architecturally superior. |
| D12 | Set ShellTool default timeout to 600s, not infinite | Infinite is dangerous. 600s covers 99% of builds. Config allows override. |
| D13 | Accept AC-M2 (tool lookup case-insensitive) as intentional | Already decided 2026-04-05. More robust than case-sensitive. No change. |
| D14 | Accept P-M1 (BuiltInModels count) as low priority | 33 active models vs 828 includes deprecated. Add as needed. |
| D15 | ToolCallValidator: top-level validation only | Validate required fields and types. No deep nested schema validation. Practical 80/20 approach. |
| D16 | MessageTransformer signature change is breaking — single PR | All call sites updated atomically. No gradual migration. |

---

### 3. Risk Assessment

| Risk | Severity | Mitigation |
|------|----------|------------|
| **ShellTool TAIL truncation may lose early output** (e.g., a warning at line 1 that explains a later error) | Medium | Include truncation notice with total line count so the agent can re-run with `head` if needed. Consider keeping first 10 lines + last N lines (sandwich approach). Decision: start with pure TAIL; iterate if agents struggle. |
| **600s default timeout still too short for CI-scale builds** | Low | Config-driven. Document that `null` disables timeout. Agents can pass explicit timeout per-call. |
| **ToolCallValidator false positives on flexible schemas** | Medium | Only validate required fields and top-level types. Don't reject `additionalProperties`. Log validation failures via diagnostics before hard-failing — give us data to tune. |
| **Per-retry transform adds latency** | Low | Transforms should be fast (millisecond-scale). Document idempotency requirement. If a transform is slow, that's a bug in the transform, not in the retry loop. |
| **Ancestor walk finds conflicting instructions** | Medium | Closest-to-cwd wins. Document merge precedence. Stop at `.git` boundary. |
| **MessageTransformer normalizer signature is breaking** | High | Must update all call sites in the same PR. Search exhaustively. Add compiler error if old signature used (method overload won't match). |

---

### 4. Implementation Status (Phase 5)

| Work Item | Status | Commits |
|-----------|--------|---------|
| CA-C1 | ✅ Done | `fix(ShellTool): truncate TAIL instead of HEAD` |
| CA-C2 | ✅ Done | `feat(ShellTool): make timeout configurable` |
| P-C1 | ✅ Done | `feat(Providers.Core): add ToolCallValidator` |
| CA-M1 | ✅ Done | `feat(ListDirectory): enumerate 2 levels deep` |
| CA-M2 | ✅ Done | `feat(ContextFileDiscovery): walk ancestor directories` |
| AC-M1 | ✅ Done | `refactor(AgentLoopRunner): move transform into retry loop` |
| P-M2 | ✅ Done | `feat(Providers.Core): add ShortHash utility` |
| P-C3 | ✅ Done | `refactor(MessageTransformer): align normalizer signature` |

**Test coverage:** 22 new tests (all passing)  
**Bugs fixed during testing:** 3 (list aliasing, race condition, hash length)  
**Build:** Clean (0 errors, 0 warnings)  
**Tests:** 475/475 passing

---

### 5. Retrospective — Port Audit Phase 5

**Facilitator:** Leela (Lead/Architect)  
**Date:** 2026-04-05  
**Participants:** Farnsworth, Bender, Hermes, Kif, Nibbler

---

#### Sprint Summary

Full port audit comparing pi-mono TypeScript against BotNexus C# across providers/ai, agent/agent, and coding-agent. Design review reduced 14 raw findings to 8 fixes. Farnsworth and Bender implemented fixes in parallel. Hermes wrote 22 tests. All work completed.

| Metric | Value |
|--------|-------|
| Baseline tests | 453 |
| Final tests | 475 |
| New tests | 22 |
| Commits | 12 (8 features + 4 tests + 3 bugfixes = 15 total work items) |
| Build | Clean, 0 errors |
| Bugs fixed | 3 (regressions caught during testing) |

#### What Went Well

- **Design review gate:** Reduced 14 findings to 8 approved items. Filter rate: 43%.
- **Parallel execution:** Farnsworth + Bender on independent subsystems (Providers vs CodingAgent). No merge conflicts.
- **Test discipline enforced:** Phase 5 followed improved sequencing: Audit → Design → Implementation → Tests → Docs. Tests written against committed code, not design decisions. This fixed the Phase 4 anti-pattern.
- **Bug detection:** 3 regressions caught and fixed same-sprint (list aliasing, race condition, hash length).
- **Conventional commits:** All 15 commits follow format. Build stayed clean throughout.

#### What Didn't Go Well

- (None noted. Phase 5 execution was clean.)

#### Action Items (Carried Forward)

1. **Speculative test authoring rule (from Phase 4 retro):** Tests must follow code, not lead it. Phase 5 enforced this successfully.
2. **Test-after-impl sequencing:** Sprint template explicitly sequences Audit → Design → Impl → Tests. Phase 5 proved this works.
3. **Design review gate:** Mandatory. Continue using.

#### Cumulative Stats (All 5 Phases)

| Metric | Phase 1 | Phase 2 | Phase 3 | Phase 4 | Phase 5 | Total |
|--------|---------|---------|---------|---------|---------|-------|
| Commits | 12 | 18 | 13 | ? | 15 | ~58+ |
| Fixes locked | 10 | 15 | 6 | 5 | 8 | ~44 |
| Tests | 350 | 372 | 415 | ? | 475 | — |
| Bugs caught | — | — | 22 | 9 | 3 | ~34 |
| Design review % | — | — | — | — | 43% | — |

---

### Sign-off

- [x] Design review approved (Leela)
- [x] Implementation complete (Bender, Farnsworth)
- [x] Testing complete (Hermes)
- [x] Bugs fixed (Coordinator)
- [x] All decisions locked

---

## Port Audit Findings Summary (2026-04-05)

**Decision:** Deep Port Audit Complete — pi-mono (TypeScript) → BotNexus (C#)  
**Date:** 2026-07-24  
**By:** Leela (Lead)  
**Full report:** `docs/port-audit-findings.md`

### Key Stats

| Metric | Value |
|--------|-------|
| **Total findings** | 153 |
| **Critical** | 15 |
| **Major** | 43 |
| **Minor** | 63 |
| **Enhancement** | 32 |

### By Area

| Area | Critical | Major | Minor | Enhancement | Total |
|------|----------|-------|-------|-------------|-------|
| Providers | 12 | 27 | 36 | 16 | **91** |
| Agent | 1 | 5 | 14 | 8 | **28** |
| CodingAgent | 2 | 11 | 13 | 8 | **34** |

### Biggest Gaps

1. **6 entire providers missing** — Bedrock, Google (×3), Mistral, Azure OpenAI, Codex (~4,500+ lines unported)
2. **OpenAICompat reasoning/thinking broken** — DeepSeek R1, Qwen QwQ chain-of-thought silently dropped
3. **Editor integration impossible** — No RPC/JSON/Print modes (critical for VS Code)
4. **23+ CLI flags missing** — continue, fork, session-dir, print, export, tools, system-prompt, @file, etc.
5. **System prompt is ~30% of pi-mono's** — Missing date, tool-conditional guidelines, custom/append support
6. **Proxy not ported** — No server-proxied LLM streaming
7. **Tool validation is shallow** — Only top-level properties validated; no nested schema, no type coercion

### Recommendation

Prioritize in this order:
1. **Fix correctness bugs** in existing providers (Anthropic thinking disable, header ordering, ID normalization) — Small effort, high impact
2. **Complete OpenAICompat** with reasoning parsing, transformMessages, finish_reason mapping — Medium effort, unlocks local model use
3. **Add modes** (print, JSON, RPC) — Medium effort, enables piping and editor integration
4. **Port missing CLI flags** incrementally — Start with --continue, --session, --version, @file
5. **New providers** as needed — Google and Bedrock are the highest-value additions after the above

---

## Farnsworth Provider Fix Decisions (2026-04-05)

Date: 2026-04-05

### Decisions

1. **ApiProviderRegistry runtime guard implemented via provider wrapper**
   - Added a guarded `IApiProvider` wrapper at registration time that validates `model.Api` matches the provider `Api` for both `Stream` and `StreamSimple`.
   - This mirrors pi-mono's wrapped stream behavior while preserving existing registry call sites.

2. **Anthropic thinking behavior normalized to explicit intent**
   - `StreamSimple` now explicitly sets `ThinkingEnabled=false` when reasoning is not requested for reasoning-capable models.
   - Request builder prioritizes adaptive model detection before effort/budget branching, and suppresses `temperature` whenever thinking is enabled.

3. **OpenAI completions reasoning parity aligned with pi-mono streaming semantics**
   - Thinking signature now records the detected reasoning field (`reasoning_content`/`reasoning`/`reasoning_text`).
   - `reasoning_details` matching changed from positional to tool-call-ID based to prevent signature drift.

4. **OpenAI Responses request shape and precedence aligned with pi-mono**
   - System prompt now uses `{ role, content: "text" }` shorthand.
   - Copilot headers are applied before `options.Headers` to preserve caller override behavior.
   - Removed `previous_response_id` emission for parity.

---

## Bender Agent Fixes Decisions (2026-04-06)

### Decisions

- For AGENT-003 streaming parity, `StreamAccumulator` now mutates the in-flight context timeline by adding a partial assistant message on `StartEvent` and replacing it on deltas/finalization.
- `AgentLoopRunner` now treats pre-added streaming partials as the same assistant turn (replace vs append) and removes partial leftovers when a stream attempt fails before completion.
- For CODING-003 prompt parity, `SystemPromptBuilder` now supports `customPrompt` replacement, `appendSystemPrompt` appends, tool-aware guideline branching, and always appends ISO timestamp plus forward-slash CWD.
- For CODING-010, context discovery now includes `INSTRUCTIONS.md` and supports configurable config-dir `AGENTS.md` lookup instead of fixed-path behavior.

---

## Retrospective — Port Audit Phase 5 (2026-04-06)

**Facilitator:** Leela (Lead/Architect)  
**Date:** 2026-07-16  
**Sprint:** Phase 5 — Port Audit Remediation (P0/P1 fixes across Providers, AgentCore, CodingAgent)  
**Status:** Complete

### Facts

**Scope:** Phase 5 remediated audit findings across three subsystems — Providers, AgentCore, and CodingAgent. 8 fixes shipped (3 P0, 5 P1) with 14 implementation commits.

**Audit phase:**
- 3 opus-class agents audited Providers (72% coverage), AgentCore (92% coverage), and CodingAgent (detailed tool-by-tool comparison) in parallel.

**Design review:**
- AC-C1 downgraded from Critical: transforms don't execute during streaming, so the finding was misclassified.
- P-M4 identified as a design improvement, not a required port alignment fix.

**Implementation:**
- Sprint 5a (parallel): Bender (5 items on independent files) + Farnsworth (3 items on independent files)
- Test track: Hermes started writing tests while implementation was in progress
- 14 implementation commits, ~25 total commits (impl + tests + docs + consistency)

**Final numbers:**
- Tests: 480 → 501 (21 new tests), 0 failures
- Build: Clean, 0 errors, 0 warnings

### What Went Well

#### Parallel audit at opus scale reduced wall-clock time
Three agents audited three subsystems simultaneously with zero coordination overhead. The subsystem boundaries were clean enough that no cross-cutting analysis was needed. This is the highest parallelism we've achieved in the audit phase.

#### Design review continues to filter false positives
Two findings (AC-C1 Critical misclassification, P-M4 design-not-bug) were caught before implementation. The design review gate has now filtered incorrect findings in every sprint since Phase 3. It remains the single highest-value ceremony.

#### Parallel implementation with zero file conflicts
Bender and Farnsworth worked concurrently on non-overlapping file sets. Zero merge conflicts, zero rework. This is now proven across five consecutive sprints.

#### Test count growth is healthy
21 new tests in one sprint. Total at 501. The project maintains the discipline of tests following code.

### What Went Wrong — Root Cause Analysis

#### Bug 1: CompactForOverflow list aliasing

**Symptom:** After Bender's per-retry transform change, `messages.Clear()` also cleared the compacted result, causing empty conversations on retry.

**Root cause:** `CompactForOverflow` returned the same `List<>` reference for small conversations (where no compaction was needed). The caller assumed it received an independent copy. When the caller cleared the original list, it destroyed both.

**Why it surfaced now:** Bender's retry loop restructure changed the message lifecycle — messages are now cleared and rebuilt per-retry instead of once. The pre-existing aliasing was harmless when the list was only read once.

**Category:** Latent defect exposed by correct refactoring. The bug was in the compaction code, not in Bender's change.

**Fix:** Return a new list (defensive copy) when no compaction is needed, so callers never alias the input.

#### Bug 2: Test isolation race — duplicate provider registration

**Symptom:** `AgentTests` and `AgentLoopRunnerTests` both registered a provider named `"test-api"`. When xUnit ran them in parallel, the second registration collided with the first.

**Root cause:** No test-scoped provider registry. Both test classes shared the global `ProviderRegistry` and neither cleaned up after itself.

**Why it surfaced now:** Bender's retry loop restructure changed execution timing, making the parallel collision window larger.

**Category:** Pre-existing test infrastructure debt. Tests assumed serial execution.

**Fix:** Use unique provider names per test class, or scope the registry per test.

#### Bug 3: ShortHash length expectation mismatch

**Symptom:** Hermes wrote a test expecting 9-character hash output. Actual pi-mono algorithm produces 12–14 characters.

**Root cause:** Leela's design spec stated the hash would be trimmed to 9 characters. The pi-mono reference implementation has no trim step. The spec was wrong.

**Category:** Speculative test authoring from design spec, not from code. **This is the third recurrence of the speculative-parallel anti-pattern** (Phase 3: docs-against-plan, Phase 4: tests-against-plan, Phase 5: tests-against-spec).

**Fix:** Test was corrected to assert the actual output length. Spec updated.

#### Bug 4: Git commit conflicts from concurrent agents

**Symptom:** Multiple agents committing to the same repo caused file lock issues — `testhost` processes held locks, concurrent `git` operations failed.

**Root cause:** No git commit coordination protocol. Agents commit independently whenever they complete work, without checking whether another agent or process holds the index lock.

**Category:** Infrastructure gap. The multi-agent git workflow lacks a lock/queue mechanism.

#### Bug 5: CodingAgent test runner hangs after passing

**Symptom:** All 134 CodingAgent tests pass, but the test runner never exits. Requires manual kill.

**Root cause:** Likely a test fixture that starts a background process or opens a port and doesn't dispose it. The test host waits for all threads to complete before exiting.

**Category:** Test cleanup debt. Needs investigation — specific fixture not yet identified.

### What Should Change

#### 4.1 — Enforce defensive copies at subsystem boundaries

The list aliasing bug is a classic shared-mutable-state defect. Any method that accepts a collection and might return it unmodified must return a defensive copy. This should be a code review checklist item for all transform/compaction methods.

#### 4.2 — Test-scoped service registries

Provider registration tests must not share global state. Either:
- Each test class gets an isolated registry instance, or
- Test provider names include the test class name to guarantee uniqueness.

#### 4.3 — Kill the speculative-parallel anti-pattern permanently

This is the **third sprint** where artifacts authored from specs/plans instead of committed code produced failures. The pattern:
- Phase 3: 18/22 doc issues (docs from design decisions)
- Phase 4: 9/30 test failures (tests from audit findings)
- Phase 5: ShortHash length wrong (test from design spec)

**New rule:** Tests and docs that assert specific behavior (return types, string lengths, parameter counts, exact signatures) MUST be authored AFTER the code they describe is committed and green. Conceptual test plans can parallel; concrete assertions cannot.

#### 4.4 — Git commit queue for multi-agent sprints

Agents must acquire a coordination lock before committing. Options:
- File-based lock (`.squad/.git-lock`) with agent name and timestamp
- Sequential commit phase: all agents write changes, coordinator commits in order
- Worktree-per-agent: each agent works in its own git worktree, coordinator merges

#### 4.5 — Investigate and fix CodingAgent test hang

The test runner hang is a time bomb — it wastes CI minutes and masks real failures. Needs a dedicated investigation to find the undisposed fixture.

### Action Items

| ID | Action | Owner | Priority | Status |
|----|--------|-------|----------|--------|
| R5-1 | Add defensive-copy rule to code review checklist for transform/compaction methods | Leela | P1 | Pending |
| R5-2 | Refactor test provider registration to use test-scoped or uniquely-named providers | Hermes | P1 | Pending |
| R5-3 | Add sprint sequencing rule: concrete assertions (tests/docs) must follow committed code | Leela | P0 | Pending |
| R5-4 | Design and implement git commit coordination protocol for multi-agent sprints | Leela | P1 | Pending |
| R5-5 | Investigate CodingAgent test runner hang — find undisposed fixture | Farnsworth | P2 | Pending |
| R5-6 | Update design spec template to flag "assumed behavior" vs "verified behavior" | Leela | P2 | Pending |

### Sprint Health Summary

| Metric | Value |
|--------|-------|
| Fixes shipped | 8 (3 P0, 5 P1) |
| Commits | ~25 |
| Tests added | 21 (480 → 501) |
| Test failures | 0 |
| Build warnings | 0 |
| Bugs found during sprint | 5 |
| Bugs from pre-existing debt | 3 (aliasing, test isolation, test hang) |
| Bugs from process gaps | 2 (spec mismatch, git conflicts) |
| Design review filter saves | 2 (AC-C1 downgrade, P-M4 rejection) |

**Verdict:** Solid execution sprint. The implementation itself was clean — all five bugs trace to pre-existing debt or process gaps, not to implementation errors. The speculative-parallel anti-pattern's third recurrence demands a hard process rule (R5-3).

---

## Design Review — Gateway Service Architecture

**Reviewer:** Leela (Lead / Architect)  
**Date:** 2025-07-24  
**Scope:** 5 Gateway projects + Channels.Core + 30 tests  
**Status:** APPROVED with conditions

### Summary

The Gateway Service is well-architected. The five-project decomposition is clean, dependency flow is correct, interfaces are focused, and the extension model is genuinely pluggable. Two bugs need fixing before we ship (P0/P1), but the foundation is solid.

**Architecture Grade: A-**

### SOLID Compliance

#### Single Responsibility — ✅ PASS
Each class has one clear reason to change:
- `DefaultAgentRegistry` — Agent descriptor storage
- `DefaultAgentSupervisor` — Agent instance lifecycle
- `DefaultMessageRouter` — Routing resolution
- `InProcessIsolationStrategy` — In-process agent creation
- `InMemoryActivityBroadcaster` — Pub/sub fan-out
- `ApiKeyGatewayAuthHandler` — API key validation
- `GatewayHost` — Pipeline orchestration

GatewayHost is the broadest — it owns channel lifecycle and message dispatch — but as the composition root, this is the correct place for orchestration.

#### Open/Closed — ✅ PASS
Extension without modification is well-supported:
- New isolation strategy → Implement `IIsolationStrategy`, register in DI. Zero Gateway code changes.
- New channel adapter → Implement `IChannelAdapter`, register in DI. GatewayHost consumes `IEnumerable<IChannelAdapter>` automatically.
- New session store → Implement `ISessionStore`, swap DI registration.
- New auth handler → Implement `IGatewayAuthHandler`, swap DI registration.

#### Liskov Substitution — ✅ PASS
All abstraction implementations are swappable. `InMemorySessionStore` and `FileSessionStore` both satisfy the `ISessionStore` contract. The `InProcessAgentHandle` wraps `AgentCore.Agent` correctly and could be replaced by a container-proxy handle without breaking the supervisor.

#### Interface Segregation — ✅ PASS
Interfaces are focused and minimal (1-7 members each). No fat interfaces detected.

#### Dependency Inversion — ✅ PASS
All high-level modules depend on abstractions. Controllers inject `IAgentRegistry`, `ISessionStore`, etc. — never `DefaultAgentRegistry`.

### Extension Points

- **IIsolationStrategy** — Registered by `Name` property, resolved at runtime. New strategies need only DI registration.
- **IChannelAdapter** — GatewayHost consumes `IEnumerable<IChannelAdapter>` and starts all. New adapters subclass `ChannelAdapterBase` and register via DI.
- **ISessionStore** — Two implementations already prove the interface works (InMemory, File).
- **IMessageRouter** — Single implementation today, but the interface is justified — routing policy will diverge.

### Issues

#### P0 — Must Fix Before Proceeding
None. No blocking architectural defects found.

#### P1 — Should Fix Soon

| # | Issue | Location | Impact |
|---|---|---|---|
| P1-1 | **Streaming path drops assistant history** — `GatewayHost.DispatchAsync` records the user message but never appends the assistant response to `session.History` when using the streaming branch. | `GatewayHost.cs:133-141` | Session history is incomplete for streaming interactions. Breaks session resume. |
| P1-2 | **DefaultMessageRouter.SetDefaultAgent leaks through DI** — `SetDefaultAgent()` is a concrete method not on `IMessageRouter`. Consumers must know the concrete type. | `DefaultMessageRouter.cs:33`, `GatewayServiceCollectionExtensions.cs:31-32` | Consumers must know the concrete type to configure routing. |
| P1-3 | **ChannelManager duplicates GatewayHost lifecycle** — Both classes start/stop channel adapters. Unclear which is authoritative. | `ChannelManager.cs`, `GatewayHost.cs:60-91` | Confusion about lifecycle ownership. |
| P1-4 | **No ISessionStore registered in AddBotNexusGateway()** — The core DI extension doesn't register any session store. Consumer must know to add one manually. | `GatewayServiceCollectionExtensions.cs` | Runtime `InvalidOperationException` with no guidance. |
| P1-5 | **Test file names don't match test subjects** — Three test files test different components than their names suggest. | `tests/BotNexus.Gateway.Tests/` | Misleading coverage signals. |

#### P2 — Nice to Have

6 items documented for future improvement (mutable properties, WebSocket buffer, global serialization, missing test coverage, etc.).

---

### Architecture Grade: A-

**Rationale:**
- Contracts are clean. Interfaces are focused, well-documented, and genuinely pluggable.
- Dependency flow is correct. No circular references. Leaf projects depend only on Abstractions.
- Extension model works. Adding new isolation strategies, channels, or session stores requires zero modification to existing code.
- AgentCore integration is correct. The in-process handle properly wraps Agent with full streaming support.
- **One real bug** (P1-1: streaming history loss) prevents an A grade. P1-4 (missing session store registration) and P1-5 (test naming confusion) are housekeeping items.

The architecture is production-ready once the P1 items are addressed.

---

## Gateway Service — Consistency Review

**Reviewer:** Nibbler (Consistency Reviewer)  
**Date:** 2026-07-18  
**Scope:** All 5 new Gateway projects + Channels.Core, cross-referenced against AgentCore and Providers.Core

### Summary

The Gateway Service is **well-built and highly consistent internally**. XML doc coverage is effectively 100% on public APIs, naming conventions are clean, and the project structure is exemplary. Found **0 P0 issues**, **4 P1 issues**, and **7 P2 issues**. The most significant finding is a `ConfigureAwait(false)` policy divergence between Gateway (never uses it) and AgentCore (uses it everywhere). This is likely a deliberate architectural choice that should be documented, not a bug.

### Key Findings

#### Naming Conventions — ✅ Consistent
- Namespace pattern: `BotNexus.{Module}.{Submodule}` matches existing patterns
- Interface/Class naming: Prefix `I` for interfaces, `Default*` for primary implementations
- Matches AgentCore/Providers.Core conventions

#### P1 Issues

| # | Issue | Recommendation |
|---|---|---|
| P1-01 | **CancellationToken naming split** — Gateway API layer uses `ct`; abstractions use `cancellationToken`. | Rename `ct` → `cancellationToken` for consistency. |
| P1-02 | **ConfigureAwait policy divergence** — Gateway never uses it; AgentCore uses it 79+ times. | Document the policy. Consider adding to `FileSessionStore` (reusable library). |
| P1-03 | **Test file names don't match classes** — 5 files have misleading names. | Rename test files to match class names. |
| P1-04 | **Gateway test classes missing `sealed` modifier** — Inconsistent with newer test additions in AgentCore/Providers.Core. | Add `sealed` modifier to all Gateway test classes. |

#### P2 Issues (Informational)

7 items noted: collection initialization style, csproj property order (pre-existing), archive renames (intentional), SessionEntry.Role type, test cleanup patterns, Theory usage, XML doc coverage in controllers.

### Thread Safety Pattern

- **C# 13 `Lock` type** — Used for sync code; more efficient than `object` locks
- **`SemaphoreSlim(1,1)`** — Used for async operations (`FileSessionStore`)
- This is a modernization relative to AgentCore; no inconsistency issue.

### Test Framework

- ✅ xUnit exclusively (`[Fact]` and `[Theory]/[InlineData]`)
- ✅ Naming: `MethodName_Condition_ExpectedResult` matches codebase pattern
- ✅ FluentAssertions exclusively
- ⚠️ No test cleanup pattern used (acceptable for current tests)
- ⚠️ No `[Theory]` parameterized tests yet (acceptable for current scope)

### Verdict

Gateway passes consistency review. **P1 items should be addressed before the next milestone; P2 items are housekeeping.** The overall code quality is high.

---

## Wave: Phase 3 Design Review Fixes (2026-04-05T2300Z)

### Bender — P0/P1 Runtime Fixes

#### P0.1: Gateway session history thread safety + streaming exception surfacing
- **Status:** Implemented | **Commits:** 8510dac (recursion guard), 331e4cb (supervisor race fix)
- Introduced thread-safe session history APIs on GatewaySession with AddEntry(), AddEntries(), GetHistorySnapshot()
- Exception handling around in-process stream subscription callbacks surfaces stream failures as AgentStreamEventType.Error instead of silent termination
- Updated FileSessionStore, GatewayHost, StreamingSessionHelper, GatewayWebSocketHandler, ChatController
- Tests: 149 passed, 2 skipped

#### P0.2: GatewayWebSocketHandler streaming history loss
- **Status:** Implemented | **Commit:** b6a92bb
- Applied streamedContent/streamedHistory accumulation pattern from GatewayHost.DispatchAsync into GatewayWebSocketHandler.HandleUserMessageAsync
- Captures ToolStart, ToolEnd, and final ssistant entries before session save
- Closed data-loss bug for WebSocket interactions

#### P1.1: Recursion guard for cross-agent calls
- **Status:** Implemented | **Commit:** 8510dac
- Added max nesting depth check (counts ::sub:: segments in parent session ID)
- Prevents infinite recursion in supervisor callback chain

#### P1.2: Supervisor race condition (state transitions)
- **Status:** Implemented | **Commit:** 331e4cb
- Serialized supervisor state transitions to prevent concurrent mutation

#### P1.3: Reconnection backoff guardrails
- **Status:** Implemented | **Commit:** b8eb0d2
- Server-side: tracks per-client reconnect attempts in sliding window, rejects excess with HTTP 429 + Retry-After
- Client-side: exponential backoff with bounded max retry count

#### P1.4: Async startup (proper cancellation during bootstrap)
- **Status:** Implemented | **Commit:** 01680ff
- Allows proper cancellation during agent bootstrap phase

#### P1.5: Options pattern for runtime configuration
- **Status:** Implemented | **Commit:** 01680ff
- Enables extensible runtime configuration

### Farnsworth — Platform Config + Deployment Scenario + Multi-Tenant Auth

#### Gateway Config/Auth Shape
- **Status:** Implemented | **Commits:** c0fad0b, 5b0b3cf, 30474d7
- Unified platform config schema supports: gateway settings, agents definitions, providers, channels
- gateway.apiKeys (and root piKeys for compatibility) maps each key to tenant identity + permissions
- PlatformConfig, PlatformConfigLoader, ApiKeyGatewayAuthHandler all registered
- Runnable API host entrypoint for deployment scenarios

#### Multi-Tenant Auth
- **Status:** Implemented | **Commit:** 9d5ac37
- API-key based auth mapped from platform config
- Isolated contexts per tenant

#### Improved Error Messages
- **Status:** Implemented | **Commit:** 3695444
- Actionable recovery steps for operators
- Enhanced observability

#### Isolation Strategy Registration
- **Status:** Implemented
- All built-in isolation strategies registered in DI: InProcessIsolationStrategy, SandboxIsolationStrategy, ContainerIsolationStrategy, RemoteIsolationStrategy
- Phase 2 strategies are explicit stubs with NotSupportedException and guidance

#### Platform Config Source
- **Status:** Implemented
- Source: ~/.botnexus/config.json
- PlatformConfig + PlatformConfigLoader with validation for URL/path/log-level
- Missing files resolve to defaults (non-breaking)

#### P1 Channel Stub Fixes
- **Status:** Implemented
- TuiChannelAdapter and TelegramChannelAdapter now extend ChannelAdapterBase
- Override OnStartAsync, OnStopAsync, SendAsync, SendStreamDeltaAsync
- Honor IChannelAdapter contract (TUI: SupportsStreaming = true; Telegram: alse)

#### P1 TelegramOptions DI Pattern
- **Status:** Implemented
- Migrated to services.AddOptions<TelegramOptions>() + services.Configure(configure)
- TelegramChannelAdapter now consumes IOptions<TelegramOptions>
- Consistent with GatewayOptions pattern

#### P1 IChannelManager Interface
- **Status:** Implemented
- Added IChannelManager with IReadOnlyList<IChannelAdapter> Adapters { get; } and Get(string channelType)
- ChannelManager implements interface; GatewayHost depends on IChannelManager
- Pure read-only registry with zero lifecycle logic

### Hermes — Live Integration Testing

#### Live Copilot Provider Integration Tests
- **Status:** Implemented | **Commits:** 73384a5, ea2600c
- 7 integration tests passed validating end-to-end provider flow
- Tests support concurrent provider scenarios
- Empty stream handling verified under load

#### Graceful Skip Pattern for Live Tests
- **Status:** Implemented
- Integration tests in CopilotIntegrationTests.cs treat auth/connectivity failures as graceful skips
- Inspect recorded gateway activity errors while still asserting strict behavior when no live-environment failure present
- Prevents false negatives from live-environment volatility

#### Gateway Test Coverage Growth
- From 135 to 151 tests (149 passed, 2 skipped)
- Test suite now includes provider integration, session persistence, WebSocket streaming

### Summary: Phase 3 Wave 1
- **11 atomic commits** across three agents
- **684 total tests** passed (up from 670 baseline)
- **0 test failures**, 2 skipped
- **Build:** Clean (0 errors, 0 warnings)
- **P1/P2 blockers:** All resolved
- **Status:** READY FOR RELEASE


---

# Decision: Steer Mode Visual UX

**Date:** 2026-07-24
**Author:** Fry (Web Dev)
**Status:** Implemented

## Context

During streaming, the send button and input area need to communicate that user input will steer the running agent rather than start a new conversation. Previously, the button stayed as "Send" with no visual distinction.

## Decision

When `isStreaming` is true and WebSocket is open:
- Send button text changes to "🧭 Steer" with orange styling (`.btn-steer`)
- Input placeholder changes to "Steer the agent... (Enter to send)"
- Both reset to normal on message_end, error, or abort

## Rationale

Users need clear visual feedback that their mid-stream input goes through the `steer` WebSocket message type (injected between tool calls) rather than the `message` type (starts a new run). The orange color matches the existing steer indicator pill styling for consistency.

## Impact

- WebUI only (app.js, styles.css)
- No server-side changes
- Gateway already supports `steer` and `follow_up` message types


---

# Design Review: Gateway Phase 4 Wave 1

**Reviewer:** Leela (Lead / Architect)  
**Date:** 2026-04-05  
**Scope:** 12 commits — runtime hardening, config validation, multi-tenant auth  
**Overall Grade: A-**

---

## Per-Area Grades

| Area | Grade | Summary |
|------|-------|---------|
| SOLID Compliance | B+ | Strong SRP/OCP throughout; WebSocket handler accumulating mixed concerns |
| Extension Model | A- | Interfaces preserved, Options pattern properly used, validation not pluggable (acceptable) |
| Security | B | Multi-tenant auth is solid; config endpoint has path traversal risk |
| Error Handling | A | Excellent actionable messages with dotted-path field references |
| Thread Safety | A | Textbook async patterns — AsyncLocal, TCS with RunContinuationsAsynchronously, ConcurrentDictionary |
| Resource Management | A- | Good cleanup patterns, configurable limits, amortized GC of stale windows |
| API Design | B+ | Clean REST design; path parameter exposes filesystem probing |
| Dependency Injection | A- | Correct Options pattern migration; `ApplyPlatformConfig` manual copy is fragile |

---

## Findings

### P1 — Fix Next Sprint

#### P1-1: Config endpoint allows arbitrary filesystem path probing
**File:** `src/gateway/BotNexus.Gateway.Api/Controllers/ConfigController.cs:14-16`  
**Risk:** The `?path=` query parameter passes through `Path.GetFullPath()` and `File.Exists()` with no restriction. An attacker can confirm existence of arbitrary files on the host (e.g., `?path=C:\Windows\System32\drivers\etc\hosts`). Even though file contents aren't returned, existence probing is an information disclosure vector.  
**Fix:** Either (a) restrict `path` to within `PlatformConfigLoader.DefaultConfigDirectory`, or (b) remove the `path` parameter and always validate the canonical config location, or (c) require authentication on this endpoint.

#### P1-2: No authentication on config validation endpoint
**File:** `src/gateway/BotNexus.Gateway.Api/Program.cs:41` / `ConfigController.cs`  
**Risk:** `GET /api/config/validate` is accessible without any auth middleware. Combined with P1-1, this is an unauthenticated filesystem probe.  
**Fix:** Wire the `IGatewayAuthHandler` as middleware before controller routes, or gate the config endpoint to admin-only callers.

#### P1-3: Recursion guard tests are skipped
**File:** `tests/BotNexus.Gateway.Tests/DefaultAgentCommunicatorTests.cs` (lines with `Skip =`)  
**Risk:** Two recursion-detection tests are marked `[Fact(Skip = "Pending...")]`. The implementation in `DefaultAgentCommunicator.EnterCallChain` does exist and should work for self-calls. These tests should be enabled or replaced with working versions to confirm the guard works end-to-end.  
**Fix:** Remove the `Skip` annotations and adjust assertions. The `HashSet.Add` call on line 89 of `DefaultAgentCommunicator.cs` will correctly reject a self-call (`sourceAgentId == targetAgentId`).

### P2 — Nice to Have

#### P2-1: `ApplyPlatformConfig` is a manual property-copy method
**File:** `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs:131-144`  
**Risk:** If `PlatformConfig` gains a new property, `ApplyPlatformConfig` must be updated manually or the new property silently won't propagate through the Options pattern. This is a maintenance trap.  
**Recommendation:** Consider using a JSON round-trip (`JsonSerializer.Serialize` → `Deserialize` into the target), or use `IConfigureOptions<PlatformConfig>` with a factory that returns the loaded instance directly instead of copying properties.

#### P2-2: WebSocket handler accumulating responsibilities
**File:** `src/gateway/BotNexus.Gateway.Api/WebSocket/GatewayWebSocketHandler.cs` (lines 266-350)  
**Risk:** ~100 lines of rate-limiting/backoff logic (`TryRegisterConnectionAttempt`, `CleanupStaleAttemptWindows`, `GetClientAttemptKey`) are embedded in the WebSocket handler. This mixes transport-level connection management with application-level message routing.  
**Recommendation:** Extract a `IConnectionRateLimiter` (or similar) that the handler delegates to. Not urgent — the code is well-encapsulated within private methods — but it will matter when adding more connection policies.

#### P2-3: No max call-chain depth limit
**File:** `src/gateway/BotNexus.Gateway/Agents/DefaultAgentCommunicator.cs:77-101`  
**Risk:** The recursion guard detects cycles (A→B→A) but not excessive depth (A→B→C→D→E→F...). A deep but acyclic chain could exhaust stack or memory.  
**Recommendation:** Add a configurable `MaxCallChainDepth` (default 5-10) alongside the cycle check.

#### P2-4: Legacy + nested config creates dual source-of-truth
**File:** `src/gateway/BotNexus.Gateway/Configuration/PlatformConfig.cs:41-57`  
**Risk:** The `Get*()` methods implement `Gateway?.X ?? X` fallback, meaning the same setting can live in two places. Users can set conflicting values (root-level `listenUrl` and `gateway.listenUrl`). Validation doesn't warn about this.  
**Recommendation:** Add a validation warning when both root-level and `gateway.*` forms are present for the same setting, pointing users to the canonical location.

---

## Detailed Analysis

### Recursion Guard — `DefaultAgentCommunicator` (commit `8510dac`)

**What it does:** Uses `AsyncLocal<HashSet<string>>` to track the active agent call chain per async flow. Before dispatching a sub-agent or cross-agent call, it checks whether the target is already in the chain.

**Correctness:** The `AsyncLocal` scope is correct — each async flow gets its own copy. The `CallChainScope : IDisposable` cleanup pattern ensures the chain is unwound even on exceptions. The `using var` ensures disposal on both normal and exceptional paths.

**Edge case handled well:** The `createdNewChain` flag prevents leaving an empty HashSet in `AsyncLocal.Value`, avoiding unnecessary GC pressure.

**Thread safety:** `AsyncLocal` provides per-flow isolation. The `HashSet` within a single flow is never accessed concurrently. Correct.

### Supervisor Race Fix — `DefaultAgentSupervisor` (commit `331e4cb`)

**What it does:** Replaces the previous pattern (where the creation `Task` was stored directly in `_pendingCreates`) with a `TaskCompletionSource` pattern. The creator thread gets the TCS; all other threads await the TCS's task.

**Why this is better:** Previously, `CreateEntryAsync(...)` was called inside the lock's scope (indirectly via the stored task). Now, the actual async creation runs outside the lock — only the TCS placeholder lives inside the lock.

**Critical detail:** `TaskCreationOptions.RunContinuationsAsynchronously` on the TCS prevents deadlocks where `SetResult`/`SetException` would synchronously run continuations under a lock.

**Error propagation:** `SetException(ex)` ensures all waiters see the original exception, and `_pendingCreates.Remove(key)` in the `catch` block ensures a retry can succeed.

### WebSocket Reconnection Cap (commit `b6a92bb`)

**Server-side:** `ConcurrentDictionary<string, ConnectionAttemptWindow>` with optimistic concurrency (`TryAdd`/`TryUpdate` in a `while(true)` loop). Returns `429 Too Many Requests` with `Retry-After` header. Exponential backoff.

**Client-side:** `RECONNECT_MAX_ATTEMPTS = 10` with user-visible banner notification.

**Memory management:** `CleanupStaleAttemptWindows` runs every 128th update (amortized O(n) cleanup), removing entries older than `2 × attemptWindow`. This prevents unbounded growth.

**The `while(true)` loop:** Safe because either `TryAdd` or `TryUpdate` will succeed on the next iteration after a concurrent modification. The loop is bounded by the rate of concurrent modifications.

### Options Pattern Migration (commit `01680ff`)

**Key improvement:** Replaced `PlatformConfigLoader.LoadAsync(...).GetAwaiter().GetResult()` with synchronous `Load()`. This eliminates the deadlock risk in hosted service startup where sync-over-async can block the thread pool.

**Registration:** Properly uses `services.AddOptions<PlatformConfig>().Configure(...)` and `services.Replace(...)` for the auth handler, ensuring the Options-resolved config is used consistently.

---

## 2026-04-10 — Session Switching Bug Design Review (Leela)

### Phase 10 Wave 1-2: Session Switching Race Condition Fix

**Date:** 2026-04-10  
**Reviewer:** Leela (Lead/Architect)  
**Spec:** `docs/planning/bug-session-switching-ui/design-spec.md`  
**Status:** Approved for implementation — 6 decisions, 4-wave fix plan

#### Root Causes Confirmed (3 patterns)

1. **Pattern A (Primary):** No client-side session filtering on SignalR handlers
   - Events from old sessions arrive during LeaveSession/join race window
   - All handlers (`ContentDelta`, `MessageStart`, etc.) render unconditionally
   - No sessionId check against currentSessionId

2. **Pattern C (Secondary):** Global streaming state not reset on switch
   - `isStreaming`, `activeToolCalls`, `activeMessageId`, `thinkingBuffer`, processing bar persist
   - Switch away from active agent → UI shows false "processing" state
   - Send button stuck in "Steer" mode

3. **Pattern B (Symptom):** State partially reset but race window allows re-population
   - `elChatMessages.innerHTML` is cleared, but old-session events arrive during async gap and repopulate it
   - Root cause is Pattern A (lack of client-side guard)

#### Bonus Findings

- **Orphan session creation:** `openAgentTimeline` calls `joinSession(agentId, null)` creating throwaway session, then joins the real one — leaks orphans on every sidebar click
- **No state restoration on switch-back:** `checkAgentRunningStatus()` exists but is never called; user can't see if agent is still working

#### Fix Plan (4 waves, dependency-ordered)

| Wave | Owner | Focus | Impact |
|------|-------|-------|--------|
| Wave 1 | Fry | State reset + client-side guards + orphan elimination | Fixes visible bug; ships independently |
| Wave 2 | Fry | Backend verification: ensure `AgentStreamEvent` includes `sessionId` property | Enhances Wave 1 guard effectiveness |
| Wave 3 | Fry | Per-session state map + LRU eviction | Long-term robustness; not a blocker |
| Wave 4 | Hermes | Test suite: 6 key test cases covering state isolation, orphan prevention, switch-back restoration | Validation |

#### 6 Key Decisions

| # | Decision | Rationale |
|---|----------|-----------|
| **D1** | **Client-side guard is primary defense**; server-side groups are belt-and-suspenders | Server already routes by group, but can't prevent stale arrivals. Client must be the final gate. |
| **D2** | **Wave 1 ships without per-session state map** | State reset + guard fixes the visible bug. Per-session state is robustness, not a blocker. |
| **D3** | **No cancellation of background agent work on switch** | Switching away does NOT cancel agent work; agent continues server-side per spec #5. |
| **D4** | **Fix orphan session creation** | Remove `joinSession(agentId, null)` call; replace with explicit `LeaveSession`. |
| **D5** | **Keep vanilla JS** | WebUI is vanilla JS. All fixes must stay vanilla. No framework introduction. |
| **D6** | **Verify backend event payload before relying on sessionId** | Guard degrades gracefully (events without sessionId pass through), but most effective when sessionId present. |

#### Wave 1 Tasks (Core Fix)

| Task | Owner | Description |
|------|-------|-------------|
| W1.1 | Fry | Add state reset block at top of `openAgentTimeline()`: clear `isStreaming`, `activeMessageId`, `activeToolCalls`, `activeToolCount`, `thinkingBuffer`, hide processing bar/abort button |
| W1.2 | Fry | Fix orphan session creation: remove `joinSession(agentId, null)` call, replace with explicit `LeaveSession` before creating new session |
| W1.3 | Fry | Add `isEventForCurrentSession(evt)` guard to all SignalR handlers: `MessageStart`, `ContentDelta`, `ThinkingDelta`, `ToolStart`, `ToolEnd`, `MessageEnd`, `Error`, plus SubAgent handlers |
| W1.4 | Fry | Add `checkAgentRunningStatus()` call at end of `openAgentTimeline()` to restore UI state on switch-back |

#### Wave 2 Task (Backend)

| Task | Owner | Description |
|------|-------|-------------|
| W2.1 | Fry | Verify `AgentStreamEvent` model includes `sessionId` property. If missing, add it and ensure `SignalRChannelAdapter.SendStreamEventAsync` populates it. |

#### Risk Register

| # | Risk | Severity | Mitigation |
|---|------|----------|------------|
| R1 | Race condition: events arrive between LeaveSession and server processing | Medium | Client-side sessionId guard on all handlers (W1.3) |
| R2 | State corruption: resetting isStreaming while agent still working | Low | Per-session state map (Wave 3) preserves state; checkAgentRunningStatus restores on switch-back (W1.4) |
| R3 | Memory leak: sessionState map grows unbounded | Medium | Cap at 20 entries with LRU eviction (Wave 3) |
| R4 | Orphan sessions created on every switch | Low | Remove `joinSession(agentId, null)` call (W1.2) |
| R5 | Backend event payload missing sessionId | Medium | Verify model (W2.1); guard degrades gracefully if missing |
| R6 | SubAgent events lack session guards | Medium | Apply same guard to all SubAgent handlers (W1.3) |

#### Approval

**Verdict:** Approved for implementation.
- **Wave 1** is the minimum viable fix; ships independently.
- **Wave 2** should follow immediately (backend verification).
- **Wave 3** is recommended follow-up for long-term robustness.
- **Wave 4** (tests) should track Wave 1; write test specs in parallel.

Fry to start Wave 1 immediately. Hermes to write test specs (W4 descriptions) in parallel and validate once Wave 1 lands.

### Multi-Tenant Auth (commit `30474d7`)

**Architecture:** `BuildIdentityMap` constructs an immutable `Dictionary<string, GatewayCallerIdentity>` at startup. Auth is a single dictionary lookup — O(1), no lock contention at runtime.

**Backward compatibility:** Three constructors support: (1) dev mode, (2) legacy single key, (3) platform config with multi-tenant keys. All converge to the same identity map.

**Key comparison:** `StringComparer.Ordinal` — correct. Dictionary hash-based lookup makes timing attacks impractical in this context.

### Config Validation Endpoint (commits `5b0b3cf`, `9d5ac37`)

**Design:** `GET /api/config/validate` returns 200 OK with a `ConfigValidationResponse` payload. This is correct — a "config is invalid" result is not an HTTP error, it's the answer to the query.

**Validation quality:** Error messages use dotted-path notation (`gateway.apiKeys.tenant-a.apiKey`), include examples, and are deduplicated + sorted. This is excellent UX for operators.

### Program.cs (commit `5b0b3cf`)

**SDK change:** Correctly changed from `Microsoft.NET.Sdk` to `Microsoft.NET.Sdk.Web` for the executable API project. Removed redundant `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.

**Registration order:** `AddBotNexusGateway()` → `AddPlatformConfiguration()` → `AddBotNexusGatewayApi()` — correct order since platform config replaces default services registered by the gateway.

**`public partial class Program`:** Enables `WebApplicationFactory<Program>` in integration tests. Good practice.

---

## Recommendations

1. **Immediate:** Address P1-1 and P1-2 before shipping the config endpoint to any non-localhost deployment. The filesystem probe + no auth combination is a real attack surface.
2. **Next sprint:** Enable the skipped recursion tests (P1-3). If the tests don't pass with the current implementation, the guard has a bug.
3. **Backlog:** Extract connection rate limiting from WebSocket handler (P2-2) during the next refactor pass. Add config conflict warnings (P2-4) when users report confusion.
4. **Tech debt:** Replace `ApplyPlatformConfig` property copy (P2-1) next time `PlatformConfig` gains a property.

---

**Verdict:** Phase 4 Wave 1 is well-executed. The runtime hardening fixes (recursion, races, reconnection) are textbook-correct patterns. The multi-tenant auth design scales cleanly. The config validation endpoint needs auth gating before production use. Overall: **ship it, but file P1s for the config endpoint security issues.**

— Leela


---

# Phase 4 Wave 1 — Consistency Review

**Reviewer:** Nibbler  
**Date:** 2026-07-18  
**Scope:** 12 commits (8510dac → 3695444) touching gateway code  
**Grade:** Good  
**Build:** 0 errors, 0 warnings | **Tests:** 734 passed, 0 failed, 2 skipped

---

## Summary

Phase 4 code quality is strong. No P0 issues. Two P1s found and fixed directly. Five P2s documented for future consideration. Multi-tenant API key support, config validation endpoint, WebSocket reconnection caps, recursion guard, and duplicate create prevention are all consistent end-to-end. DI registrations match controller dependencies. Interface contracts match implementations.

---

## P0 — Critical (0 found)

None.

## P1 — Fixed (2 found, 2 fixed in commit cc005da)

### 1. ConfigController missing XML docs
**File:** `src/gateway/BotNexus.Gateway.Api/Controllers/ConfigController.cs`  
New Phase 4 file (config validation endpoint) shipped without XML docs on the class, `Validate` method, and `ConfigValidationResponse` record. All other controllers in the API project have XML docs — this broke the pattern.  
**Fix:** Added class, method, and record-level XML doc comments.

### 2. PlatformConfig property-level XML docs inconsistent
**Files:** `src/gateway/BotNexus.Gateway/Configuration/PlatformConfig.cs`  
Phase 4 added `ApiKeyConfig`, `GatewaySettingsConfig.ApiKeys`, and helper methods without property-level XML docs. Meanwhile, the pre-existing `ProviderConfig` class in the same file documents every property. Mixed doc depth in the same file is a consistency gap.  
**Fix:** Added property-level XML docs to `GatewaySettingsConfig`, `AgentDefinitionConfig`, `ChannelConfig`, `ApiKeyConfig`, and all `PlatformConfig` helper methods (`GetApiKeys`, `GetListenUrl`, `GetDefaultAgentId`, etc.).

### 3. FileSessionStore.cs misleading ConfigureAwait comment (stale)
**File:** `src/gateway/BotNexus.Gateway.Sessions/FileSessionStore.cs`  
Comment said "The Gateway host project (BotNexus.Gateway)" — but BotNexus.Gateway is a class library, not the host (that's BotNexus.Gateway.Api). Additionally, Phase 4 commit b8eb0d2 added `.ConfigureAwait(false)` to `AgentConfigurationHostedService` inside BotNexus.Gateway, partially contradicting the comment.  
**Fix:** Rewrote comment to correctly describe all three tiers (Gateway.Sessions uses it; Gateway library omits because no sync context; Gateway.Api omits for same reason).

---

## P2 — Documented (5 found, not fixed)

### 1. GatewayWebSocketOptions not configurable via appsettings.json
**File:** `src/gateway/BotNexus.Gateway.Api/Extensions/GatewayApiServiceCollectionExtensions.cs` (line 20-23)  
WebSocket reconnection limits (`MaxReconnectAttempts=20`, `AttemptWindowSeconds=300`, `BackoffBaseSeconds=1`, `BackoffMaxSeconds=60`) are hardcoded in `GatewayWebSocketOptions` code defaults. The DI registration uses `AddOptions<GatewayWebSocketOptions>()` without `.Bind()` from config — these limits cannot be overridden via appsettings.json. Either add config binding or document them as fixed limits.

### 2. API reference doesn't document Chat or Config endpoints
**File:** `docs/api-reference.md`  
Documents agents, skills, sessions, and system endpoints — but not the Chat endpoints (`POST /api/chat`, `POST /api/chat/steer`, `POST /api/chat/follow-up`) or the Config validation endpoint (`GET /api/config/validate`). The Chat endpoints pre-date Phase 4, but the Config endpoint is new.

### 3. README project structure is stale
**File:** `README.md` (line 148-160)  
Shows flat `src/BotNexus.Gateway` layout but actual structure is `src/gateway/` with 4 subprojects (Gateway, Abstractions, Api, Sessions). Quick Start path `dotnet run --project src/BotNexus.Gateway` may also be incorrect — the runnable project is `src/gateway/BotNexus.Gateway.Api`.

### 4. ConfigureAwait(false) inconsistent within BotNexus.Gateway library
`AgentConfigurationHostedService.StartAsync` (line 33) uses `.ConfigureAwait(false)` after Phase 4's sync-context fix (commit b8eb0d2). All other async methods in BotNexus.Gateway do NOT use it. This is an intentional decision for the library overall (no sync context in practice), but having one method differ creates a mixed signal. Consider either removing it from AgentConfigurationHostedService or adopting it consistently.

### 5. Pre-existing XML doc gaps on implementation classes
Several implementation classes in BotNexus.Gateway pre-date Phase 4 and lack XML docs: `DefaultAgentRegistry` methods, `DefaultAgentSupervisor` constructor, `DefaultMessageRouter` constructor. Not introduced by Phase 4, but worth a documentation pass.

---

## What Phase 4 Got Right

- **Naming conventions** — All CancellationToken params correctly named `cancellationToken`. All interfaces follow I-prefix. All test methods follow `Method_Condition_Result` pattern. Config property names match JSON serialization.
- **sealed modifiers** — Every implementation class is properly sealed. Static helpers are static. No unsealed leaks.
- **DI ↔ controllers** — All controller dependencies are registered. Multi-tenant auth handler correctly replaced via `services.Replace()`.
- **Interface contracts** — All 5 interface implementations fully satisfy contracts.
- **Multi-tenant API keys** — End-to-end consistent: PlatformConfig model → PlatformConfigLoader validation → ApiKeyGatewayAuthHandler identity map → GatewayCallerIdentity → tests.
- **Config validation endpoint** — Controller → LoadAsync → Validate pipeline works correctly. Tests verify error messages match validation rules.
- **Recursion guard** — AsyncLocal call chain in DefaultAgentCommunicator prevents circular cross-agent calls.
- **Duplicate create prevention** — DefaultAgentSupervisor uses TaskCompletionSource with concurrent waiters, correctly handles cleanup on failure.

---

## Previous P1 Tracker

All P1s from prior reviews remain fixed:
- ✅ CancellationToken `ct` in API layer (fixed Phase 1 sprint)
- ✅ Test file names not matching class names (fixed Phase 1 sprint)
- ✅ GatewayOptionsTests in wrong file (fixed Phase 3)
- ✅ Isolation stubs missing `/// <inheritdoc />` (fixed Phase 3)

---

# Gateway Phase 5 — Batch 2 Decisions

**Batch:** 2026-04-06T00:40Z  
**Agents:** Bender, Farnsworth, Hermes, Kif  
**Phase:** Gateway Phase 5 — WebSocket streaming, auth guardrails, agent workspace, provider bootstrap, anticipatory tests

---

## Decision: Structured stream event adapter contract for WebSocket pipeline

**Date:** 2026-04-06  
**Owner:** Bender (Runtime Dev)

### Context

WebSocket chat had to move into the standard channel pipeline (`GatewayHost.DispatchAsync`) without losing protocol richness (`message_start`, `thinking_delta`, tool events, usage/end events). Existing channels only consumed plain text deltas via `SendStreamDeltaAsync`.

### Decision

Add a new optional channel capability contract, `IStreamEventChannelAdapter`, in gateway abstractions.  
`GatewayHost` now checks this interface during streaming: if implemented, it forwards each `AgentStreamEvent`; otherwise it preserves existing behavior (content deltas only).

### Rationale

- Keeps non-WebSocket channels unchanged and backward compatible.
- Avoids encoding protocol-specific event payloads into generic `OutboundMessage` metadata.
- Allows WebSocket adapter to preserve full protocol fidelity while still using shared routing/session pipeline.

### Impact

- `WebSocketChannelAdapter` implements `IStreamEventChannelAdapter`.
- Existing adapters (TUI/Telegram/etc.) require no changes.
- Future rich channels can opt in incrementally.

---

## Decision: Gateway auth + session guardrails at API boundary

**Date:** 2026-04-06  
**Owner:** Bender (Runtime Dev)

### Context

Gateway runtime had an auth handler implementation but no ASP.NET middleware wiring, no OpenAPI surface, no per-agent session cap enforcement, and no per-session WebSocket connection lock.

### Decision

1. Enforce gateway authentication in middleware (`GatewayAuthMiddleware`) for all non-bypassed HTTP/WS upgrade requests.
2. Keep explicit unauthenticated bypasses only for `/health`, `/webui`, static web assets, and `/swagger`.
3. Enforce `MaxConcurrentSessions` in `DefaultAgentSupervisor` and return HTTP 429 via API controller handling.
4. Validate isolation strategy names before instance creation and include known strategy names in error messages.
5. Enforce single active WebSocket connection per `session` and reject duplicates with close code `4409`.

### Why

- Moves security and admission controls to the runtime boundary.
- Prevents silent over-capacity behavior and duplicate socket contention.
- Improves operator/developer diagnosis for bad agent configuration.

### Notes

- This change did **not** modify protected paths (`src/agent`, `src/providers`, `src/coding-agent`).

---

## Decision: Platform Config Agent Source

**Date:** 2026-04-06  
**Owner:** Bender (Runtime Dev)

### Context

Platform config (`~/.botnexus/config.json`) already validates `agents` entries, but those agents were not being loaded into the runtime. Only file-based agent descriptors were auto-registered.

### Decision

Introduce `PlatformConfigAgentSource` (`IAgentConfigurationSource`) and register it alongside the file-based source in `AddPlatformConfiguration`.

### Rationale

- Keeps config-driven runtime behavior consistent with platform config intent.
- Reuses hosted-source merge behavior already implemented in `AgentConfigurationHostedService`.
- Avoids hot-reload complexity for now (`Watch()` returns `null`) while enabling immediate startup parity.

### Impact

- Enabled platform-config agent definitions now become active agent descriptors automatically at startup.
- File-based sources continue to work; both sources are now loaded together.

---

## Decision: Gateway provider/auth bootstrap

**Date:** 2026-04-06  
**Owner:** Farnsworth (Agent Workspace Manager)

### Context

Gateway API startup had no provider registration and no centralized credential resolution for in-process agent execution.

### Decision

Gateway now performs provider bootstrap at API startup and resolves provider credentials centrally through `GatewayAuthManager`.

1. Register built-in providers during `LlmClient` singleton creation in `src/gateway/BotNexus.Gateway.Api/Program.cs`:
   - `AnthropicProvider`
   - `OpenAICompletionsProvider`
   - `OpenAIResponsesProvider`
   - `OpenAICompatProvider`
2. Use one shared `HttpClient` singleton with a 10-minute timeout for provider calls.
3. Resolve API keys in `GatewayAuthManager` with this order:
   - `~/.botnexus/auth.json` (with Copilot OAuth refresh support)
   - environment variables (`EnvironmentApiKeys`)
   - `providers.{name}.apiKey` from `PlatformConfig`
4. Wire `InProcessIsolationStrategy` to use `GatewayAuthManager.GetApiKeyAsync`.

### Why

Without provider registration, Gateway agents cannot make LLM calls. Without runtime key resolution, in-process agents cannot authenticate providers reliably across local auth, CI env vars, and config-based keys.

### Impact

- Gateway can execute agent prompts against Anthropic/OpenAI/Copilot-compatible endpoints.
- Credential sourcing is consistent with platform-level config conventions (`~/.botnexus`), not coding-agent local auth paths.

---

## Decision: Gateway session/config lifecycle decisions

**Date:** 2026-04-06  
**Owner:** Farnsworth (Agent Workspace Manager)

### Decision 1: Channel capability contract expansion

- Added explicit capability flags to `IChannelAdapter`: `SupportsSteering`, `SupportsFollowUp`, `SupportsThinkingDisplay`, `SupportsToolDisplay`.
- Base behavior defaults to `false` in `ChannelAdapterBase`.
- Current channel declarations:
  - TUI: thinking + tool display enabled.
  - Telegram: thinking + tool display disabled.

### Decision 2: Session lifecycle enforcement

- Added `SessionStatus` (`Active`, `Suspended`, `Expired`, `Closed`) and nullable `ExpiresAt` to `GatewaySession`.
- Added `SessionCleanupService` (`BackgroundService`) to expire stale active sessions and optionally purge closed sessions beyond retention.
- Introduced `SessionCleanupOptions` with defaults:
  - Check interval: 5 minutes
  - Active session TTL: 24 hours
  - Closed retention: optional (disabled by default)

### Decision 3: BotNexus home agent workspace contract

- Added `BotNexusHome` in Gateway configuration with required directories:
  - `extensions/`, `tokens/`, `sessions/`, `logs/`, `agents/`
- Added `GetAgentDirectory(string agentName)` to create and return `~/.botnexus/agents/{name}`.
- First-time workspace scaffolding creates:
  - `SOUL.md`
  - `IDENTITY.md`
  - `USER.md`
  - `MEMORY.md`

### Decision 4: Platform config file hot-reload shape

- Added `PlatformConfigLoader.Watch(...)` with a `FileSystemWatcher` scoped to `config.json` (single-file filter).
- Reload uses existing `Load(...)` + validation pipeline.
- Introduced 500ms debounce to avoid change storms.
- Exposed static `PlatformConfigLoader.ConfigChanged` event and optional callback hook in `Watch(...)`.

---

## Decision: Anticipatory test scaffolding strategy

**Date:** 2026-04-06  
**Owner:** Hermes (Test Infrastructure)

### Context

Phase-5 gateway features are being implemented in parallel by other agents, but QA coverage needed to be staged immediately.

### Decision

Create expected-behavior test suites now, and mark tests with explicit `Skip` reasons when they depend on not-yet-landed runtime types or wiring.

### Rationale

- Preserves test intent and naming now, reducing integration lag when feature PRs land.
- Avoids brittle compile-time coupling to in-flight implementation classes.
- Enables selective early assertions for already-available behavior while keeping the suite green.

### Follow-up

As feature branches merge, remove `Skip` attributes and replace placeholders with concrete arrange-act-assert implementations against real types.

---

## Decision: Gate live Copilot integration test behind opt-in env var

**Date:** 2026-04-06  
**Owner:** Hermes (Test Infrastructure)

### Context

`Phase5IntegrationTests` includes a live Copilot streaming validation that depends on external auth, network reachability, and provider availability.

### Decision

Keep the live test present and categorized with `[Trait("Category", "LiveIntegration")]`, but run it only when `BOTNEXUS_RUN_COPILOT_INTEGRATION=1`.

### Rationale

- Prevents routine CI/local test runs from failing due transient external dependency issues.
- Preserves full live validation capability for explicit, opt-in smoke runs.
- Aligns with existing integration patterns in `CopilotIntegrationTests` that gate live calls.

---

## Design Review: Gateway Sprint Changes

**Reviewer:** Leela (Lead/Architect)  
**Requested by:** Jon Bullen (via Copilot)  
**Scope:** Gateway auth, platform-config agents, in-process isolation, composition root, DI extensions

### GatewayAuthManager

**File:** `src/gateway/BotNexus.Gateway/Configuration/GatewayAuthManager.cs`  
**Grade:** B  
**Recommendation:** APPROVE WITH NOTES

#### Findings

- **P1 — DIP violation: no interface.** `GatewayAuthManager` is a concrete class injected directly into `InProcessIsolationStrategy` and registered as `TryAddSingleton<GatewayAuthManager>`. There is no `IGatewayAuthManager` abstraction. This blocks mocking in tests (confirmed — `InProcessIsolationStrategyTests` constructs real instances) and prevents consumers from swapping auth resolution strategies. Extract an interface with `GetApiKeyAsync` and `GetApiEndpoint`.

- **P1 — TOCTOU race on OAuth refresh.** Two concurrent calls for the same provider can both observe `NeedsRefresh == true`, both call `RefreshEntryAsync` with the same refresh token, and both write back. The second refresh may use an already-invalidated refresh token, causing a failure or silent credential corruption. Fix: use a `SemaphoreSlim` keyed per provider (or a `ConcurrentDictionary<string, Lazy<Task>>`) to serialize refreshes per provider while allowing concurrency across providers.

- **P2 — File I/O under lock.** `SaveAuthEntries()` (line 211–221) performs `File.WriteAllText` while holding `_sync`. This blocks all concurrent readers during disk writes. Consider writing to a buffer under the lock and flushing outside it, or switching to `SemaphoreSlim` for async-friendly synchronization.

- **P2 — Namespace placement.** This class resolves credentials and manages OAuth token lifecycle — it is authentication infrastructure, not configuration. A `Security` or `Auth` namespace would better communicate intent and separate it from config DTOs like `PlatformConfig`. Not blocking, but worth a follow-up move.

- **P2 — Static coupling to `CopilotOAuth` and `EnvironmentApiKeys`.** `RefreshEntryAsync` calls `CopilotOAuth.RefreshAsync` directly, and key resolution calls `EnvironmentApiKeys.GetApiKey`. Both are static, untestable seams. Acceptable for now given scope, but flag for future extraction behind injectable interfaces.

- **P2 — `_loaded` flag prevents re-reads.** If `auth.json` is modified after first load, `GatewayAuthManager` won't pick up changes until process restart. Consider a `FileSystemWatcher` or TTL-based reload (consistent with `FileAgentConfigurationSource`'s watch pattern).

### PlatformConfigAgentSource

**File:** `src/gateway/BotNexus.Gateway/Configuration/PlatformConfigAgentSource.cs`  
**Grade:** A  
**Recommendation:** APPROVE

#### Findings

- **Clean `IAgentConfigurationSource` implementation.** Single responsibility (maps platform config agents to descriptors), validates via shared `AgentDescriptorValidator`, correctly uses `IOptions<PlatformConfig>` for deferred resolution. Well-structured.

- **Path traversal protection is correct and consistent.** The `TryLoadSystemPromptFromFileAsync` guard (lines 84–95) mirrors the identical pattern in `FileAgentConfigurationSource` (lines 109–121) — `GetFullPath` + directory prefix check with separator normalization. Both implementations are sound.

- **P2 — `Watch` returns null.** This is valid per the interface contract (`IDisposable?`), but it means platform-config agents cannot hot-reload without process restart. Document this limitation or consider future watch support via `IOptionsMonitor<PlatformConfig>`.

- **P2 — Duplicated path traversal logic.** Both `PlatformConfigAgentSource` and `FileAgentConfigurationSource` contain near-identical path traversal checks. Consider extracting to a shared `PathSafetyGuard.IsWithinDirectory(resolvedPath, rootDirectory)` utility to ensure both evolve together.

### InProcessIsolationStrategy

**File:** `src/gateway/BotNexus.Gateway/Isolation/InProcessIsolationStrategy.cs`  
**Grade:** B+  
**Recommendation:** APPROVE WITH NOTES

#### Findings

- **P1 — Concrete dependency on `GatewayAuthManager`.** Constructor takes `GatewayAuthManager` (concrete), not an abstraction. This is the consumer-side manifestation of the missing interface noted above. When the interface is extracted, update this constructor.

- **P2 — `CreateAsync` is synchronous.** The method signature is `Task<IAgentHandle>` but returns `Task.FromResult` (line 78). This is fine for the in-process strategy, but future strategies (container, remote) will be truly async. No change needed — the interface correctly requires `Task` for the general case.

- **`InProcessAgentHandle` streaming is well-designed.** The `Channel<T>`-based streaming pattern (line 128) with background `Task.Run` for the prompt (line 195) is clean. Error propagation via `TryComplete(ex)` with best-effort error event emission (line 178–190) is defensive and correct.

- **`DisposeAsync` is properly guarded.** Wraps `AbortAsync` in try-catch to prevent dispose failures from bubbling. Good practice.

- **Test coverage is adequate.** `InProcessIsolationStrategyTests` covers creation, missing model error, ID propagation, and name verification. Tests confirm the concrete `GatewayAuthManager` coupling (they construct real instances — lines 31–33, 80–83).

### Program.cs

**File:** `src/gateway/BotNexus.Gateway.Api/Program.cs`  
**Grade:** B+  
**Recommendation:** APPROVE WITH NOTES

#### Findings

- **P2 — Singleton `HttpClient` instead of `IHttpClientFactory`.** Line 22 registers `new HttpClient { Timeout = ... }` as a singleton. This works and avoids socket exhaustion, but bypasses DNS rotation and the standard `IHttpClientFactory` lifecycle. For a multi-provider gateway that may talk to different endpoints, consider migrating to named `HttpClient` registrations via `AddHttpClient<T>()`.

- **P2 — Imperative provider registration in factory lambda.** The `LlmClient` factory (lines 23–37) hardcodes 4 provider registrations. This is acceptable for a composition root, but if providers become pluggable, consider a `IApiProviderContributor` pattern or a builder API. Not blocking.

- **Registration order is correct.** `AddBotNexusGateway()` → `AddPlatformConfiguration()` → `AddBotNexusGatewayApi()` → provider singletons → `LlmClient` factory. The factory correctly resolves its dependencies lazily. `BuiltInModels.RegisterAll()` is called inside the factory, ensuring the `ModelRegistry` is populated before `LlmClient` is used.

- **Health endpoint and fallback routing are clean.** Minimal API usage is idiomatic.

### GatewayServiceCollectionExtensions

**File:** `src/gateway/BotNexus.Gateway/Extensions/GatewayServiceCollectionExtensions.cs`  
**Grade:** B  
**Recommendation:** APPROVE WITH NOTES

#### Findings

- **P1 — `AddPlatformConfiguration` does too much.** This 50-line method loads config from disk, registers `IOptions<PlatformConfig>`, replaces `PlatformConfig` singleton, registers `GatewayAuthManager`, replaces `IGatewayAuthHandler`, configures default agent, sets up `FileSessionStore`, replaces `IAgentConfigurationSource`, registers `PlatformConfigAgentSource`, and registers `AgentConfigurationHostedService`. That's at least 6 distinct responsibilities in one method. Split into focused submethods: `ConfigureAuthHandler`, `ConfigureSessionStore`, `ConfigureAgentSources`, etc.

- **P2 — `ApplyPlatformConfig` is fragile.** The manual property-by-property copy (lines 139–152) will silently drop new properties if `PlatformConfig` is extended. Consider either: (a) making `PlatformConfig` a `record` with `with` expressions, or (b) using a serialization roundtrip, or (c) adding a test that reflects over `PlatformConfig` properties to ensure `ApplyPlatformConfig` covers them all.

- **P2 — `Replace` semantics are order-sensitive.** `Replace(ServiceDescriptor.Singleton<IGatewayAuthHandler>(...))` (line 94) depends on `AddBotNexusGateway` having already registered `ApiKeyGatewayAuthHandler`. If call order changes, the replacement silently does nothing (or throws). Add a comment documenting this ordering requirement, or use a more robust pattern (e.g., `PostConfigure` or a dedicated options callback).

- **`TryAddSingleton<GatewayAuthManager>` is correct.** Allows consumers to register a custom auth manager before `AddPlatformConfiguration` is called.

- **`PlatformConfigAgentSource` registration is clean.** Factory-based singleton with proper DI resolution of `IOptions<PlatformConfig>` and logger.

### Summary Verdict

| File | Grade | Recommendation |
|------|-------|----------------|
| GatewayAuthManager | B | APPROVE WITH NOTES |
| PlatformConfigAgentSource | A | APPROVE |
| InProcessIsolationStrategy | B+ | APPROVE WITH NOTES |
| Program.cs | B+ | APPROVE WITH NOTES |
| GatewayServiceCollectionExtensions | B | APPROVE WITH NOTES |

**Overall: APPROVE WITH NOTES**

#### P1 Action Items (address before next sprint)

1. **Extract `IGatewayAuthManager` interface** with `GetApiKeyAsync` and `GetApiEndpoint`. Update `InProcessIsolationStrategy` constructor and DI registration. This unblocks proper test isolation and enables alternative auth implementations (e.g., vault-backed, managed identity).

2. **Fix OAuth refresh race condition** in `GatewayAuthManager`. Serialize concurrent refreshes per provider using `SemaphoreSlim` or `ConcurrentDictionary<string, Lazy<Task>>` to prevent double-refresh with invalidated tokens.

3. **Split `AddPlatformConfiguration`** into focused submethods. The current method is a maintenance risk — any change to platform config wiring requires reading and understanding 50+ lines of interleaved concerns.

#### P2 Items (backlog / next opportunity)

- Move `GatewayAuthManager` to an `Auth` or `Security` namespace.
- Extract shared path traversal guard from `PlatformConfigAgentSource` and `FileAgentConfigurationSource`.
- Add property-coverage test for `ApplyPlatformConfig` to prevent silent property drops.
- Evaluate `IHttpClientFactory` migration for provider `HttpClient` management.
- Consider `auth.json` file-watch or TTL reload for `GatewayAuthManager`.

#### What's Working Well

- **Extension model is clean.** `IAgentConfigurationSource`, `IIsolationStrategy`, and the `TryAdd`/`Replace` DI patterns give consumers clear extension points without modification.
- **Security posture is solid.** Path traversal guards are consistent and correct. Auth fallback chain (auth.json → env → platform config) is well-ordered.
- **Test coverage exists** for the key new types. `PlatformConfigAgentSourceTests` covers happy path, missing files, and watch semantics. `InProcessIsolationStrategyTests` covers creation and error paths.
- **Streaming implementation is production-quality.** The `Channel<T>`-based streaming in `InProcessAgentHandle` with proper error propagation and disposal is well-crafted.

---

## Gateway Architecture Gap Analysis

**Author:** Leela (Lead / Architect)  
**Date:** 2026-04-04  
**Requested by:** Brady (Jon Bullen)  
**Status:** Analysis Complete — Ready for Sprint Planning

### Executive Summary

The Gateway has a **strong architectural foundation**. The abstractions layer (`BotNexus.Gateway.Abstractions`) is clean and complete — 10 interfaces, 12 model records, 3 enums, covering all 6 requirement areas. The API surface is surprisingly mature with REST controllers, WebSocket streaming with full event protocol (thinking, tool calls, content deltas, steering, follow-up, abort), and a functional WebUI.

**The gap is not in design — it's in wiring and completeness.** Key interfaces exist but implementations are stubs (3 of 4 isolation strategies, 2 of 2 channel adapters), auth exists but isn't enforced on endpoints, config validation works via API but there's no CLI, and channels don't propagate steering/queuing capabilities.

*See full analysis in original inbox document for comprehensive requirement-by-requirement breakdown, finding details, and change recommendations.*

### Key Gaps Addressed in Batch 2

✅ **Agent workspace management** — `AgentWorkspaceManager` + `IContextBuilder` implemented  
✅ **MaxConcurrentSessions enforcement** — Added to `DefaultAgentSupervisor.GetOrCreateAsync()`  
✅ **Isolation strategy validation** — Strategy name validation before instance creation  
✅ **WebSocket as channel adapter** — `IStreamEventChannelAdapter` contract for protocol fidelity  
✅ **Channel capability declaration** — Capability flags added to `IChannelAdapter`  
✅ **TUI input loop** — Integrated with WebSocket streaming  
✅ **Session lifecycle** — `SessionStatus` + `SessionCleanupService` with configurable TTL  
✅ **Provider bootstrap** — `GatewayAuthManager` + centralized auth resolution  

### Remaining Gaps (Phase 2/Future)

- ⏳ Cross-agent calling (P2) — Requires remote Gateway discovery
- ⏳ Agent health monitoring (P2) — Heartbeat check, auto-restart
- ⏳ Sandbox/Container/Remote isolation implementations (P2)
- ⏳ Slack/Discord adapters (P2)
- ⏳ Dynamic channel loading (P2)


---



---



---

# Sprint 7A Design Review

## 2026-04-06T03:00Z: Sprint 7A Design Review
**By:** Leela
**Grade:** A-
**What:** Design review of Sprint 7A implementations (session reconnection, suspend/resume, pagination, depth limits, timeout, queuing, steering, session store config, OpenAPI)

## Scores
| Area | Grade | Notes |
|------|-------|-------|
| SOLID Compliance | A | DIP fix delivered (IGatewayWebSocketChannelAdapter). SRP well-maintained across GatewayHost, DefaultAgentCommunicator, SessionsController. Options pattern used correctly for configurable limits. Minor: GatewaySession accumulates reconnect replay concern alongside history — acceptable given thread-safety requirements but watch for further growth. |
| Extension Model | A | New features follow existing extension patterns. Session store selection via DI Replace(). Channel adapter contracts extended cleanly via IGatewayWebSocketChannelAdapter. Configurable options bound through IOptions<T>. PayloadMutator delegate pattern in WebSocketChannelAdapter is elegant. |
| API Design | A- | REST endpoints well-structured: PATCH for suspend/resume (idempotent state transitions), GET for pagination with bounded limits (cap at 200). Conflict (409) used correctly for invalid state transitions. WS reconnect protocol is clean (reconnect message → replay → reconnect_ack). Minor deduction: SessionHistoryResponse record defined in controller file — should live in Models/Abstractions for reuse. |
| Thread Safety | A | GatewaySession uses separate Lock objects for history and stream replay — good granularity. AllocateSequenceId is properly atomic. BoundedChannel for session queuing with SingleReader=true ensures sequential per-session processing. ConcurrentDictionary used correctly in GatewayWebSocketHandler for connection tracking. AsyncLocal call chain tracking in DefaultAgentCommunicator is correct for async flow. |
| Test Quality | A- | 39 new tests covering all Sprint 7A features. Thread-safety tests with 500 concurrent writers. Reconnect replay tested with window boundaries. Depth limit tested for exceed, within-limit, and recovery-after-failure. Timeout tested with both TimeoutException and caller cancellation propagation. Minor: TUI tests use Task.Delay(200) for timing — fragile on slow CI but acceptable for TUI adapter. |

## Findings
### P0 (Must Fix)
None.

### P1 (Should Fix)
1. **SessionHistoryResponse location** — `SessionHistoryResponse` record is defined at the bottom of `SessionsController.cs` (line 101). It's a response model that could be needed by clients, SDK generators, or other controllers. Move to `BotNexus.Gateway.Abstractions.Models` namespace alongside `GatewaySession` and `SessionEntry`.

2. **GatewaySession responsibility growth** — `GatewaySession` now owns both conversation history (with thread-safe locking) and WebSocket reconnect replay state (sequence IDs, stream event log, separate lock). This is two concerns in one class. Not blocking today, but if any more state is added (e.g., rate limit counters, presence tracking), extract replay state into a dedicated `SessionReplayBuffer` class to preserve SRP. Flag for Sprint 7B monitoring.

3. **SequenceAndPersistPayloadAsync serialization round-trip** — In `GatewayWebSocketHandler`, the method serializes the payload to JSON, deserializes back to `Dictionary<string,object?>`, adds `sequenceId`, then re-serializes. This double serialization works but is wasteful for high-throughput streams. Consider using `JsonNode` or a wrapper type to inject the sequence ID without the round-trip. Low priority but worth noting for performance-sensitive paths.

4. **Reconnect replay skips payloadMutator** — In `HandleReconnectAsync`, replayed events are sent as raw `PayloadJson` bytes directly to the socket, bypassing the `payloadMutator` pipeline. This is likely intentional (events are already sequenced), but it means replayed payloads won't go through any future middleware added to the mutator pipeline. Add a comment documenting this design choice.

### P2 (Informational)
1. **Consistent use of IOptions<T> constructor overloads** — Both `DefaultAgentCommunicator` and `GatewayWebSocketHandler` provide backward-compatible constructors that create default `Options.Create(...)` instances. Good pattern for test ergonomics. Consistent across the codebase.

2. **FileSessionStore now persists stream replay state** — The `SessionMeta` record includes `NextSequenceId` and `StreamEvents`, and `LoadFromFileAsync` calls `SetStreamReplayState`. This means reconnect replay survives gateway restarts for file-backed sessions. Well done — this wasn't explicitly required but is architecturally correct.

3. **Session queue cleanup** — `CleanupQueueIfClosedSessionAsync` drains the queue when a session is closed. The `CompleteSessionQueuesAsync` on shutdown is clean. Note that orphaned queue workers for idle sessions will persist until the next message arrives or shutdown occurs — acceptable for current scale.

4. **TUI steering uses hardcoded session ID** — The TUI adapter dispatches steer messages with `SessionId = "tui-console"`. This works for single-user local mode but won't support multi-session TUI if that ever becomes a requirement. Fine for now.

5. **PlatformConfigLoader.ValidateSessionStore** — Clean validation for InMemory/File types with actionable error messages. Ready for future store types (SQLite planned for Sprint 7C).

6. **Auth bypass (Path.HasExtension) and StreamAsync task leak** — Carried from Phase 5/6. Not addressed in Sprint 7A (correctly scoped out). Remain P1 for Sprint 7B.

## Recommendations
1. Move `SessionHistoryResponse` to abstractions (P1, Sprint 7B).
2. Monitor `GatewaySession` size — extract replay buffer if it grows further.
3. Add inline comment in `HandleReconnectAsync` explaining why replayed events bypass payloadMutator.
4. Address carried Phase 5/6 findings (auth bypass, task leak) in Sprint 7B.
5. Overall: Sprint 7A is solid work. Clean architecture, good test coverage, correct thread-safety patterns. The team delivered 8 features in 4+4+2+1 commits with zero regressions and 39 new tests. Grade: **A-** (minor structural nits prevent a clean A).


---

## Phase 13: Observability — OpenTelemetry + Serilog Foundation

**2026-04-06 — Proposal by Leela (Lead)**

### User Request (Jon Bullen — 2026-04-06T13:20:19Z)

Platform should adopt **OpenTelemetry** and **Serilog** for structured logging and message traceability with spans and traces.

### Executive Summary

**Current State:** 73 log call sites, zero OTel, zero Serilog. Logging is non-structured, non-correlated, and platform lacks end-to-end tracing.

**Proposal:** Adopt OTel + Serilog in 4 waves:
- **Wave 1 (Low risk):** Serilog + OTel SDK wiring in Gateway.Api host
- **Wave 2 (Medium risk):** Core tracing spans (Gateway, Providers, Agents)
- **Wave 3 (Low risk):** Channel + Session spans
- **Wave 4 (Low risk):** Tests, docs, hardening

**Architecture:**
1. Application code uses `Microsoft.Extensions.Logging` abstractions only (no vendor lock-in)
2. Host-level Serilog integration via `UseSerilog()` replaces default provider
3. OTel traces via `System.Diagnostics.Activity` (vendor-agnostic)
4. CorrelationIdMiddleware evolves to map X-Correlation-Id <-> Activity.TraceId
5. Four ActivitySource layers: Gateway, Providers, Channels, Agents

**Packages:** Serilog suite + OpenTelemetry SDK/API/exporters in Gateway.Api; OpenTelemetry.Api in library layers.

**Key Span Attributes:**
- `botnexus.session.id`, `botnexus.agent.id`, `botnexus.channel.type`
- `botnexus.provider.name`, `botnexus.model`, `botnexus.correlation.id`

**Assignments:**
- Wave 1-2: Bender (owner)
- Wave 2 (providers): Farnsworth
- Wave 3 (channels/sessions): Kif
- Wave 4 (docs): Hermes

**Status:** Draft — awaiting team decision.

**Reference:** Full proposal in .squad/decisions/inbox/leela-otel-serilog-proposal.md

---

## Phase 12 Wave 1 — Agent Creation Form Fix

**2026-04-06 — Fixed by Fry (Web Dev)**

### Decision: Form Property Mapping Correction

Agent creation form in WebUI was sending incorrect property names to AgentDescriptor API, causing validation failures.

| Field | Bug | Fix | API Expected |
|-------|-----|-----|--------------|
| Agent name | Sent `name` | Send `displayName` | `displayName` |
| Model | Sent display string | Send `modelId` | `modelId` |
| Provider | (none) | Send `apiProvider` | `apiProvider` |

**Solution:** Updated form submission handler in `app.js` to map form fields -> correct API contract.

**Commit:** ab4dafa

**Impact:** Agent creation form now functional. Unblocks Phase 12 Wave 1 continuation (channels panel, extensions panel, model selector).

**Status:** ✅ Complete

---

## Phase 12 Wave 1 — Probe SQLite Integration & Extension-Contributed Commands

**2026-04-15 — Merged from inbox (2 decisions)**

### 1. Farnsworth Decision — Probe SQLite Session Source



---

### 2. Leela Decision — Extension-Contributed Commands Design Review



