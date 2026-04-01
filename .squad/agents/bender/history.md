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

## Your Work Assignment

**Phase 2 P1 — Item 10: Anthropic Tool Calling** (50 points)
- Add tool calling support to Anthropic provider for feature parity with OpenAI
- Update AnthropicProvider to support tool definitions, calls, and responses
- Test against same integration tests as OpenAI
- Blocked by Phase 1 P0, unblocks Phase 3 P0

## Learnings

### 2026-04-01 — Architecture Review: Anthropic Provider Gaps (from Leela)

**Critical findings affecting your work:**
- **Tool Calling Missing:** OpenAI provider supports tool calling. Anthropic provider does not. Needs implementation for feature parity (P1).
- **No DI Extension:** Anthropic provider exists but has no `AddAnthropicProvider()` method in ServiceCollectionExtensions. OpenAI has one; Anthropic needs one too (P0 blocker).
- **CA2024 Warning:** AnthropicProvider streaming has `EndOfStream` check instead of `ReadLineAsync` check. Minor fix for compiler warning (P1).
- **Provider Parity:** Once tool calling is added to Anthropic, it should be tested against the same integration tests as OpenAI to ensure feature parity.

Build is clean, tests pass. ProviderRegistry exists but is unused — evaluate integration or removal.

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-04-01 — Gateway Multi-Agent Routing Implemented

- Gateway dispatch now resolves targets via an injectable `IAgentRouter` instead of hardcoding `runners[0]`.
- Agent targeting is metadata-driven (`agent`, `agent_name`, `agentName`), supports explicit broadcast (`all`/`*`), and logs per-agent dispatch.
- `IAgentRunner` now carries `AgentName`, enabling deterministic name-to-runner resolution for multi-agent environments.
- Gateway config now includes routing controls: `DefaultAgent` and `BroadcastWhenAgentUnspecified`.

### 2026-04-01 — Dynamic Extension Loader Foundation Landed

- Added `AddBotNexusExtensions(IConfiguration)` in Core to discover configured provider/channel/tool keys and load extension assemblies from `ExtensionsPath/{type}/{key}`.
- Loader creates one collectible `AssemblyLoadContext` per extension folder, supports `IExtensionRegistrar` first, and falls back to convention registration for `ILlmProvider`, `IChannel`, and `ITool`.
- Security gates are in place for extension keys (reject rooted paths, invalid chars, `.`/`..` traversal), and failures are warning/error logged without crashing startup.
- Gateway DI now invokes extension loading during service registration so configured extensions are wired automatically at startup.

### 2026-04-01 — Tool Extensions Now Register via Registrar + Core Interface

- `BotNexus.Tools.GitHub` now exposes a dynamic-loading registrar (`GitHubExtensionRegistrar : IExtensionRegistrar`) so extension config under `Tools:Extensions:{key}` binds and registers `ITool` services through the extension loader.
- `GitHubTool` now implements `BotNexus.Core.Abstractions.ITool` directly, removing the project’s compile-time dependency on `BotNexus.Agent` and keeping extension contracts rooted in Core.
- `AgentLoop` now accepts optional additional tools (`IEnumerable<ITool>`) and merges them into the runtime `ToolRegistry`, enabling built-in and dynamically-loaded tools to coexist in invocation flow.

### 2026-04-01 — Extension Build/Publish Pipeline via MSBuild Metadata

- Added shared `src/Extension.targets` that extension projects can import and activate with `<ExtensionType>` + `<ExtensionName>` metadata.
- `Build` now copies extension outputs into solution-root `extensions/{type}/{name}/`, and `Publish` mirrors outputs into `{PublishDir}/extensions/{type}/{name}/`.
- Applied metadata/imports to Discord, Slack, Telegram, OpenAI, Anthropic, and GitHub extension projects; gateway development config now points `BotNexus:ExtensionsPath` at `../../extensions`.

### 2026-04-01 — Channel Extensions Self-Register via Registrar, WebSocket Remains Core

- Discord, Slack, and Telegram now expose `IExtensionRegistrar` implementations that bind `ChannelConfig` and register `IChannel` only when the instance is enabled and configured.
- Gateway service registration remains hard-coded only for `WebSocketChannel` + `GatewayWebSocketHandler`; external channels are loaded exclusively through `AddBotNexusExtensions()`.
- Runtime verification: `/api/channels` still reports the built-in `websocket` channel with no external channels enabled, while channel registrars are discovered and executed from `extensions/channels/*`.

### 2026-04-01 — Gateway API Key Authentication for REST + WebSocket

- Added `ApiKeyAuthenticationMiddleware` that protects all `/api/*` routes and the configured WebSocket path (`/ws` by default).
- API key is accepted via `X-Api-Key` header or `?apiKey=` query parameter for WebSocket upgrade requests.
- Missing/invalid keys now return HTTP 401 with JSON payload `{ "error": "Unauthorized", "message": "Invalid or missing API key." }`.
- If `BotNexus:Gateway:ApiKey` is empty, middleware logs a warning and allows unauthenticated requests for dev mode.
- Added integration tests for success, failure, dev-mode bypass, health bypass, and WebSocket query-key path.

## Sprint 1 Summary — 2026-04-01T17:33Z

✅ **COMPLETE** — All 2 Foundation Items Delivered (5 more from Farnsworth)

### Your Deliverables (Bender)

1. ✅ **fix-runner-dispatch** — Multi-agent routing via `IAgentRouter`, metadata-driven targeting, broadcast support
2. ✅ **dynamic-assembly-loader** (8fe66db) — Complete ExtensionLoader with folder discovery, AssemblyLoadContext isolation, registrar + convention registration

### Build Status
- ✅ Green, all tests passing
- ✅ Zero errors, warnings resolved

### Next Phase (Phase 2 P0)
- **Item 8: Copilot Provider** (Farnsworth, 60pt) — Implement OAuth device code flow, OpenAI-compatible HTTP
- **Item 9: Providers Base** (Fry, 40pt) — Extract shared HTTP code to Providers.Base

### Team Status
All 7 foundation items completed (Farnsworth: 5, Bender: 2). Decisions merged and deduplicated. Ready for Phase 2.

## Sprint 2 Summary — 2026-04-01T17:45Z

✅ **COMPLETE** — Dynamic Loading Fully Wired (3 items, plus 2 from Farnsworth)

### Your Deliverables (Bender) — Sprint 2

1. ✅ **extension-build-pipeline** — MSBuild targets for extension build/publish organization
2. ✅ **channel-dynamic-loading** (a130b6b) — Discord, Slack, Telegram registrars, configuration-driven loading
3. ✅ **tool-dynamic-loading** (435ec37) — GitHub tool registrar, AgentLoop tool registry integration

### Key Achievements

- **Extension.targets** MSBuild pipeline deployed to all extension projects
- **Discord, Slack, Telegram** now self-register via IExtensionRegistrar
- **GitHub tool** self-registers and integrates into AgentLoop tool registry
- **Configuration-driven loading** for all external channels and tools
- **WebSocket remains core** for platform stability
- **Runtime verification** confirms correct dynamic discovery

### Build Status
- ✅ Solution green, 0 errors, 0 warnings
- ✅ All tests passing
- ✅ No regressions

### Integration Points

- Coordinates with Farnsworth's ExtensionLoader
- Follows IExtensionRegistrar pattern across channels, providers, tools
- Supports Farnsworth's Copilot provider extension delivery

### Team Status
**Sprint 2 COMPLETE:** All 5 Sprint 2 items delivered (Farnsworth: 2, Bender: 3). Dynamic loading foundation fully integrated and tested. Ready for Phase 3.

### 2026-04-01 — Extension Loader Security Hardening

- Added extension security controls under BotNexus:Extensions: RequireSignedAssemblies (default alse), MaxAssembliesPerExtension (default 50), and DryRun (default alse).
- Extension folder resolution now rejects escaping reparse points (symlink/junction targets outside extensions root) in addition to traversal segment checks.
- Loader now validates assembly metadata before load, optionally enforces strong-name signature presence, logs full assembly path/version/discovered types, and supports dry-run validation without runtime loading.
- Extension AssemblyLoadContext now only shares approved contract assemblies (BotNexus.Core*, Microsoft.Extensions.*) to reduce host-internal exposure.
- Added unit coverage for invalid assemblies, strong-name enforcement, folder assembly cap, dry-run behavior, reparse-point escape rejection, detailed assembly logging, and host assembly isolation behavior.

### 2026-04-01 — Extension Loader Security Hardening (corrected)

- Added extension security controls under BotNexus:Extensions: RequireSignedAssemblies (default false), MaxAssembliesPerExtension (default 50), and DryRun (default false).
- Extension folder resolution now rejects escaping reparse points (symlink/junction targets outside extensions root) in addition to traversal segment checks.
- Loader now validates assembly metadata before load, optionally enforces strong-name signature presence, logs full assembly path/version/discovered types, and supports dry-run validation without runtime loading.
- Extension AssemblyLoadContext now only shares approved contract assemblies (BotNexus.Core*, Microsoft.Extensions.*) to reduce host-internal exposure.
- Added unit coverage for invalid assemblies, strong-name enforcement, folder assembly cap, dry-run behavior, reparse-point escape rejection, detailed assembly logging, and host assembly isolation behavior.

### 2026-04-01 — Slack Events API Webhook Endpoint
- Added a Core-level `IWebhookHandler` contract and Gateway webhook route mapping for registered handlers.
- Slack channel registrar now registers `/webhooks/slack` only when Slack is enabled/configured, including required signing secret.
- Slack webhook handling now validates Slack request signatures, responds to URL verification challenges, and publishes message events onto `IMessageBus` for normal channel processing flow.
- Added unit coverage for URL verification, event callback parsing, valid/invalid signature handling, and conditional Slack webhook registration.

