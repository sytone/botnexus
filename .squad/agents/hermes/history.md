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

