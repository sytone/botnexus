# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-11 Complete, Phase 12 Wave 1 Initiated.** Build green, 337 tests passing. Bender leads runtime architecture: session lifecycle, queueing, channel dispatch. Phase 12 Wave 1 assignments: Auth bypass fix (P0), WebUI channel adapter, rate limiting, correlation IDs, Telegram steering, SQLite store. Key recent: dynamic extension loader, Telegram Bot API implementation, streaming/thinking support. Active on gateway sprint: session reconnection, suspend/resume, TUI steering, bounded queueing.

---

## Archived Entries (2026-04-06 to 2026-04-10)

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
