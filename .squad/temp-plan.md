# BotNexus Implementation Plan — Dynamic Extension Architecture

**Author:** Leela (Lead/Architect)
**Date:** 2026-04-01 (revised 2026-04-01)
**Status:** Proposed (Rev 2 — incorporates Copilot P0, OAuth, and conventional commits directives)
**Requested by:** Jon Bullen

## Executive Summary

Jon's directive fundamentally reshapes our roadmap. The original P2 item — "Design plugin architecture" — is now the foundation that everything else builds on. Channels, providers, and tools must be dynamically loaded from folder-based extension assemblies, referenced by configuration. Nothing loads unless explicitly configured.

This plan re-examines all P0/P1/P2 items through that lens, merges what overlaps, reorders by dependency, and maps every work item to a team member.

**Rev 2 additions:** Jon's directive makes the Copilot provider **P0 — higher priority than all other providers**. Copilot is OpenAI-compatible (base URL: https://api.githubcopilot.com) but authenticates via OAuth device code flow, not API key. This introduces new OAuth abstractions in Core and a dedicated BotNexus.Providers.Copilot extension project. Provider priority is now: Copilot (P0) > OpenAI (P1) > Anthropic (P2). All work follows conventional commit format.

---

## Part 1: Dynamic Assembly Loading — Architecture Design

### 1.1 Folder Structure Convention

extensions/ folder holds channels/, providers/, tools/ sub-folders. Each implementation has its own folder with a dll and dependencies. ExtensionsPath configurable in BotNexusConfig. Default: ./extensions

Key folders:
- extensions/channels/discord/ → BotNexus.Channels.Discord.dll
- extensions/channels/telegram/ → BotNexus.Channels.Telegram.dll
- extensions/channels/slack/ → BotNexus.Channels.Slack.dll
- extensions/providers/copilot/ → BotNexus.Providers.Copilot.dll
- extensions/providers/openai/ → BotNexus.Providers.OpenAI.dll
- extensions/providers/anthropic/ → BotNexus.Providers.Anthropic.dll
- extensions/tools/github/ → BotNexus.Tools.GitHub.dll

### 1.2 Configuration Model

Current config has hard-coded typed properties. Must become dictionary-based so the set of extensions is driven entirely by config.

New appsettings.json shape:
- ExtensionsPath: string (default ./extensions)
- Providers: Dictionary of provider configs keyed by folder name
- Channels.Instances: Dictionary of channel configs keyed by folder name
- Tools.Extensions: Dictionary for dynamically-loaded tool config

ProviderConfig gains Auth discriminator: "apikey" (default) or "oauth"
ExtensionLoader skips API key validation for OAuth providers

### 1.3 Discovery and Loading Process

Core class: ExtensionLoader (in BotNexus.Core or new BotNexus.Extensions project)

Loading sequence at startup:
1. Read config — Enumerate keys under Providers, Channels.Instances, Tools.Extensions
2. Resolve folders — For each key, compute {ExtensionsPath}/{type}/{key}/
3. Validate folder — Log warning and skip if missing
4. Load assemblies — Create AssemblyLoadContext per extension (collectible for hot-reload)
5. Scan for types — Search loaded assemblies for concrete types implementing target interface
6. Register in DI — ServiceProvider.AddSingleton<ILlmProvider>(instance)

---

## Part 2: OAuth Core Abstractions (Phase 1 P0)

New in Core namespace BotNexus.Core.OAuth:

### IOAuthProvider Interface
`
Task<string> GetAccessTokenAsync(CancellationToken)
bool HasValidToken
`

Acquires valid token, performing OAuth flow if needed. HasValidToken checks if cached token still valid.

### IOAuthTokenStore Interface
`
Task<OAuthToken> LoadTokenAsync(string providerName, CancellationToken)
Task SaveTokenAsync(string providerName, OAuthToken token, CancellationToken)
Task ClearTokenAsync(string providerName, CancellationToken)
`

Abstraction for secure token persistence. Default implementation: encrypted file storage at ~/.botnexus/tokens/{providerName}.json
Future: OS keychain integration (Windows Credential Manager, macOS Keychain, Linux Secret Service)

### OAuthToken Record
`
string AccessToken
DateTime ExpiresAt
string? RefreshToken (optional)
`

### Integration with Extension Loader

ProviderConfig.Auth discriminator: "apikey" or "oauth"
ExtensionLoader checks Auth field. For "oauth", it validates IOAuthProvider is implemented, skips API key validation

---

## Part 3: GitHub Copilot Provider (Phase 2 P0)

Provider Name: copilot
Base URL: https://api.githubcopilot.com
Auth: OAuth device code flow

### Implementation

New project: BotNexus.Providers.Copilot
Implements: ILlmProvider (via LlmProviderBase) + IOAuthProvider
HTTP format: OpenAI-compatible chat completions, streaming, tool calling
Same request/response DTOs as OpenAI provider

### OAuth Device Code Flow

1. POST https://github.com/login/device/code with client_id
   Response: { device_code, user_code, verification_uri, interval, expires_in }

2. Display to user: "Go to {verification_uri} and enter code: {user_code}"

3. Poll POST https://github.com/login/oauth/access_token with device_code
   Every {interval} seconds until token returned, user denies, or timeout

4. Cache token via IOAuthTokenStore
5. Use as Bearer token in Authorization header
6. On subsequent requests: check HasValidToken, re-authenticate if expired

### Shared OpenAI-Compatible Code

Extract shared request/response DTOs, SSE streaming parser, HTTP client patterns to BotNexus.Providers.Base
Both OpenAI and Copilot reference Providers.Base and use shared HTTP layer
Each provider adds its own auth mechanism

### Config Shape

`json
{
  "BotNexus": {
    "Providers": {
      "copilot": {
        "Auth": "oauth",
        "DefaultModel": "gpt-4o",
        "ApiBase": "https://api.githubcopilot.com",
        "OAuthClientId": "..."
      }
    }
  }
}
`

---

## Part 4: Implementation Phases & Work Items

### Phase 1: Core Extensions (Foundations)

**P0 (Blocking all subsequent work):**

1. **provider-dynamic-loading** — Farnsworth: Core ExtensionLoader class, AssemblyLoadContext per extension, folder discovery, DI registration. 50 points.

2. **channel-di-registration** — Amy: Register Discord, Slack, Telegram in Gateway DI conditional on config. 25 points.

3. **anthropic-provider-di** — Amy: Add AddAnthropicProvider extension method matching OpenAI pattern. 10 points.

4. **oauth-core-abstractions** — Farnsworth: IOAuthProvider, IOAuthTokenStore, OAuthToken in Core.OAuth namespace. 20 points.

5. **provider-openai-sync-fix** — Fry: Remove MessageBusExtensions.Publish sync-over-async (.GetAwaiter().GetResult()). Redesign to fully async. 30 points.

**P1 (Important, not blocking):**

6. **gateway-authentication** — Hermes: Add API key validation to Gateway REST/WebSocket. 40 points.

7. **slack-webhook-endpoint** — Ralph: Add POST /webhook/slack endpoint in Gateway, validate Slack signatures. 35 points.

### Phase 2: Provider Parity & Copilot (The Exciting Part)

**P0 (Copilot first):**

8. **copilot-provider** — Farnsworth: BotNexus.Providers.Copilot project, OpenAI-compatible HTTP, OAuth device code flow. 60 points.

9. **providers-base-shared** — Fry: Extract HTTP common code (DTOs, streaming, retry) to Providers.Base. 40 points.

**P1:**

10. **anthropic-tool-calling** — Bender: Add tool calling to Anthropic provider (feature parity with OpenAI). 50 points.

11. **provider-config-validation** — Ralph: Schema validation for all provider configs, helpful error messages. 15 points.

### Phase 3: Completeness & Scale

**P0:**

12. **tool-dynamic-loading** — Fry: Extend loader to handle Tools (like GitHub), same folder pattern. 30 points.

13. **config-validation-all** — Ralph: Validate all config sections on startup, fail fast if invalid. 20 points.

**P1:**

14. **cron-task-fixes** — Amy: Review cron task failures, fix any regressions. 25 points.

15. **session-manager-tests** — Fry: Add integration tests for session persistence across restarts. 30 points.

### Phase 4: Scale & Harden

**P0:**

16. **observability-metrics** — Hermes: Add .NET metrics (tool calls, agent loops, provider latency). 40 points.

17. **config-documentation** — Ralph: Document appsettings.json structure, env var overrides, examples. 25 points.

**P1:**

18. **gateway-logging-structured** — Amy: Structured logging via Serilog, trace correlation across channels. 30 points.

19. **provider-registry-cleanup** — Fry: Integrate ProviderRegistry into DI or remove dead code. 15 points.

20. **api-health-endpoint** — Hermes: GET /health checks all providers, channels, MCP servers. 20 points.

21. **assembly-hot-reload** — Farnsworth: Research & prototype AssemblyLoadContext unload for hot-reload. 35 points.

22. **iac-containerization** — Ralph: Dockerfile, docker-compose.yml for easy deployment. 30 points.

23. **integration-tests-e2e** — Fry: Full E2E flow tests: config load → Copilot auth → agent loop → response. 50 points.

24. **roadmap-next-quarter** — Leela: Plan Q2 features (multi-agent coordination, tool chains, etc). 25 points.

---

## Part 5: Team Role Assignments

- **Leela** (Lead/Architect) — oversight, architecture decisions, Q2 planning (item 24)
- **Farnsworth** (CTO-like) — dynamic loader, ExtensionLoader class, assembly contexts, Copilot provider, hot-reload research
- **Amy** (DevOps/Platform) — channel registration, DI fixes, cron review, structured logging
- **Fry** (Backend) — OpenAI sync-fix, shared HTTP code extraction, tool loading, E2E tests
- **Bender** (Provider Expert) — Anthropic tool calling, provider parity
- **Hermes** (Operations) — auth/security, observability, health endpoints
- **Ralph** (Config/Infra) — Slack webhook, config validation, IaC, documentation

---

## Part 6: Prioritization & Sequencing

**Release 1 (Foundation):** Phase 1 P0 (items 1-5) + Phase 1 P1 (items 6-7)
- Enables dynamic loading, Copilot path clear, foundational auth

**Release 2 (Copilot Ready):** Phase 2 P0 (items 8-9)
- Copilot works end-to-end with OAuth

**Release 3 (Feature Parity):** Phase 2 P1 + Phase 3 P0 (items 10-13)
- All providers on equal footing, tool extensibility

**Release 4 (Hardened):** Phase 3 P1 + Phase 4 (items 14-24)
- Production-ready, observable, documented

---

## Part 7: Conventional Commits Requirement

All commits must follow conventional commit format:
- feat: New feature
- fix: Bug fix
- refactor: Code refactor (no feature change)
- docs: Documentation only
- test: Test additions
- chore: Build, CI, dependency updates

Each work item = one or more commits, each commit tagged with affected area (e.g., feat(providers): add copilot oauth flow)

Granular history makes it easy to see what changed and roll back if needed.

---

## Decisions

1. ✅ Dynamic loading is the foundation — all work builds on it
2. ✅ Copilot is P0 with OAuth device code flow
3. ✅ Configuration-driven extension discovery
4. ✅ All commits use conventional format, granular per work item
5. ✅ 24-item roadmap across 4 releases
6. ✅ Team roles assigned, estimates provided

Ready for implementation. First work: Farnsworth starts on provider-dynamic-loading (item 1).
