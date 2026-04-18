# Gateway Projects Rules

## Dependency boundary

Projects in `src/gateway/` may depend on:
- `src/agent/` — agent core and provider abstractions
- `src/common/` — shared infrastructure (Cron, Memory, Prompts, Tools)
- `src/domain/` — domain primitives and models

**Prohibited dependencies:**
- `src/extensions/` — extensions depend on the gateway, not the other way around. The gateway discovers and loads extensions dynamically via the extension loader.
- `poc/` — proof-of-concept projects are standalone

## Project structure

| Project | Purpose | Allowed deps |
|---------|---------|-------------|
| `BotNexus.Gateway` | Core gateway runtime — agent supervision, isolation, session management, extension loading, dispatch | Agent, Common, Domain |
| `BotNexus.Gateway.Api` | ASP.NET host — controllers, middleware, Program.cs | Gateway, Domain (no direct SignalR references) |
| `BotNexus.Gateway.Abstractions` | Interfaces for extensions — `IEndpointContributor`, `IApiContributor`, agent/session/channel contracts | Domain only |
| `BotNexus.Gateway.Contracts` | Lightweight shared types — session summaries, config models | Domain only |
| `BotNexus.Gateway.Channels` | Base channel adapter classes (`ChannelAdapterBase`) | Domain, Gateway.Abstractions |
| `BotNexus.Gateway.Sessions` | Session store implementations (File, SQLite, InMemory) | Domain, Gateway.Contracts |

## Key architectural rules

- **Gateway.Api has zero extension knowledge.** No references to SignalR, MCP, or any extension project. Extensions are loaded dynamically from `~/.botnexus/extensions/` by the extension loader.
- **Gateway.Abstractions is the extension contract surface.** Extensions reference Abstractions for interfaces like `IChannelAdapter`, `IEndpointContributor`, `IAgentTool`.
- **Gateway.Contracts is for lightweight shared types.** Types here can be referenced by both gateway internals and extensions without pulling in the full gateway.
