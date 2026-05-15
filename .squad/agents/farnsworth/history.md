# Project Context

- **Owner:** Jon Bullen
- **Project:** BotNexus — modular AI agent execution platform built in C#/.NET
- **Stack:** C# (.NET latest), modular class libraries: Core, Agent, Api, Channels, Command, Cron, Gateway, Heartbeat, Providers, Session, Tools.GitHub, WebUI
- **Created:** 2026-04-01

## Core Context

**Phases 1-12 Complete.** Farnsworth leads platform: core abstractions, provider implementations, session management, command system, API layer.

**Key Delivered Systems:**
- Phase 11 Config & Schema: `IConfigPathResolver`, JSON schema generation, CLI schema command
- Phase 11 CLI Decomposition: Program.cs refactored to thin DI wiring with extracted command handlers
- Sub-Agent Foundation (Waves 1-4): `ISubAgentManager` abstraction, spawn/list/kill lifecycle, REST endpoints, WebSocket events, tool registry security scoping
- Extension Commands (Waves 1-2): `ICommandContributor` interface, `CommandRegistry`, command palette integration, built-in commands
- Gateway Lifecycle (Wave 2): `GatewayCommand` with detached/attached modes, health checking, PID management
- Probe Tool: standalone diagnostics with Minimal API, streamed ingestion, SQLite session DB, correlation endpoints
- Gateway Dispatching Layer: `IConversationDispatcher` + `DefaultConversationDispatcher` adapter, contract records, DI registration
- OpenClaw Memory Wave 1 remediation (delegated from Bender rejection): pure delegation fix, dead code removal

## Learnings

- ExistenceQuery-backed dual lookup (AgentId owner OR participant ID) across all session stores with shared filtering.
- SessionStoreBase centralizes ListAsync, ListByChannelAsync, and GetExistenceAsync filtering logic.
- System.CommandLine singleton registration is appropriate for stateless command classes.
- System.CommandLine `Command` objects cannot be added to two parents — call `.Build()` twice for dual-tree registration.
- Phase 2 conversation dispatch: dedicated layer owns inbound resolution/session binding, returns DispatchResult; Hub/Host become transport relays.
- `IConversationDispatcher` + `DefaultConversationDispatcher` registered in DI as the handoff seam for transport rewiring.
- CLI cross-platform: TCP port pre-checks, SkipBuild/SkipTests flags for reliability.

- Prompt template CLI commands should share the cron resolver (`CronOptionsPromptTemplateResolver`) so config-defined and file-backed templates resolve consistently.
- Prompt template file discovery order is shared home (`~/.botnexus/prompts`), agent home (`~/.botnexus/agents/{agentId}/prompts`), then workspace prompts when available.
- Key implementation paths for issue #29: `src\\gateway\\BotNexus.Cli\\Commands\\PromptCommands.cs`, `src\\gateway\\BotNexus.Cron\\Prompts\\CronOptionsPromptTemplateResolver.cs`, `src\\gateway\\BotNexus.Gateway\\Configuration\\PlatformConfig.cs`.
- `.prompt.md` and `.prompt.json` should both resolve in the shared prompt pipeline; when both exist for one template name, markdown wins.
- Markdown prompt templates use YAML front matter for metadata/defaults/required parameters and preserve the markdown body as the renderable prompt text.

## Recent Work (2026-05-14)

**Completed:** Issue #29 prompt template library feature.  
- Rebase conflicts resolved after main integration.
- Prompt template CLI commands finalized.
- All tests passing (full build, full test suite, targeted tests).
- PR #242 opened: https://github.com/sytone/botnexus/pull/242
- Commits: `04a16c7e`, `f1a264c3`, `479e448a`, `d0e3e8ee`, `c4cd5c9b`
- Deferred: broader web prompt gallery/UX beyond `/prompts` scope.
