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
- 2026-04-01: `BotNexus.Core` already references `Microsoft.Extensions.Configuration.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions` (v10.0.5), so extension registration contracts can use these abstractions directly without package changes.
- **OAuth contracts live in Core Abstractions** (2026-04-01): OAuth integration points are defined under `src/BotNexus.Core/Abstractions` via `IOAuthProvider`, `IOAuthTokenStore`, and `OAuthToken`.
- **ProviderConfig auth discriminator** (2026-04-01): `ProviderConfig` includes an `Auth` property defaulting to `"apikey"` for selecting API key vs OAuth provider auth behavior.
- **Config binding is now extension-keyed and case-insensitive** (2026-04-01): `ProvidersConfig` and `ChannelsConfig.Instances` are dictionary-based with `StringComparer.OrdinalIgnoreCase`, enabling folder-name keys (e.g., `openai`, `telegram`) without casing fragility.
- 2026-04-01: `ProviderRegistry` now infers provider keys from provider namespaces/types (e.g., OpenAI -> `openai`) and is DI-registered so agent loops can resolve provider per agent model/provider config with default fallback.

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
