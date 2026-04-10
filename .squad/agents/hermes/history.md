# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-11 Complete, Phase 12 Wave 1 In Progress.** Build green, 337 tests passing. Hermes owns test framework, integration testing, automation. Phase 12 Wave 1 assignment: Wave 1 test coverage expansion (~30 tests), config path test approach. Implemented comprehensive test matrix (unit/integration/E2E), parallel test isolation, cross-platform compatibility. Currently: config path behavior validation, Wave 1 coverage expansion in progress.

---

## 2026-04-06T07:50:00Z — Phase 11 Wave 1: Comprehensive Test Coverage

**Status:** ✅ Complete  
**Agents:** Farnsworth (Config/Schema), Bender (Extension Loading), Hermes (Testing), Kif (Docs)

**Testing Work (Hermes):**
- Created ConfigPathResolverTests (path traversal, edge cases)
- Created SchemaValidationTests (validation behavior tests)
- Extended PlatformConfigurationTests (loader edge cases, round-trip, concurrent reads)
- 23 new tests added
- Gateway test count: 312 (up from 289)
- Commits: 42ff15a, e9040ca, 542d33a

**Cross-Team Results:**
- Farnsworth: Config schema generation, ConfigPathResolver extraction
- Bender: Dynamic extension loading system with manifest discovery
- Kif: 14 XML doc comments, comprehensive module READMEs
- **Total:** 891 tests passing (868→891, +23), Build clean, 0 warnings

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

## 2026-04-09T11:39:00Z - McpInvokeTool comprehensive test suite
- Created 22 tests for McpInvokeTool covering tool metadata (name, label, schema), three actions (list_servers, list_tools, call), parameter validation, error handling (missing params, unconfigured servers, MCP errors), content handling (text, image, multiple blocks, empty content), connection caching/lifecycle, and dispose cleanup.
- Created McpInvokeToolTests.cs (unit tests) and McpInvokeToolIntegrationTests.cs (integration tests with MockMcpTransport).
- Added internal test constructor to McpInvokeTool accepting ConcurrentDictionary<string, McpClient> for dependency injection.
- Copied MockMcpTransport from BotNexus.Extensions.Mcp.Tests for in-memory MCP transport testing.
- Tests follow existing xUnit + FluentAssertions patterns from BotNexus.Extensions.Mcp.Tests.
- Validation: `dotnet test tests\BotNexus.Extensions.McpInvoke.Tests --verbosity quiet` passed (22/22).
- Commit: c3cf681

## Learnings
- 2026-04-10: Session switching backend coverage is best validated with SignalR integration tests that assert `LeaveSession` group removal, `JoinSession` group membership, and `SignalRChannelAdapter` event isolation across two live hub connections.
- 2026-04-10: Reliability hardening for flaky tests should prefer bounded retries (`IOException` + short sleep) over single-attempt temp directory deletion in test `Dispose` paths.
- 2026-04-10: Gateway integration classes need a shared `[Collection("IntegrationTests")]` to avoid parallel port/resource contention, and env-var mutation tests should be grouped under a dedicated xUnit collection.
- 2026-04-09T17:12:06-07:00: Flaky timing tests are more stable with polling helpers (up to 5s) instead of fixed `Task.Delay(...)` checks, especially for async process output/status transitions.
- 2026-04-09T17:12:06-07:00: Concurrency/timing assertions should use generous CI-safe thresholds (e.g., 1500ms for two 200ms parallel calls) and explicit sequencing when cancellation races are under test.
- 2026-04-10: For C# `required` contract tests, validate `RequiredMemberAttribute` presence via reflection string match to avoid framework-version compile coupling.
- 2026-04-10: Sub-agent tool payloads currently serialize `SubAgentStatus` as numeric enums (not strings), so assertions should validate integer enum values in JSON responses.

## 2026-04-10 - SignalR session switching integration expansion
- Added 4 new `SignalRIntegrationTests` scenarios from Nova research:
  - send during active join
  - leave + join + immediate send
  - multiple agents with interleaved joins/sends
  - concurrent clients in different sessions with strict event isolation
- Updated `RegisterAgentAsync` helper to accept explicit `agentId` for multi-agent integration coverage.
- Validation:
  - `dotnet build Q:\repos\botnexus\tests\BotNexus.Gateway.Tests\BotNexus.Gateway.Tests.csproj --verbosity quiet` ✅
  - `dotnet test Q:\repos\botnexus\tests\BotNexus.Gateway.Tests\BotNexus.Gateway.Tests.csproj --filter "FullyQualifiedName~SessionSwitch" --verbosity minimal` ✅ (11/11)
  - Targeted new-test execution ✅ (`Hub_SessionSwitch_ConcurrentClientsDifferentSessions_ReceiveOnlyOwnEvents`)

## 2026-04-10 - WebUI Playwright E2E project scaffold for session switching
- Created new `tests/BotNexus.WebUI.Tests` xUnit project with `Microsoft.Playwright`, `Microsoft.Playwright.Xunit`, `FluentAssertions`, and `Microsoft.AspNetCore.Mvc.Testing`.
- Added project reference to `src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj` and added project to `BotNexus.slnx`.
- Added E2E infrastructure (`WebUiE2ETestHost`, custom Kestrel `WebApplicationFactory`, recording supervisor/handle) and authored five session-switch scenarios in `SessionSwitchingE2ETests.cs`:
  - basic switch + send
  - switch back + send
  - rapid switch + send
  - send during loading
  - inbound event isolation
- Added `PlaywrightFactAttribute` skip gate (`BOTNEXUS_RUN_PLAYWRIGHT_E2E=1`) and `xunit.runner.json` to disable parallel test execution for browser safety.
- Validation:
  - `dotnet build tests\BotNexus.WebUI.Tests\BotNexus.WebUI.Tests.csproj --verbosity quiet` ✅
  - `pwsh tests\BotNexus.WebUI.Tests\bin\Debug\net10.0\playwright.ps1 install chromium` ✅
  - default `dotnet test` run passes with E2E tests skipped unless env flag is enabled ✅

## 2026-04-10T14:47:53-07:00 - Playwright E2E host Kestrel fix
- Fixed KestrelWebApplicationFactory in 	ests/BotNexus.WebUI.Tests/WebUiE2ETestHost.cs to start a real Kestrel host via CreateHost while retaining TestServer host.
- Added explicit Kestrel HTTP client (CreateKestrelClient) and updated test bootstrap to register agents against the real HTTP endpoint.
- Added factory disposal of real Kestrel host and test service overrides (ISessionStore -> InMemorySessionStore) for deterministic E2E behavior.
- Validation:
  - dotnet build tests\\BotNexus.WebUI.Tests\\BotNexus.WebUI.Tests.csproj --verbosity quiet ✅
  - BOTNEXUS_RUN_PLAYWRIGHT_E2E=1 dotnet test tests\\BotNexus.WebUI.Tests --filter "BasicSwitchAndSend" ✅ (1/1)
  - BOTNEXUS_RUN_PLAYWRIGHT_E2E=1 dotnet test tests\\BotNexus.WebUI.Tests ❌ (1 passed, 4 failed; failures now in session-switch behavior assertions, not connection refused)
