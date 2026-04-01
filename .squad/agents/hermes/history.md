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

**Phase 1 P0 — Item 5: Provider OpenAI Sync-over-Async Fix** (30 points) [PLATFORM STABILITY]
- Remove MessageBusExtensions.Publish() sync-over-async anti-pattern (.GetAwaiter().GetResult())
- This is a deadlock hazard in ASP.NET Core environments
- Redesign to fully async or refactor message publishing pattern
- All tests must still pass
- See decisions.md for full scope
- Unblocks Phase 2 and Phase 3

**Phase 2 P1 — Item 9: Providers Base Shared Code** (40 points)
- Extract shared HTTP code from OpenAI provider to BotNexus.Providers.Base:
  - Request/response DTOs (ChatCompletion, Message, Tool, ToolCall, etc.)
  - SSE streaming parser
  - HTTP client retry/backoff patterns
- Update OpenAI provider to reference shared code
- Copilot provider (Phase 2 Item 8, Farnsworth) will also use shared code
- Reduces duplication, improves maintainability

**Phase 3 P0 — Item 12: Tool Dynamic Loading** (30 points)
- Extend ExtensionLoader (built by Farnsworth) to handle Tools
- Follow same folder pattern: extensions/tools/{name}/
- Auto-discover and register tools from configuration
- Tool system uses ToolBase abstract class (see Architecture)
- Unblocks Phase 4 tool expansion

**Phase 3 P1 — Item 15: Session Manager Tests** (30 points)
- Add integration tests for session persistence across process restarts
- Test SessionManager.cs behavior: save, reload, state recovery
- Full E2E flow: agent session → restart → resume where left off
- May reveal issues to fix in item 23 (E2E integration tests)

**Phase 4 P1 — Item 18: Gateway Logging Structured** (30 points)
- Integrate Serilog for structured logging in Gateway
- Add trace correlation IDs across all channel messages
- Structured log output (JSON format for easy parsing)
- Makes troubleshooting and monitoring easier

**Phase 4 P1 — Item 23: Integration Tests E2E** (50 points)
- Full end-to-end flow tests:
  - Config load → Copilot OAuth auth → agent execution → tool calls → responses
  - Test multiple providers (Copilot, OpenAI)
  - Test multiple channels (Discord, Slack, Telegram)
  - Ensure everything works together, not just unit tests
- May reveal regressions in earlier phases

## Learnings

### 2026-04-01 — Architecture Review: Auth & Channel Gaps (from Leela)

**Critical findings affecting your work:**
- **No Auth Anywhere:** Gateway REST, WebSocket, and API endpoints have zero authentication/authorization. Anyone who can reach port 18790 owns the system. This is P0 blocking for any public deployment (P1 - defer implementation but urgently needed).
- **Slack Webhook Gap:** Slack channel supports webhook mode (`HandleMessageAsync` is public), but Gateway has no incoming webhook POST endpoint to receive Slack event subscriptions. You'll need to add an endpoint that accepts Slack's challenge and event callbacks (P1).
- **Channel Registration:** Discord/Slack/Telegram channels are implemented but not registered in DI. They're dead code until registration is added (see Amy's P0 list).
- **WebSocket Security:** Currently no token validation on WebSocket connection. Once you add auth, WebSocket must validate the auth token.

Baseline: build is clean, all 124 tests pass. Ready for implementation.

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-04-01 — ExtensionLoader test strategy and quality gate

- ExtensionLoader now has a high-fidelity unit suite in `tests/BotNexus.Tests.Unit/Tests/ExtensionLoaderTests.cs` covering happy path, missing/empty folders, invalid assemblies, no-match assemblies, multiple implementation registration, registrar/convention flows, path traversal/junction hardening, config binding, and AssemblyLoadContext isolation behavior.
- Fixture extension assemblies are in `tests/BotNexus.Tests.Extensions.Convention` and `tests/BotNexus.Tests.Extensions.Registrar`; they are intentionally used as test plugin payloads to exercise real dynamic loading paths.
- Focused coverage run confirms `BotNexus.Core.Extensions.ExtensionLoaderExtensions` line coverage at **92.05%** (`coverage.cobertura.xml` from filtered ExtensionLoader tests).
- Full solution test run currently has an unrelated pre-existing integration failure in `GatewayApiKeyAuthTests.HealthEndpoint_BypassesAuthentication` (503 vs expected 200), independent of loader unit test changes.
