# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## 2026-04-03T17:45:00Z — System Messages Sprint (Team Sync)

**Delivered by:** Farnsworth (Platform)  
**Collaborating:** Bender (Runtime), Leela (Lead), Fry (Web)  

**Cross-Agent Deliverables:**
- SystemMessage model + IActivityStream extension for infrastructure foundation
- SystemMessageStore (100 msg retention) + GET /api/system/messages endpoint
- ProviderStartupValidationService ensures auth checks at startup
- Bender broadcasts device auth code+URL via system messages (runtime layer)
- Leela hardened config write safety + secure token storage in ~/.botnexus/tokens/
- Fry built device auth UX banners (copy code, clickable URL) + thinking indicator with pulsing animation

**Status:** ✅ Sprint complete. All systems communicating device auth flow via system messages.

## Team Directives (All Agents Must Follow)

1. **Dynamic Assembly Loading** (2026-04-01T16:29Z)
   - All extensions (channels, providers, tools) must be dynamically loaded from `extensions/{type}/{name}/` folders
   - Configuration drives what loads — nothing loads by default unless referenced in config
   - Reduces security risk, keeps codebase abstracted
   - See decisions.md Section "Part 1: Dynamic Assembly Loading Architecture"

2. **Conventional Commits Format** (2026-04-01T16:43Z)
   - Use feat/fix/refactor/docs/test/chore prefixes on ALL commits
   - Commit granularly — one commit per work item or logical unit, not one big commit at end
   - Makes history clean, reversible, and easy to review

3. **Copilot Provider P0** (2026-04-01T16:46Z)
   - Copilot is the only provider Jon uses — it is P0, all other providers P1/P2
   - Use OAuth device code flow (like Nanobot) — no API key
   - Base URL: https://api.githubcopilot.com
   - Prioritize Copilot work before OpenAI, Anthropic

## Recent Session Summaries

### 2026-04-03 — Nullable Generation Settings Implementation

**Session:** Sprint 4 config work  
**Status:** ✅ Success

**Work Completed:**
- Made Temperature, MaxTokens, ContextWindowTokens nullable (double?, int?) across:
  - GenerationSettings (Core model)
  - AgentDefaults (config defaults)
  - All 3 provider implementations:
    - Copilot: Conditional payload inclusion
    - OpenAI: Conditional ChatCompletionOptions setting
    - Anthropic: Always include max_tokens (required by API), optional temperature
  
- Context window sizing: Uses `settings.ContextWindowTokens ?? 65536` fallback
- Config reload: Added `NullableDoubleEquals()` helper for temperature comparison
- Files changed: 7 files across Core, Agent, and provider modules

**Rationale:**
- Enables each provider to use its native defaults unless explicitly configured
- Unblocks model selector UI work (Fry's tasks)
- Reduces hardcoded BotNexus overrides of model defaults
- Backward compatible: existing configs with explicit values work unchanged

**Key Decision:** Providers are responsible for their own defaults. BotNexus acts as a pass-through when values are null. This is more flexible than forcing a single set of defaults.

**Parallel Work:** Completed without blocking Fry's UI tasks. Both areas integrated cleanly.

---

## 2026-04-04T00:49:47Z — Pi Provider Architecture Port Sprint (Team Sync)

**Sprint Status:** ✅ Complete  
**Timestamp:** 2026-04-04T00:49:47Z  
**Orchestration:** See `.squad/orchestration-log/2026-04-04T00-49-47Z-farnsworth.md`

**Your Contribution (Farnsworth — Platform Dev):**
- Ported Pi provider architecture from badlogic/pi-mono
- Implemented ModelDefinition & CopilotModels registry (30+ models enumerated)
- Built 3 API format handlers: AnthropicMessages, OpenAiCompletions, OpenAiResponses
- Rewrote CopilotProvider with model-aware routing and normalization
- 3 commits total

**Team Outcomes:**
- **Bender (Runtime):** Verified AgentLoop + Gateway integration — no changes needed, architecture is drop-in. Commit e916394.
- **Hermes (Tester):** 72 new tests for model registry, handler routing, format handlers. Fixed 3 pre-existing failures. 494 total tests passing. Commit 5d293d4.
- **Kif (Documentation):** Updated architecture docs, model mapping tables, configuration reference.

**Cross-Team Decisions Merged:**
1. Repeated tool call detection needed (Squad investigation)
2. Copilot Responses API investigation (your findings)
3. Provider Response Normalization Layer (Leela, architectural)
4. Responses API Migration Sprint Plan (Leela, awaiting approval)

**Next Phase:** Responses API migration sprint unblocks new event-driven streaming model to eliminate Copilot-specific quirks.

---

## Your Work Assignment — Executive Role

**Phase 1 P0 — Item 1: Provider Dynamic Loading** (50 points) [CRITICAL PATH BLOCKER]
- Build ExtensionLoader class in Core (or new BotNexus.Extensions project)
- Implement AssemblyLoadContext per extension for isolation and future hot-reload
- Discover extensions via folder structure: extensions/{type}/{name}/
- Auto-register discovered types in DI ServiceCollection
- Support folder-based discovery for channels, providers, tools
- See decisions.md "Part 1: Dynamic Assembly Loading Architecture" for full spec
- Unblocks all Phase 2 and Phase 3 work

**Phase 1 P0 — Item 4: OAuth Core Abstractions** (20 points) [COPILOT ENABLER]
- Create BotNexus.Core.OAuth namespace with:
  - IOAuthProvider interface: GetAccessTokenAsync(), HasValidToken property
  - IOAuthTokenStore interface: LoadTokenAsync(), SaveTokenAsync(), ClearTokenAsync()
  - OAuthToken record: AccessToken, ExpiresAt, RefreshToken?
- Integrate with ExtensionLoader so ProviderConfig.Auth discriminator ("apikey" vs "oauth") routes correctly
- Default IOAuthTokenStore impl: encrypted file storage at ~/.botnexus/tokens/{providerName}.json
- Unblocks Phase 2 Copilot provider (item 8)

**Phase 2 P0 — Item 8: Copilot Provider** (60 points) [STRATEGIC PRIORITY]
- Create BotNexus.Providers.Copilot extension project under extensions/providers/copilot/
- Implement ILlmProvider via LlmProviderBase + IOAuthProvider
- Use OpenAI-compatible HTTP (same DTOs as OpenAI provider)
- Implement OAuth device code flow:
  - POST /login/device/code to github.com
  - Display user_code and verification_uri to user
  - Poll /login/oauth/access_token until token received or timeout
  - Cache via IOAuthTokenStore
  - Use as Bearer token in Authorization header
- Config: Auth="oauth", DefaultModel="gpt-4o", ApiBase="https://api.githubcopilot.com"
- See decisions.md "Part 3: GitHub Copilot Provider" for full spec
- Unblocks Phase 3 (tool extensibility) and Production readiness

**Phase 2 P1 — Item 9: Providers Base Shared Code** (40 points)
- Extract shared HTTP code from OpenAI provider to BotNexus.Providers.Base:
  - Request/response DTOs (ChatCompletion, Message, Tool, ToolCall, etc.)
  - SSE streaming parser
  - HTTP client retry/backoff patterns
- Update OpenAI provider to reference shared code
- Copilot provider (item 8) will also use shared code
- Reduces duplication, improves maintainability

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- 2026-04-06: Added Phase 2 channel stub projects `BotNexus.Channels.Tui` and `BotNexus.Channels.Telegram` with DI registration (`AddBotNexusTuiChannel`, `AddBotNexusTelegramChannel`) and lifecycle-safe `IChannelAdapter` placeholders for future inbound loop/API wiring.
- 2026-04-06: `ToolExecutor.PrepareToolCallAsync` now validates raw tool-call arguments against `tool.Definition.Parameters` with `ToolCallValidator` before invoking `PrepareArgumentsAsync`, returning a tool-error result on schema failures.
- Message bus publishing is now async-only; the sync `MessageBusExtensions.Publish()` wrapper was removed to eliminate sync-over-async deadlock risk.
- 2026-04-01: BotNexus CLI now includes `backup create|restore|list` in `src/BotNexus.Cli/Program.cs`, with zip backup of home data excluding `backups/` and `logs/`, plus pre-restore safety backups.
- 2026-04-01: `BotNexus.Core` already references `Microsoft.Extensions.Configuration.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions` (v10.0.5), so extension registration contracts can use these abstractions directly without package changes.
- **OAuth contracts live in Core Abstractions** (2026-04-01): OAuth integration points are defined under `src/BotNexus.Core/Abstractions` via `IOAuthProvider`, `IOAuthTokenStore`, and `OAuthToken`.
- **ProviderConfig auth discriminator** (2026-04-01): `ProviderConfig` includes an `Auth` property defaulting to `"apikey"` for selecting API key vs OAuth provider auth behavior.
- **Config binding is now extension-keyed and case-insensitive** (2026-04-01): `ProvidersConfig` and `ChannelsConfig.Instances` are dictionary-based with `StringComparer.OrdinalIgnoreCase`, enabling folder-name keys (e.g., `openai`, `telegram`) without casing fragility.
- 2026-04-01: `ProviderRegistry` now infers provider keys from provider namespaces/types (e.g., OpenAI -> `openai`) and is DI-registered so agent loops can resolve provider per agent model/provider config with default fallback.
- 2026-04-01: Extension assemblies may carry their own copies of `BotNexus.Core`/`BotNexus.Providers.Base`; the loader must reuse host-loaded shared assemblies to avoid type-identity mismatches that break `IExtensionRegistrar`/`ILlmProvider` discovery.
- 2026-04-01: `BotNexus.Providers.Copilot` ships as an extension with `CopilotExtensionRegistrar`, OAuth device-code auth via `GitHubDeviceCodeFlow`, and JSON token persistence at `%USERPROFILE%\.botnexus\tokens\copilot.json`.
- 2026-04-01: Gateway observability now uses ASP.NET Core `IHealthCheck` + `/health` and `/ready`, with readiness tied to enabled channel runtime state and configured provider initialization.
- 2026-04-01: Baseline platform metrics are emitted via `System.Diagnostics.Metrics` (`botnexus.messages.processed`, `botnexus.tool_calls.executed`, `botnexus.provider.latency`, `botnexus.extensions.loaded`), and message processing logs carry `CorrelationId` scopes end-to-end.
- 2026-04-01: Startup now resolves BotNexus home from `BOTNEXUS_HOME` or `%USERPROFILE%\.botnexus`, creates home subfolders (`extensions/*`, `tokens`, `sessions`, `logs`) and loads user overrides from `config.json`.
- **Generation settings now nullable for provider defaults** (2026-04-02): `GenerationSettings.Temperature`, `MaxTokens`, and `ContextWindowTokens` are now `nullable` types. When `null`, providers use their own defaults instead of BotNexus overriding with hardcoded values. Providers (Copilot, OpenAI, Anthropic) conditionally include these in API requests only when non-null. Default fallback of 65536 tokens used for context window sizing when null.
- **BotNexus.Providers.Core standalone project** (2026-04-04): Created `src/providers/BotNexus.Providers.Core/` as a pure port of pi-mono's AI package core abstractions into idiomatic C#/.NET 10. Zero dependencies on existing BotNexus platform. Contains: content blocks (TextContent, ThinkingContent, ImageContent, ToolCallContent) with JsonPolymorphic serialization, message types (UserMessage, AssistantMessage, ToolResultMessage), streaming events + LlmStream (Channel-based producer/consumer), ApiProviderRegistry + ModelRegistry, LlmClient top-level stream functions, EnvironmentApiKeys, and utilities (MessageTransformer, StreamingJsonParser, UnicodeSanitizer, SimpleOptionsHelper, CopilotHeaders). 20 source files, builds clean with 0 warnings.
- **UserMessageContent dual-mode pattern** (2026-04-04): pi-mono's `string | ContentBlock[]` union for user message content ported as a `UserMessageContent` class with implicit string conversion and a custom JsonConverter that reads/writes either a JSON string or a JSON array of content blocks.
- **New Copilot provider (Providers.Core architecture)** (2026-04-04): Created `src/providers/BotNexus.Providers.Copilot/` as a standalone implementation of `IApiProvider` from `BotNexus.Providers.Core`. Implements OpenAI Completions wire format with Copilot auth headers (X-Initiator, Copilot-Vision-Request, Openai-Intent). Uses `JsonObject`/`JsonNode` for dynamic payload building to avoid anonymous-type serialization pitfalls with `System.Text.Json`. `CopilotOAuth` static class handles GitHub device code flow with two-stage token lifecycle: GitHub OAuth token (long-lived, stored as RefreshToken after first exchange) → Copilot session token (short-lived, ~25min). Solution folder `/src/providers/` needed to disambiguate from old `src/BotNexus.Providers.Copilot/` in slnx. `AssemblyName` set to `BotNexus.Providers.Copilot.New` to avoid output collision. Zero dependencies beyond `BotNexus.Providers.Core`.
- **OpenAI Completions provider (Providers.Core architecture)** (2026-04-04): Created `src/providers/BotNexus.Providers.OpenAI/` implementing `IApiProvider` with `Api = "openai-completions"`. Raw HttpClient SSE streaming — no OpenAI SDK dependency. Full message conversion (user/assistant/tool), tool function format, SSE chunk parsing with text/thinking/toolcall state machine, OpenAICompletionsCompat support (developer role, strict schemas, max_tokens field name, thinking format), Copilot dynamic headers, reasoning_effort mapping from ThinkingLevel. `AssemblyName` set to `BotNexus.Providers.OpenAI.Completions` to avoid collision with old `src/BotNexus.Providers.OpenAI/` provider. 0 warnings, 0 errors.
- 2026-04-05: `DefaultMessageConverter.Create()` is now the shared default `ConvertToLlmDelegate` in AgentCore; CodingAgent uses it directly and system agent messages are wrapped as provider user summaries (`<summary>...</summary>`).
- 2026-04-05: `ModelRegistry` now owns model identity helpers — `SupportsExtraHigh(LlmModel)` and `ModelsAreEqual(LlmModel, LlmModel)` — and OpenAI Responses now consumes `SupportsExtraHigh` instead of provider-local xhigh checks.
- 2026-04-05: `BuiltInModels.RegisterAll()` now composes provider-specific registration methods (`RegisterCopilotModels`, `RegisterAnthropicModels`, `RegisterOpenAIModels`) so direct-provider catalogs can evolve independently.
- 2026-04-05: Direct-provider baseline models are now registered in Providers.Core for `anthropic` and `openai` with `FreeCost` placeholders, explicit API model IDs, and provider base URLs.
- 2026-04-05: `LlmModel` now carries `SupportsExtraHighThinking`; `ModelRegistry.SupportsExtraHigh()` is capability-based instead of model-id string matching.
- 2026-04-06: Gateway now ships `DefaultAgentCommunicator` (sub-agent sessions scoped as `{parentSessionId}::sub::{childAgentId}` with Phase 2 cross-agent stub) and default `ApiKeyGatewayAuthHandler` registration in `AddBotNexusGateway`.

## Sprint 1 Summary — 2026-04-01T17:33Z

✅ **COMPLETE** — All 5 Foundation Items Delivered (with 2 more from Bender)

### Your Deliverables (Farnsworth)

1. ✅ **config-model-refactor** (5c6f777) — Dictionary-based provider/channel config, case-insensitive keys
2. ✅ **extension-registrar-interface** — `IExtensionRegistrar` contract, enables extension self-registration
3. ✅ **oauth-core-abstractions** (96c2c08) — `IOAuthProvider`, `IOAuthTokenStore`, `OAuthToken` in Core.OAuth
4. ✅ **fix-sync-over-async** — Removed `MessageBusExtensions.Publish()` sync-over-async hazard
5. ✅ **provider-registry-integration** (4cfd246) — ProviderRegistry now DI-registered, runtime provider resolution

### Build Status
- ✅ Green, all tests passing
- ✅ Zero errors, warnings resolved

### Next Phase (Phase 2 P0)
- **Item 8: Copilot Provider** (Farnsworth, 60pt) — Implement OAuth device code flow, OpenAI-compatible HTTP
- **Item 9: Providers Base** (Fry, 40pt) — Extract shared HTTP code to Providers.Base

### Team Status
All 7 foundation items completed (Farnsworth: 5, Bender: 2). Decisions merged and deduplicated. Ready for Phase 2.

## Sprint 2 Summary — 2026-04-01T17:45Z

✅ **COMPLETE** — Dynamic Loading Fully Wired (2 items from Bender, plus Copilot provider)

### Your Deliverables (Farnsworth) — Sprint 2

1. ✅ **provider-dynamic-loading** — ExtensionLoader with AssemblyLoadContext, folder discovery, DI registration
2. ✅ **copilot-provider** (52ad353) — OAuth device code flow, OpenAI-compatible HTTP, FileOAuthTokenStore

### Key Achievements

- **BotNexus.Providers.Copilot** extension project fully implemented
- **CopilotProvider : LlmProviderBase, IOAuthProvider** with streaming and tool calling
- **OAuth Device Code Flow** via GitHubDeviceCodeFlow with token persistence
- **CopilotExtensionRegistrar** for automatic DI registration
- Full unit test coverage (chat, streaming, tools, device flow, token caching, re-auth)
- **Decision merged to decisions.md** — Part 4: GitHub Copilot Provider Implementation

### Build Status
- ✅ Solution green, 0 errors, 0 warnings
- ✅ All 124+ tests passing
- ✅ No regressions

### Unblocks
- Phase 3 work (tool calling, observability)
- Production deployment with Copilot as default provider
- Future OAuth pattern re-use

### Team Status
**Sprint 2 COMPLETE:** Dynamic assembly loading foundation fully wired. Farnsworth and Bender delivered all items. Ready for Phase 3.

## Sprint 3 Summary — 2026-04-01T18:17Z

✅ **COMPLETE** — Observability Foundation Delivered (1 item)

### Your Deliverables (Farnsworth) — Sprint 3

1. ✅ **observability-foundation** (7beda23) — Serilog structured logging, health checks, metrics, OpenTelemetry hooks

### Key Achievements

- **Serilog Integration** — Structured logging with correlation IDs for distributed tracing, file and console sinks
- **Health Check Endpoints** — `/health` (liveness), `/health/ready` (readiness) for Kubernetes orchestration
- **Agent Execution Metrics** — Request count, latency, success rate per agent
- **Extension Loading Metrics** — Load time, assembly count, registrar performance tracking
- **Provider Connectivity** — Health status per provider, last check time, re-authentication hooks
- **APM Hooks** — OpenTelemetry instrumentation ready for Datadog, Application Insights integration
- **Zero Regressions** — All 140+ tests passing, build green

### Build Status
- ✅ Solution green, 0 errors, 0 warnings
- ✅ All tests passing with observability integration tests
- ✅ Production-ready monitoring and debugging infrastructure

### Integration Points
- Works with Bender's API key auth and webhook validation (all logged with correlation IDs)
- Supports Hermes' E2E test scenarios with metrics validation
- Enables future APM dashboard creation

### Team Status
**Sprint 3 COMPLETE:** Observability foundation fully deployed. Farnsworth, Bender, Hermes delivered all 6 items. Security and monitoring hardening complete. Production-ready.

## Sprint 4 Summary — 2026-04-01T18:22Z

✅ **COMPLETE** — Configuration & Observability (1 item)

### Your Deliverables (Farnsworth) — Sprint 4

1. ✅ **unified-config-home** (8b25bd7) — Unified ~/.botnexus/ configuration directory with BOTNEXUS_HOME support

### Key Achievements

- **Unified Config Home** — ~/.botnexus/ structure with tokens/, sessions/, logs/, extensions/ subdirectories
- **BOTNEXUS_HOME Support** — Environment variable override or platform default (%USERPROFILE%\.botnexus on Windows, ~/.botnexus on Unix)
- **Auto-Directory Creation** — Startup creates required folders with appropriate permissions
- **User Config Overrides** — ~/.botnexus/config.json merges with application defaults
- **Token Persistence** — OAuth tokens stored at ~/.botnexus/tokens/{providerName}.json (encrypted)
- **Session History** — Agent sessions persisted to ~/.botnexus/sessions/{agentName}.jsonl
- **Platform-Aware Defaults** — Conditional paths for Windows, Linux, macOS

### Build Status
- ✅ Solution green, 0 errors, 0 warnings
- ✅ All 192 tests passing (158 unit + 19 integration + 15 E2E)
- ✅ Code coverage: 90%+ for core libraries, 98% for extension loader
- ✅ Zero regressions from all prior sprints

### Integration Points
- Works with all Sprint 1-3 features for unified configuration
- Foundation for container deployment and persistent user data
- Enables future cloud storage backends for tokens/sessions

### Team Status
**ALL 4 SPRINTS COMPLETE:** 24/26 items delivered. Farnsworth: 8 items across all sprints (oauth, copilot, observability, config consolidation). Production-ready platform ready for deployment.


## 2026-04-02 — Team Updates

- **Nibbler Onboarded:** New Consistency Reviewer added to team. Owns post-sprint audits.
- **New Ceremony:** Consistency Review ceremony established (trigger: after sprint completion or arch changes). Leela's audit (2026-04-02) found 22 issues across 5 files.
- **Decision:** Cross-Document Consistency Checks merged into decisions.md. All agents treat consistency as a quality gate.


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

## 2026-04-02 — Backup CLI & Test Isolation Infrastructure

### Your Deliverables (Farnsworth)

**Backup CLI Implementation** — src/BotNexus.Cli/Program.cs
- New command group: `backup create|restore|list`
- `backup create` — creates full backup of ~/.botnexus to ~/.botnexus-backups (external location)
- `backup restore {backup-id}` — restores from named backup
- `backup list` — lists available backups with metadata
- Self-backup exclusion: skips ~/.botnexus-backups when creating new backup (bug fixed by Coordinator)
- All 11 integration tests passing (Hermes wrote tests)

### Key Architecture Decisions

1. **Backup Location: External to Home**
   - Location: ~/.botnexus-backups (sibling to ~/.botnexus, NOT inside)
   - Rationale: backups are emergency snapshots, kept separate from runtime data
   - Prevents recursive backup issues (backups being backed up)
   - Cleaner cleanup semantics for test isolation

2. **Test Isolation Pattern** (cross-team decision, led by Coordinator)
   - Introduced: test.runsettings for foolproof BOTNEXUS_HOME environment variable
   - Introduced: Directory.Build.props to auto-apply runsettings to all test projects
   - Result: all 465 tests pass, ZERO ~/.botnexus contamination on dev machines/CI
   - Pattern becomes team standard for test infrastructure going forward

### Build Status
- ✅ Solution green, 0 errors, 0 warnings
- ✅ All 465 tests passing (11 new backup integration tests included)
- ✅ ZERO home directory contamination verified

### Integration Points
- Backup command integrates with unified ~/.botnexus/ configuration home
- Test isolation infrastructure enables safe backup testing without developer home pollution
- Backup location strategy informs where other external data lives (logs, caches, temp state)

### Team Status
**Backup infrastructure COMPLETE:** CLI command fully implemented, comprehensive test coverage, foolproof test isolation established. Ready for production backup/restore workflows.

## 2026-04-02 — Packaging Workflow Scripts

### Your Deliverables (Farnsworth)

- Added `scripts/pack.ps1` to publish gateway/cli/extensions in Release and package each as `.nupkg` into `artifacts/`.
- Added `scripts/install.ps1` to install packages into configurable app path (`~/.botnexus/app` default), map extension packages into `extensions/{type}/{name}`, emit `version.json`, and update `~/.botnexus/config.json` `ExtensionsPath`.
- Added `scripts/update.ps1` to stop running gateway process (if any), run install, then restart gateway from installed binaries.

### Validation

- Baseline build succeeded.
- Baseline tests show a pre-existing failure in `BotNexus.Tests.Unit.Tests.CopilotProviderTests.ChatAsync_ReturnsCompletionPayload` (expected `/v1/chat/completions`, actual `/chat/completions`).
- Ran `scripts/pack.ps1` and confirmed packages were generated in `artifacts/`.

## 2026-04-02 — CLI Tool Installability & Native Install/Update Commands

### Your Deliverables (Farnsworth)

## 2026-04-05T11:52:58Z — Sprint 4 Consolidation: P0/P1 Implementation Complete

**Status:** ✅ COMPLETE  
**Timestamp:** 2026-04-05T11:52:58Z  
**Teams:** All 8 agents coordinated  
**Session Log:** `.squad/log/2026-04-05T11-52-58Z-sprint4-close.md`

**Your Deliverables (Farnsworth — Provider Fixes):**

1. **Provider Fixes (6 commits, 5 P0/P1 decisions):**
   - P0-4: ModelsAreEqual — remove BaseUrl comparison (match on Id+Provider only)
   - P0-5: StopReason mapping — Refusal/Sensitive from provider responses
   - P1-11: SimpleOptionsHelper apiKey fallback chain
   - P1-17: Anthropic provider decomposition (3 internal classes: MessageConverter, RequestBuilder, StreamParser)
   - P1-18: JSON standardization across all providers (JsonSerializer.SerializeToElement)

2. **Test Coverage (new):**
   - Provider mapping tests per API format
   - StopReason mapping validation
   - JSON payload construction verification
   - 16 new tests added, 438 total passing

**Cross-Agent Integration:**
- Coordinated with Bender on AgentCore P0/P1 fixes (message lifecycle, queue state)
- Validated provider changes work with CodingAgent tool fixes
- Supported Hermes' test suite expansion
- Enabled Kif's documentation on provider architecture

**Orchestration Log:** `.squad/orchestration-log/2026-04-05T11-52-58Z-farnsworth.md`

**Build Status:** ✅ Green (0 errors, 0 warnings), all tests passing

**Decision Inbox:** All 4 inbox files merged to decisions.md and deleted

**Next Phase:** Ready for E2E integration testing with full provider + AgentCore + CodingAgent stack.

- Added bootstrap install scripts:
  - `scripts/install-cli.ps1` (Windows PowerShell)
  - `scripts/install-cli.sh` (Linux/macOS bash)
- Extended `src/BotNexus.Cli/Program.cs` with native commands:
  - `botnexus install [--install-path <path>] [--packages <path>]`
  - `botnexus update [--install-path <path>] [--packages <path>]`
- Ported package extraction logic from `scripts/install.ps1` into CLI:
  - Gateway installs to `{install-path}/gateway`
  - Extensions install to `{install-path}/extensions/{type}/{name}`
  - `BotNexus.Cli.nupkg` is explicitly skipped
  - `version.json` written with install metadata
  - `~/.botnexus/config.json` `BotNexus.ExtensionsPath` updated
- Updated gateway launch resolution for `start`:
  1. Prefer installed gateway at `~/.botnexus/app/gateway/BotNexus.Gateway.dll`
  2. Fall back to repo project `src/BotNexus.Gateway/BotNexus.Gateway.csproj`
- Added CLI integration coverage updates:
  - Help now validated for `install` and `update`
  - New install integration scenario validates package deployment and CLI package skip behavior

### Validation

- Ran bootstrap script: `powershell -ExecutionPolicy Bypass -File scripts/install-cli.ps1`
- Verified global tool help includes new commands: `botnexus --help`
- Verified deployment command: `botnexus install --packages Q:\repos\botnexus\artifacts`
- Verified update command: `botnexus update --packages Q:\repos\botnexus\artifacts`
- Verified `start` works in both modes:
  - Installed binary launch path (content root under `~/.botnexus/app/gateway`)
  - Repo fallback (`dotnet run --project ...`) when install path not present for selected home
- Ran full suite: `dotnet test --no-restore -v minimal --tl:off` (all passing)

## 2026-04-02 — Repository + CLI Versioning System

### Your Deliverables (Farnsworth)

- Added repo-root `Directory.Build.props` with default `<Version>` and `<InformationalVersion>` set to `0.0.0-dev` for all projects.
- Added shared `scripts/common.ps1` with `Resolve-Version` (env override, git tag `v*`, or `0.0.0-dev.{short-hash}[.dirty]`).
- Updated `scripts/pack.ps1` and `scripts/install-cli.ps1` to consume `Resolve-Version` and pass `/p:Version` + `/p:InformationalVersion` into `dotnet publish` / `dotnet pack`.
- Updated CLI startup/versioning in `src/BotNexus.Cli/Program.cs`:
  - `--version` prints one-line `botnexus <resolved-version>`
  - version resolution now prefers release/dev git semantics when default dev assembly metadata is present
- Enhanced `botnexus status` to print:
  - CLI version
  - installed version (from `{installPath}/version.json`)
  - gateway status + PID info
  - version match indicator
- Updated `WriteVersionManifest()` to include `"Version"` in `version.json`.
- Kept install manifest `"Commit"` as short hash for alignment with dev-version format.
- Updated CLI integration assertion to validate `version.json` now includes `"Version"`.

### Validation

- `dotnet build --nologo --verbosity minimal --tl:off` ✅
- `dotnet run --project src\BotNexus.Cli -- --version` ✅ (`botnexus 0.0.0-dev.<hash>.dirty`)
- `powershell -ExecutionPolicy Bypass -File scripts\pack.ps1` ✅ (artifacts use resolved dev version)
- Manual install verification via CLI `install` confirmed `version.json` now includes `"Version"`.
- `dotnet test --no-restore -v minimal --tl:off` ✅ (all test projects passing)
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

## 2026-04-05T07:12:57Z — P0 Sprint Implementation Phase (Team Completion)

**Status:** ✅ COMPLETE  
**Teams:** Farnsworth (Platform), Bender (Runtime), Hermes (QA), Kif (Docs)  
**Orchestration Log:** `.squad/orchestration-log/2026-04-05T07-12-57Z-*.md` (7 entries)  
**Session Log:** `.squad/log/2026-04-05T07-12-57Z-implementation-phase.md`

**Your Work (Farnsworth):**
- Provider P0 fixes: 1 commit (9f5a8cf) ✅
- Provider P1 fixes: 3 commits (d4c07f9, 610c175, 00c0197) ✅
- All builds green | All tests passing

**Team Outcomes:**
- Farnsworth: Provider fixes (P0+P1) — 4 commits, build ✓
- Bender: Tool P0 + AgentCore P0/P1 + CodingAgent P1 — 6 commits, tests ✓
- Hermes: 101 regression tests (3 projects) — 1 commit, coverage ✓
- Kif: 7 training guides (~2500 lines) — 1 commit, docs ✓

**All systems green. Ready for integration.**

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


## Session: P1 Design Review Fixes — Channel Stubs (2026-04-05)
Implemented three review P1s: channel stubs now inherit `ChannelAdapterBase`, Telegram options moved to DI options pattern, and `IChannelManager` abstraction added with GatewayHost/DI updated.
Verification: `dotnet build` and `dotnet test tests\BotNexus.Gateway.Tests\` both passed after changes.
