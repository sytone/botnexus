# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-11 Complete, Phase 12 Wave 1 Initiated.** Build green, 337 tests passing. Bender leads runtime architecture: session lifecycle, queueing, channel dispatch. Phase 12 Wave 1 assignments: Auth bypass fix (P0), WebUI channel adapter, rate limiting, correlation IDs, Telegram steering, SQLite store. Key recent: dynamic extension loader, Telegram Bot API implementation, streaming/thinking support. Active on gateway sprint: session reconnection, suspend/resume, TUI steering, bounded queueing.

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

## 2026-04-06 — Platform-config agent auto-registration + local gateway scripts
- `src\gateway\BotNexus.Gateway\Configuration\PlatformConfigAgentSource.cs` now maps `PlatformConfig.Agents` entries into `AgentDescriptor` records, loads `SystemPromptFile` from the platform config directory, skips disabled agents, and exposes no watcher (`Watch()` returns `null`).
- `src\gateway\BotNexus.Gateway\Extensions\GatewayServiceCollectionExtensions.cs` now registers platform-config and file-based agent sources together in `AddPlatformConfiguration`, so platform-config-defined agents are loaded at startup.
- Added developer entry points: `scripts\start-gateway.ps1` (build + run gateway API with `ASPNETCORE_ENVIRONMENT=Development`, `-Port`) and `scripts\dev-loop.ps1` (solution build + gateway tests + run/watch gateway).
- Key docs for onboarding this flow: `docs\sample-config.json`, `docs\sample-config.md`, and updated `docs\development-workflow.md`.

### 2026-04-06 — Gateway auth/runtime guardrails shipped
- Added `GatewayAuthMiddleware` (`src\gateway\BotNexus.Gateway.Api\GatewayAuthMiddleware.cs`) and wired it in `Program.cs` so REST + WebSocket upgrade requests hit `IGatewayAuthHandler` before routing, with explicit bypasses for `/health`, `/webui`, static assets, and `/swagger`.
- Gateway API now publishes OpenAPI metadata with XML comments and `Swashbuckle.AspNetCore`; Swagger UI is hosted at `/swagger` and uses assembly versioning for the document version.
- `DefaultAgentSupervisor` now enforces `MaxConcurrentSessions` (0 = unlimited) and throws `AgentConcurrencyLimitExceededException` for capacity breaches; `ChatController` maps that to HTTP 429.
- Isolation strategy validation now surfaces descriptor-level errors before strategy creation and reports available strategy names for fast operator diagnosis.
- `GatewayWebSocketHandler` now enforces one active socket per session and rejects duplicate `?session=` connections with close code 4409.

### 2026-04-06 — WebSocket channel pipeline + activity stream
- Added `BotNexus.Channels.WebSocket` with `WebSocketChannelAdapter : ChannelAdapterBase` and registered it in `AddBotNexusGateway`; inbound `/ws` `message` frames now dispatch through `GatewayHost.DispatchAsync` as `InboundMessage` (`ChannelType=websocket`, `TargetAgentId` pinned from query).
- Introduced optional `IStreamEventChannelAdapter` contract in gateway abstractions and updated `GatewayHost` to forward full `AgentStreamEvent` payloads when supported, preserving WebSocket protocol events (`message_start`, `thinking_delta`, `tool_*`, `message_end`, `error`) without changing other channels.
- Added dedicated `/ws/activity` endpoint (`ActivityWebSocketHandler`) for `IActivityBroadcaster` subscriptions with optional `?agent=` filtering; suitable for dashboards and runtime debugging.
- Upgraded TUI channel lifecycle with cancellable console input loop (`/clear`, `/quit`, background dispatch to gateway pipeline) so local runtime sessions can now ingest user input.

### 2026-04-06 — Cross-agent session scoping + channel capability hardening
- `src\gateway\BotNexus.Gateway\Agents\DefaultAgentCommunicator.cs` now validates cross-agent targets through `IAgentRegistry`, routes local cross-agent calls through deterministic sessions (`{source}::cross::{target}`), and uses async-local call-path tracking to reject recursive chains like `A -> B -> A`.
- `src\gateway\BotNexus.Gateway.Abstractions\Agents\IAgentCommunicator.cs` now documents local-first cross-agent behavior (remote endpoint still unsupported), including updated exception contracts.
- `src\channels\BotNexus.Channels.Tui\TuiChannelAdapter.cs` now implements `IStreamEventChannelAdapter` and renders thinking/tool/error events so `SupportsThinkingDisplay`/`SupportsToolDisplay` behavior matches runtime output.
- `src\channels\BotNexus.Channels.Telegram\TelegramChannelAdapter.cs` now explicitly declares steering/follow-up unsupported capability flags; `tests\BotNexus.Gateway.Tests\ChannelCapabilityTests.cs` and `DefaultAgentCommunicatorTests.cs` were updated to lock these contracts.

### 2026-04-06 — Sprint 7A session reconnect + queueing runtime controls
- `GatewayWebSocketHandler` now uses `IGatewayWebSocketChannelAdapter` + `ISessionStore`, sequences every outbound frame with `sequenceId`, records replay payloads in session state, and supports `{"type":"reconnect","sessionKey":"...","lastSeqId":N}` with bounded replay (`GatewayWebSocketOptions.ReplayWindowSize`, default 1000).

### 2026-04-06 — MessageCount property for session list
- Added `MessageCount` computed property to `GatewaySession` (returns `History.Count`) to fix WebUI session list showing "0 msgs" for all sessions
- The API endpoint `GET /api/sessions` now includes `messageCount` in the JSON response (System.Text.Json camelCase serialization)
- This was a simple computed property addition — no changes to session persistence, no new tests required (existing 436 Gateway tests still pass)
- Commit `830cb3f`: "Add MessageCount property to GatewaySession"
- `GatewaySession` now persists WebSocket replay state (`NextSequenceId`, `StreamEventLog`) with helper APIs (`AllocateSequenceId`, `AddStreamEvent`, `GetStreamEventsAfter`), and `FileSessionStore` now round-trips that state through `.meta.json`.
- `SessionsController` now exposes `PATCH /api/sessions/{id}/suspend` and `PATCH /api/sessions/{id}/resume` with 404/409 semantics; `GatewayHost` rejects non-active sessions before prompt execution.
- `GatewayHost` now runs per-session bounded queues (`System.Threading.Channels`) with busy backpressure responses and tracked worker lifecycle on shutdown; control metadata `control=steer` routes to `IAgentHandle.SteerAsync` instead of normal prompt flow.
- `TuiChannelAdapter` now advertises `SupportsSteering = true`, parses `/steer <message>`, dispatches steer control metadata, and prints steering acknowledgment in console output.

### 2026-04-06 — Gateway CORS policy + agent update endpoint
- Added named gateway CORS policy in `src\gateway\BotNexus.Gateway.Api\Program.cs`; development now allows any origin/method/header, while non-development uses configured origins from `gateway.cors.allowedOrigins` (or legacy root `cors.allowedOrigins`) with fallback `http://localhost:5005`.
- Placed `app.UseCors(...)` before `GatewayAuthMiddleware` so browser preflight and cross-origin requests succeed before auth middleware runs.
- Added `PUT /api/agents/{agentId}` in `AgentsController` and introduced `IAgentRegistry.Update(...)` + `DefaultAgentRegistry.Update(...)` so agent model/provider/system prompt config can be changed in place without unregister/re-register.
- Extended platform config model + validation with `CorsConfig` and URL validation for allowed origins.

### 2026-04-06 — Gateway CLI parity for platform config management
- `src\gateway\BotNexus.Cli\Program.cs` now includes `init`, `agent list/add/remove`, and `config get/set` via `System.CommandLine`, alongside existing `validate`, all honoring global `--verbose` and command-level exit codes.
- `init` now resolves paths from `PlatformConfigLoader.DefaultHomePath` + `DefaultConfigPath`, ensures `~/.botnexus` scaffold directories (`sessions`, `agents`, `logs`, `tokens`, `extensions`), and writes a minimal valid assistant config.
- Runtime pattern locked: mutate config through PlatformConfig + PlatformConfigLoader load/validate flow, then persist and revalidate to keep CLI behavior aligned with gateway validation rules.
- Key paths for follow-up runtime work: `src\gateway\BotNexus.Cli\Program.cs`, `src\gateway\BotNexus.Gateway\Configuration\PlatformConfigLoader.cs`, and `src\gateway\BotNexus.Gateway\Configuration\BotNexusHome.cs`.
- 2026-04-06: Added gateway-level dynamic extension loading contracts and an AssemblyLoadContext loader path using botnexus-extension.json manifests. Startup now discovers/loads extensions before the API host listens.

## 2026-04-06T07:50:00Z — Phase 11 Wave 1: Dynamic Extension Loading

**Status:** ✅ Complete  
**Agents:** Farnsworth (Config/Schema), Bender (Extension Loading), Hermes (Testing), Kif (Docs)

**Extension Loading Work (Bender):**
- Added IExtensionLoader interface + extension models to Gateway.Abstractions
- Implemented AssemblyLoadContextExtensionLoader with manifest discovery
- Created botnexus-extension.json manifest format
- Integrated extension loading into Gateway.Api startup
- Added Extensions config section to PlatformConfig
- Commits: 40a1588, aa7ac5e, b1aff30

**Cross-Team Results:**
- Farnsworth: Config schema generation, path resolution, CLI command
- Hermes: 23 new tests covering all new subsystems
- Kif: 14 XML doc comments, full module documentation
- **Total:** 891 tests passing (868→891, +23), Build clean, 0 warnings

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

## Learnings

### 2026-04-06 — Gateway auth bypass hardening (Path.HasExtension regression)
- Hardened GatewayAuthMiddleware.ShouldSkipAuth to a route-based allowlist (/health, /swagger*, /webui*) plus explicit static-file detection through IWebHostEnvironment.WebRootFileProvider.
- Added an API guard (/api/* never bypasses) so extension-like API routes such as /api/agents.json always require auth.
- Route/file-provider checks are safer than extension-based checks because they only bypass known public surfaces instead of trusting path shape.

### 2026-04-06 — Gateway middleware Wave 2 (auth DI + rate limit + correlation IDs)
- Refactored GatewayAuthMiddleware to take IWebHostEnvironment in the constructor and removed per-request service locator calls from static file auth bypass checks.
- Added RateLimitingMiddleware with per-client fixed-window throttling (caller identity preferred, IP fallback), default 60 requests per 60 seconds, /health bypass, and HTTP 429 + Retry-After on limit hits.
- Added CorrelationIdMiddleware that propagates or generates X-Correlation-Id, writes it to response headers, and stores it in HttpContext.Items["CorrelationId"] before downstream middleware executes.

### 2026-04-06 — Wave 3 Item 1: Rate limiter stale-entry eviction
- Added periodic stale-entry cleanup to RateLimitingMiddleware with a 60-second cleanup cadence.
- Entries are now pruned when LastAccessed is older than 2x the configured rate-limit window, preventing unbounded per-client dictionary growth.

### 2026-04-06 — Wave 3 Item 2: SQLite session store
- Added SqliteSessionStore implementing ISessionStore with auto-create tables (sessions, session_history) and JSON metadata persistence.
- Extended platform session-store config to support 	ype: Sqlite with required gateway.sessionStore.connectionString, added DI registration, and added focused gateway tests.

### 2026-04-06 — GET /api/models endpoint for WebUI
- Created ModelsController with GET /api/models endpoint to resolve WebUI 404 when fetching available LLM models.
- ModelRegistry is registered as a singleton in Program.cs and BuiltInModels.RegisterAll() populates it at startup with all models from Copilot, Anthropic, and OpenAI providers.
- Endpoint iterates all providers from ModelRegistry.GetProviders(), fetches models per provider via ModelRegistry.GetModels(provider), and returns ModelInfo DTOs with name, modelId, id (alias), and provider fields.
- Response format matches WebUI expectations (app.js line 1683 checks for name/modelId/id, line 1622 checks provider).

## Learnings

### Tool Architecture
- Created BotNexus.Tools as a shared library for reusable tool implementations
- Tools moved from coding-agent: ReadTool, WriteTool, EditTool, ShellTool, ListDirectoryTool, GrepTool, GlobTool
- PathUtils moved to BotNexus.Tools.Utils for shared path resolution logic
- IToolRegistry interface provides abstraction for tool discovery and resolution
- DefaultToolRegistry implements in-memory tool lookup with case-insensitive name matching
- Tools are registered as singletons in DI and collected via IEnumerable<IAgentTool>
- Extensions can contribute tools via IAgentTool discoverable contract
- InProcessIsolationStrategy now injects IToolRegistry to resolve tools per agent
- Agents get all tools by default if ToolIds is empty, or specific tools if configured
- AgentDefinitionConfig and PlatformConfig now support ToolIds property for tool configuration

### 2026-04-06 — OTel Wave 2 runtime spans (Gateway + AgentCore)
- Added GatewayDiagnostics (BotNexus.Gateway) and instrumented GatewayHost.ProcessInboundMessageAsync (gateway.dispatch, gateway.agent_process), DefaultMessageRouter.ResolveAsync (gateway.route with routed agent count), and DefaultAgentSupervisor.GetOrCreateAsync (gateway.agent_lifecycle).
- Added AgentDiagnostics (BotNexus.Agents) and instrumented in-process agent execution paths (agent.prompt, agent.stream) plus cross/sub-agent handoff spans in DefaultAgentCommunicator (agent.cross_call) with call-depth tagging.
- Added OpenTelemetry.Api package references to both BotNexus.Gateway and BotNexus.AgentCore; solution build + gateway tests remained green (438/438).

### 2026-04-06 — OTel Wave 3 channel + session spans
- Added `ChannelDiagnostics` (`BotNexus.Channels`) in Channels.Core and instrumented channel lifecycle (`channel.start`, `channel.stop`) plus WebSocket inbound/outbound operations (`channel.receive`, `channel.send`) with `botnexus.channel.type` and `botnexus.message.type` tags.
- Added steering trace coverage (`channel.steer`) in WebSocket message dispatch path and threaded WebSocket message type into channel inbound dispatch for consistent semantic attributes.
- Added session operation spans (`session.get`, `session.get_or_create`, `session.save`, `session.delete`, `session.list`) across in-memory/file/SQLite session stores and gateway runtime call sites, all using `botnexus.*` tags.
- Added OpenTelemetry.Api to `BotNexus.Channels.Core`; solution build and gateway tests pass after instrumentation (444/444).




### 2026-04-06T16:37:00Z — Unified Config Phase A (Provider + Agent schema)
- Expanded ProviderConfig with Enabled (default 	rue) and Models allowlist support.
- Expanded AgentDefinitionConfig with display/description metadata, AllowedModels, SubAgents, MaxConcurrentSessions, Metadata, and IsolationOptions.
- Updated PlatformConfigAgentSource mapping to flow all new fields into AgentDescriptor, including JSON object conversion for metadata/isolation options and display-name fallback to agent ID.
- Implemented PlatformConfigAgentSource.Watch() by subscribing to PlatformConfigLoader.ConfigChanged and returning a disposable subscription.
- Added AllowedModelIds to AgentDescriptor and updated gateway tests for new mapping + watch behavior.
- Validation: dotnet build Q:\repos\botnexus\BotNexus.slnx ✅; dotnet test Q:\repos\botnexus\tests\BotNexus.Gateway.Tests --no-restore --verbosity minimal ✅ (passed after retrying transient flaky test).
### 2026-04-09 — Extension loader DI safety for tools + hook handlers
- Updated `AssemblyLoadContextExtensionLoader` to skip auto-registering `IAgentTool` implementations unless they expose at least one DI-compatible constructor (interfaces/abstract deps, IServiceProvider, or optional defaults).
- Switched extension hook handler activation from `Activator.CreateInstance` to `ActivatorUtilities.CreateInstance` using a temporary service provider built from the current `IServiceCollection`, so constructor-injected hook handlers can load safely.

### 2026-04-11 — DefaultSubAgentManager core orchestration
- Added `DefaultSubAgentManager` in `src\gateway\BotNexus.Gateway\Agents\` implementing `ISubAgentManager` with in-memory `ConcurrentDictionary` tracking for sub-agent state and parent→children mappings.
- Implemented spawn/list/get/kill lifecycle with child session IDs in the format `{parentSessionId}::subagent::{uniqueId}`, ownership checks for kills, and per-session concurrent spawn limit enforcement from `GatewayOptions.SubAgents.MaxConcurrentPerSession`.
- Added background prompt execution with timeout CTS (`CancelAfter`), completion/failure/timeout status transitions, and parent delivery via `IAgentHandle.FollowUpAsync()` in `OnCompletedAsync`.
- Validation: `dotnet build Q:\repos\botnexus\src\gateway\BotNexus.Gateway\BotNexus.Gateway.csproj --verbosity quiet` ✅
### 2026-04-10 — Wave 3 sub-agent tools (spawn/list/manage)
- Implemented `SubAgentSpawnTool`, `SubAgentListTool`, and `SubAgentManageTool` in `src\gateway\BotNexus.Gateway\Tools\` following the existing `SessionTool` pattern (Tool definition schema, argument prep, execution dispatch, helper methods, and text JSON responses).
- `spawn_subagent` now builds `SubAgentSpawnRequest` with parent agent/session context and returns `{ subAgentId, sessionId, status, name }` from manager results.
- `list_subagents` returns scoped sub-agent summaries for the current parent session including status/model/turn counts and task previews; `manage_subagent` supports `status` and `kill` actions with validation.
- Validation: `dotnet build Q:\repos\botnexus\src\gateway\BotNexus.Gateway\BotNexus.Gateway.csproj --verbosity quiet` ✅

### 2026-04-11 — Phase 2: Multi-Session Client Model (zero-server-call switching)

**Status:** ✅ Complete  
**Commit:** b70d369  
**File:** `src/BotNexus.WebUI/wwwroot/app.js` — 306 insertions, 135 deletions

**What changed:**
- Added `SessionStore` and `SessionStoreManager` classes with LRU eviction (cap 20). All SignalR events route through `storeManager.routeEvent()` to per-session stores. Background session events are stored (never dropped) and badge the sidebar.
- `switchView()` is synchronous DOM re-render from cached DOM fragments. Revisiting a previously viewed agent is instant — zero server calls.
- `Connected` handler calls `SubscribeAll()` on connect and reconnect. Fallback to legacy `joinSession()` if server lacks `multiSession` capability.
- All 13 SignalR event handlers rewritten: extract `sessionId`, update store stream state, render only if active.
- `openAgentTimeline()` rewritten from 130+ lines with try/finally/safety-timer to ~100 lines. No `LeaveSession`, no `JoinSession` on switch.
- `sendMessage()` no longer blocked by `sessionSwitchInProgress`. First-message flow uses `joinSession()` → `Subscribe()`.
- `getSessionState()` routes through `SessionStore.streamState` when store exists.

**Removed:**
- `sessionSwitchInProgress` flag, `timelineSwitchVersion` counter, `joinSessionVersion` counter
- 8-second safety timer, `isEventForCurrentSession()` guard, all `LeaveSession` calls on switch
- Deprecated globals: `isStreaming`, `activeMessageId`, `activeToolCalls`, `activeToolCount`, `thinkingBuffer`, `toolCallDepth`

**Validation:** `node --check` ✅, `dotnet build --verbosity quiet` ✅ (0 errors)
### 2026-04-11 — Gateway DelayTool implementation
- Added DelayToolOptions and wired GatewayOptions.DelayTool default config (MaxDelaySeconds=1800, DefaultDelaySeconds=60).
- Implemented DelayTool (delay) with required seconds, optional eason, argument validation, clamped wait bounds, Task.Delay execution, progress update callback, and graceful cancellation result text.
- Registered delay config binding in gateway DI and added DelayTool to in-process agent tool assembly so it is available to all agents by default.
- Validation: dotnet build Q:\repos\botnexus --verbosity quiet ✅

### 2026-04-11 — Wave 2: Client-Side Infinite Scrollback (IntersectionObserver)

**Status:** ✅ Complete
**Commit:** 5ab9951
**File:** `src/BotNexus.WebUI/wwwroot/app.js` — 129 insertions, 155 deletions

**Removed:**
- `loadEarlierMessages()` — broken nuke-and-rebuild function (wiped innerHTML, lost scroll position)
- `loadOlderSessions()` — N+1 sequential session fetch function
- "Load older sessions" button + click handler in `openAgentTimeline`
- "Load earlier messages" button + click handler in `renderSessionMessages`

**Added:**
- `createSessionDividerEl(sessionId, timestamp)` — reusable divider element factory
- `renderHistoryBatch(messages, sessionBoundaries, container)` — renders messages with boundary dividers at correct positions
- `setupScrollbackObserver(channelType, agentId, initialCursor, initialHasMore)` — IntersectionObserver on sentinel element with 200px rootMargin preload
- `showTopSpinner(sentinel)` / `hideTopSpinner(sentinel)` — loading indicator management
- `showEndOfHistory(sentinel)` — "Beginning of conversation history" end state
- `_scrollbackCleanups` Map — per-view observer lifecycle management

**Changed:**
- `openAgentTimeline()` cold path — replaced multi-session fetch + per-session render with single `GET /api/channels/{channelType}/agents/{agentId}/history?limit=50` call, followed by `renderHistoryBatch` + `setupScrollbackObserver`
- `renderSessionDivider()` — refactored to use `createSessionDividerEl` helper

**Key design:**
- Cursor-based cross-session pagination (server handles session boundaries)
- Scroll-jump prevention via `scrollHeight` delta on prepend
- Single in-flight guard (`isFetching` flag) prevents concurrent fetches
- Channel-switch safety: fetch callback discards response if active view changed
- Observer cleanup per `(agentId, channelType)` key to prevent leaks

**Validation:** `node --check` ✅, `dotnet build --verbosity quiet` ✅
### 2026-04-12 — Wave 1 cleanup: shared OpenAI stream processor + tool line ending normalization
- Extracted shared streaming/parsing into `OpenAIStreamProcessor` in `BotNexus.Providers.Core.Streaming` and switched both `OpenAICompletionsProvider` and `OpenAICompatProvider` to delegate SSE parsing there.
- Moved tool-call ID sanitation/truncation primitive into `BotNexus.Providers.Core.Utilities.ToolCallIdExtensions.NormalizeToolCallId(int)` and updated provider call sites to use it.
- Eliminated duplicate `NormalizeLineEndings` implementations by adding `BotNexus.Tools.Extensions.StringExtensions.NormalizeLineEndings()` and updated `EditTool`/`ReadTool`; also trimmed `PathUtils` by removing unused `IsGitIgnored` and simplifying `GetRelativePath` signature.
- Validation: `dotnet build src\\providers\\BotNexus.Providers.Core\\BotNexus.Providers.Core.csproj`, `dotnet build src\\providers\\BotNexus.Providers.OpenAI\\BotNexus.Providers.OpenAI.csproj`, `dotnet build src\\providers\\BotNexus.Providers.OpenAICompat\\BotNexus.Providers.OpenAICompat.csproj`, `dotnet build src\\providers\\BotNexus.Providers.Anthropic\\BotNexus.Providers.Anthropic.csproj`, `dotnet build src\\tools\\BotNexus.Tools\\BotNexus.Tools.csproj` ✅ (full solution currently fails on unrelated gateway test drift already present in worktree).
### 2026-04-12 — Wave 3 runtime: sub-agent archetype identity + cron internal trigger
- Sub-agent spawns now carry SubAgentArchetype and create distinct child agent identities in the format {parent}::subagent::{archetype}::{uniqueId} while preserving ::subagent:: discoverability.
- DefaultSubAgentManager now registers per-sub-agent descriptors for runtime execution and unregisters them on completion/kill to avoid parent ID reuse.
- Cron prompt execution now routes through IInternalTrigger (CronTrigger) instead of IChannelAdapter, so cron runs bypass channel dispatch and create internal cron sessions directly.
- Validation: dotnet test tests\\BotNexus.Cron.Tests\\BotNexus.Cron.Tests.csproj and dotnet test tests\\BotNexus.Domain.Tests\\BotNexus.Domain.Tests.csproj ✅; full solution build currently blocked by concurrent typed-ID migration errors in unrelated gateway/memory files.
