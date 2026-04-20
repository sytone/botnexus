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

**Phase 1 P0 — Item 2: Channel DI Registration** (25 points) [PLATFORM BLOCKER]
- Register Discord, Slack, Telegram channels in Gateway DI conditional on config Enabled flags
- Update BotNexusServiceExtensions.cs to add conditional registration for each channel
- Only register channels that are Enabled: true in appsettings.json
- See decisions.md for config shape
- Unblocks Phase 1 P1 (Slack webhook) and Phase 2 (full system)

**Phase 1 P0 — Item 3: Anthropic Provider DI** (10 points) [COMPLETENESS]
- Add AddAnthropicProvider() extension method to ServiceCollectionExtensions
- Follow same pattern as AddOpenAiProvider()
- Register AnthropicProvider in DI
- Integrates with ExtensionLoader once Phase 1 Item 1 is done
- Unblocks Phase 2 P1 Item 10 (Anthropic tool calling)

**Phase 1 P1 — Item 6: Gateway Authentication** (40 points)
- Add API key validation to Gateway REST and WebSocket endpoints
- Implement middleware for API key checking on /api/* routes
- Add token validation on WebSocket connection handshake
- Return 401 Unauthorized if missing or invalid
- Make API key configurable via config

**Phase 3 P0 — Item 12: Tool Dynamic Loading** (30 points)
- Extend ExtensionLoader to handle Tools (like existing GitHub tool)
- Follow same folder pattern: extensions/tools/{name}/
- Auto-discover and register tools from configuration
- Unblocks Phase 4 tool expansion

**Phase 4 P1 — Item 19: API Health Endpoint** (20 points)
- Add GET /health endpoint in Gateway API
- Check health of all providers, channels, MCP servers
- Return aggregated health status and component details
- Return 200 OK if all healthy, 503 if any unhealthy

**Phase 4 P1 — Item 20: Assembly Hot-Reload Research** (35 points)
- Research AssemblyLoadContext unload capabilities for hot-reload
- Prototype dynamic reload of extension assemblies without process restart
- Document approach and limitations

## Learnings

### 2026-04-01 — Architecture Review: P0 DI Gaps (from Leela)

**Critical findings affecting your work:**
- **Channel DI Registration:** Discord, Slack, and Telegram are implemented but NOT registered in Gateway's ServiceCollection. Only WebSocketChannel is registered. You'll need to add conditional registration in `BotNexusServiceExtensions.cs` based on config Enabled flags (P0 blocker).
- **Anthropic Provider DI:** Anthropic provider exists but has no DI extension method like OpenAI's `AddOpenAiProvider()`. Will need `AddAnthropicProvider()` added to ServiceCollectionExtensions (P0 blocker).
- **MessageBusExtensions Sync-over-Async:** The `Publish()` method uses `.GetAwaiter().GetResult()` — this is a deadlock hazard in ASP.NET Core. May need to redesign message bus or go fully async (P0 blocker).
- **ProviderRegistry Dead Code:** ProviderRegistry class exists in Providers but is never registered in DI or used. Evaluate: integrate into DI flow or remove.

Build is clean (0 errors, 2 warnings). All 124 tests pass. Contract layer is solid — no circular dependencies.

<!-- Append new learnings below. Each entry is something lasting about the project. -->

## 2026-04-02 — Team Updates

- **Nibbler Onboarded:** New Consistency Reviewer added to team. Owns post-sprint audits.
- **New Ceremony:** Consistency Review ceremony established (trigger: after sprint completion or arch changes). Leela's audit (2026-04-02) found 22 issues across 5 files.
- **Decision:** Cross-Document Consistency Checks merged into decisions.md. All agents treat consistency as a quality gate.


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
- Scenario registry process formalized: Hermes maintains as living document after sprint completion
- Consistency review ceremony established: triggered after sprint or architecture changes

**Process Updates:**
- All decisions from inbox merged into decisions.md (decisions #9, #10, #11)
- Inbox files deleted (merged, not orphaned)
- Cross-agent consistency checks now a formal ceremony with Nibbler as owner
- Documentation updated and consistency audit completed (Leela: 22 issues fixed across 5 files)

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
### 2026-04-12 — Top Banner and Agent Dropdown UI Design

**Task:** Designed CSS for top banner (branding + announcements), layout restructure, and agent dropdown pattern.

**Design Decisions:**
- **Banner:** Two-tier system (header + conditional announcements). Header: gradient background, logo + title, ~48px height. Announcements: left border accent (type-coded colour), dismissible per-item, dark background.
- **Layout:** Changed #app from flex-row to flex-column. Banner spans full width at top. New .app-body wrapper contains sidebar + main canvas in flex-row.
- **Agent Dropdown:** Replaced tree-style agent groups with <select> dropdown. Custom-styled with CSS arrow, consistent with existing form inputs. Session list below shows only non-expired sessions for selected agent.
- **Design Tokens:** All styles use existing CSS variables for consistency (--bg-*, --text-*, --accent, etc.).
- **Accessibility:** Focus indicators (3px rgba shadow), ARIA labels planned, left border colour coding (WCAG-compliant contrast).

**Key Pattern:** Conditional display — announcement bar hidden when empty (.announcement-bar.has-announcements shows via flex). Keeps banner compact when no announcements exist.

**Deliverable:** Complete CSS written for Fry to implement. Decision doc created at .squad/decisions/inbox/amy-banner-design.md.

**Lesson:** Professional UI in developer tools means clean, functional, and compact. Design system consistency (using existing tokens) prevents visual drift and maintains accessibility guarantees. Conditional visibility patterns save vertical space without sacrificing features.

### 2026-04-20 — Read-Only Banner Styling and Accessibility Polish

**Task:** Review and improve read-only sub-agent session banner for visual clarity, design consistency, and WCAG AA compliance.

**Design Decisions:**
- **Visual Accent:** Added 3px left border in `--warning` color to reinforce read-only state (distinct from interactive accent pattern)
- **Status Badges:** Implemented pill-style backgrounds matching `.streaming-badge` pattern with state-specific colors:
  - Running: `rgba(0, 180, 216, 0.12)` background + `--accent` text
  - Completed: `rgba(46, 204, 113, 0.12)` background + `--success` text
- **Contrast Fix:** Changed label from `--text-muted` to `--text-secondary` for WCAG AA compliance (6.6:1 ratio)
- **Accessibility:** Added `role="status"` and `aria-label` for screen reader support
- **Interactive Polish:** Added hover states to clickable sub-agent sidebar items with smooth transitions

**WCAG AA Verification:**
- Badge text: 12.9:1 contrast ✓
- Label text: 6.6:1 contrast ✓
- Status badges: High contrast guaranteed via pill backgrounds ✓

**Key Pattern:** Status variant classes (`.read-only-status.completed`) allow semantic styling while maintaining component consistency. Semi-transparent backgrounds (`rgba()`) create visual hierarchy without departing from the dark theme palette.

**Lesson:** Accessibility isn't optional — verify contrast ratios upfront, not after implementation. Design tokens enforce consistency and prevent hardcoded colors. Status indicators need visual differentiation (color + icon) for both color-blind users and at-a-glance scanning. Screen reader support (`role`, `aria-label`) must be part of initial design, not retrofitted.