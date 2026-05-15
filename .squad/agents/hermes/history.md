# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform built in C#/.NET
- **Stack:** C# (.NET latest), modular class libraries
- **Created:** 2026-04-01

## Core Context

**Hermes's Specialization:** Test framework, QA strategy, integration testing, cross-platform compatibility. Owns comprehensive test matrix, test discipline enforcement.

**Key Delivered Coverage:**
- Phase 11 Config Path Tests: ConfigPathResolver, SchemaValidation, PlatformConfiguration edge cases (23 tests; 891 total)
- Phase 12 Sub-Agent Waves 1-4: Model validation, manager concurrency, integration, REST, WebSocket (51 tests)
- DDD Wave 4: Existence dual-lookup, SessionStoreBase contracts (26 + 10 tests; 794 total)
- Extension-Contributed Commands Wave 1: CommandRegistry + CommandModel (10 tests)
- Dispatcher routing regression: Non-default isolation, default fallback, metadata preservation (42 tests)
- OpenClaw Memory Wave 1: Full QA validation, 61 Memory + 6 Prompts tests passing
- Tool timeout (Issue #24): TDD contracts locked pre-implementation, 24 targeted tests passing
- Conversation extraction: 66/66 + 62/62 conversation tests validated
- PR CI snapshot portability: Normalized OS/path-variant lines in snapshot comparers

**Test Discipline:**
- Always update tests when APIs change (no exceptions)
- Comprehensive edge case coverage before approval
- Cross-store consistency validation for data-layer changes
- Reflection-based tests for non-public testing needs
- Real temp files for parser testing; mocks for unit/contract tests
- TDD: lock test contracts before implementation starts

## Learnings

- When renaming test projects, preserve friend-assembly compatibility by keeping legacy `AssemblyName` if production internals are exposed via `InternalsVisibleTo`.
- Conversation archive regression tests must assert session sealing/persistence (not deletion), plus ActiveSessionId cleared and fresh session on reopen.
- Conversation history refresh regressions: GET /api/conversations/{id}/history must return latest page at offset=0.
- Dynamic SignalR extension loading can fail hub DI if extension resolves a separate assembly identity; guard with activation tests.
- SQLite file-backed tests: disable pooling (`Pooling = false`) and clear pools before deleting DB files on Windows.
- Cross-platform test portability: all fixture paths must use `Path.Combine()`, not hardcoded backslash separators.
- Gateway decoupling test coupling concentrated in InProcessIsolationStrategy constructor wiring; shift to DI/extension-loader-backed seams.
- Coverage gap: no gateway-level tests verify graceful degradation when extensions are absent or fail load.
- CLI update regression coverage must assert pull failures short-circuit before stop/start.
- Sub-agent wake delivery tests should assert both dispatch metadata and stream-event channel capabilities.
- [CORRECTED] Legacy cron cleanup regressions must assert Blazor uses DELETE /api/conversations/{cron-session-id} (archive path only) and never falls back to session deletion; gateway must preserve/seal session history for linked/orphan projections.
- Portal startup regressions must protect initial history load from stale `cron-session:*` 404s by removing orphan projections and continuing to load other conversations.
- ChatPanel hard-refresh coverage must treat stale `ActiveConversationId` values (including removed `cron-session:*`) as empty-state render paths and assert JS interop bind safety (`preventEnterSubmit` only for interactive views with valid ElementReference semantics).
- Location security regression coverage must assert database connection strings are persisted in config but API list/get/create/update only return a redacted placeholder and `HasConfiguredSecret=true`; UI must render redacted value with sensitive styling and never echo synthetic secrets.
- CLI location listing regressions must redact database connection-string values as (redacted) while continuing to show filesystem paths and API endpoints (assert raw secret tokens like Password= and User Id= never appear in output).
- Gateway boundary guardrails should be enforced with a csproj architecture test that scans `src\gateway\**\*.csproj` for forbidden `ProjectReference`, `PackageReference`, and `Reference` includes targeting `src\extensions` or `BotNexus.Extensions.*`.
- Agent-change broadcast should flow through `IAgentChangeNotifier` (contracts) so `BotNexus.Gateway.Api` stays extension-agnostic while SignalR implements transport-specific notification in the extension assembly.
- 2026-05-14: Gateway boundary guard test (GatewayProjectDependencyBoundaryTests.cs) added, fail-before/pass-after validated, commits 1a8a8863 & 8f7a4a21 ready for approval.
- 2026-07-29: Prompt-template regression suite now covers `.prompt.md` discovery/render precedence plus `.prompt.json` compatibility in `tests\gateway\BotNexus.Cron.Tests\CronOptionsPromptTemplateResolverTests.cs` (front matter required, body required, metadata required/default behavior).
- 2026-07-29: CLI prompt coverage now verifies markdown list/render/run flows and multiline body preservation through gateway POST payloads in `tests\gateway\BotNexus.Cli.Tests\Commands\PromptCommandsTests.cs`.

- 2026-05-15: Effective Config QA — Validated backend + frontend implementation (commits 41cc4e8c, 3ffa849a). Full build 0 warnings, full test suite 0 failures. Approved for merge.
- 2026-07-29: Issue #245 vertical-slice TDD contract lives in `AgentPanelVerticalSliceTests.cs` and locks shell selectors (`data-testid='agent-panel'`, `.agent-panel-tab`, conversation active tab selector) plus mobile CSS hooks in `app.css`.
- 2026-08-01: Issue #245 follow-up unskipped all AgentPanel vertical-slice tests; stabilized tab assertions to read `.agent-tab-label` text (icon-safe), verified placeholder tab copy, and hardened CSS path resolution via repo-root discovery (`BotNexus.slnx` probe).
- 2026-08-02: Issue #245 Phase 2 workspace QA added backend coverage for tree read, file read, missing file, directory-as-file, and traversal/path escape rejection in `WorkspaceControllerTests`, `WorkspacePathSecurityTests`, and `WorkspaceControllerIntegrationTests`.
- 2026-08-02: Added/validated Blazor workspace bUnit coverage for loading/empty/error/file-content flows plus mobile CSS hooks in `WorkspacePanelTests`, and extended vertical slice + REST client tests (`AgentPanelVerticalSliceTests`, `GatewayRestClientTests`).
- 2026-08-02: Validation passed with targeted workspace suites and full solution build/test (`dotnet build BotNexus.slnx`, `dotnet test BotNexus.slnx`), with no new skips introduced.
