# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform (OpenClaw-like) built in C#/.NET. Lean core with extension points for assembly-based plugins. Multiple agent execution modes (local, sandbox, container, remote). Currently focusing on local execution. SOLID patterns with vigilance against over-abstraction. Comprehensive testing (unit + E2E integration).
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels (Base/Discord/Slack/Telegram), Command, Cron, Gateway, Heartbeat, Providers (Base/Anthropic/OpenAI), Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-11 Complete, Phase 12 Wave 1 Initiated.** Build green, 337 tests passing. Farnsworth owns platform configuration, session store, cross-agent guardrails. Phase 12 Wave 1 assignments: 3 API endpoints (type move, channels, extensions), CLI command decomposition, config schema + path resolver. Key recent: config versioning, dynamic extension loader foundation, Telegram setup. Active on gateway sprint: session store abstraction, cross-agent timeout, history pagination.

---

## Archived Entries (2026-04-06 to 2026-04-09)

**Phase 11 Config & Schema Work:**
- Extracted IConfigPathResolver/ConfigPathResolver from CLI reflection logic (supports bracket array indexing)
- JSON schema generation via PlatformConfigSchema with key-casing normalization
- CLI schema command for docs/botnexus-config.schema.json generation
- Commit: e57eae1; 23 new tests; 891 total tests passing

**Phase 11 CLI Decomposition:**
- Refactored Program.cs into thin DI wiring with extracted command handlers
- Cross-platform reliability: TCP port pre-checks, SkipBuild/SkipTests flags

**Phase 12 Sub-Agent Foundation (W1+W2+W3+W4):**
- ISubAgentManager abstraction with spawn/list/kill lifecycle
- DefaultSubAgentManager singleton with recursion prevention, concurrency limits
- REST endpoints, WebSocket event emissions, tool registry security scoping
- Commits: f57b157 (W1), 25c8876 (W2+3 DI), c75a033 (W4 REST)

---
## 2026-04-10T16:30Z — Sub-Agent Spawning Feature: Waves 1 + 2 + 4 (Platform Dev)

**Status:** ✅ Complete  
**Commits:** f57b157 (W1), 25c8876 (W2+3 DI), c75a033 (W4 REST)

**Your Role:** Platform Dev. Wave 1 abstractions, DI wiring, REST endpoints.

**Wave 1 Deliverables:**
- `ISubAgentManager` abstraction in `BotNexus.Gateway.Abstractions`
- `SubAgentSpawnRequest`, `SubAgentInfo`, `SubAgentStatus` models
- `SubAgentOptions` configuration class (maxConcurrentPerSession, defaultMaxTurns, etc.)
- Integrated with existing session infrastructure (reuses `IAgentSupervisor`, session ID format preserved)

**Wave 2+3 DI Work:**
- `DefaultSubAgentManager` registered as singleton in DI
- Sub-agent tool registration in `InProcessIsolationStrategy`
- Recursion prevention wired: `spawn_subagent`, `list_subagents`, `manage_subagent` excluded from sub-agent sessions
- Tool stack depth tracking for safety

**Wave 4 REST Endpoints:**
- `GET /api/agents/sub` — list active sub-agents
- `POST /api/agents/sub` — spawn sub-agent
- `DELETE /api/agents/sub/{id}` — kill sub-agent
- WebSocket event emission: `subagent_spawned`, `subagent_completed`, `subagent_failed`

**Integration:**
- Tool security scoping: explicit allowlist validation against registry
- Completion delivery: reuses existing `FollowUpAsync` message queue
- Resource protection: `maxConcurrentPerSession` enforced per agent descriptor

---


## Learnings

- 2026-04-12: Added ExistenceQuery-backed dual lookup (AgentId owner OR participant ID match) across in-memory, file, and sqlite session stores with shared filtering for time range, type, and limit.
- 2026-04-12: Extracted SessionStoreBase to centralize ListAsync, ListByChannelAsync, and GetExistenceAsync filtering logic while keeping store-specific persistence/locking behavior in derived stores.
- 2026-04-12: Added SessionStoreBase.ListAsync(AgentId?, SessionStatus?, CancellationToken) overload to enforce consistent status filtering for contract tests without changing ISessionStore surface.
- **Wave 4 Completion:** DDD refactoring Wave 4 delivered. ExistenceQuery + dual-lookup implementation across 3 stores complete. All 794 gateway tests passing. Build green (0 errors, 0 warnings). Decisions merged to decisions.md. Architecture docs updated by Kif for Waves 2-3 changes.
- 2026-04-14: Built standalone tools/BotNexus.Probe (independent .sln) with Minimal API host, streamed Serilog/JSONL ingestion, optional OTLP JSON receiver, Gateway REST+SignalR clients, and correlation endpoint /api/correlate/{id}.
- 2026-04-14: Added Probe dual-mode entrypoint (serve + CLI) with command handlers for logs/sessions/session/correlate/files/gateway/traces/trace, JSON/text output modes, and explicit no-results exit code semantics for automation.
- 2026-04-14: Added read-only SQLite session DB ingestion to BotNexus.Probe with shared-connection SessionDbReader (busy retry + query-only pragmas), API/CLI preference for sqlite with transparent JSONL fallback, and session counts/search/history endpoints over sqlite metadata.
- 2026-04-14: Updated Probe sessions web UI to consume rich sqlite payloads (agent/channel/type/status filters, metadata-rich detail cards, tool/compaction history badges) while remaining compatible with legacy JSONL response shapes.


## 2026-04-15 — Extension-Contributed Commands Implementation, Wave 1 (Platform Dev)

**Status:** ✅ Complete  
**Build:** Green (0 errors)

**Context:** Wave 1 implementation of Extension-Contributed Commands feature. Created core contracts and command registry enabling extensions to register user-facing slash commands.

**Deliverables:**

### Contracts (BotNexus.Gateway.Contracts)
- ICommandContributor interface with ExecuteAsync(CommandExecutionContext) method
- CommandDescriptor record (name, description, category, clientSideOnly, subCommands) — serializable for API
- CommandResult record (title, body, isError, metadata)
- CommandExecutionContext record (input, agentId, sessionId, cancellationToken)

### Gateway Commands
- CommandRegistry class aggregating all ICommandContributor instances from DI
- Dispatch logic: input → command name → contributor → result
- Collision detection and error handling
- Integration with AssemblyLoadContextExtensionLoader via DiscoverableServiceContracts
- BuiltInCommandContributor for platform commands (/help, /status, /agents, /new)

### DI Integration
- Registration in Program.cs ServiceCollection
- Singleton registration for CommandRegistry
- Built-in contributor registered as ICommandContributor instance

**Build Status:**
✅ All assemblies compile without errors or warnings
✅ 10 unit tests passing (registration, dispatch, parsing, duplicates, error handling)

**Next Wave:** Wave 2 — WebUI command palette integration

