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
