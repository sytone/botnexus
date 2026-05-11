# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Summary of Prior Work (2026-04-01 to 2026-05-07):**
- **Phase 11 Extension Loading:** `IExtensionLoader` interface, `AssemblyLoadContextExtensionLoader`, `botnexus-extension.json` manifest discovery integrated into Gateway startup. Commits: 40a1588, aa7ac5e, b1aff30.
- **Telegram Bot API:** `TelegramBotApiClient` HTTP wrapper, long polling, streaming edits with markdown/thinking/tool formatting. `TelegramOptions` (BotToken, polling intervals). SupportsStreaming=true for buffered delta flushing. 3 commits: d5035ab, a8f71a5, 4b2bffd.
- **WebSocket Channel + Activity Stream:** `BotNexus.Channels.WebSocket` adapter with /ws dispatch. `IStreamEventChannelAdapter` contract. `/ws/activity` endpoint with `IActivityBroadcaster` subscriptions.
- **Session Lifecycle & Control:** `GatewayWebSocketHandler` (one socket per session, sequence tracking, bounded replay). `SessionsController` PATCH suspend/resume. Per-session bounded queues via `System.Threading.Channels`. `TuiChannelAdapter` steering.
- **MCP Server Graceful Initialization (P0 Fix):** Per-server try/catch in `McpServerManager.StartServersAsync`, skip failed servers with warnings. MCP servers are optional dependencies.
- **Cross-Agent Session Scoping:** `DefaultAgentCommunicator` with registry validation, deterministic session IDs, async-local recursion prevention. `TuiChannelAdapter` event rendering.
- **Phase 12 Sub-Agent Foundation (W2+W3):** `DefaultSubAgentManager` orchestrator. `SubAgentSpawnTool`, `SubAgentListTool`, `SubAgentManageTool` with recursion prevention, timeout enforcement, ownership checks.
- **Active Stream:** Multi-session WebUI model, session store + manager (LRU eviction), SignalR event handler rewrites, paginated history.
**Phases 1-11 Complete, Phase 12 Wave 1 Initiated.** Build green, 337 tests passing. Bender leads runtime architecture: session lifecycle, queueing, channel dispatch. Phase 12 Wave 1 assignments: Auth bypass fix (P0), WebUI channel adapter, rate limiting, correlation IDs, Telegram steering, SQLite store. Key recent: dynamic extension loader, Telegram Bot API implementation, streaming/thinking support. Active on gateway sprint: session reconnection, suspend/resume, TUI steering, bounded queueing.
- **PR #179 (2026-05-07): Memory tool surface fix — Removed MemoryStoreTool from gateway production exposure. Consolidated to memory_save, memory_search, memory_get per AGENTS.md. Commit: 7a4785a6**

---

## Session History

**Phase 11 Extension Loading:**
- IExtensionLoader interface, AssemblyLoadContextExtensionLoader, botnexus-extension.json manifest discovery integrated into Gateway startup
- Commits: 40a1588, aa7ac5e, b1aff30

**Telegram Bot API (Bender):**
- TelegramBotApiClient HTTP wrapper, long polling, streaming edits with markdown/thinking/tool formatting
- TelegramOptions (BotToken, polling intervals); SupportsStreaming=true for buffered delta flushing
- 3 commits: d5035ab, a8f71a5, 4b2bffd

**WebSocket Channel + Activity Stream:**
- BotNexus.Channels.WebSocket adapter with /ws message dispatch pipeline (ChannelType=websocket, TargetAgentId pinned from query)
- IStreamEventChannelAdapter contract for WebSocket protocol events (message_start, thinking_delta, tool_*, message_end, error)
- Dedicated /ws/activity endpoint (ActivityWebSocketHandler) for IActivityBroadcaster subscriptions with optional ?agent= filtering

**Session Lifecycle & Control:**
- GatewayWebSocketHandler: one active socket per session, reject duplicate ?session= connections (close code 4409), sequence ID tracking, bounded replay (ReplayWindowSize=1000)
- SessionsController: PATCH /api/sessions/{id}/suspend/resume (404/409 semantics)
- GatewayHost: per-session bounded queues (System.Threading.Channels), busy backpressure responses, control metadata routing (control=steer → IAgentHandle.SteerAsync)
- TuiChannelAdapter: /steer user input parsing, steering acknowledgment

**MCP Server Graceful Initialization (P0 Fix):**
- McpServerManager.StartServersAsync: try/catch per-server initialization, skip failed servers with warning logs, success logging per server
- Key insight: MCP servers are optional dependencies — session succeeds even if GitHub MCP auth fails

**Cross-Agent Session Scoping:**
- DefaultAgentCommunicator: registry validation, deterministic session IDs ({source}::cross::{target}), async-local call-path tracking rejects recursive chains
- TuiChannelAdapter: IStreamEventChannelAdapter implementation, thinking/tool/error event rendering

---

## 2026-05-07T22:05:56Z — Gateway Runtime Rewire via Dispatcher (Runtime Dev)

**Status:** ✅ Complete  
**Session:** Dispatching Cleanup Wave — Phase 2 runtime integration  

**Implementation:**
- Rewired `GatewayHost` and `GatewayHub` inbound resolution to use `IConversationDispatcher`
- Hub now delegates to dispatcher via `ResolveOrCreateSessionAsync`, preserves router for lifecycle (disconnect/mute)
- Outbound metadata routing: `BindingId`, `ThreadId`, `DisplayPrefix` preserved in response pipeline
- **Result:** 44/44 gateway tests passing + Conversations.Tests passing

**Key Design:** Keep `IConversationRouter` in `GatewayHub` (only for `MuteBindingByAddressAsync` on disconnect). Lifecycle operations orthogonal to pure dispatch resolution — cleaner separation.

**Cross-team Context:**
- Farnsworth created dispatcher contracts + adapter ✅
- Hermes expanded routing regression coverage (42/42 ✅)
- Leela approved phase 2 cleanup (✅ APPROVED)

---

## Learnings

### 2026-04-10 — MCP server graceful initialization (P0 bug fix)
- Fixed critical bug in `McpServerManager.StartServersAsync` where ANY single server initialization failure (timeout, auth error, process crash) would kill the entire agent session creation.
- Added `ILogger` parameter to `McpServerManager` constructor (optional, defaults to `NullLogger`) and injected from `InProcessIsolationStrategy` via `ILoggerFactory`.
- Wrapped per-server initialization in try/catch blocks: timeout exceptions, general initialization exceptions, and transport creation failures are now logged as warnings and the failed server is skipped.
- Added success logging: `LogInformation` when a server initializes successfully (includes server ID and tool count).
- Key insight: MCP servers are optional dependencies — if one fails (e.g., GitHub MCP missing `GITHUB_TOKEN`), other servers and the agent session itself should still succeed.
- Real-world impact: Cron jobs that don't need GitHub tools were failing because the GitHub MCP server couldn't auth → now they succeed with a warning log.
- Files modified: `extensions\mcp\BotNexus.Extensions.Mcp\McpServerManager.cs`, `src\gateway\BotNexus.Gateway\Isolation\InProcessIsolationStrategy.cs`
- Tests: All 148 MCP tests pass, Gateway isolation tests pass, build clean.

### 2026-04-10T16:30Z — Sub-Agent Spawning Feature: Wave 2 + Wave 3 (Runtime Dev)

**Status:** ✅ Complete  
**Commits:** ff63957 (W2), 4d4b6a7 (W3 tools)

**Your Role:** Runtime Dev. Wave 2 manager implementation, Wave 3 tooling.

**Wave 2 Deliverables:**
- `DefaultSubAgentManager` orchestrator
  - `SpawnAsync()` — create child session via `IAgentSupervisor`, manage timeout/maxTurns via `CancellationTokenSource`
  - `ListAsync()` — query active sub-agents by parent session ID
  - `KillAsync()` — terminate sub-agent with ownership validation
  - `OnCompletedAsync()` — completion delivery pipeline
  - Parent→child map tracking with `ConcurrentDictionary`
- `SubAgentCompletionHook` — detects session completion, extracts summary, calls `OnCompletedAsync()`
- Concurrent session limits enforced per agent config
- Recursion prevention via `MaxDepth` + depth tracking

**Wave 3 Tool Deliverables:**
- `SubAgentSpawnTool` (IAgentTool) — spawn with model/tool/prompt overrides
- `SubAgentListTool` (IAgentTool) — list sub-agents for calling session
- `SubAgentManageTool` (IAgentTool) — kill + status actions with ownership checks
- Tool registration in `InProcessIsolationStrategy` with recursion-prevention gating

**Safety:**
- Timeout enforcement via `CancellationToken`
- MaxTurns enforced at agent loop start
- Tool allowlist validation against registry
- Ownership checks: only parent can kill/manage children
- Orphaned session cleanup on parent deletion

---

### 2026-04-06 — Telegram Bot API adapter runtime implementation
- Replaced Telegram channel stub with first-party runtime calls (`sendMessage`, `editMessageText`, `getUpdates`, `setWebhook`, `deleteWebhook`) via `HttpClient` + `System.Text.Json`.
- Added polling-mode inbound routing with update-offset tracking and webhook-mode startup path; inbound/outbound paths now enforce `AllowedChatIds`.
- Enabled streaming behavior (`SupportsStreaming = true`) with buffered delta flushing into `editMessageText`, plus thinking/tool rendering formatting and markdown-safe chunked sends.

## 2026-04-06T08:10:00Z — Phase 11 Wave 2: Telegram Bot API Implementation & CLI Decomposition

**Status:** ✅ Complete  
**Team:** Bender (Telegram API), Farnsworth (CLI), Scribe (Logging)  
**Orchestration:** `.squad/orchestration-log/2026-04-06T08-10-00Z-bender.md`  

**Your Contribution (Bender — Telegram Bot API):**
- Created TelegramBotApiClient HTTP wrapper for Telegram Bot API
- Implemented long polling with offset tracking and retry logic
- Implemented send with markdown, chunking, streaming edits
- Added thinking/tool message formatting support
- Extended TelegramOptions with BotToken, polling intervals (MinWait, MaxWait)
- 3 commits: d5035ab, a8f71a5, 4b2bffd

**Team Outcomes:**
- **Farnsworth (CLI):** Decomposed Program.cs (767→23 lines), extracted Commands/ structure (ValidateCommand, InitCommand, AgentCommands, ConfigCommands). 5 commits.
- **Scribe (Memory):** Wave 1 orchestration logs + session log + decision merging + cross-agent history updates. 1 commit.

**Combined Validation:**
- Build: ✅ Clean, 0 errors, 0 warnings
- Tests: ✅ 891 passing, 0 failures
- Smoke: ✅ CLI help, Telegram polling validation

**Session Log:** `.squad/log/2026-04-06T08-10-00Z-phase11-wave2.md`

## Recent Learnings (2026-04-10+)

### 2026-04-10 — MCP server graceful initialization (P0 bug fix)
- Fixed critical bug in McpServerManager.StartServersAsync where ANY single server initialization failure would kill the entire agent session creation.
- Added ILogger parameter, wrapped per-server initialization in try/catch, skip failed servers with warning logs.
- Key insight: MCP servers are optional dependencies — session succeeds even if GitHub MCP auth fails.

### 2026-04-11 — Phase 2: Multi-Session Client Model (WebUI)
- Added SessionStore and SessionStoreManager (LRU eviction, cap 20), synchronous DOM re-render switchView(), SubscribeAll() on connect.
- All 13 SignalR event handlers rewritten: extract sessionId, update store stream state, render only if active.

### 2026-01-21 — Wave 1: Gateway Process Manager Implementation
- Created core process management infrastructure in `src/gateway/BotNexus.Cli/Services/`:
  - `IGatewayProcessManager`, `GatewayProcessManager` — lifecycle management (start/stop/status)
  - `IHealthChecker`, `HttpHealthChecker` — exponential backoff health polling (200ms → 2000ms, 10s timeout)
  - `GatewayProcessTypes` — supporting types (StartOptions, Results, State, Status)
- PID file: `~/.botnexus/gateway.pid` (plain text integer)
- Detached mode: new console window via `UseShellExecute = true`
- Windows-only for v1: `OperatingSystem.IsWindows()` guard
- Stale PID cleanup: automatic on read, PID recycling detection via process name check
- Hard kill: `Process.Kill()` with 5s exit wait
- Health endpoint discovered: `http://localhost:5005/health` (no auth, 200 OK with `{"status":"ok"}`)
- **ServeCommand structure learned:**
  - Gateway DLL: `{repoRoot}/src/gateway/BotNexus.Gateway.Api/bin/Release/net10.0/BotNexus.Gateway.Api.dll`
  - Foreground loop: build → spawn → wait for exit → 5s restart prompt
  - Auto-initializes config via `InitCommand` if missing
  - Deploys extensions to `~/.botnexus/extensions/`
  - Default port: 5005, env: Development
- Commit: `e8b81299` (Wave 1 complete, ready for Wave 2 integration by Farnsworth)


### 2026-04-20 — Wave 3 sub-agent completion race fix
- Removed `IsRunning`/`FollowUpAsync` branching in `DefaultSubAgentManager.OnCompletedAsync`; parent wake now always uses `_dispatcher.DispatchAsync` so completion delivery is serialized via the session queue.
- Added wake-delivery telemetry in `GatewayTelemetry`: `SubAgentWakeDispatched` and `SubAgentWakeDeliveryFailed`, and instrumented success/failure paths in `OnCompletedAsync`.
- Updated sub-agent wake tests to reflect always-dispatch semantics and verified `SubAgentCompletionWake` test suite passes (8/8).

## 2026-05-07T01:07:16Z — Issue #24 Tool Timeout Configuration: Runtime Implementation (Runtime Dev)

**Status:** ✅ Complete — Implementation Ready for Merge  
**Role:** Runtime Dev / Implementation Engineer  
**PR:** https://github.com/sytone/botnexus/pull/177

**Scope:** Multi-agent Phase 1 delivery. Bender routed to wire configurable timeouts from platform config through descriptor metadata into agent runtime options.

**Your Work:**
1. **Config Model Extension:**
   - Added nullable int? fields to AgentDefinitionConfig.ToolTimeoutSeconds
   - Added nullable int? fields to AgentDefaultsConfig.ToolTimeoutSeconds
   - Implements backward-compatible zero-config defaults

2. **Merger & Inheritance:**
   - Added MergeToolTimeoutSeconds() to AgentConfigMerger
   - Follows existing merge pattern: explicit agent value → fallthrough to defaults
   - Consistent with MergeToolIds precedent

3. **Descriptor Metadata Flow:**
   - Updated PlatformConfigAgentSource to extract timeout from config
   - Inline gents.defaults fallback for test configs bypassing deserialization
   - Metadata key: Metadata["toolTimeoutSeconds"]

4. **Runtime Strategy Implementation:**
   - Added ResolveToolTimeout() in InProcessIsolationStrategy
   - TryConvertPositiveSeconds() defensive conversion for JSON types (int, long, double, string, JsonElement)
   - Invalid values logged (Warning) + graceful fallback to AgentCore 120s default
   - Debug log on successful application with agent ID and seconds

5. **Test Coverage (Commit ff0ce8cc):**
   - 3 regression tests: per-agent config, defaults inheritance, timeout event emission
   - All 24 issue-related tests passing (targeted + full AgentCore/Gateway suite)
   - Full build green during implementation

**Key Decisions Implemented:**
- Metadata-bag approach minimizes descriptor contract surface expansion
- Defensive JSON conversion handles all realistic types
- Logging structured for runtime diagnostics

**Orchestration Log:** .squad/orchestration-log/2026-05-07T01-07-16Z-bender.md

### 2026-05-07 — CLI update git-pull cancellation handling
- UpdateCommand.RunGitPullAsync now drains redirected stdout/stderr while waiting, preventing git pull deadlocks when verbose output is off.
- Added explicit cancellation handling: cancelled pulls return exit code 130, kill the git process tree best-effort, and skip gateway stop/start.
- Failure output now surfaces the first useful stderr/stdout line instead of only A task was canceled..
- Tests: tests/BotNexus.Cli.Tests/Commands/UpdateCommandTests.cs adds cancellation coverage for ExecuteAsync.
### 2026-05-07 — SignalR conversation routing fix (Phase 1)
- Fixed `GatewayHub.SendMessageCore` to normalize and pass client `conversationId` into `ResolveOrCreateSessionAsync`, so session resolution uses the same conversation context as dispatch.
- Updated `GatewayHub.ResolveOrCreateSessionAsync` to forward `conversationId` into `IConversationRouter.ResolveInboundAsync` instead of always forcing `null`.
- Result: non-default conversations now resolve to their own session at hub time, so SignalR group subscription and gateway routing stay aligned.


### 2026-05-07 — Phase 1 Bug Fix: SignalR Conversation Routing

- **Task:** Implement conversation routing fix per design review — pass client conversationId through GatewayHub into session resolution
- **Changes:**
  - GatewayHub.SendMessageCore: normalize optional conversationId, pass to ResolveOrCreateSessionAsync
  - GatewayHub.ResolveOrCreateSessionAsync: accept conversationId parameter, forward to IConversationRouter.ResolveInboundAsync (instead of always 
ull)
- **Result:** Non-default conversations now resolve to target session at hub time; SignalR group subscription aligns with gateway routing
- **Test Status:** Targeted SignalR routing tests pass; full suite blocked by unrelated baseline failures (pre-existing)
- **Risk:** LOW — existing conversation router path used by GatewayHost; we're just exposing it to GatewayHub


### 2026-05-07 — Dispatcher rewire slice (GatewayHost + SignalR Hub)
- Rewired inbound session/conversation resolution in GatewayHost to use IConversationDispatcher.DispatchAsync(InboundMessageContext) and removed direct ResolveInboundAsync usage from runtime processing.
- Preserved outbound routing metadata (BindingId, ThreadId, DisplayPrefix) via dispatcher ChannelSource so replies and stream routing keep thread/topic affinity.
- Rewired GatewayHub session resolution to call IConversationDispatcher instead of directly calling IConversationRouter.
- Kept IConversationRouter in GatewayHub only for disconnect-time binding muting (MuteBindingByAddressAsync) since that operation is outside inbound dispatch resolution.
- Validation: dotnet build BotNexus.slnx --nologo --tl:off passed; targeted tests for GatewayHostTests, SignalRHubTests, and SignalRThreadRoutingTests passed (44/44).

### 2026-05-07 — MCP stdio send error normalization
- `StdioMcpTransport` now checks process exit before writes and normalizes `IOException`/`ObjectDisposedException` into clear `InvalidOperationException` messages with exit code context.
- Windows-specific `cmd echo` behavior made the stderr/stdout robustness test brittle; switched that test helper to PowerShell stdout/stderr emission for deterministic JSON output.
## 2026-05-07 — OpenClaw Memory Wave 1 Initial Implementation (Runtime Implementation)

**Role:** Runtime seams mapping and initial Wave 1 delivery  
**Branch:** feature/openclaw-memory-alignment  
**Status:** REJECTED (blocking issues B1, B2) → Remediated by Farnsworth  

**Initial Implementation (Commits e21e9e38, 494804c8):**
- Wave 1 contracts fully implemented (1A): memory_save tool, context loading, config mapping
- Runtime tool delivery (1B): memory_save with daily-note routing
- Gateway context wiring (1C): MEMORY.md + recent notes integration
- Test coverage (1D, partial): 739 new lines across 9 files

**Architecture Assessment (Leela Review):**
- ✅ Scope correctness: No premature Wave 2/3 abstractions
- ✅ Path safety: EnsureWithinRoot guard, cross-platform handling, traversal validation
- ❌ **B1 (HIGH severity):** MemorySaveTool reimplemented filesystem logic instead of delegating through workspace manager (DIP + DRY violation)
- ❌ **B2 (HIGH severity):** Daily note loading duplicated with behavioral divergence; DailyMemoryNote + RecentMemoryNotes are dead code

**Rejection Outcome (Leela's leela-memory-wave1-review.md):**
- **REJECT** — both blocking issues require remediation before merge
- Strict lockout protocol applied: Bender cannot revise rejected work
- Remediation delegated to Farnsworth

**Contribution Legacy:**
- Initial scope was correct for Wave 1
- Path safety design and traversal validation concepts carried forward to remediation
- Foundation for Farnsworth's clean fix (58d03d13)

**Learning:** Strict rejection protocol ensures architectural rigor — rejected work delegates to fresh implementer, prevents scope drift from remediation.

### 2026-05-07 — PR #181 mainline merge refresh
- Synced `fix/update-pull-cancel` with `origin/main` in dedicated worktree `Q:\repos\botnexus-pr-181`; merge applied cleanly with no content conflicts.
- Verified compile baseline after merge (`dotnet build BotNexus.slnx --nologo --tl:off`) before push.
---

## 2026-05-07 — Conversation Project Extraction: Implementation

**Status:** ✅ Complete (Leela design → Bender implementation → Hermes QA → Nibbler consistency → PR merged)  
**Session:** Conversation project refactor orchestration  
**Coordination:** With Leela (design), Hermes (QA), Nibbler (consistency)  

**Your Role:** Runtime Developer. Implemented Leela's architectural decision.

**Deliverables:**
- Created `src\gateway\BotNexus.Gateway.Conversations` project structure
- Created `tests\BotNexus.Gateway.Conversations.Tests` test project
- Moved 4 runtime classes from Gateway.Sessions/Gateway:
  - InMemoryConversationStore
  - FileConversationStore
  - SqliteConversationStore
  - DefaultConversationRouter
- Moved 7 conversation-focused test files from Gateway.Tests
- Updated all namespaces to `BotNexus.Gateway.Conversations`
- Updated project references in solution
- Updated DI wiring in `GatewayServiceCollectionExtensions`
- Added shared `TestOptionsMonitor` to new test project via linked compile include

**Validation:**
- Full solution build: ✅ Green (0 errors)
- Targeted validation passed: Leela's verification checklist complete
- Test count: BotNexus.Gateway.Conversations.Tests 66/66 passing
- No circular project dependencies
- Dependency graph correct

**Commit:** `1aca5967` (implementation complete)

**PR:** https://github.com/sytone/botnexus/pull/178

---

## Learnings
### 2026-05-08 — Extension-loading hosts need dispatcher fallback for lazy hub activation
- SignalR GatewayHub activates lazily when a client connects, so missing IConversationDispatcher may hide until runtime even if extension load succeeds.
- Registering IConversationDispatcher -> DefaultConversationDispatcher in AddExtensionLoading() creates a safe fallback for hosts that compose extension loading outside AddBotNexusGateway().

### 2026-05-08 — Conversation history refresh must page from newest entries
- Root cause for "message disappears after refresh" was pagination anchored to oldest entries in `ConversationsController.GetHistory`; with long histories (>200), refresh loaded only old turns and omitted latest user/assistant turns.
- Fix pattern: keep chronological order inside each page, but compute the page window from the tail (`offset` from newest) so `limit=200&offset=0` always includes the newest turns.
### 2026-05-11 — Conversation cleanup semantics preserve reactivation
- Conversation delete/cleanup is implemented as archive/close semantics, not hard-delete.
- Archiving now clears ActiveSessionId so the next inbound trigger starts a fresh session instead of reusing stale runtime state.
- Router reopens archived conversations when a bound channel speaks again (or explicit conversationId is used), preserving multi-channel bindings and enabling cron/channel resumes after cleanup.

### 2026-05-11 — Cleanup/archive must seal active sessions, not delete persisted history
- Updated conversation cleanup flow to always call conversation archive and stop routing cron cleanup through session deletion.
- `DELETE /api/conversations/{id}` now seals the active session record in place (status = Sealed) before archiving the conversation, preserving historical session records for history APIs while still removing the conversation from active lists.
- Added regression coverage that archived conversations keep session records and still hide/reopen correctly on new inbound activity.

---

## 2026-05-11 — Conversation Cleanup: Archive/Close Recoverability & Session Linkage

**Status:** ✅ Delivered  
**Commit:** 90c6f955 `fix(gateway): reopen archived conversations after cleanup`  
**Team Coordination:** Fry (UI), Hermes (tests)

**Runtime Semantics Delivered:**
1. DELETE /api/conversations/{id} now treated as close/archive (not hard-delete)
2. Close operation clears ActiveSessionId for fresh session creation on next activity
3. Archived conversations reopen automatically when:
   - Inbound activity matches an existing channel binding
   - Conversation explicitly addressed by ID
4. Channel bindings preserved across close/reopen cycle (critical for cron continuity)

**Cross-Agent Alignment:**
- Fry's UI close button uses same API with appropriate messaging
- Hermes wrote comprehensive tests for session cleanup and binding preservation
