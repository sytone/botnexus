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
- Repository examples now live under `examples/`; `projects/teams-proxy` moved to `examples/teams-proxy`, and `poc/BotNexus.CodingAgent` moved to `examples/BotNexus.CodingAgent`.
