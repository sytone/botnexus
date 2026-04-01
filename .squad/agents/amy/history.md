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

**Phase 1 P0 — Item 2: Channel DI Registration** (25 points) [PLATFORM BLOCKER]
- Register Discord, Slack, Telegram channels in Gateway DI conditional on config Enabled flags
- Update BotNexusServiceExtensions.cs to add conditional registration for each channel
- Only register channels that are Enabled: true in appsettings.json
- See decisions.md for config shape
- Unblocks Phase 1 P1 (Slack webhook) and Phase 2 (full system)

**Phase 1 P0 — Item 3: Anthropic Provider DI** (10 points) [COMPLETENESS]
- Add AddAnthropicProvider() extension method to ServiceCollectionExtensions
- Follow same pattern as AddOpenAiProvider()
- Register AnthropicProvider in DI
- Integrates with ExtensionLoader once Phase 1 Item 1 is done
- Unblocks Phase 2 P1 Item 10 (Anthropic tool calling)

**Phase 1 P1 — Item 6: Gateway Authentication** (40 points)
- Add API key validation to Gateway REST and WebSocket endpoints
- Implement middleware for API key checking on /api/* routes
- Add token validation on WebSocket connection handshake
- Return 401 Unauthorized if missing or invalid
- Make API key configurable via config

**Phase 3 P0 — Item 12: Tool Dynamic Loading** (30 points)
- Extend ExtensionLoader to handle Tools (like existing GitHub tool)
- Follow same folder pattern: extensions/tools/{name}/
- Auto-discover and register tools from configuration
- Unblocks Phase 4 tool expansion

**Phase 4 P1 — Item 19: API Health Endpoint** (20 points)
- Add GET /health endpoint in Gateway API
- Check health of all providers, channels, MCP servers
- Return aggregated health status and component details
- Return 200 OK if all healthy, 503 if any unhealthy

**Phase 4 P1 — Item 20: Assembly Hot-Reload Research** (35 points)
- Research AssemblyLoadContext unload capabilities for hot-reload
- Prototype dynamic reload of extension assemblies without process restart
- Document approach and limitations

## Learnings

### 2026-04-01 — Architecture Review: P0 DI Gaps (from Leela)

**Critical findings affecting your work:**
- **Channel DI Registration:** Discord, Slack, and Telegram are implemented but NOT registered in Gateway's ServiceCollection. Only WebSocketChannel is registered. You'll need to add conditional registration in `BotNexusServiceExtensions.cs` based on config Enabled flags (P0 blocker).
- **Anthropic Provider DI:** Anthropic provider exists but has no DI extension method like OpenAI's `AddOpenAiProvider()`. Will need `AddAnthropicProvider()` added to ServiceCollectionExtensions (P0 blocker).
- **MessageBusExtensions Sync-over-Async:** The `Publish()` method uses `.GetAwaiter().GetResult()` — this is a deadlock hazard in ASP.NET Core. May need to redesign message bus or go fully async (P0 blocker).
- **ProviderRegistry Dead Code:** ProviderRegistry class exists in Providers but is never registered in DI or used. Evaluate: integrate into DI flow or remove.

Build is clean (0 errors, 2 warnings). All 124 tests pass. Contract layer is solid — no circular dependencies.

<!-- Append new learnings below. Each entry is something lasting about the project. -->
