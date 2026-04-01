# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-04-01 — Initial Architecture Review

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

**Concerns Identified:**
- Anthropic provider lacks tool calling support (OpenAI has it, Anthropic does not)
- Anthropic provider has no DI extension method (OpenAI has AddOpenAiProvider)
- MessageBusExtensions.Publish() uses sync-over-async (.GetAwaiter().GetResult()) — deadlock risk
- No assembly loading or plugin discovery mechanism exists yet
- Channel implementations (Discord/Slack/Telegram) not registered in Gateway DI — registration code is missing
- Slack channel uses webhook mode but no webhook endpoint exists in Gateway
- No authentication or authorization on any endpoint
- WebUI has no build tooling (vanilla JS, no bundling)
- ProviderRegistry exists but is never registered in DI or used
