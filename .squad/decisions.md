# Squad Decisions

## Active Decisions

### 1. Initial Architecture Review Findings (2026-04-01)

**Author:** Leela (Lead/Architect)  
**Status:** Proposed  
**Requested by:** Jon Bullen

**Context:** First-pass architecture review of BotNexus — the initial port and setup. No PRD, no spec, no docs. System has never been built, deployed, or run. This review establishes the baseline.

**Key Findings:**

**The Good:**
- Clean contract layer: Core defines 13 well-designed interfaces. Dependencies flow inward. No circular references.
- Build is green: Solution compiles on .NET 10.0 with 0 errors, 2 minor warnings. All 124 tests pass.
- SOLID compliance: Interfaces are small and focused. Single implementations justified by extension model.
- Hierarchical config: BotNexusConfig well-structured with per-agent overrides and sensible defaults.
- Test foundation: 121 unit tests + 3 integration tests. xUnit + FluentAssertions + Moq.
- Agent loop: Well-structured agentic loop with tool calling, session persistence, hooks, MCP support.

**The Concerning:**
- Channel registration gap: Discord, Slack, Telegram are implemented but never registered in Gateway DI.
- Anthropic provider incomplete: OpenAI supports tool calling; Anthropic does not. No DI extension method.
- No auth anywhere: No authentication/authorization on Gateway REST, WebSocket, or API endpoints.
- Sync-over-async hazard: `MessageBusExtensions.Publish()` wraps async with `.GetAwaiter().GetResult()` — deadlock timebomb.
- ProviderRegistry unused: Class exists but never registered in DI or referenced. Dead code.
- Slack webhook gap: Slack channel uses webhook mode but Gateway has no incoming webhook endpoint.
- No plugin/assembly loading: README mentions extensibility, but no mechanism exists.
- Gateway dispatches to first runner only: `runners[0].RunAsync()` — only first IAgentRunner is used.

**P0 — Must Fix Before First Run:**
1. Register channel implementations in Gateway DI (conditional on config Enabled flags)
2. Add Anthropic DI extension (matching OpenAI pattern)
3. Remove sync-over-async wrapper (delete or rewrite MessageBusExtensions.Publish())
4. Add basic configuration documentation (appsettings.json structure)

**P1 — Should Fix Soon:**
5. Add authentication (at minimum, API key auth on REST/WebSocket)
6. Implement Anthropic tool calling (feature parity with OpenAI)
7. Fix first-runner-only dispatch (route by agent name or document intentional single-runner design)
8. Add Slack webhook endpoint (Gateway needs POST for Slack events)
9. Fix CA2024 warning in AnthropicProvider streaming

**P2 — Should Plan:**
10. Design plugin architecture (assembly loading, plugin discovery, dynamic registration)
11. Add observability (metrics, tracing, health check endpoints)
12. Documentation (architecture, setup guide, API reference)
13. Evaluate ProviderRegistry (integrate or remove dead code)

**Status:** SUPERSEDED by Rev 2 Implementation Plan (see below). This review identified gaps; Rev 2 provides the roadmap to fix them.

---

### 2. User Directives — Process & Architecture (2026-04-01)

**Collected by:** Jon Bullen via Copilot CLI  
**Status:** Approved  

**2a. Dynamic Assembly Loading for Extensions** (2026-04-01T16:29Z)

**What:** All channels, providers, tools, etc. must NOT be available by default. They should only be dynamically loaded into the DI container when referenced in configuration. Folder-based organization: each area (providers, tools, channels) has a folder with sub-folders per implementation (e.g., providers/copilot, channel/discord, channel/telegram). Configuration refers to folder names and the core platform loads assemblies from those folders that expose the required interfaces. This keeps things abstracted and reduces security risk of things being loaded and exposing endpoints without the user realising.

**Why:** User request — captured for team memory. This is a foundational architectural decision that reshapes how the platform handles extensibility and security.

**2b. Conventional Commits Format** (2026-04-01T16:43Z)

**What:** Always use conventional commit format (e.g., `feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`). Commit changes as each area of work is completed — not one big commit at the end. This keeps history clean and makes it easy to see what changed and roll back if needed.

**Why:** User request — captured for team memory. This is a process rule that all agents must follow when committing code.

**2c. Copilot Provider is P0, OAuth Authentication** (2026-04-01T16:46Z)

**What:** When prioritizing provider work, Copilot is always P0 — it is the only provider Jon uses. Authentication to Copilot should be via OAuth, following the same approach used by Nanobot (the project BotNexus was ported from and based on). Other providers (OpenAI, Anthropic) are lower priority.

**Why:** User request — captured for team memory. This shapes provider priority and auth implementation. The team should look at Nanobot's OAuth flow as the reference pattern for Copilot provider auth.

---

### 3. BotNexus Implementation Plan — Rev 2 (2026-04-01)

**Author:** Leela (Lead/Architect)  
**Date:** 2026-04-01 (revised 2026-04-01)  
**Status:** Proposed  
**Requested by:** Jon Bullen  

**Executive Summary:**

Jon's directives fundamentally reshape the roadmap. The original P2 item — "Design plugin architecture" — is now the foundation that everything else builds on. Channels, providers, and tools must be dynamically loaded from folder-based extension assemblies, referenced by configuration. Nothing loads unless explicitly configured.

This plan re-examines all P0/P1/P2 items through the dynamic loading lens, merges what overlaps, reorders by dependency, and maps every work item to a team member.

**Rev 2 Key Changes:**
- Jon's directive makes Copilot provider **P0 — higher priority than all other providers**
- Copilot uses OpenAI-compatible API (base URL: https://api.githubcopilot.com)
- Auth via OAuth device code flow, not API key
- Introduces OAuth abstractions in Core and dedicated BotNexus.Providers.Copilot extension
- Provider priority reordered: **Copilot (P0) > OpenAI (P1) > Anthropic (P2)**
- All work follows conventional commit format (feat/fix/refactor/docs/test/chore)

---

### 4. Decision: GitHub Copilot Provider — P0, OAuth Device Code Flow (2026-04-01)

**Author:** Leela (Lead/Architect)  
**Date:** 2026-04-01  
**Status:** Approved  
**Requested by:** Jon Bullen  

**Context:** Jon's directive: "If you need to preference work on providers, copilot is always P0, it will be all I ever use. This should be via OAuth as per the approach Nanobot used."

BotNexus was ported from Nanobot. In Nanobot, the GitHub Copilot provider is defined as:
```python
ProviderSpec(
    name="github_copilot",
    backend="openai_compat",
    default_api_base="https://api.githubcopilot.com",
    is_oauth=True,
)
```

Key facts:
- Copilot uses the **OpenAI-compatible API format** (same as OpenAI provider)
- Base URL: `https://api.githubcopilot.com`
- Auth: **OAuth device code flow** — no API key, token acquired at runtime
- In Nanobot, `is_oauth=True` providers skip API key validation and handle their own auth

---

## Part 1: Dynamic Assembly Loading Architecture

### 1.1 Folder Structure Convention

```
{AppRoot}/
  extensions/
    channels/
      discord/        → BotNexus.Channels.Discord.dll + dependencies
      telegram/       → BotNexus.Channels.Telegram.dll + dependencies
      slack/          → BotNexus.Channels.Slack.dll + dependencies
    providers/
      copilot/        → BotNexus.Providers.Copilot.dll + dependencies
      openai/         → BotNexus.Providers.OpenAI.dll + dependencies
      anthropic/      → BotNexus.Providers.Anthropic.dll + dependencies
    tools/
      github/         → BotNexus.Tools.GitHub.dll + dependencies
```

Each sub-folder is a self-contained deployment unit. The `extensions/` root path is configurable via `ExtensionsPath` in `BotNexusConfig`. Default: `./extensions`.

### 1.2 Configuration Model

Current config has hard-coded typed properties. These must become **dictionary-based** so the set of extensions is driven entirely by config.

**Key changes:**
- `ProvidersConfig` becomes `Dictionary<string, ProviderConfig>` keyed by folder name
- `ProviderConfig` gains `Auth` discriminator: `"apikey"` (default) or `"oauth"`
- `ChannelsConfig` moves per-channel config to `Instances: Dictionary<string, ChannelConfig>`
- `ToolsConfig` adds `Extensions: Dictionary<string, Dictionary<string, object>>` for dynamic tools
- Root config adds `ExtensionsPath: string`

### 1.3 Discovery and Loading Process

**Core class:** `ExtensionLoader` (in `BotNexus.Core` or new `BotNexus.Extensions` project)

**Loading sequence at startup:**
1. Read config — Enumerate keys under Providers, Channels.Instances, Tools.Extensions
2. Resolve folders — Compute `{ExtensionsPath}/{type}/{key}/` for each key
3. Validate folder — Log warning and skip if missing
4. Load assemblies — Create `AssemblyLoadContext` per extension (collectible for hot-reload)
5. Scan for types — Search loaded assemblies for concrete types implementing target interface
6. Register in DI — `ServiceProvider.AddSingleton<ILlmProvider>(instance)`

---

## Part 2: OAuth Core Abstractions (Phase 1 P0 Item)

New in Core namespace `BotNexus.Core.OAuth`:

### IOAuthProvider Interface
```csharp
Task<string> GetAccessTokenAsync(CancellationToken)
bool HasValidToken
```
Acquires valid token, performing OAuth flow if needed. HasValidToken checks if cached token is still valid.

### IOAuthTokenStore Interface
```csharp
Task<OAuthToken> LoadTokenAsync(string providerName, CancellationToken)
Task SaveTokenAsync(string providerName, OAuthToken token, CancellationToken)
Task ClearTokenAsync(string providerName, CancellationToken)
```
Abstraction for secure token persistence. Default implementation: encrypted file storage at `~/.botnexus/tokens/{providerName}.json`
Future: OS keychain integration (Windows Credential Manager, macOS Keychain, Linux Secret Service)

### OAuthToken Record
```csharp
string AccessToken
DateTime ExpiresAt
string? RefreshToken (optional)
```

### Integration with Extension Loader
ProviderConfig.Auth discriminator: "apikey" or "oauth"
ExtensionLoader checks Auth field. For "oauth", validates IOAuthProvider is implemented, skips API key validation.

---

## Part 3: GitHub Copilot Provider (Phase 2 P0 Item)

**Provider Name:** copilot  
**Base URL:** https://api.githubcopilot.com  
**Auth:** OAuth device code flow

### Implementation

New project: `BotNexus.Providers.Copilot`  
Implements: `ILlmProvider` (via `LlmProviderBase`) + `IOAuthProvider`  
HTTP format: OpenAI-compatible chat completions, streaming, tool calling  
Same request/response DTOs as OpenAI provider

### OAuth Device Code Flow

1. `POST https://github.com/login/device/code` with `client_id`
   Response: `{ device_code, user_code, verification_uri, interval, expires_in }`

2. Display to user: "Go to {verification_uri} and enter code: {user_code}"

3. Poll `POST https://github.com/login/oauth/access_token` with `device_code`
   Every {interval} seconds until token returned, user denies, or timeout

4. Cache token via `IOAuthTokenStore`
5. Use as Bearer token in `Authorization` header
6. On subsequent requests: check `HasValidToken`, re-authenticate if expired

### Shared OpenAI-Compatible Code

Extract shared request/response DTOs, SSE streaming parser, HTTP client patterns to `BotNexus.Providers.Base`
Both OpenAI and Copilot reference Providers.Base and use shared HTTP layer
Each provider adds its own auth mechanism

### Config Shape

```json
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
```

---

## Part 4: Implementation Phases & Work Items (24 Items)

### Phase 1: Core Extensions (Foundations) — 7 Items

**P0 (5 items — Blocking all subsequent work):**

| ID | Work Item | Owner | Points | Description |
|---|---|---|---|---|
| 1 | provider-dynamic-loading | Farnsworth | 50 | Core ExtensionLoader class, AssemblyLoadContext per extension, folder discovery, DI registration |
| 2 | channel-di-registration | Amy | 25 | Register Discord, Slack, Telegram in Gateway DI conditional on config |
| 3 | anthropic-provider-di | Amy | 10 | Add AddAnthropicProvider extension method matching OpenAI pattern |
| 4 | oauth-core-abstractions | Farnsworth | 20 | IOAuthProvider, IOAuthTokenStore, OAuthToken in Core.OAuth namespace |
| 5 | provider-openai-sync-fix | Fry | 30 | Remove MessageBusExtensions.Publish sync-over-async (.GetAwaiter().GetResult()), redesign to fully async |

**P1 (2 items — Important, not blocking):**

| ID | Work Item | Owner | Points | Description |
|---|---|---|---|---|
| 6 | gateway-authentication | Hermes | 40 | Add API key validation to Gateway REST/WebSocket |
| 7 | slack-webhook-endpoint | Ralph | 35 | Add POST /webhook/slack endpoint in Gateway, validate Slack signatures |

### Phase 2: Provider Parity & Copilot — 4 Items

**P0 (2 items — Copilot first):**

| ID | Work Item | Owner | Points | Description |
|---|---|---|---|---|
| 8 | copilot-provider | Farnsworth | 60 | BotNexus.Providers.Copilot project, OpenAI-compatible HTTP, OAuth device code flow |
| 9 | providers-base-shared | Fry | 40 | Extract HTTP common code (DTOs, streaming, retry) to Providers.Base |

**P1 (2 items):**

| ID | Work Item | Owner | Points | Description |
|---|---|---|---|---|
| 10 | anthropic-tool-calling | Bender | 50 | Add tool calling to Anthropic provider (feature parity with OpenAI) |
| 11 | provider-config-validation | Ralph | 15 | Schema validation for all provider configs, helpful error messages |

### Phase 3: Completeness & Scale — 5 Items

**P0 (2 items):**

| ID | Work Item | Owner | Points | Description |
|---|---|---|---|---|
| 12 | tool-dynamic-loading | Fry | 30 | Extend loader to handle Tools (like GitHub), same folder pattern |
| 13 | config-validation-all | Ralph | 20 | Validate all config sections on startup, fail fast if invalid |

**P1 (3 items):**

| ID | Work Item | Owner | Points | Description |
|---|---|---|---|---|
| 14 | cron-task-fixes | Amy | 25 | Review cron task failures, fix any regressions |
| 15 | session-manager-tests | Fry | 30 | Add integration tests for session persistence across restarts |
| (future) | ProviderRegistry evaluation | Fry | TBD | Integrate ProviderRegistry into DI or remove dead code |

### Phase 4: Scale & Harden — 8+ Items

**P0 (2 items):**

| ID | Work Item | Owner | Points | Description |
|---|---|---|---|---|
| 16 | observability-metrics | Hermes | 40 | Add .NET metrics (tool calls, agent loops, provider latency) |
| 17 | config-documentation | Ralph | 25 | Document appsettings.json structure, env var overrides, examples |

**P1 (6+ items):**

| ID | Work Item | Owner | Points | Description |
|---|---|---|---|---|
| 18 | gateway-logging-structured | Amy | 30 | Structured logging via Serilog, trace correlation across channels |
| 19 | api-health-endpoint | Hermes | 20 | GET /health checks all providers, channels, MCP servers |
| 20 | assembly-hot-reload | Farnsworth | 35 | Research & prototype AssemblyLoadContext unload for hot-reload |
| 21 | iac-containerization | Ralph | 30 | Dockerfile, docker-compose.yml for easy deployment |
| 22 | integration-tests-e2e | Fry | 50 | Full E2E flow tests: config load → Copilot auth → agent loop → response |
| 23 | roadmap-next-quarter | Leela | 25 | Plan Q2 features (multi-agent coordination, tool chains, etc) |

---

## Part 5: Prioritization & Release Plan

**Release 1 (Foundation):** Phase 1 P0 + P1 (items 1-7)
- Enables dynamic loading, clears Copilot path, foundational auth

**Release 2 (Copilot Ready):** Phase 2 P0 (items 8-9)
- Copilot works end-to-end with OAuth device code flow

**Release 3 (Feature Parity):** Phase 2 P1 + Phase 3 P0 (items 10-13)
- All providers on equal footing, tool extensibility ready

**Release 4 (Hardened):** Phase 3 P1 + Phase 4 (items 14-23)
- Production-ready, observable, documented, containerized

---

## Part 6: Conventional Commits Requirement

All commits must follow conventional commit format:
- `feat:` New feature
- `fix:` Bug fix
- `refactor:` Code refactor (no feature change)
- `docs:` Documentation only
- `test:` Test additions
- `chore:` Build, CI, dependency updates

Each work item = one or more commits, each commit tagged with affected area (e.g., `feat(providers): add copilot oauth flow`)

Granular history makes it easy to see what changed and roll back if needed.

---

## Summary of Decisions

1. ✅ **Dynamic loading is foundation** — all work builds on it (user directive 2a)
2. ✅ **Copilot is P0 with OAuth** — device code flow, OpenAI-compatible API (user directives 2c, decision 4)
3. ✅ **Configuration-driven discovery** — dictionary-based config, no hard-coded types
4. ✅ **Conventional commits required** — feat/fix/refactor/docs/test/chore format (user directive 2b)
5. ✅ **24-item roadmap** across 4 releases with team assignments
6. ✅ **OAuth abstractions in Core** — IOAuthProvider, IOAuthTokenStore for extensible auth

**Ready for implementation.** First work: Farnsworth starts on provider-dynamic-loading (Phase 1 P0 item 1).

---

### 5. Sprint 1 Completion — 7 Foundation Items Done (2026-04-01T17:33Z)

**Status:** Complete  
**Completed by:** Farnsworth (5 items), Bender (2 items)  

All Phase 1 P0 foundation work delivered:

1. ✅ **config-model-refactor** (Farnsworth, 5c6f777)
   - Dictionary-based provider/channel config
   - Case-insensitive key matching via `StringComparer.OrdinalIgnoreCase`
   - Enables configuration-driven extension discovery

2. ✅ **extension-registrar-interface** (Farnsworth)
   - `IExtensionRegistrar` contract in Core.Abstractions
   - Extensions provide own registration logic
   - Loader discovers and invokes registrars automatically

3. ✅ **oauth-core-abstractions** (Farnsworth, 96c2c08)
   - `IOAuthProvider`, `IOAuthTokenStore`, `OAuthToken` in Core.OAuth
   - Default: encrypted file storage at `~/.botnexus/tokens/{providerName}.json`
   - Integrated with ExtensionLoader auth discriminator

4. ✅ **fix-sync-over-async** (Farnsworth)
   - Removed `MessageBusExtensions.Publish()` sync-over-async wrapper
   - All message bus publishing now fully async
   - Eliminates deadlock hazard

5. ✅ **provider-registry-integration** (Farnsworth, 4cfd246)
   - ProviderRegistry now DI-registered
   - Runtime provider resolution by model/provider key
   - Eliminates dead code, enables multi-provider dispatch

6. ✅ **fix-runner-dispatch** (Bender)
   - `IAgentRouter` injectable routing layer in Gateway
   - Multi-agent routing: metadata-driven (`agent`, `agent_name`), broadcast support (`all`, `*`)
   - `IAgentRunner.AgentName` enables deterministic routing
   - Config: `DefaultAgent`, `BroadcastWhenAgentUnspecified`

7. ✅ **dynamic-assembly-loader** (Bender, 8fe66db)
   - `ExtensionLoader` in Core + `ExtensionLoaderExtensions.AddBotNexusExtensions()`
   - Configuration-driven discovery: `BotNexus:Providers`, `BotNexus:Channels:Instances`, `BotNexus:Tools:Extensions`
   - Folder convention: `{ExtensionsPath}/{type}/{key}/`
   - One collectible `AssemblyLoadContext` per extension (isolation, future hot-reload)
   - Registrar-first, fallback to convention registration (`ILlmProvider`, `IChannel`, `ITool`)
   - Path validation (reject rooted paths, `.`/`..`, traversal)
   - Comprehensive logging, continues on missing/empty folders

**Build Status:** ✅ Green, all tests passing, 0 errors

**Unblocks:** Phase 2 P0 — Copilot Provider (item 8, Farnsworth 60pt), Providers Base (item 9, Fry 40pt)

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
