# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-04-01 — Initial Architecture Review & Implementation Plan (Rev 2)

**Build & Test Baseline:**
- Solution builds cleanly on .NET 10.0 with only 2 minor warnings (CA2024 async stream, CS8425 EnumeratorCancellation)
- 124 tests pass (121 unit, 3 integration): `dotnet test BotNexus.slnx`
- Build command: `dotnet build BotNexus.slnx`
- NuGet restore required first: `dotnet restore BotNexus.slnx`

**Architecture:**
- Clean contract-first design: Core defines 13 interfaces, implementations in outer modules
- Dependencies flow inward — no circular references detected
- Two entry points: Gateway (full bot platform, port 18790) and Api (OpenAI-compatible REST proxy)
- Gateway is the orchestrator: hosts channels, message bus, agent loop, cron, heartbeat, WebUI
- Message flow: Channel → MessageBus → Gateway loop → AgentRunner → CommandRouter or AgentLoop → Channel response

**Key File Paths:**
- Solution: `BotNexus.slnx` (17 src + 2 test projects)
- Core contracts: `src/BotNexus.Core/Abstractions/` (13 interfaces)
- Core config: `src/BotNexus.Core/Configuration/BotNexusConfig.cs` (root config, section "BotNexus")
- DI entry: `src/BotNexus.Core/Extensions/ServiceCollectionExtensions.cs` (AddBotNexusCore)
- Gateway bootstrap: `src/BotNexus.Gateway/Program.cs` + `BotNexusServiceExtensions.cs`
- Agent loop: `src/BotNexus.Agent/AgentLoop.cs` (max 40 tool iterations)
- Session persistence: `src/BotNexus.Session/SessionManager.cs` (JSONL files)
- WebUI: `src/BotNexus.WebUI/wwwroot/` (vanilla JS SPA, no framework)

**Patterns:**
- All projects target net10.0, ImplicitUsings=enable, Nullable=enable
- Test stack: xUnit + FluentAssertions + Moq + coverlet
- Provider pattern with LlmProviderBase abstract class providing retry/backoff
- Channel abstraction via BaseChannel template method pattern
- MCP (Model Context Protocol) support with stdio and HTTP transports
- Tool system uses ToolBase abstract class with argument helpers
- Configuration is hierarchical POCOs bound from "BotNexus" section in appsettings.json

**Concerns Identified & Roadmap:**
- Anthropic provider lacks tool calling support (OpenAI has it, Anthropic does not)
- Anthropic provider has no DI extension method (OpenAI has AddOpenAiProvider)
- MessageBusExtensions.Publish() uses sync-over-async (.GetAwaiter().GetResult()) — deadlock risk
- No assembly loading or plugin discovery mechanism exists yet
- **DECISION:** Dynamic assembly loading is now foundation. Copilot is P0 with OAuth. 24-item roadmap across 4 releases. See decisions.md for full plan.

**Team Directives Merged:**
1. Dynamic assembly loading — extensions folder-based, configuration-driven, no default loading
2. Conventional commits — all agents use feat/fix/refactor/docs/test/chore format, granular per-item commits
3. Copilot provider P0 — OAuth device code flow, OpenAI-compatible API, only provider Jon uses

**Your Responsibilities (Leela):**
- Lead/Architect oversight of entire roadmap
- Architecture decisions during Phase 1-3 execution
- Plan Q2 features (item 23, Phase 4)
- Monitor team progress and adjust as needed
- Channel implementations (Discord/Slack/Telegram) not registered in Gateway DI — registration code is missing
- Slack channel uses webhook mode but no webhook endpoint exists in Gateway
- No authentication or authorization on any endpoint
- WebUI has no build tooling (vanilla JS, no bundling)
- ProviderRegistry exists but is never registered in DI or used

### 2026-04-01 — Dynamic Extension Architecture Plan

**Key Architectural Decisions:**
- Jon's directive elevates plugin/extension architecture from P2 to THE foundational P0 item. Everything else builds on dynamic assembly loading.
- Config model must shift from typed properties (e.g., `ProvidersConfig.OpenAI`) to dictionary-based (`Dictionary<string, ProviderConfig>`) so extensions are config-driven, not compile-time-driven.
- Folder convention: `extensions/{type}/{name}/` (e.g., `extensions/channels/discord/`). Config keys match folder names.
- Two-tier registration: extensions can implement `IExtensionRegistrar` for full DI control, or fall back to convention-based discovery (loader scans for IChannel/ILlmProvider/ITool implementations).
- WebSocket channel stays hard-coded in Gateway — it's core infrastructure, not an extension.
- Built-in tools (exec, web search, MCP) stay in the Agent project. Only external tools are extensions.
- `AssemblyLoadContext` per extension for isolation and future hot-reload capability.
- ProviderRegistry (currently dead code) gets integrated as the resolver for per-agent provider selection.
- Original 13 review items reshuffled: P0 channel/provider DI items merged into dynamic loading story; P2 plugin architecture promoted to P0.

**Plan Output:** `.squad/decisions/inbox/leela-implementation-plan.md` — 22 work items across 4 phases, mapped to 6 team members with dependencies and sizing.

### 2026-04-01 — Implementation Plan Rev 2: Copilot P0, OAuth, Conventional Commits

**Trigger:** Three new directives from Jon arrived after the initial plan:
1. Copilot provider is P0 — the only provider Jon uses. OAuth auth, not API key.
2. Conventional commits required — granular commits as work completes.
3. Dynamic assembly loading (already incorporated in Rev 1).

**Copilot Provider Architecture Decisions:**
- Copilot uses OpenAI-compatible HTTP format (same chat completions API, streaming, tool calling) against `https://api.githubcopilot.com`.
- Auth is GitHub OAuth device code flow — no API key. Provider implements `IOAuthProvider` to acquire/cache/refresh tokens at runtime.
- New `IOAuthProvider` and `IOAuthTokenStore` interfaces added to Core abstractions. Providers implementing `IOAuthProvider` skip API key validation in the loader and registry.
- `ProviderConfig` gains an `Auth` discriminator (`"apikey"` | `"oauth"`) so the config model can express both auth modes.
- Shared OpenAI-compatible HTTP client logic (request DTOs, SSE streaming) should be extracted to `Providers.Base` to avoid duplication between OpenAI and Copilot providers.
- Default token store uses encrypted file storage under `~/.botnexus/tokens/`. Interface allows future OS keychain implementations.

**Provider Priority Reordering:**
- Copilot: P0 (only provider Jon uses, must work first)
- OpenAI: P1 (mostly working, foundational for testing)
- Anthropic: P2 (tool calling is nice-to-have, deprioritized)

**Plan Changes:**
- Added 2 new work items: `oauth-core-abstractions` (Phase 1, P0, S) and `copilot-provider` (Phase 2, P0, L).
- Demoted `anthropic-tool-calling` from P1 to P2.
- Sprint 2 execution order leads with Copilot provider (Farnsworth: `provider-dynamic-loading` → `copilot-provider`).
- Added Part 6: Process Guidelines with conventional commits specification.
- Updated dependency graph, team member tables, and decision log.
- Plan is now 24 work items across 4 phases.

**Decision Output:** `.squad/decisions/inbox/leela-copilot-provider.md`
