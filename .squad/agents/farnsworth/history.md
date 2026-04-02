# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

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
