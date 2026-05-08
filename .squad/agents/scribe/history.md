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

**Phase 1 P1 — Item 6: Gateway Authentication** (40 points)
- Add API key validation to Gateway REST and WebSocket endpoints
- Implement middleware for API key checking on /api/* routes
- Add token validation on WebSocket connection handshake
- Return 401 Unauthorized if missing or invalid
- Make API key configurable via config

**Phase 4 P0 — Item 16: Observability Metrics** (40 points)
- Add .NET metrics for:
  - Tool calls (count, latency by tool)
  - Agent loops (count, latency)
  - Provider calls (count, latency by provider)
  - Message processing (throughput, queue depth)
- Use System.Diagnostics.Metrics for .NET metrics
- Make metrics exportable to observability platforms

**Phase 4 P1 — Item 19: API Health Endpoint** (20 points)
- Add GET /health endpoint in Gateway API
- Check health of all providers, channels, MCP servers
- Return aggregated health status and component details
- Return 200 OK if all healthy, 503 if any unhealthy
- Format: JSON with provider/channel/server status and last check time

## Learnings

Initial setup complete.

<!-- Append new learnings below. Each entry is something lasting about the project. -->


### 2026-04-02 — Sprint 5 Complete: Agent Workspace, Memory, Deployment Lifecycle

**Overview:** Sprint 5 delivered the core agent infrastructure (workspace + identity), memory management system (long-term + daily with consolidation), and comprehensive deployment lifecycle validation (10 real-process E2E scenarios).

**Achievement:** 48/50 items done. 2 P2 items deferred (Anthropic tool-calling, plugin architecture deep-dive). Team grew from 6 to 8 agents (Nibbler + Zapp added).

**Workspace & Identity (Leela ws-01/02, Farnsworth ws-03/04/05):**
- Agent workspace structure: ~/.botnexus/agents/{agent-name}/ with SOUL/IDENTITY/USER/AGENTS/TOOLS/MEMORY files
- BotNexusHome.Initialize() creates workspace structure and stub files
- Multi-agent awareness via auto-generated AGENTS.md (from config + identity files)
- File-based persistent identity and personality system
- Integration tests for workspace creation, file structure, and initialization

**Context Builder & Memory Services (Bender ws-06 through ws-12, Farnsworth ws-13):**
- IContextBuilder interface replaces flat systemPrompt with file-driven context assembly
- Context loads workspace files (SOUL, IDENTITY, USER, AGENTS, TOOLS, MEMORY) at session start
- Memory tools added: memory_search (FTS), memory_save, memory_get, memory_list
- Daily memory files (~/.botnexus/agents/{name}/memory/YYYY-MM-DD.md) auto-loaded for today + yesterday
- Long-term MEMORY.md consolidation via LLM-based distillation
- Token budget trimming integrated into context builder

**Heartbeat & Memory Consolidation (Bender ws-15, Farnsworth ws-16):**
- IHeartbeatService runs daily consolidation job: distills daily files → MEMORY.md
- Controlled pruning prevents unbounded memory growth
- Health check integrated with heartbeat service

**Deployment Lifecycle Testing (Hermes ws-17 through ws-21):**
- Implemented 10 real-process E2E scenarios in tests/BotNexus.Tests.Deployment/
- GatewayProcessFixture: spawns Gateway via dotnet run with isolated temp dirs, health probes
- Scenarios cover: install, config creation, startup, agent workspace setup, message routing, multi-agent handoff, session persistence, graceful shutdown, restart with session restoration, platform update, health management, OAuth integration
- All 10 pass. Scenario registry now 86% coverage (48/56 total scenarios).
- Key discovery: Sessions persisted across restart; workspace creation is lazy (on first message); extension loading is explicit, not auto-scanning.

**Scenario Registry & Team Expansion (Leela ws-22, Zapp scenario-registry + deployment-lifecycle-tests):**
- Zapp added to team: owns E2E deployment validation, deployment lifecycle tests
- Nibbler added to team: owns consistency reviews, post-sprint audits
- Kif added to team: owns user-facing documentation, getting-started guide, style guide, GitHub Pages
- Scenario registry process formalized: Hermes maintains as living document after sprint completion
- Consistency review ceremony established: triggered after sprint or architecture changes

**Process Updates:**
- All decisions from inbox merged into decisions.md (decisions #9, #10, #11)
- Inbox files deleted (merged, not orphaned)
- Cross-agent consistency checks now a formal ceremony with Nibbler as owner
- Documentation updated and consistency audit completed (Leela: 22 issues fixed across 5 files)
- Scribe logged getting-started guide session (2026-04-01T23-27-getting-started.md)
- Scribe updated Kif's history with getting-started guide delivery (706 lines, 13 sections, all examples verified)
- Scribe updated Leela's history with Kif onboarding

**Outstanding:**
- 2 P2 items deferred to next sprint: Anthropic tool-calling feature parity, plugin architecture deep-dive
- Hearbeat service still needs HealthCheck.AggregateAsync() implementation (minor gap)
- Plugin discovery (AssemblyLoadContext per extension) not yet fully tested with real extension deployments

## Session Completion: 2026-04-02

**Sprints Completed:** 1-6  
**Items Done:** 71 of 73 (97.3%)  
**Tests Passing:** 395  
**Scenario Coverage:** 64/64 (100%)  
**Team Size:** 12 agents  

**Major Achievements:**
- Dynamic extension loading fully operational
- Copilot OAuth integration complete and tested
- Multi-agent routing with assistant classification deployed
- Agent workspaces with durable file storage working
- Centralized memory system with consolidation running
- Centralized cron service architecture finalized (pending implementation)
- Authentication/authorization layer deployed across Gateway, WebSocket, REST
- Security hardening: ~/.botnexus/ live environment fully protected
- Observability framework (metrics, tracing, health checks) integrated
- WebUI deployed with real-time status feeds
- Full E2E scenario coverage: 64/64 scenarios passing

**Deferred (P2):** 2 Anthropic items awaiting clarification

**Decisions Merged:**
1. Cron service as independent first-class scheduler
2. Live environment protection (~/.botnexus/ isolation)

**Next Steps:** Production deployment readiness, Sprint 7 planning for P2 items.

### 2026-05-07 — Issue Prioritization & Conversation Project Extraction

**Overview:** Leela completed backlog prioritization review and submitted architectural design decision for conversation project extraction.

**Backlog Prioritization (2026-05-07T15:06:00Z):**
- Reviewed 14 open issues, 0 open PRs
- Recommended priority sequence: #127 (fan-out root cause) → #128 (fan-out adapter follow-up) → #99 (E2E test confidence)
- Noted dependency: #115 precedes #112
- Deferred older backlog enhancements pending capacity

**Conversation Project Extraction Decision (2026-05-06):**
- Status: Approved — ready for implementation
- Scope: Extract conversation stores (`InMemoryConversationStore`, `FileConversationStore`, `SqliteConversationStore`) and router from `BotNexus.Gateway.Sessions` to new project `BotNexus.Gateway.Conversations`
- Maintains separation of concerns; conversation lifecycle independently testable
- Dependency graph after refactor: `Domain ← Contracts ← Gateway.Conversations ← Gateway (host)` with sibling `Gateway.Sessions` (no circular refs)
- 6-commit staging plan: project creation → store migration → router migration → DI updates → test project creation → verification
- Risk mitigations documented for SQLite coupling, shared DB, namespace breaks, test fixture sharing
- Decision merged to `squad/decisions.md` (decision item: "Conversation Project Extraction — Architectural Design Review")

**Cross-Agent Updates:**
- Decision merged from `decisions/inbox/` to canonical `decisions.md` (deduplication complete)
- Scribe logged orchestration evidence: `orchestration-log/2026-05-07T15-06-00Z-leela.md`
- Scribe logged session summary: `log/2026-05-07T15-06-00Z-open-issue-prioritization.md`

**Session Context:** Prioritization and architecture only — no implementation started. Ready for implementing agent (likely Fry or developer-assistant) to begin fan-out root cause work (#127).

