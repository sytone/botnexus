# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-11 Complete, Phase 12 Wave 1 In Progress.** Build green, 337 tests passing. Hermes owns test framework, integration testing, automation. Phase 12 Wave 1 assignment: Wave 1 test coverage expansion (~30 tests), config path test approach. Implemented comprehensive test matrix (unit/integration/E2E), parallel test isolation, cross-platform compatibility. Currently: config path behavior validation, Wave 1 coverage expansion in progress.

---

## Archived Entries (2026-04-06 to 2026-04-11)

**Phase 11 Testing Work:**
- Config path tests: ConfigPathResolverTests (traversal, edge cases)
- Schema validation tests: SchemaValidationTests
- Extended PlatformConfigurationTests (loader edge cases, round-trip, concurrent reads)
- 23 new tests added; 891 total tests passing (868→891); build clean, 0 warnings
- Commits: 42ff15a, e9040ca, 542d33a

**Phase 12 Sub-Agent Testing (W1+W2+W3+W4):**
- Wave 1 model validation: serialization, enum coverage for SubAgentStatus
- Wave 2 manager tests: spawn/list/kill operations, concurrency enforcement, recursion prevention, timeout behavior
- Wave 3+4 integration tests: REST endpoint validation, event emission patterns
- Commits: b614205 (W1 models), 041d65a (W3+4 integration)

**E2E and Playwright Testing:**
- DelayTool: 9 unit tests (1 currently failing on reason propagation)
- FileWatcherTool: 10 unit tests (all passing)
- ChannelHistory: 10 endpoint contract tests (all passing)
- ListByChannel: 3 store method tests (all passing)
- Scrollback E2E: 6 tests (currently failing - UI rendering issues)
- Commits tracked in recent history; Playwright fixture extended for multi-session seeding

---
## 2026-04-10T16:30Z — Sub-Agent Spawning Feature: Wave 1 + 2 + 3 + 4 Testing (Tester)

**Status:** ✅ Complete  
**Commits:** b614205 (W1 models), 041d65a (W3+4 integration)

**Your Role:** Tester. Comprehensive test coverage across all waves.

**Wave 1 Tests:**
- Model validation: serialization, enum coverage for `SubAgentStatus`
- Configuration validation: `SubAgentOptions` defaults and boundaries
- Request/response DTO shape validation
- 5 unit tests

**Wave 2 Tests:**
- `DefaultSubAgentManager` spawn/list/kill operations
- Concurrent session limit enforcement
- Recursion prevention via depth tracking
- Timeout behavior under load
- Completion delivery via `FollowUpAsync`
- Orphaned session handling on parent deletion
- 15 unit tests

**Wave 3 Tests:**
- `SubAgentSpawnTool` execution (parameter validation, tool allowlist)
- `SubAgentListTool` session scoping
- `SubAgentManageTool` ownership checks (parent can kill, non-parent rejected)
- Full spawn → work → complete → notify cycle
- Timeout trigger test
- Kill test with proper cleanup
- Concurrent spawn limit enforcement
- 15 integration tests

**Wave 4 Tests:**
- REST endpoint coverage: GET list, DELETE kill, error cases
- WebSocket event routing: `subagent_spawned`, `subagent_completed`, `subagent_failed`
- WebUI interaction end-to-end
- Multi-agent scenarios
- 10-20 E2E tests

**Total:** 51 SubAgent tests, all passing

---

## 2026-04-06 - Wave 2 test coverage (Telegram + CLI + Extension loader)
- Added comprehensive Telegram adapter tests covering allow-list enforcement, 4096+ chunking, markdown escaping, polling offset progression, polling shutdown, polling/webhook startup modes, streaming edit behavior, and BotToken/timeout validation handling.
- Added CLI command tests under 	ests/BotNexus.Gateway.Tests/Cli/ for alidate, init, gent, and config flows, including success and error exit codes (missing config, invalid key/path behavior, schema output generation).
- Added extension loader tests under 	ests/BotNexus.Gateway.Tests/Extensions/ for discovery, manifest validation/skip behavior, load + DI registration, collectible AssemblyLoadContext lifecycle, unload behavior, and bad assembly handling.
- Validation: dotnet test tests\\BotNexus.Gateway.Tests\\BotNexus.Gateway.Tests.csproj passed (341/341).

## 2026-04-06 - Wave 2 test coverage (corrected)
- Added Telegram adapter tests for allow-list enforcement, 4096+ chunking, markdown escaping, polling offset progression, graceful polling shutdown, polling/webhook startup modes, streaming accumulation with edit calls, and BotToken/timeout validation.
- Added CLI command tests in tests/BotNexus.Gateway.Tests/Cli for validate, init, agent, and config commands, including success and error exit-code cases.
- Added extension loader tests in tests/BotNexus.Gateway.Tests/Extensions for discovery, manifest validation/skip behavior, load + DI registration, collectible AssemblyLoadContext lifecycle, unload behavior, and bad assembly handling.      
- Validation: dotnet test tests\\BotNexus.Gateway.Tests\\BotNexus.Gateway.Tests.csproj passed (341/341).

## 2026-04-06 - Wave 1 coverage phase 12 (auth bypass + channels + extensions)
- Added 23 new Gateway test cases across `GatewayAuthMiddlewareTests`, `ChannelsControllerTests`, and `ExtensionsControllerTests` to close Wave 1 gaps.
- Auth bypass coverage now explicitly guards API extension-like routes (`/api/agents.json`, `/api/agents.JSON`, `/api/agents/foo.bar`, `/api/sessions.xml`) while preserving allowlist skips (`/health`, `/swagger/index.html`, `/webui/styles.css`) and handling empty/null path edges.
- Added controller response coverage for empty registries, single/multiple records, capability/metadata mapping, DTO payload shape, and fallback `"unknown"` extension type behavior.
- Validation: `dotnet build Q:\\repos\\botnexus --verbosity quiet` and `dotnet test Q:\\repos\\botnexus\\tests\\BotNexus.Gateway.Tests\\BotNexus.Gateway.Tests.csproj --verbosity minimal` passed (368/368).

## 2026-04-06 - Wave 2 coverage phase 12 (rate limit + correlation + metadata + config versioning)
- Expanded `RateLimitingMiddlewareTests` with limit-boundary, 429 short-circuit, Retry-After numeric validation, `/health` case-insensitive bypass, independent client buckets (IP + caller identity), unknown-IP bucket behavior, fallback defaults, and window reset behavior.
- Expanded `CorrelationIdMiddlewareTests` to validate response header presence on every request, incoming header preservation, whitespace-header GUID regeneration, GUID validity, and per-request uniqueness.
- Expanded `SessionsControllerTests` metadata endpoint coverage for empty metadata fetch, non-existent PATCH 404, merge-only PATCH behavior, null-removal precision, persistence after save, and JSON type conversion expectations.
- Expanded `PlatformConfigurationTests` versioning coverage for sync-load missing version default (`1`), warnings for unsupported versions, no warnings for supported versions, and trace warning emission checks.
- Validation: `dotnet build Q:\repos\botnexus --verbosity quiet` and `dotnet test Q:\repos\botnexus\tests\BotNexus.Gateway.Tests --verbosity quiet` passed.


## 2026-04-12 - DDD Wave 4 Test Coverage
- Implemented existence dual-lookup tests (26da24c) validating ExistenceQuery interface across in-memory, file, and SQLite stores
- Validated AgentId owner matches, participant ID matches, combined search (owner OR participant), time range filtering, session type discrimination, result limit enforcement
- Implemented SessionStoreBase contract tests (c1c9475) for new ListAsync(AgentId?, GatewaySessionStatus?, CancellationToken) overload
- Ensured backward compatibility with existing ISessionStore.ListAsync(AgentId?, CancellationToken)
- Cross-store consistency validation complete
- Test Discipline: Updated all affected test suites when ExistenceQuery/SessionStoreBase APIs changed. Ensured contract compliance before store implementations.
- Validation: 794/794 gateway tests passing. Build green (0 errors, 0 warnings).
