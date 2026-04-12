# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-11 Complete, Phase 12 Wave 1 Initiated.** Build green, 337 tests passing. Farnsworth owns platform configuration, session store, cross-agent guardrails. Phase 12 Wave 1 assignments: 3 API endpoints (type move, channels, extensions), CLI command decomposition, config schema + path resolver. Key recent: config versioning, dynamic extension loader foundation, Telegram setup. Active on gateway sprint: session store abstraction, cross-agent timeout, history pagination.

---

## 2026-04-06T01:45Z — Gateway Phase 6 Batch 1: Dev-Loop Reliability + Cross-Agent Coordination

**Status:** ✅ Complete. Commit: 974d91c

**Deliverables:**
- Standardized `dev-loop.ps1` → `start-gateway.ps1 -SkipBuild` flow to eliminate duplicate Gateway builds and file-lock failures
- TCP port pre-check in `start-gateway.ps1` for fail-fast behavior on port collisions
- `-SkipBuild` and `-SkipTests` flags for faster iterative loops
- Configuration validation for end-to-end gateway startup
- Sample config file for local testing

**Cross-Agent Notes:**
- **Fry's WebUI** now separates activity feed to dedicated `ws://host/ws/activity` WebSocket endpoint
  - **Action Required:** Ensure `/ws/activity` endpoint is available and serves activity events independently of main `/ws`
  - Activity feed will silently fail/retry if endpoint unavailable; monitor logs
- **Fry's new message type:** WebUI sends `{"type": "follow_up", "content": "..."}` for queued messages during streaming
  - **Action Required:** Ensure Gateway/runtime handles `follow_up` message type alongside existing `steer` type
- **Hermes' tests** now validate both endpoints (main WebSocket + activity WebSocket) end-to-end with LiveGatewayIntegrationTests

**Owner Review Required:** Squad should not implement follow-on provider changes without explicit approval.


- 2026-04-06: Gateway config mutation logic moved from CLI inline reflection into IConfigPathResolver + ConfigPathResolver, adding bracket array index support (path[0]) and reusable path discovery for DI consumers.
- 2026-04-06: Platform config load now runs JSON Schema validation via PlatformConfigSchema with key-casing normalization before existing manual validation, and CLI exposes otnexus config schema --output ... to regenerate docs/botnexus-config.schema.json.

## 2026-04-10T16:30Z — Sub-Agent Spawning Feature: Waves 1 + 2 + 4 (Platform Dev)

**Status:** ✅ Complete  
**Commits:** f57b157 (W1), 25c8876 (W2+3 DI), c75a033 (W4 REST)

**Your Role:** Platform Dev. Wave 1 abstractions, DI wiring, REST endpoints.

**Wave 1 Deliverables:**
- `ISubAgentManager` abstraction in `BotNexus.Gateway.Abstractions`
- `SubAgentSpawnRequest`, `SubAgentInfo`, `SubAgentStatus` models
- `SubAgentOptions` configuration class (maxConcurrentPerSession, defaultMaxTurns, etc.)
- Integrated with existing session infrastructure (reuses `IAgentSupervisor`, session ID format preserved)

**Wave 2+3 DI Work:**
- `DefaultSubAgentManager` registered as singleton in DI
- Sub-agent tool registration in `InProcessIsolationStrategy`
- Recursion prevention wired: `spawn_subagent`, `list_subagents`, `manage_subagent` excluded from sub-agent sessions
- Tool stack depth tracking for safety

**Wave 4 REST Endpoints:**
- `GET /api/agents/sub` — list active sub-agents
- `POST /api/agents/sub` — spawn sub-agent
- `DELETE /api/agents/sub/{id}` — kill sub-agent
- WebSocket event emission: `subagent_spawned`, `subagent_completed`, `subagent_failed`

**Integration:**
- Tool security scoping: explicit allowlist validation against registry
- Completion delivery: reuses existing `FollowUpAsync` message queue
- Resource protection: `maxConcurrentPerSession` enforced per agent descriptor

---

## 2026-04-06T07:50:00Z — Phase 11 Wave 1: Config Schema & Path Resolution

**Status:** ✅ Complete  
**Agents:** Farnsworth (Config/Schema), Bender (Extension Loading), Hermes (Testing), Kif (Docs)

**Config & Schema Work (Farnsworth):**
- Extracted IConfigPathResolver/ConfigPathResolver from CLI reflection logic
- Added JSON schema generation via PlatformConfigSchema
- Integrated schema validation into PlatformConfigLoader
- Added otnexus config schema CLI command
- Generated docs/botnexus-config.schema.json
- Refactored CLI config get/set to use ConfigPathResolver via DI
- Commit: e57eae1

**Cross-Team Results:**
- Bender: Dynamic extension loading (IExtensionLoader, manifest discovery)
- Hermes: 23 new tests (ConfigPathResolver, SchemaValidation, edge cases)
- Kif: 14 XML doc comments, 3 module READMEs, 0 warnings
- **Total:** 891 tests passing (868→891, +23), Build clean, 0 warnings

## 2026-04-06T08:35Z — CLI Command Handler Decomposition

Refactored `src/gateway/BotNexus.Cli/Program.cs` into thin command registration + DI wiring and extracted command handlers into `Commands/ValidateCommand.cs`, `Commands/InitCommand.cs`, `Commands/AgentCommands.cs`, and `Commands/ConfigCommands.cs`.
Behavior parity preserved for `validate`, `init`, `agent list|add|remove`, and `config get|set|schema` (same options, output, and exit code behavior).
Validation: `dotnet build src\gateway\BotNexus.Cli\BotNexus.Cli.csproj --nologo --tl:off`, CLI smoke checks (`--help`, `config schema`), and full-solution build succeeded; full-solution tests showed one existing flaky timing failure in `ToolExecutorTests.ExecuteAsync_ParallelMode_RunsConcurrently`.

## 2026-04-06T08:10:00Z — Phase 11 Wave 2: CLI Decomposition & Command Extraction

**Status:** ✅ Complete  
**Team:** Bender (Telegram API), Farnsworth (CLI), Scribe (Logging)  
**Orchestration:** `.squad/orchestration-log/2026-04-06T08-10-00Z-farnsworth.md`  

**Your Contribution (Farnsworth — CLI Refactoring):**
- Decomposed Program.cs from 767 lines to 23 lines (thin registration + DI wiring)
- Created Commands/ directory structure with:
  - ValidateCommand (static validation handler)
  - InitCommand (config initialization)
  - AgentCommands (list/add/remove agent operations)
  - ConfigCommands (get/set/schema config operations)
- Preserved behavior parity — all CLI commands work identically to pre-refactor
- 5 commits: c5ec538, 04e89f0, 23519ac, 4fc1a39, ac88300

**Team Outcomes:**
- **Bender (Telegram):** TelegramBotApiClient HTTP wrapper, long polling, streaming edits, thinking/tool formatting. 3 commits.
- **Scribe (Memory):** Wave 1 orchestration logs + session log + decision merging + cross-agent history updates. 1 commit.

**Combined Validation:**
- Build: ✅ Clean, 0 errors, 0 warnings
- Tests: ✅ 891 passing, 0 failures  
- CLI Smoke: ✅ --help, config schema, init validation

**Session Log:** `.squad/log/2026-04-06T08-10-00Z-phase11-wave2.md`

### 2026-04-06T09:40Z — Gateway API metadata endpoints
- Added GET /api/channels via ChannelsController, returning ChannelAdapterResponse with { Name, DisplayName, IsRunning, SupportsStreaming, SupportsSteering, SupportsFollowUp, SupportsThinking, SupportsToolDisplay } mapped from IChannelManager.Adapters.
- Added GET /api/extensions via ExtensionsController, returning ExtensionResponse rows with { Name, Version, Type, AssemblyPath } from IExtensionLoader.GetLoaded() (one row per declared extension type).
- Moved SessionHistoryResponse into BotNexus.Gateway.Abstractions.Models for reuse across API/test surfaces.
- 2026-04-06: Gateway Wave 2 aligned SupportsThinkingDisplay naming in channel DTOs, added session metadata GET/PATCH endpoints with null-removal merge semantics, and introduced config ersion warning handling for schema evolution.

## 2026-04-06T23:32:48Z — Phase B: Agent Directory Restructure

- Updated BotNexusHome scaffolding to create workspace/ and data/sessions/ per agent.
- Added legacy auto-migration in GetAgentDirectory() via MigrateLegacyWorkspace() to move flat SOUL.md/IDENTITY.md/USER.md/MEMORY.md files into workspace/.
- Updated FileAgentWorkspaceManager.GetWorkspacePath() to return {agentDir}/workspace.
- Reviewed WorkspaceContextBuilder: no direct file path logic; it continues to work through IAgentWorkspaceManager.
- Updated Gateway tests for new layout and migration behavior (BotNexusHomeTests, FileAgentConfigurationWriterTests).
- Validation: dotnet build Q:\repos\botnexus\BotNexus.slnx ✅ and dotnet test Q:\repos\botnexus\tests\BotNexus.Gateway.Tests --no-restore --verbosity minimal ✅ (452 passed).

## 2026-04-09T13:37Z — Web Tools Extension: WebFetchTool and WebSearchTool

**Status:** ✅ Complete

**Deliverables:**
- **WebFetchTool** (`web_fetch`): Fetches URLs and returns content as readable text or raw HTML
  - Schema: `url` (required), `max_length` (default: 5000, max: 20000), `raw` (default: false), `start_index` (default: 0)
  - Uses lightweight regex-based HTML-to-text conversion (no external dependencies)
  - HtmlToText utility: Strips script/style/nav/footer/header tags, converts block elements to newlines, converts links to markdown format `[text](url)`, decodes HTML entities, normalizes whitespace
  - Pagination support via `start_index` with truncation messages
  - Configurable timeout and User-Agent from `WebFetchConfig`

- **WebSearchTool** (`web_search`): Multi-provider web search with formatted markdown results
  - Schema: `query` (required)
  - Three provider implementations via `ISearchProvider` interface:
    - **BraveSearchProvider**: GET `https://api.search.brave.com/res/v1/web/search` with `X-Subscription-Token` header
    - **TavilySearchProvider**: POST `https://api.tavily.com/search` with API key in body
    - **BingSearchProvider**: GET `https://api.bing.microsoft.com/v7.0/search` with `Ocp-Apim-Subscription-Key` header
  - Provider selection based on `WebSearchConfig.Provider` ("brave", "tavily", "bing")
  - API key resolution using `${env:VAR}` syntax (copied pattern from StdioMcpTransport)
  - Returns formatted markdown: `## Search Results for "{query}"` with numbered list of `[title](url)` + snippet

**Files Created:**
- `extensions\web\BotNexus.Extensions.WebTools\HtmlToText.cs` — Regex-based HTML-to-text converter with partial regex methods
- `extensions\web\BotNexus.Extensions.WebTools\WebFetchTool.cs` — URL fetcher with pagination
- `extensions\web\BotNexus.Extensions.WebTools\WebSearchTool.cs` — Multi-provider search with markdown output
- `extensions\web\BotNexus.Extensions.WebTools\Search\ISearchProvider.cs` — Provider interface + SearchResult record
- `extensions\web\BotNexus.Extensions.WebTools\Search\BraveSearchProvider.cs` — Brave Search API implementation
- `extensions\web\BotNexus.Extensions.WebTools\Search\TavilySearchProvider.cs` — Tavily Search API implementation
- `extensions\web\BotNexus.Extensions.WebTools\Search\BingSearchProvider.cs` — Bing Search API implementation

**Patterns Followed:**
- IAgentTool pattern from ExecTool and McpInvokeTool
- `JsonDocument.Parse("""...""")` for tool schema definitions
- `AgentToolResult` with `AgentToolContent(AgentToolContentType.Text, text)` for results
- Argument helper methods with JsonElement handling (ReadString, ReadOptionalInt, ReadOptionalBool)
- HttpClient injection with owned/external lifecycle management
- No external NuGet dependencies — System.Net.Http, System.Text.Json, System.Text.RegularExpressions only

**Build:** ✅ `dotnet build extensions\web\BotNexus.Extensions.WebTools` — Clean, 0 warnings, 0 errors

## Learnings

- 2026-04-09: `WebSearchTool` now supports provider `"copilot"` with `CopilotMcpSearchProvider`, using MCP `tools/call` to invoke `web_search` against `https://api.githubcopilot.com/mcp` (or configured endpoint).
- 2026-04-09: Copilot MCP search auth requires headers `Authorization`, `X-MCP-Toolsets=web_search`, `X-MCP-Host=github-coding-agent`, and `X-Initiator=agent`; provider caches a lazily initialized `McpClient` for reuse.
- 2026-04-09: `InProcessIsolationStrategy` now enables `botnexus-web.search` when provider is `"copilot"` even without `ApiKey`, wiring auth via `_authManager.GetApiKeyAsync(descriptor.ApiProvider, ct)` and endpoint normalization to `.../mcp`.
- 2026-04-10: Wave 1 sub-agent contracts are anchored in `BotNexus.Gateway.Abstractions` with `Agents/ISubAgentManager.cs` and shared models `Models/SubAgentSpawnRequest.cs` + `Models/SubAgentInfo.cs` (including `SubAgentStatus` enum) for async background sub-agent lifecycle.
- 2026-04-10: Gateway configuration now nests sub-agent controls via `GatewayOptions.SubAgents` and `Configuration/SubAgentOptions.cs` with defaults for concurrency, depth, max turns, timeout, and model fallback.
- 2026-04-10: Validation for this contract/config wave uses targeted project builds: `dotnet build src\gateway\BotNexus.Gateway.Abstractions\BotNexus.Gateway.Abstractions.csproj --verbosity quiet` and `dotnet build src\gateway\BotNexus.Gateway\BotNexus.Gateway.csproj --verbosity quiet`.
- 2026-04-10: `AddBotNexusGateway` now binds `gateway` + `gateway:subAgents` configuration and registers `ISubAgentManager`/`DefaultSubAgentManager`; `InProcessIsolationStrategy` injects `spawn_subagent`, `list_subagents`, and `manage_subagent` tools per-session while skipping any session ID containing `::subagent::` to prevent recursive spawning.
- 2026-04-10: Added session-scoped sub-agent REST endpoints (`GET /api/sessions/{sessionId}/subagents`, `DELETE /api/sessions/{sessionId}/subagents/{subAgentId}`) and lifecycle activity emissions from `DefaultSubAgentManager` using event keys `subagent_spawned`, `subagent_completed`, `subagent_failed`, and `subagent_killed`.
## 2026-04-11T00:00Z — Phase 1 Multi-Session Server Foundation
- Added SessionSummary DTO and ISessionWarmupService abstraction in Gateway.Abstractions.
- Added SessionWarmupOptions + GatewayOptions.SessionWarmup, with config binding in AddBotNexusGateway.
- Implemented SessionWarmupService (IHostedService + cache + agent/session refresh + lifecycle event hook).
- Added GatewayHub.SubscribeAll() and Subscribe(sessionId), and Connected capabilities.multiSession=true.
- Updated SignalRHubTests hub factory for new warmup dependency.
- Validation: dotnet build Q:\repos\botnexus --verbosity quiet (passed).

## 2026-04-12T00:20:29Z — Wave 1 Infinite Scrollback API
- Added ISessionStore.ListByChannelAsync and implemented it in InMemorySessionStore, FileSessionStore, SqliteSessionStore, plus test ResettableInMemorySessionStore.
- Added ChannelHistoryController with GET /api/channels/{channelType}/agents/{agentId}/history implementing cross-session cursor pagination, oldest-first payload ordering, hasMore/nextCursor, and session boundary markers.
- Added/updated tests for channel filtering + ordering and controller cursor behavior.
- Validation: dotnet test (targeted Gateway tests) and dotnet build Q:\repos\botnexus --verbosity quiet passed.

- 2026-04-12: Added `src/domain/BotNexus.Domain` (net9.0) with zero dependencies, introducing domain primitives (value objects + smart enums) and dedicated JSON converters under `Primitives/` and `Serialization/`.
- 2026-04-12: Removed legacy root-level gateway duplicates from `PlatformConfig`, migrated all consumers/tests to `Gateway.*`, and added `PlatformConfigLoader.MigrateLegacyGatewaySettings` to preserve compatibility with old config files.
- 2026-04-12: Validation now applies schema checks to the migrated in-memory config object, allowing one-time legacy root-key migration while keeping schema strict for persisted nested shape.

## 2026-04-12T04:10Z — Wave 2 Session Model + Value Object Adoption
- Completed Wave 2 gateway session model migration: `SessionStatus.Closed` renamed to `Sealed`, and `GatewaySession` now includes `SessionType`, `IsInteractive`, and `Participants`.
- Added domain participant model in `src/domain/BotNexus.Domain/Primitives/SessionParticipant.cs` (`SessionParticipant`, `ParticipantType`) and persisted session metadata in in-memory/file/sqlite stores.
- Adopted domain primitives across gateway contracts and runtime: typed `ChannelKey` and `MessageRole` in channel adapters, session stores, APIs, streaming helpers, compactor, and memory indexer.
- Updated SQLite migration/load paths to support new columns and legacy status compatibility (`closed` remapped to `Sealed`).
- Validation: `dotnet build BotNexus.slnx --nologo --tl:off`, `dotnet test tests\BotNexus.Gateway.Tests\BotNexus.Gateway.Tests.csproj --nologo --tl:off --no-build`, and `dotnet test tests\BotNexus.Domain.Tests\BotNexus.Domain.Tests.csproj --nologo --tl:off` all passed.

## 2026-04-12T03:00Z — Wave 2 Session Model Orchestration (Cross-Agent Update)

**Coordination with Hermes (SessionModelWave2Tests.cs):**
- SessionModelWave2Tests written against Wave 2 abstractions (commit 836f6bf); compile issues expected during integration
- Tests cover: Sealed enum rename, SessionType/IsInteractive/Participants property contracts, domain participant model round-trip, ChannelKey/MessageRole integration, backward-compatibility scenarios (legacy closed → Sealed)
- Status: Tests should compile cleanly once db21650 integrated
- Action: Monitor compile results; if failures emerge, coordinate with Hermes on contract mismatches
