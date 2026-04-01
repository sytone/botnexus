# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Learnings

### 2026-04-01 — Architecture Review: P0 DI Gaps (from Leela)

**Critical findings affecting your work:**
- **Channel DI Registration:** Discord, Slack, and Telegram are implemented but NOT registered in Gateway's ServiceCollection. Only WebSocketChannel is registered. You'll need to add conditional registration in `BotNexusServiceExtensions.cs` based on config Enabled flags (P0 blocker).
- **Anthropic Provider DI:** Anthropic provider exists but has no DI extension method like OpenAI's `AddOpenAiProvider()`. Will need `AddAnthropicProvider()` added to ServiceCollectionExtensions (P0 blocker).
- **MessageBusExtensions Sync-over-Async:** The `Publish()` method uses `.GetAwaiter().GetResult()` — this is a deadlock hazard in ASP.NET Core. May need to redesign message bus or go fully async (P0 blocker).
- **ProviderRegistry Dead Code:** ProviderRegistry class exists in Providers but is never registered in DI or used. Evaluate: integrate into DI flow or remove.

Build is clean (0 errors, 2 warnings). All 124 tests pass. Contract layer is solid — no circular dependencies.

<!-- Append new learnings below. Each entry is something lasting about the project. -->
