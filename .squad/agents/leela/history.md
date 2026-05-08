# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI/Copilot), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

## 2026-05-07 — Conversation Project Extraction: Architectural Design Review

**Status:** ✅ Complete (Design Approved, Implementation Complete)  
**Session:** Conversation project refactor orchestration  
**Coordination:** With Bender (implementation), Hermes (QA), Nibbler (consistency)  

**Your Role:** Lead Architect. Reran architecture review from fresh baseline, produced comprehensive design decision.

**Decision:** Extract 3 conversation stores (InMemory, File, Sqlite) + DefaultConversationRouter from Gateway.Sessions/Gateway into dedicated BotNexus.Gateway.Conversations project. Keep contracts in Gateway.Contracts (dependency inversion), domain models in Domain. New test project: BotNexus.Gateway.Conversations.Tests.

**Key Architectural Decisions:**
- **Dependency graph:** Domain ← Contracts ← Gateway.Conversations ← Gateway (host)
- **No circular deps:** Gateway.Conversations and Gateway.Sessions are siblings
- **Contracts stay:** IConversationStore/IConversationRouter remain in Gateway.Contracts
- **Domain stays:** Conversation models remain in Domain
- **DI root unchanged:** GatewayServiceCollectionExtensions continues concrete registration

**Risk Mitigation Table:** Documented 6 risks (all LOW-MEDIUM, all mitigated including SQLite coupling, shared database file, DI registration, namespace break, router interface dependency, test helper sharing).

**Verification Checklist Provided:** Build, tests, namespace validation, project references, circular dependency checks.

**Commit:** `d8552f1f` (design decision)

---

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
- **PR #179 (2026-05-07): Memory tool surface fix — Approved deletion of MemoryStoreTool; consolidated to canonical tools (memory_save, memory_search, memory_get). Commit: 44788462**

**Carried Findings (Sprint 7B):**
- `Path.HasExtension` auth bypass in `GatewayAuthMiddleware`
- StreamAsync background task leak in providers
- SessionHistoryResponse should move to Abstractions.Models
- Monitor GatewaySession SRP — extract replay buffer if it grows further

**Phase 7 Focus:** Resilience (reconnection, pagination, queueing), channel consolidation, test hardening, observability.

---

## 2026-05-08 — OpenClaw Memory Alignment — Design Questions Resolution (Architect)

**Status:** ✅ Complete  
**Activity:** Prepared decision guide for OpenClaw Memory Alignment spec

**Context:** Six open design questions in the OpenClaw Memory Alignment spec were walked through with Sytone (Product Owner). Your role: architected decision guide, framed options, analyzed trade-offs. Sytone selected all six resolutions. Kif recorded to canonical decisions.md.

**Decisions Approved:**

1. **Daily notes format:** Plain Markdown only (no YAML frontmatter)
2. **Storage path:** Default canonical + optional per-agent override in config
3. **Embedding provider:** Optional; local + cloud behind `IEmbeddingProvider` abstraction
4. **Consolidation trigger:** Automatic schedule (cron) + manual override
5. **Index rebuild / cache:** <30s FTS rebuild; embeddings cached by content hash in SQLite
6. **AGENTS.md generation:** Minimal memory instructions (3–5 lines); details in tool descriptions

**Implications for Your Next Phase:**

- Waves 1-5 are now constrained by these decisions
- Wave 1 must use plain Markdown for note creation; no YAML injection
- Wave 3 memory search must degrade gracefully when no `IEmbeddingProvider` is registered
- Consolidation service needs both cron (automatic) and CLI trigger (manual override)

**Next Steps:** Coordinate with implementation agents (Fry, Hermes, Bender) to schedule Wave 1 kickoff.



## 2026-07-28 — Gateway Detached Process Design Review (Lead)

**Status:** ✅ Complete  
**Session:** Design Review ceremony for `improvement-gateway-detached-process`

**Your Role:** Lead/Architect. Design review facilitation, architectural decisions, wave breakdown.

**Codebase Findings:**
- CLI lives in `src/gateway/BotNexus.Cli/` using System.CommandLine + Spectre.Console
- Current `ServeCommand.cs` blocks via `Process.WaitForExitAsync()` — root cause of UX issue
- Health endpoint already exists: `GET /health` → `{"status":"ok"}` (no auth required)
- `BotNexusHome` class manages `~/.botnexus/` structure including `logs/` directory
- Serilog file sinks already configured in `Program.cs`

**Architectural Decisions:**
1. `IGatewayProcessManager` interface lives in CLI project (process management is CLI concern)
2. PID file is plain text at `~/.botnexus/gateway.pid` (single integer, no JSON)
3. Detached mode is default; `--attached` flag for foreground debugging
4. Windows-only for v1 (`OperatingSystem.IsWindows()` guard)
5. Hard kill for stop (console apps don't respond to `CloseMainWindow()`)
6. 10-second health check timeout with exponential backoff
7. Automatic stale PID cleanup on any PID file read

**Wave Breakdown:**
- Wave 1 (Bender): Core process manager + health checker — 2h
- Wave 2 (Farnsworth): CLI command refactor + DI registration — 1.5h
- Wave 3 (Hermes): Unit + integration tests (18-24 tests) — 2h (parallel with Wave 2)
- Wave 4 (Kif): Documentation + spec archive — 1h

**Key Risks Identified:**
- Cross-platform spawning differs on Linux/macOS → Windows-only v1
- Console apps on Windows don't respond to graceful shutdown signals → hard kill acceptable
- PID recycling mitigated by process name check

**Deliverables:**
- Complete design review document with interface contracts
- Decision file written to `.squad/decisions/inbox/leela-gateway-detached-process.md`
- 4-wave implementation plan with agent assignments and parallelization notes


## Learnings — Sub-Agent Completion Wake Bug (2026-07-22)

1. **InternalChannelAdapter lacks IStreamEventChannelAdapter** — `InternalChannelAdapter.cs` has `SupportsStreaming => true` but doesn't implement `IStreamEventChannelAdapter`. GatewayHost's streaming callback only forwards ContentDelta via `SendStreamDeltaAsync` (which sends `ContentDeltaPayload`), dropping MessageStart/MessageEnd/ToolStart/ToolEnd. The Blazor client expects `AgentStreamEvent` — type mismatch causes silent data loss.
2. **Race window between follow-up drain and status transition** — Between `AgentLoopRunner:224` (last follow-up drain) and `Agent.RunAsync:535` (status → Idle), IsRunning returns true but the loop won't drain again. FollowUpAsync enqueued during this window is stranded until next user message.
3. **DispatchAsync is the correct wake mechanism** — `GatewayHost.DispatchAsync` queues messages for serial processing via session-keyed channels. The session queue worker processes them through `ProcessInboundMessageAsync` which handles routing, agent execution, and session persistence. This is the same path user messages take.
4. **InternalChannelAdapter resolves session's original channel** — Uses `ResolveTargetAdapterAsync` to look up the session's ChannelType and delegate delivery to the actual adapter (SignalR, Telegram, etc.). Falls back to SignalR if session lookup fails. Design is correct; just missing the IStreamEventChannelAdapter interface.
5. **CronTrigger creates new sessions, not waking existing ones** — Despite the spec suggesting cron "wakes idle sessions," CronTrigger actually creates new ephemeral sessions per run. The only mechanism for waking existing sessions is DispatchAsync.
6. **Key file paths for sub-agent completion flow:** `DefaultSubAgentManager.OnCompletedAsync:218`, `InternalChannelAdapter.cs`, `GatewayHost.ProcessInboundMessageAsync:199`, `AgentLoopRunner.RunLoopAsync:224`, `Agent.RunAsync:420`.
7. **Decision: Always use DispatchAsync for completions** — Removing the IsRunning branch simplifies reasoning, eliminates the race, and produces cleaner UX (separate completion turns). Filed to `.squad/decisions/inbox/leela-subagent-completion-wakeup.md`.

## 2026-05-04 — Gateway Decoupling Implementation Signal from Farnsworth

**From:** Farnsworth (Platform Dev)  
**Re:** Your HIGH-priority recommendation #1 (Decouple Gateway from Extensions)  
**Status:** ✅ Implemented

Farnsworth has implemented your recommendation to decouple Gateway from compile-time extension references.

**Implementation Pattern:**
- Introduced IAgentToolContributor runtime contract in Gateway.Contracts
- Extension assemblies self-register tools via AssemblyLoadContextExtensionLoader
- Removed 4 compile-time <ProjectReference> entries (Skills, Mcp, McpInvoke, WebTools)
- InProcessIsolationStrategy now calls contributors per agent/session
- InProcessAgentHandle manages lifecycle cleanup

**Consequence:** Dependency direction restored (Extensions → Gateway.Contracts). Gateway no longer pulls extension implementations.

**What This Enables:** Your remaining recommendations (Agent.Abstractions extraction, Copilot OAuth decoupling) are now better positioned to succeed. Build is green, no new test failures.

---

## 2026-07-29 — Update Command Git Pull Cancellation Fix Review (Lead)

**Status:** ✅ APPROVED  
**Branch:** `fix/update-pull-cancel`  
**Commits reviewed:** `f8c91eb6` (tests), `dad0de5b` (fix)

**Root Cause:** `RunGitPullAsync` previously caught all exceptions with a generic handler, including `OperationCanceledException` from `WaitForExitAsync(ct)`. This surfaced as the confusing "git pull error: A task was canceled" message.

**What Changed:**
1. Introduced `GitPullResult` record struct to distinguish cancellation from genuine failures.
2. Added explicit `OperationCanceledException` catch with `proc.Kill(entireProcessTree: true)` cleanup.
3. Drains stdout/stderr via `Task.WhenAll` before checking exit code — avoids classic stream-deadlock.
4. Surfaces first non-empty stderr/stdout line as actionable failure detail instead of raw exception messages.
5. Returns exit code 130 (Unix SIGINT convention) on cancellation.

**Architecture Assessment:**
- Gateway stop/start sequencing is correct: pull fails → early return → gateway untouched.
- `GitPullResult` is appropriately scoped as a private record struct — no need for wider visibility.
- Test subclasses (`NoOpPreStopUpdateCommand`, `GitPullStepProbeCommand`) are a clean pattern for testing protected virtual methods without spawning real processes.

**Minor Note (non-blocking):** `FirstNonEmptyLine` splits on `Environment.NewLine` (`\r\n` on Windows), but git sometimes emits `\n`-only output. Would be more robust as `text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)`. Not blocking because `.Trim()` handles stray `\r` and the result is only used for diagnostic display.

**Test Coverage:** 7 tests pass. Two new tests cover the exact regression path (pre-cancel and pull-step cancel). Coverage is focused and sufficient for the fix scope.

---
---

## 2026-05-07 — OpenClaw Memory Model Architecture Assessment (Team Coordination)

**Status:** ✓ Assessment complete, merged to decisions.md

**Context:** Sytone requested BotNexus team to research OpenClaw memory model for migration compatibility.

**Your Contribution:**
- Architecture assessment: OpenClaw treats Markdown as authoritative; SQLite/embeddings are derived indexes
- Gap analysis identified 8 major divergences (primary authoring surface, file-based tools, daily notes path, daily notes loading, embeddings, memory flush, dreaming, conversation indexing, AGENTS.md, memory_get)
- Proposed 5-wave implementation plan with detailed scope and file/project impacts
- Wave 1: File-first authoring + daily load + replace memory_store
- Wave 2: File-based indexing (rebuild SQLite from MEMORY.md + memory/*.md)
- Wave 3: Embeddings hybrid search
- Wave 4: Pre-compaction memory flush
- Wave 5: Memory instructions + dreaming
- Migration path documented (backward compatibility for Wave 1, data export before Wave 2 cutover)

**Kif's Parallel Work:**
- Comprehensive research on OpenClaw's memory model and system prompts
- Documentation roadmap with priority tiers
- Design insights and recommendations

**Team Coordination:**
- Both research outputs merged into decisions.md (2026-05-07 section)
- Orchestration log created: .squad/orchestration-log/2026-05-07T15-17-40Z-memory-architecture-research.md
- Session log created: .squad/log/2026-05-07T15-17-40Z-openclaw-memory-research.md
- Inbox decision files deleted after merge

**Next Steps:**
- Implementation team to scope 5-wave plan against backlog (Wave 1-4 are priority)
- Architecture team to finalize Wave 1 contracts
- Migration strategy: export existing SQLite memories to memory/ files before Wave 2 cutover
## 2026-05-07 — OpenClaw Memory Wave 1 Alignment (Lead/Architect)

**Role:** Design leadership and gating decisions  
**Branch:** feature/openclaw-memory-alignment  
**Status:** ✅ COMPLETE — GO for merge  

**Decisions Issued:**
1. Wave 1 scope slice (contracts 1A, tool 1B, context 1C, tests 1D)
2. Architecture review of Bender's initial implementation — **REJECTED** (two blocking issues: B1 MemorySaveTool reimplemented filesystem logic, B2 dead DailyMemoryNote contract)
3. Remediation oversight — Farnsworth assigned B1/B2; delivered 58d03d13
4. Re-review of Farnsworth remediation — **APPROVED** (both issues resolved cleanly)
5. Tool scope decision (Option A: daily-note-only, MEMORY.md read-only, consolidation deferred to Wave 5)
6. Final readiness gate — **GO for merge** (all blocking issues resolved, 61 Memory tests pass, 6 Prompts tests pass, Wave 1 gateway tests pass)

**Key Outcomes:**
- All prior blockers resolved: B1 (pure delegation), B2 (dead code removed), spec contradiction (Phase 2b aligned)
- Non-blocking conditions C1–C3 carried to Wave 2 backlog (DateTime consistency, ContextFileOrdering tests, 4000-char budget)
- Full test suite feasibility confirmed (pre-existing failures only, no Wave 1 regressions)
- Strict rejection protocol enforced: Bender locked out, Farnsworth delegated remediation, all fixes validated

**Recommendation:** Squash-merge to main; archive planning spec folder.
